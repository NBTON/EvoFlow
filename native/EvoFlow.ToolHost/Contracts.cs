using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace EvoFlow.ToolHost;

public enum ToolRisk { Read, LocalWrite, ApplicationLaunch, Destructive, DesktopInteraction, OutsideWorkspace, ProcessExecution }
public sealed record ToolRequest(string CallId, string RunId, string Tool, int Version, JsonObject Arguments);
public sealed record ToolResult(string Status, bool Verified, string Summary, JsonObject Data, string? ErrorCode = null, double ExecutionMs = 0);
public sealed record ToolAction(string CallId, string Tool, int Version, ToolRisk Risk, bool RequiresApproval,
    string Summary, string ArgumentsHash, int TimeoutSeconds = 45);
public interface ILocalTool
{
    string Name { get; }
    ToolAction Validate(ToolRequest request);
    Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken);
}
// Browser implementations can use this contract in Phase 4; no browser engine is registered today.
public interface IBrowserTool : ILocalTool { }

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    public static ToolResult Success(string summary, JsonObject? data = null) => new("succeeded", true, summary, data ?? new());
    public static ToolResult Failure(string code, string summary) => new("failed", false, summary, new(), code);
}
