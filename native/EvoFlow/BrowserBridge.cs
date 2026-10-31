using System.Diagnostics;
using System.Text.Json.Nodes;

namespace EvoFlow;

public sealed class BrowserBridge(string home, string workspace) : IAsyncDisposable
{
    Process? process;
    readonly SemaphoreSlim gate = new(1);
    public async Task<JsonNode> ExecuteAsync(JsonNode arguments, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (process is null || process.HasExited)
            {
                process?.Dispose();
                var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "EvoFlow.ToolHost.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "--browser-server", home, workspace }) info.ArgumentList.Add(arg);
                info.Environment.Clear();
                foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA", "APPDATA" }) if (Environment.GetEnvironmentVariable(key) is { } value) info.Environment[key] = value;
                process = Process.Start(info) ?? throw new IOException("Browser host could not start.");
                _ = DrainErrorsAsync(process.StandardError);
            }
            using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            await process.StandardInput.WriteLineAsync(arguments.ToJsonString().AsMemory(), cancellationToken); await process.StandardInput.FlushAsync(cancellationToken);
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken) ?? throw new IOException("Browser host exited.");
            if (line.Length > 200000) throw new IOException("Browser output limit exceeded.");
            return JsonNode.Parse(line)!;
        }
        finally { gate.Release(); }
    }
    static async Task DrainErrorsAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer) > 0) { } } catch (IOException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (process is not null && !process.HasExited)
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { process.Kill(true); }
        }
        process?.Dispose(); gate.Dispose();
    }
}
