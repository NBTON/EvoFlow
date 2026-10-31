using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace EvoFlow;

/// <summary>Direct internal IPC, never MCP. Only this native controller can approve host effects.</summary>
public sealed class LocalToolBridge(WorkerClient worker, string python, string project) : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, (CancellationTokenSource Token, Task Task)> running = [];
    private readonly HashSet<string> delivered = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonNode> completed = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> approvedReads = new();
    private Task? pump;
    private BrowserBridge? browser;
    private Task? connectorsStartup;
    private string connectorRegistry = "";
    public string Workspace { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EvoFlow Files");
    public async Task StartAsync()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "EvoFlow.ToolHost.exe")) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "EvoFlow.ToolHost.dll")))
            throw new IOException("The compiled native ToolHost is missing from this delivery. Rebuild with scripts/Build-Delivery.ps1 before using local capabilities.");
        Directory.CreateDirectory(Workspace);
        browser = new BrowserBridge(worker.Home, Workspace);
        await worker.PostAsync("tools/register", new JsonObject { ["workspace"] = Workspace });
        pump = Task.Run(PumpAsync);
        if (worker.Connections is not null) connectorsStartup = Task.Run(() => worker.Connections.StartEnabledAsync(lifetime.Token));
    }
    private async Task PumpAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (worker.Connections is { } broker)
                {
                    var registry = broker.ExportTools(); string serialized = registry.ToJsonString();
                    if (serialized != connectorRegistry) { await worker.PostAsync("tools/connectors", new JsonObject { ["tools"] = registry }); connectorRegistry = serialized; }
                }
                var response = await worker.GetAsync("tools/pending", lifetime.Token);
                foreach (var call in response["calls"]!.AsArray())
                {
                    string id = call!["callId"]!.GetValue<string>();
                    if (call["status"]!.ToString() != "pending")
                    { if (running.TryGetValue(id, out var active)) active.Token.Cancel(); continue; }
                    if (!delivered.Add(id)) continue;
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    cancellation.CancelAfter(TimeSpan.FromSeconds(175));
                    running[id] = (cancellation, ExecuteAsync(call.DeepClone(), cancellation.Token));
                }
                foreach (var id in running.Where(p => p.Value.Task.IsCompleted).Select(p => p.Key).ToArray())
                { await running[id].Task; running[id].Token.Dispose(); running.Remove(id); }
                await Task.Delay(200, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { worker.ToolProgress?.Invoke(new JsonObject { ["summary"] = "Native tool connection stopped. Restart EvoFlow to reconnect.", ["status"] = "failed" }); }
    }
    private async Task ExecuteAsync(JsonNode call, CancellationToken ct)
    {
        string id = call["callId"]!.ToString();
        string key = call["runId"] + ":" + call["tool"] + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(call["arguments"]!.ToJsonString())));
        string tool = call["tool"]!.ToString(), operation = call["arguments"]?["operation"]?.ToString() ?? "";
        if (tool.StartsWith("mcp_", StringComparison.Ordinal))
        {
            JsonNode connectorResult;
            bool observation = worker.Connections?.States.SelectMany(s => s.Tools).Any(t => t.Name == tool && t.RemoteName is "get_window_state" or "get_browser_state" or "list_windows" or "verify_state" or "get_guide") == true;
            try
            {
                if (!observation && completed.TryGetValue(key, out var previousConnector))
                { connectorResult = previousConnector.DeepClone(); connectorResult["verified"] = false; connectorResult["summary"] = "This exact MCP action was already handled. No effect was replayed; observe before another attempt. " + previousConnector["summary"]; }
                else
                {
                    connectorResult = worker.Connections is null ? EvoFlow.Connectors.McpBroker.Failure("connector_unavailable", "Native connector broker is unavailable.") : await worker.Connections.ExecuteAsync(call, ct);
                    if (!observation) completed[key] = connectorResult.DeepClone();
                }
            }
            catch (OperationCanceledException) { connectorResult = EvoFlow.Connectors.McpBroker.Failure("canceled", "Connector request canceled; no automatic replay.", "canceled"); }
            worker.ToolProgress?.Invoke(connectorResult.DeepClone());
            try { await worker.PostAsync($"tools/{id}/result", new JsonObject { ["deliveryToken"] = call["deliveryToken"]!.DeepClone(), ["result"] = connectorResult }); } catch (Exception) { }
            return;
        }
        bool readOnly = tool == "application_search" || tool == "browser" && operation == "inspect" || tool == "filesystem" && operation is "list" or "inspect" or "read"
            || tool == "document" && operation == "inspect" || tool == "windows_ui" && operation is "windows" or "inspect";
        JsonNode result;
        if (!readOnly && completed.TryGetValue(key, out var previous))
        {
            result = previous.DeepClone();
            result["verified"] = false;
            result["summary"] = "This exact action was already handled in this request; no repeated effect or approval. Prior result: " + previous["summary"] + ". Inspect current state if needed.";
        }
        else
        {
            var host = Path.Combine(AppContext.BaseDirectory, "tools", "EvoFlow.ToolHost.exe");
            using var process = new Process { StartInfo = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Workspace } };
            foreach (var value in new[] { Workspace, worker.Home, python, Path.Combine(project, "worker", "evoflow_worker", "document_handler.py") }) process.StartInfo.ArgumentList.Add(value);
            process.StartInfo.Environment.Clear();
            foreach (var env in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA" })
                if (Environment.GetEnvironmentVariable(env) is { } value) process.StartInfo.Environment[env] = value;
            try
            {
                process.Start();
                using var kill = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
                var errors = process.StandardError.ReadToEndAsync(ct);
                var request = new JsonObject();
                foreach (var field in new[] { "callId", "runId", "tool", "version", "arguments" }) request[field] = call[field]!.DeepClone();
                await process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), ct);
                await process.StandardInput.FlushAsync(ct);
                result = new JsonObject { ["status"] = "failed", ["verified"] = false, ["summary"] = "Tool host exited without a verified result.", ["data"] = new JsonObject() };
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    if (line.Length > 250000) throw new InvalidOperationException("Native tool output budget exceeded.");
                    var message = JsonNode.Parse(line)!;
                    if (message["type"]?.ToString() == "action")
                    {
                        var action = message["action"]!;
                        worker.ToolProgress?.Invoke(action.DeepClone());
                        bool approved = !action["requiresApproval"]!.GetValue<bool>() || readOnly && approvedReads.ContainsKey(key)
                            || worker.ApproveTool is not null && await worker.ApproveTool(action.DeepClone(), ct);
                        if (approved && readOnly) approvedReads[key] = true;
                        ct.ThrowIfCancellationRequested();
                        await process.StandardInput.WriteLineAsync(new JsonObject { ["approved"] = approved }.ToJsonString().AsMemory(), ct);
                        await process.StandardInput.FlushAsync(ct);
                    }
                    else if (message["type"]?.ToString() == "result") result = message["result"]!.DeepClone();
                    else if (message["type"]?.ToString() == "native_launch")
                    {
                        if (call["tool"]!.ToString() != "application_launch" || message["application"]!.ToString() != call["arguments"]!["application"]!.ToString()) throw new InvalidOperationException("Unexpected native launch handoff.");
                        var launch = await EvoFlow.ToolHost.ApplicationLauncher.LaunchAsync(message["application"]!.ToString(), ct);
                        await process.StandardInput.WriteLineAsync(launch.ToJsonString().AsMemory(), ct);
                        await process.StandardInput.FlushAsync(ct);
                    }
                    else if (message["type"]?.ToString() == "native_browser")
                    {
                        if (call["tool"]!.ToString() != "browser" || message["arguments"]!.ToJsonString() != call["arguments"]!.ToJsonString()) throw new InvalidOperationException("Unexpected browser handoff.");
                        var outcome = await browser!.ExecuteAsync(message["arguments"]!, ct);
                        await process.StandardInput.WriteLineAsync(outcome.ToJsonString().AsMemory(), ct); await process.StandardInput.FlushAsync(ct);
                    }
                }
                await process.WaitForExitAsync(ct);
                await errors;
                if (call["tool"]!.ToString() == "application_launch" && result["status"]!.ToString() == "succeeded")
                {
                    double executionMs = result["executionMs"]?.GetValue<double>() ?? 0;
                    var verification = Stopwatch.StartNew();
                    result = await EvoFlow.ToolHost.ApplicationLauncher.VerifyAsync(call["arguments"]!["application"]!.ToString(), ct);
                    result["executionMs"] = executionMs + verification.Elapsed.TotalMilliseconds;
                }
            }
            catch (OperationCanceledException)
            {
                result = new JsonObject { ["status"] = "canceled", ["verified"] = false, ["summary"] = "Tool process tree stopped. Partial effects may remain; inspect before retrying.", ["data"] = new JsonObject() };
                var audit = new JsonObject { ["callId"] = id, ["runId"] = call["runId"]!.DeepClone(), ["tool"] = call["tool"]!.DeepClone(), ["status"] = "canceled", ["verified"] = false, ["timestamp"] = DateTimeOffset.UtcNow.ToString("O") };
                await File.AppendAllTextAsync(Path.Combine(worker.Home, "tool-audit.jsonl"), audit.ToJsonString() + Environment.NewLine);
            }
            catch (Exception) { result = new JsonObject { ["status"] = "failed", ["verified"] = false, ["summary"] = "The native tool process could not complete. No success was verified.", ["data"] = new JsonObject() }; }
            finally { try { if (process.Id > 0 && !process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
            if (!readOnly) completed[key] = result.DeepClone();
        }
        worker.ToolProgress?.Invoke(result.DeepClone());
        try { await worker.PostAsync($"tools/{id}/result", new JsonObject { ["deliveryToken"] = call["deliveryToken"]!.DeepClone(), ["result"] = result }); }
        catch (Exception) { /* Canceled/expired calls cannot be revived by a late result. */ }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (pump is not null) await pump;
        foreach (var active in running.Values) active.Token.Cancel();
        await Task.WhenAll(running.Values.Select(x => x.Task));
        foreach (var active in running.Values) active.Token.Dispose();
        if (browser is not null) await browser.DisposeAsync();
        if (connectorsStartup is not null) { try { await connectorsStartup; } catch (OperationCanceledException) { } }
        lifetime.Dispose();
    }
}
