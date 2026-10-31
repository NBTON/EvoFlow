using System.Text.Json;
using System.Text.Json.Nodes;

namespace EvoFlow.ToolHost;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--browser-server")
        {
            await using var browser = new BrowserService(args[1], args[2]);
            while (await Console.In.ReadLineAsync() is { } input)
            {
                ToolResult outcome;
                try
                {
                    if (input.Length > 100000) throw new ArgumentException("Browser input budget exceeded.");
                    outcome = await browser.ExecuteAsync(JsonNode.Parse(input)!.AsObject()).WaitAsync(TimeSpan.FromSeconds(40));
                }
                catch (Exception) { outcome = Wire.Failure("browser_failed", "Browser action failed, timed out or a control became inaccessible. Inspect before retrying."); }
                Console.WriteLine(JsonSerializer.Serialize(outcome, Wire.Json));
            }
            return;
        }
        ToolResult result; ToolRequest? request = null; ToolAction? action = null;
        string home = args.Length > 1 ? args[1] : "";
        var started = DateTimeOffset.UtcNow;
        bool authorized = false;
        System.Diagnostics.Stopwatch? execution = null;
        try
        {
            if (args.Length != 4) throw new ArgumentException("Tool host configuration is missing.");
            var line = await Console.In.ReadLineAsync();
            if (line is null || line.Length > 110000) throw new ArgumentException("Invalid request size.");
            request = JsonSerializer.Deserialize<ToolRequest>(line, Wire.Json) ?? throw new ArgumentException("Invalid request.");
            var tools = new LocalTools(new PathPolicy(args[0], home), args[2], args[3]);
            action = tools.Validate(request);
            Console.WriteLine(JsonSerializer.Serialize(new { type = "action", action }, Wire.Json));
            var approval = JsonNode.Parse(await Console.In.ReadLineAsync() ?? "{}");
            if (approval?["approved"]?.GetValue<bool>() != true)
                result = new("denied", false, "Action was declined. No effect performed.", new(), "approval_denied");
            else
            {
                authorized = true;
                var fresh = tools.Validate(request);
                if (fresh != action) throw new ArgumentException("Action changed after review. Please retry.");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(action.TimeoutSeconds));
                execution = System.Diagnostics.Stopwatch.StartNew();
                if (request.Tool == "application_launch")
                {
                    // The native client launches apps so their lifetime is independent of this
                    // short-lived approval/policy helper. The model has no access to this pipe.
                    Console.WriteLine(JsonSerializer.Serialize(new { type = "native_launch", application = request.Arguments["application"]!.GetValue<string>() }, Wire.Json));
                    result = JsonSerializer.Deserialize<ToolResult>(await Console.In.ReadLineAsync() ?? "{}", Wire.Json) ?? Wire.Failure("launch_unverified", "Native launch returned no result.");
                }
                else if (request.Tool == "browser")
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { type = "native_browser", arguments = request.Arguments }, Wire.Json));
                    result = JsonSerializer.Deserialize<ToolResult>(await Console.In.ReadLineAsync() ?? "{}", Wire.Json) ?? Wire.Failure("browser_failed", "Browser returned no result.");
                }
                else result = await tools.ExecuteAsync(request, deadline.Token);
            }
        }
        catch (OperationCanceledException) { result = new("timed_out", false, "Execution reached its timeout; owned processes were stopped. Check files for completed effects before retrying.", new(), "timeout"); }
        catch (ArgumentException ex) { result = Wire.Failure("validation", ex.Message); }
        catch (Exception) { result = Wire.Failure("execution_failed", "The local operation failed or its controls were inaccessible. No success was verified."); }
        result = result with { ExecutionMs = execution?.Elapsed.TotalMilliseconds ?? 0 };
        // Local audit uses only typed identity, hashes and status. Never arguments, file contents,
        // window titles, stdout, exception details, provider tokens or supplied UI values.
        if (!string.IsNullOrEmpty(home))
        {
            Directory.CreateDirectory(home);
            var audit = JsonSerializer.Serialize(new { callId = request?.CallId, runId = request?.RunId,
                tool = request?.Tool, version = request?.Version, argumentsHash = action?.ArgumentsHash,
                risk = action?.Risk.ToString(), approvalRequired = action?.RequiresApproval,
                authorized, authorizationSource = authorized ? action?.RequiresApproval == true ? "native_user" : "established_policy" : "none",
                status = result.Status, verified = result.Verified, errorCode = result.ErrorCode,
                executionMs = result.ExecutionMs,
                startedAt = started, finishedAt = DateTimeOffset.UtcNow }, Wire.Json);
            // Separate helpers can finish concurrently. Serialize append across processes.
            using var auditLock = new Mutex(false, "Local\\EvoFlowToolAudit-" + Wire.Hash(home)[..20]);
            bool acquired = false;
            try
            {
                try { acquired = auditLock.WaitOne(TimeSpan.FromSeconds(3)); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("Audit append is busy.");
                File.AppendAllText(Path.Combine(home, "tool-audit.jsonl"), audit + Environment.NewLine);
            }
            finally { if (acquired) auditLock.ReleaseMutex(); }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { type = "result", result }, Wire.Json));
    }
}
