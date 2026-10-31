using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace EvoFlow;

public sealed class WorkerClient : IAsyncDisposable
{
    private Process? process;
    private bool disposed;
    private LocalToolBridge? tools;
    public EvoFlow.Connectors.McpBroker? Connections { get; private set; }
    public string? ConnectionSetupError { get; private set; }
    public Func<JsonNode, CancellationToken, Task<bool>>? ApproveTool { get; set; }
    public Action<JsonNode>? ToolProgress { get; set; }
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(120) };
    public string Home { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EvoFlow");
    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
        var basePath = AppContext.BaseDirectory;
        var bundledPython = Path.Combine(basePath, "runtime", "python.exe");
        var locationPath = Path.Combine(basePath, "worker-location.json");
        var location = File.Exists(locationPath) ? JsonNode.Parse(await File.ReadAllTextAsync(locationPath))! : new JsonObject();
        var python = File.Exists(bundledPython) ? bundledPython : location["python"]?.GetValue<string>() ?? "";
        if (!File.Exists(python)) throw new InvalidOperationException("Worker Python is missing. Run scripts/Setup.ps1, then rebuild.");
        var project = File.Exists(bundledPython) ? basePath : location["project"]!.GetValue<string>();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var info = new ProcessStartInfo(python)
        {
            WorkingDirectory = project,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "-m", "evoflow_worker", "serve", "--token-stdin", "--home", Home }) info.ArgumentList.Add(arg);
        info.Environment["PYTHONPATH"] = Path.Combine(project, "worker");
        info.Environment["PYTHONUNBUFFERED"] = "1";
        // Suppress automatic tracing of raw graph state. The worker's explicit,
        // optional LangSmith monitor uses DPAPI and sends operational metadata only.
        info.Environment["LANGSMITH_TRACING"] = "false";
        process = Process.Start(info) ?? throw new InvalidOperationException("Could not start local worker");
        var errors = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(token); await process.StandardInput.FlushAsync();
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (line is null) { var error = await errors; throw new InvalidOperationException("Worker startup failed: " + error[..Math.Min(error.Length, 500)]); }
        var ready = JsonNode.Parse(line)!;
        http.BaseAddress = new Uri($"http://127.0.0.1:{ready["port"]!.GetValue<int>()}/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var readinessDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                await SendAsync(HttpMethod.Get, "health", cancellationToken: readinessDeadline.Token);
                Connections = new EvoFlow.Connectors.McpBroker(Home) { Approve = (action, ct) => ApproveTool is null ? Task.FromResult(false) : ApproveTool(action, ct) };
                try { await Task.Run(() => CuaSetup.ConfigureAsync(Connections, Home)); }
                catch (Exception ex) { ConnectionSetupError = ex.Message; }
                tools = new LocalToolBridge(this, python, project);
                await tools.StartAsync();
                return;
            }
            catch (HttpRequestException) { await Task.Delay(100, readinessDeadline.Token); }
        }
        throw new InvalidOperationException("Local worker did not become ready");
        }
        catch
        {
            await StopOwnedWorkerAsync();
            throw;
        }
    }
    public async Task<JsonNode> GetAsync(string path, CancellationToken cancellationToken = default) => await SendAsync(HttpMethod.Get, path, cancellationToken: cancellationToken);
    public async Task<JsonNode> PostAsync(string path, JsonNode? body = null) => await SendAsync(HttpMethod.Post, path, body);
    public async Task<JsonNode> DeleteAsync(string path) => await SendAsync(HttpMethod.Delete, path);
    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string message; try { message = JsonNode.Parse(text)?["detail"]?.ToString() ?? text; } catch { message = text; }
            throw new InvalidOperationException(message[..Math.Min(message.Length, 500)]);
        }
        return JsonNode.Parse(text) ?? new JsonObject();
    }
    public async IAsyncEnumerable<JsonNode> EventsAsync(string runId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"runs/{runId}/events", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode(); using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("event: done")) yield break;
            if (line.StartsWith("data: ")) yield return JsonNode.Parse(line[6..])!;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (tools is not null) await tools.DisposeAsync();
        if (Connections is not null) await Connections.DisposeAsync();
        await StopOwnedWorkerAsync();
        http.Dispose();
    }
    private async Task StopOwnedWorkerAsync()
    {
        if (process is not null && !process.HasExited)
        {
            try { if (http.BaseAddress is not null) await PostAsync("shutdown").WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        process?.Dispose();
        process = null;
    }
}
