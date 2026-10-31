using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ModelContextProtocol.Protocol;

namespace EvoFlow.Connectors;

/// <summary>Private redirected stdio with SDK framing. The Windows job closes all owned descendants even if EvoFlow crashes.</summary>
internal sealed class OwnedServer : IAsyncDisposable
{
    readonly Process process;
    readonly SafeFileHandle job;
    public int Pid { get; }
    public StreamClientTransport Transport { get; }
    public OwnedServer(ConnectionConfig config, IReadOnlyDictionary<string, string?> environment)
    {
        var info = new ProcessStartInfo(config.Command) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(config.Command)! };
        foreach (var argument in config.Arguments) info.ArgumentList.Add(argument);
        info.Environment.Clear();
        foreach (var item in environment) if (item.Value is not null) info.Environment[item.Key] = item.Value;
        job = CreateJobObject(IntPtr.Zero, null);
        var limits = new JobLimits { BasicLimitInformation = new BasicLimits { LimitFlags = 0x2000 } };
        if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobLimits>())) throw new IOException("Cannot create owned MCP process job.");
        process = Process.Start(info) ?? throw new IOException("MCP server did not start.");
        Pid = process.Id;
        if (!AssignProcessToJobObject(job, process.Handle)) { process.Kill(true); process.Dispose(); job.Dispose(); throw new IOException("Cannot supervise the MCP process tree."); }
        _ = DrainAsync(process.StandardError);
        Transport = new StreamClientTransport(process.StandardInput.BaseStream, new MessageBoundStream(process.StandardOutput.BaseStream));
    }
    static async Task DrainAsync(StreamReader reader)
    {
        char[] buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer) != 0) { } } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { if (!process.HasExited) process.Kill(true); }
            }
        }
        finally { job.Dispose(); process.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] struct JobLimits { public BasicLimits BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobLimits info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}

/// <summary>Bound each SDK stdio frame before JSON deserialization, including base64 images.</summary>
internal sealed class MessageBoundStream(Stream source) : Stream
{
    int bytes;
    void Check(ReadOnlySpan<byte> value) { foreach (byte b in value) { bytes = b == 10 ? 0 : bytes + 1; if (bytes > 8_000_000) throw new IOException("MCP message exceeds 8 MB."); } }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { int count = await source.ReadAsync(buffer, ct); Check(buffer.Span[..count]); return count; }
    public override int Read(byte[] buffer, int offset, int count) { int read = source.Read(buffer, offset, count); Check(buffer.AsSpan(offset, read)); return read; }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
}

internal sealed class BoundedHttpHandler : DelegatingHandler
{
    public BoundedHttpHandler() : base(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if (response.Content.Headers.ContentLength > 8_000_000) { response.Dispose(); throw new IOException("MCP HTTP response exceeds budget."); }
        var prior = response.Content;
        var bounded = new StreamContent(new MessageBoundStream(await prior.ReadAsStreamAsync(ct)));
        foreach (var header in prior.Headers) bounded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = bounded; return response;
    }
}
