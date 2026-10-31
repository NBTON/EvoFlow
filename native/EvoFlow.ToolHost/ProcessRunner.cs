using System.Diagnostics;
using System.Text;

namespace EvoFlow.ToolHost;

public sealed record ProcessOutcome(int ExitCode, string Stdout, string Stderr, bool Truncated);
public static class ProcessRunner
{
    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments, string directory)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        info.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA" })
            if (Environment.GetEnvironmentVariable(key) is { } value) info.Environment[key] = value;
        info.Environment["PATH"] = Environment.SystemDirectory;
        info.Environment["PYTHONNOUSERSITE"] = "1";
        return info;
    }
    public static async Task<ProcessOutcome> RunAsync(ProcessStartInfo info, string? input, CancellationToken cancellationToken)
    {
        using var process = Process.Start(info) ?? throw new IOException("Process could not be started.");
        using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        bool truncated = false;
        async Task<string> Drain(StreamReader reader)
        {
            var result = new StringBuilder(); var buffer = new char[4096]; int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            { int room = Math.Max(0, 16000 - result.Length); result.Append(buffer, 0, Math.Min(room, count)); if (count > room) truncated = true; }
            return result.ToString();
        }
        var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
        if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellationToken);
        return new(process.ExitCode, await stdout, await stderr, truncated);
    }
}
