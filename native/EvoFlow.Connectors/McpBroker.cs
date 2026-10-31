using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace EvoFlow.Connectors;

public sealed record DiscoveredTool(string Name, string RemoteName, string Description, JsonObject Schema, string Hash, bool Enabled, JsonObject? OutputSchema = null);
public sealed record ConnectionState(string Id, string Name, string Status, string? Error, int? OwnedPid, string? ServerVersion, IReadOnlyList<DiscoveredTool> Tools);

public sealed class McpBroker : IAsyncDisposable
{
    sealed class Live(ConnectionConfig config)
    {
        public readonly ConnectionConfig Config = config;
        public readonly SemaphoreSlim Gate = new(1);
        public readonly CuaPolicy Policy = new(config);
        public McpClient? Client; public OwnedServer? Process; public IAsyncDisposable? Http;
        public string Generation = Guid.NewGuid().ToString("N"), Status = "disconnected";
        public string? Error; public string? ServerVersion;
        public IReadOnlyList<DiscoveredTool> Tools = [];
        public IAsyncDisposable? Notifications;
        public CancellationTokenSource Stop = new();
    }
    readonly ConcurrentDictionary<string, Live> live = new();
    readonly SemaphoreSlim desktop = new(1), auditGate = new(1), configGate = new(1);
    readonly string home;
    readonly TimeSpan executionTimeout;
    readonly CancellationTokenSource lifetime = new();
    public ConnectionStore Store { get; }
    public Func<JsonNode, CancellationToken, Task<bool>>? Approve { get; set; }
    public event Action? Changed;
    public McpBroker(string home, TimeSpan? executionTimeout = null) { this.home = home; this.executionTimeout = executionTimeout ?? TimeSpan.FromSeconds(45); if (this.executionTimeout < TimeSpan.FromMilliseconds(50) || this.executionTimeout > TimeSpan.FromSeconds(45)) throw new ArgumentOutOfRangeException(nameof(executionTimeout)); Store = new(home); foreach (var c in Store.Load()) { c.Validate(); if (!live.TryAdd(c.Id, new(c))) throw new IOException("Duplicate connection identity."); } }
    public IReadOnlyList<ConnectionConfig> Configurations => live.Values.Select(s => s.Config).OrderBy(s => s.Id).ToArray();
    public async Task UpdateBuiltinAsync(ConnectionConfig config)
    {
        if (!config.Kind.StartsWith("cua_") || !config.Id.StartsWith("cua_")) throw new ArgumentException("Expected an app-owned connection.");
        config.Validate(); if (live.ContainsKey(config.Id)) await DisconnectAsync(config.Id);
        live[config.Id] = new(config); Store.Save(Configurations); Changed?.Invoke();
    }
    public IReadOnlyList<ConnectionState> States => live.Values.OrderBy(s => s.Config.Id).Select(s => new ConnectionState(s.Config.Id, s.Config.Name, s.Status, s.Error, s.Process?.Pid, s.ServerVersion, s.Tools)).ToArray();
    public async Task SaveAsync(ConnectionConfig config, string? bearer = null)
    {
        config.Validate(); await configGate.WaitAsync(lifetime.Token);
        try
        {
            if (live.TryGetValue(config.Id, out var prior))
            {
                if (prior.Config.Kind != "generic" && config != prior.Config && config != (prior.Config with { Enabled = config.Enabled, RetainEvidence = config.RetainEvidence })) throw new ArgumentException("Built-in Cua contracts cannot be edited. Enable, disable or test the reviewed capability.");
                await DisconnectAsync(config.Id);
                if (prior.Config.Endpoint != config.Endpoint || prior.Config.Command != config.Command || prior.Config.Authentication != config.Authentication || prior.Config.OAuthClientId != config.OAuthClientId)
                { Store.WriteSecret(config.Id, null); Store.WriteSecret(config.Id, null, ".oauth"); }
            }
            if (!live.ContainsKey(config.Id) && live.Count >= 16) throw new ArgumentException("Connection limit reached.");
            var values = Configurations.Where(c => c.Id != config.Id).Append(config).ToArray(); Store.Save(values);
            if (bearer is not null) Store.WriteSecret(config.Id, bearer);
            live[config.Id] = new(config); Changed?.Invoke();
        }
        finally { configGate.Release(); }
    }
    public async Task RemoveAsync(string id)
    {
        if (!live.TryGetValue(id, out var entry)) return;
        if (entry.Config.Kind != "generic") throw new ArgumentException("Built-in connections can be disabled, not removed.");
        await DisconnectAsync(id); live.TryRemove(id, out _); Store.Save(Configurations); Store.WriteSecret(id, null); Store.WriteSecret(id, null, ".oauth"); Changed?.Invoke();
    }
    public async Task StartEnabledAsync(CancellationToken ct = default)
    {
        foreach (var entry in Configurations.Where(c => c.Enabled)) { try { await ConnectAsync(entry.Id, false, ct); } catch (Exception) { /* Failure is visible in native settings. */ } }
    }
    public async Task ConnectAsync(string id, bool interactive = false, CancellationToken ct = default)
    {
        var s = live[id]; await s.Gate.WaitAsync(ct);
        try
        {
            await CloseAsync(s); s.Stop.Dispose(); s.Stop = new(); s.Status = "connecting"; s.Error = null; Changed?.Invoke();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token, s.Stop.Token);
            deadline.CancelAfter(interactive && s.Config.Authentication == "oauth" ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(25));
            var config = s.Config; config.Validate(); IClientTransport transport;
            if (config.Transport == "stdio")
            {
                if (config.ExecutableHash.Length > 0 && FileHash(config.Command) != config.ExecutableHash) throw new InvalidOperationException("server_integrity_changed");
                var env = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
                // Only OS basics plus this server's explicitly assigned credential enter the child.
                env["PATH"] = Environment.SystemDirectory;
                if (config.Authentication == "bearer" && Store.ReadSecret(id) is { } secret) env["MCP_SERVER_BEARER_TOKEN"] = secret;
                if (config.Kind.StartsWith("cua_"))
                {
                    env["CUA_DRIVER_PERMISSION_MODE"] = "standard";
                    env["CUA_DRIVER_CAPABILITY_MANIFEST_FILE"] = config.Manifest;
                    env["CUA_DRIVER_CAPABILITY_MANIFEST_APPROVED"] = "1";
                    env["CUA_DRIVER_TELEMETRY_DISABLED"] = "1";
                    if (config.Kind == "cua_browser") env["CUA_DRIVER_BROWSER_PROFILE_ROOT"] = Path.Combine(home, "connections", "cua-browser-profiles");
                }
                s.Process = new OwnedServer(config, env); transport = s.Process.Transport;
            }
            else
            {
                var options = new HttpClientTransportOptions { Endpoint = new(config.Endpoint), Name = id,
                    TransportMode = HttpTransportMode.StreamableHttp, MaxReconnectionAttempts = 0, ConnectionTimeout = TimeSpan.FromSeconds(20) };
                if (config.Authentication == "bearer")
                {
                    string secret = Store.ReadSecret(id) ?? throw new InvalidOperationException("credential_missing");
                    options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + secret };
                }
                if (config.Authentication == "oauth")
                    options.OAuth = new ClientOAuthOptions { RedirectUri = OAuthRedirect(), ClientId = string.IsNullOrWhiteSpace(config.OAuthClientId) ? null : config.OAuthClientId,
                        TokenCache = Store.TokenCache(id), AuthorizationCallbackHandler = (context, token) => interactive ? OAuthCallbackAsync(context, token) : Task.FromException<AuthorizationResult?>(new InvalidOperationException("authentication_required")) };
                var http = new HttpClientTransport(options, new HttpClient(new BoundedHttpHandler()), ownsHttpClient: true);
                s.Http = http; transport = http;
            }
            s.Client = await McpClient.CreateAsync(transport, new McpClientOptions { ClientInfo = new() { Name = "EvoFlow", Version = "1.0" }, InitializationTimeout = TimeSpan.FromSeconds(interactive ? 180 : 20) }, cancellationToken: deadline.Token);
            s.ServerVersion = s.Client.ServerInfo?.Version;
            s.Notifications = s.Client.RegisterNotificationHandler("notifications/tools/list_changed", (_, _) => { s.Tools = []; s.Generation = Guid.NewGuid().ToString("N"); s.Status = "tools_changed"; Changed?.Invoke(); return ValueTask.CompletedTask; });
            await DiscoverAsync(s, deadline.Token);
            if (live.Values.Where(v => v != s && v.Config.Enabled).Sum(v => v.Tools.Count(t => t.Enabled)) + s.Tools.Count(t => t.Enabled) > 64) throw new InvalidOperationException("enabled_registry_budget_exceeded");
            s.Status = "ready"; Changed?.Invoke();
        }
        catch (Exception ex)
        {
            await CloseAsync(s); s.Status = "failed"; s.Error = SafeError(ex); Changed?.Invoke(); throw new InvalidOperationException(s.Error);
        }
        finally { s.Gate.Release(); }
    }
    static Uri OAuthRedirect()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        return new Uri($"http://127.0.0.1:{port}/oauth/callback/");
    }
    static async Task<AuthorizationResult?> OAuthCallbackAsync(AuthorizationCallbackContext context, CancellationToken ct)
    {
        if (context.AuthorizationUri.Scheme != "https" && !context.AuthorizationUri.IsLoopback) throw new InvalidOperationException("unsupported_authentication");
        using var listener = new HttpListener(); listener.Prefixes.Add(context.RedirectUri.AbsoluteUri); listener.Start();
        Process.Start(new ProcessStartInfo(context.AuthorizationUri.AbsoluteUri) { UseShellExecute = true });
        var response = await listener.GetContextAsync().WaitAsync(ct);
        var query = response.Request.QueryString;
        byte[] text = Encoding.UTF8.GetBytes("You may return to EvoFlow. Authorization is being verified.");
        response.Response.ContentType = "text/plain"; await response.Response.OutputStream.WriteAsync(text, ct); response.Response.Close();
        return new AuthorizationResult { Code = query["code"] ?? "", State = query["state"], Iss = query["iss"] };
    }
    async Task DiscoverAsync(Live s, CancellationToken ct)
    {
        var tools = new List<DiscoveredTool>(); string? cursor = null; int bytes = 0;
        for (int page = 0; page < 4; page++)
        {
            var result = await s.Client!.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, ct);
            foreach (var tool in result.Tools)
            {
                string schema = tool.InputSchema.GetRawText(); bytes += schema.Length + (tool.Description?.Length ?? 0);
                if (tools.Count >= 128 || schema.Length > 48000 || bytes > 300000) throw new InvalidOperationException("discovery_budget_exceeded");
                if (tools.Any(t => t.RemoteName == tool.Name)) throw new InvalidOperationException("duplicate_tool_identity");
                var parsed = JsonNode.Parse(schema) as JsonObject ?? throw new InvalidOperationException("invalid_tool_schema");
                ValidateSchema(parsed); _ = JsonSchema.FromText(schema);
                var advertised = s.Policy.AdvertisedSchema(tool.Name, parsed);
                JsonObject? outputSchema = tool.OutputSchema is { } declared ? JsonNode.Parse(declared.GetRawText()) as JsonObject : null;
                if (outputSchema is not null) { ValidateSchema(outputSchema); _ = JsonSchema.FromText(outputSchema.ToJsonString()); }
                string hash = Hash(Canonical(new JsonObject { ["name"] = tool.Name, ["schema"] = parsed.DeepClone(), ["hostContract"] = advertised.DeepClone(), ["hostPolicyVersion"] = 1, ["output"] = tool.OutputSchema is { } output ? JsonNode.Parse(output.GetRawText()) : null }));
                bool enabled = s.Config.ReviewedTools.Contains(tool.Name) && CuaPolicy.ContractAllowed(s.Config, tool.Name, parsed);
                tools.Add(new($"mcp_{s.Config.Id}_{Hash(tool.Name)[..12]}", tool.Name, (tool.Description ?? "")[..Math.Min(tool.Description?.Length ?? 0, 1400)], advertised, hash, enabled, outputSchema));
            }
            cursor = result.NextCursor; if (cursor is null) break;
            if (page == 3) throw new InvalidOperationException("discovery_page_budget_exceeded");
        }
        s.Tools = tools;
    }
    static void ValidateSchema(JsonNode node, int depth = 0)
    {
        if (depth > 24) throw new InvalidOperationException("schema_depth_exceeded");
        if (node is JsonObject obj)
            foreach (var p in obj) { if (p.Key is "$id" or "$schema" && p.Value?.ToString().Length > 300) throw new InvalidOperationException("invalid_tool_schema"); if (p.Key is "$ref" or "$dynamicRef" && p.Value is not null && !p.Value.ToString().StartsWith('#')) throw new InvalidOperationException("external_schema_reference_disabled"); if (p.Value is not null) ValidateSchema(p.Value, depth + 1); }
        else if (node is JsonArray array) foreach (var child in array) if (child is not null) ValidateSchema(child, depth + 1);
    }
    public JsonArray ExportTools()
    {
        var values = live.Values.Where(s => s.Status == "ready" && s.Config.Enabled).SelectMany(s => s.Tools.Where(t => t.Enabled).Select(t => (JsonNode)new JsonObject {
            ["name"] = t.Name, ["description"] = $"MCP {s.Config.Name} / {t.RemoteName}. Native policy and approval apply. " + t.Description,
            ["parameters"] = t.Schema.DeepClone(), ["schemaHash"] = t.Hash, ["serverId"] = s.Config.Id, ["generation"] = s.Generation,
            ["group"] = s.Config.Kind, ["remoteName"] = t.RemoteName })).ToArray();
        return new JsonArray(values);
    }
    public async Task<JsonObject> ExecuteAsync(JsonNode call, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew(); JsonObject result;
        try { result = await ExecuteCoreAsync(call, ct); }
        catch (OperationCanceledException) { result = Failure("canceled", "Connector request canceled before dispatch; no mutation was replayed.", "canceled"); }
        result["executionMs"] = watch.Elapsed.TotalMilliseconds;
        var record = new JsonObject { ["kind"] = "dispatch_outcome", ["callId"] = call["callId"]?.DeepClone(), ["runId"] = call["runId"]?.DeepClone(),
            ["serverId"] = call["binding"]?["serverId"]?.DeepClone(), ["tool"] = call["tool"]?.DeepClone(), ["schemaHash"] = call["binding"]?["schemaHash"]?.DeepClone(),
            ["argumentHash"] = Hash(Canonical(call["arguments"])), ["deliveryMode"] = call["arguments"]?["delivery_mode"]?.ToString() ?? "background", ["status"] = result["status"]?.DeepClone(),
            ["errorCode"] = result["errorCode"]?.DeepClone(), ["verified"] = result["verified"]?.DeepClone(), ["executionMs"] = watch.Elapsed.TotalMilliseconds, ["at"] = DateTimeOffset.UtcNow.ToString("O") };
        await auditGate.WaitAsync(); try { await File.AppendAllTextAsync(Path.Combine(home, "mcp-audit.jsonl"), record.ToJsonString() + Environment.NewLine); } finally { auditGate.Release(); }
        return result;
    }
    async Task<JsonObject> ExecuteCoreAsync(JsonNode call, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew(); string name = call["tool"]?.ToString() ?? "";
        var s = live.Values.FirstOrDefault(s => s.Tools.Any(t => t.Name == name));
        if (s is null) return Failure("unregistered_connector", "Connection or tool is unavailable. Test/reconnect in native Connections.");
        await desktop.WaitAsync(ct);
        try
        {
            await s.Gate.WaitAsync(ct);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token, s.Stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(45));
                string generation = s.Generation;
                if (!s.Config.Enabled || s.Status != "ready" || call["binding"]?["generation"]?.ToString() != generation || call["binding"]?["serverId"]?.ToString() != s.Config.Id) return Failure("stale_connection", "Connection generation changed. Observe again; no action was replayed.");
                await DiscoverAsync(s, deadline.Token);
                var tool = s.Tools.SingleOrDefault(t => t.Name == name && t.Enabled);
                if (tool is null || call["binding"]?["schemaHash"]?.ToString() != tool.Hash) { Changed?.Invoke(); return Failure("schema_changed", "Tool contract changed. Rediscover before requesting another action."); }
                var args = call["arguments"]?.DeepClone() as JsonObject ?? throw new ArgumentException("invalid_arguments");
                if (args.ToJsonString().Length > 64000 || !JsonSchema.FromText(tool.Schema.ToJsonString()).Evaluate(args).IsValid) return Failure("invalid_arguments", "Arguments failed the discovered MCP schema.");
                var target = s.Policy.Validate(tool.RemoteName, args);
                string approvalHash = Hash(s.Config.Id + generation + tool.Hash + Canonical(args) + target);
                var action = new JsonObject { ["summary"] = $"MCP · {s.Config.Name} / {tool.RemoteName}\n{target}\nArguments: {args.ToJsonString()}\nContract: {tool.Hash[..16]} · authorization: {approvalHash[..16]}", ["risk"] = args["delivery_mode"]?.ToString() == "foreground" ? "ForegroundControl" : "ConnectorAction", ["requiresApproval"] = true };
                action["summary"] = Redact(s.Config.Id, action["summary"]!.ToString());
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                bool approved = Approve is not null && await Approve(action, deadline.Token);
                await AuditAsync(call, s, tool, approvalHash, approved ? "approved" : "denied", false, clock.Elapsed.TotalMilliseconds);
                if (!approved) return Failure("approval_denied", "Native approval declined. No connector call was dispatched.", "denied");
                deadline.CancelAfter(executionTimeout); ct.ThrowIfCancellationRequested();
                // Recheck both remote contract and live target after human review. Approval cannot outlive either.
                await DiscoverAsync(s, deadline.Token);
                if (s.Generation != generation || !s.Tools.Any(t => t.Name == name && t.Enabled && t.Hash == tool.Hash)
                    || approvalHash != Hash(s.Config.Id + generation + tool.Hash + Canonical(args) + s.Policy.Validate(tool.RemoteName, args)))
                    return Failure("approval_invalidated", "Tool contract or target changed during review; request a fresh action.");
                var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(args.ToJsonString())!;
                var before = DesktopState();
                var result = await s.Client!.CallToolAsync(tool.RemoteName, arguments, cancellationToken: deadline.Token);
                var after = DesktopState();
                var value = await ConvertResultAsync(s, tool, result, call["runId"]!.ToString(), deadline.Token);
                value["data"]!["deliveryEvidence"] = new JsonObject { ["foregroundBefore"] = before.Window, ["foregroundAfter"] = after.Window, ["pointerBeforeX"] = before.X, ["pointerBeforeY"] = before.Y, ["pointerAfterX"] = after.X, ["pointerAfterY"] = after.Y };
                value["executionMs"] = clock.Elapsed.TotalMilliseconds;
                if (s.Config.RetainEvidence)
                {
                    string directory = Path.Combine(home, "connector-evidence", Hash(call["runId"]!.ToString())[..20]); Directory.CreateDirectory(directory);
                    var selected = value.DeepClone(); selected["data"]?.AsObject().Remove("visionImages");
                    selected["callId"] = call["callId"]!.DeepClone(); selected["argumentHash"] = Hash(Canonical(args));
                    string file = Path.Combine(directory, call["callId"]!.ToString() + ".json"); await File.WriteAllTextAsync(file, selected.ToJsonString(), ct);
                    value["data"]!["evidencePath"] = file;
                }
                await AuditAsync(call, s, tool, approvalHash, value["status"]!.ToString(), value["verified"]!.GetValue<bool>(), clock.Elapsed.TotalMilliseconds);
                return value;
            }
            catch (OperationCanceledException)
            {
                bool canceled = ct.IsCancellationRequested || lifetime.IsCancellationRequested || s.Stop.IsCancellationRequested;
                await CloseAsync(s); s.Status = "disconnected"; s.Error = "Canceled or timed out. Partial effects are uncertain; reconnect and observe before retrying."; Changed?.Invoke();
                return Failure(canceled ? "canceled" : "timed_out", s.Error, canceled ? "canceled" : "timed_out");
            }
            catch (ArgumentException ex) { return Failure("policy_denied", ex.Message, "denied"); }
            catch (Exception ex)
            {
                await CloseAsync(s); s.Status = "failed"; s.Error = SafeError(ex); Changed?.Invoke(); return Failure("connector_failed", s.Error + " No automatic replay; partial effects may remain.");
            }
            finally { s.Gate.Release(); }
        }
        finally { desktop.Release(); }
    }
    async Task<JsonObject> ConvertResultAsync(Live s, DiscoveredTool tool, CallToolResult result, string runId, CancellationToken ct)
    {
        string raw = JsonSerializer.Serialize(result);
        raw = Redact(s.Config.Id, raw, json: true);
        if (raw.Length > 8_000_000 || result.Content.Count > 32) throw new IOException("MCP output exceeds budget.");
        var json = JsonNode.Parse(raw)!.AsObject();
        var content = json["content"] as JsonArray ?? [];
        var texts = new List<string>(); var images = new JsonArray(); var vision = new JsonArray();
        foreach (var item in content)
        {
            switch (item?["type"]?.ToString())
            {
                case "text": texts.Add(item["text"]?.ToString() ?? ""); break;
                case "image":
                    if (item["mimeType"]?.ToString() != "image/png") throw new IOException("Unsupported MCP image media type; only bounded PNG evidence is accepted.");
                    byte[] bytes = Convert.FromBase64String(item["data"]!.ToString());
                    if (bytes.Length is < 33 or > 4_000_000 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                        || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)) is 0 or > 4096
                        || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)) is 0 or > 4096) throw new IOException("Malformed MCP PNG.");
                    string directory = Path.Combine(home, "connector-evidence", Hash(runId)[..20]); Directory.CreateDirectory(directory);
                    string file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png"); await File.WriteAllBytesAsync(file, bytes, ct);
                    images.Add(new JsonObject { ["path"] = file, ["sha256"] = Hash(bytes), ["mimeType"] = "image/png", ["modelInspected"] = false });
                    if (bytes.Length <= 500000 && vision.Count < 1) vision.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(bytes) } });
                    break;
                default: throw new IOException("Unsupported MCP content type; audio, embedded resources and resource links are not executed.");
            }
        }
        JsonNode? structured = json["structuredContent"]?.DeepClone();
        if (structured is not null && structured is not JsonObject) throw new IOException("Malformed MCP structured content; expected an object.");
        if (tool.OutputSchema is not null && result.IsError != true && (structured is null || !JsonSchema.FromText(tool.OutputSchema.ToJsonString()).Evaluate(structured).IsValid)) throw new IOException("MCP result failed its declared output schema.");
        // Cua also includes compact JSON in text on some releases. Preserve it without treating it as instructions.
        if (structured is null && texts.Count == 1) { try { structured = JsonNode.Parse(texts[0]) as JsonObject; } catch (JsonException) { } }
        if (tool.RemoteName == "list_windows" && s.Config.Kind == "cua_desktop") { s.Policy.FilterWindows(structured); texts = [structured?.ToJsonString() ?? "No admitted Calculator windows."]; }
        if (string.Join("\n", texts).Length > 90000 || (structured?.ToJsonString().Length ?? 0) > 90000) throw new IOException("MCP text/structured output exceeds budget.");
        bool error = result.IsError == true || s.Config.Kind.StartsWith("cua_") && structured?["status"]?.ToString() is "refused" or "failed" or "error";
        if (!error) s.Policy.Observe(tool.RemoteName, structured);
        bool observed = !error && s.Config.Kind.StartsWith("cua_") && tool.RemoteName is "get_window_state" or "get_browser_state" or "list_windows";
        if (!error && s.Config.Kind == "cua_desktop" && tool.RemoteName == "verify_state") observed = structured?["status"]?.ToString() == "satisfied" && structured?["stable"]?.GetValue<bool>() == true;
        var data = new JsonObject { ["backend"] = "MCP", ["serverId"] = s.Config.Id, ["tool"] = tool.RemoteName, ["schemaHash"] = tool.Hash,
            ["text"] = string.Join("\n", texts), ["structuredContent"] = structured, ["images"] = images, ["visionImages"] = vision,
            ["verification"] = observed ? "Fresh observation; evaluate the requested postcondition in this state." : "Tool response only; inspect fresh application state before claiming effects." };
        return new JsonObject { ["status"] = error ? "failed" : "succeeded", ["verified"] = observed, ["summary"] = error ? "MCP server returned error content. Read the structured refusal; no fallback was attempted." : "MCP response received from " + s.Config.Name + " / " + tool.RemoteName,
            ["errorCode"] = error ? structured?["refusal"]?["code"]?.ToString() ?? structured?["code"]?.ToString() ?? "mcp_tool_error" : null, ["data"] = data };
    }
    async Task AuditAsync(JsonNode call, Live s, DiscoveredTool tool, string argumentHash, string outcome, bool verified, double ms)
    {
        var record = new JsonObject { ["callId"] = call["callId"]?.DeepClone(), ["runId"] = call["runId"]?.DeepClone(), ["serverId"] = s.Config.Id,
            ["generation"] = s.Generation, ["tool"] = tool.RemoteName, ["schemaHash"] = tool.Hash, ["authorizationHash"] = argumentHash,
            ["outcome"] = outcome, ["verified"] = verified, ["executionMs"] = ms, ["at"] = DateTimeOffset.UtcNow.ToString("O") };
        await auditGate.WaitAsync(); try { await File.AppendAllTextAsync(Path.Combine(home, "mcp-audit.jsonl"), record.ToJsonString() + Environment.NewLine); } finally { auditGate.Release(); }
    }
    public async Task DisconnectAsync(string id)
    {
        var s = live[id]; s.Stop.Cancel(); await s.Gate.WaitAsync();
        try { await CloseAsync(s); s.Status = "disconnected"; Changed?.Invoke(); } finally { s.Gate.Release(); }
    }
    static async Task CloseAsync(Live s)
    {
        s.Stop.Cancel();
        if (s.Notifications is not null) await s.Notifications.DisposeAsync(); s.Notifications = null;
        if (s.Client is not null) { try { await s.Client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { } s.Client = null; }
        if (s.Process is not null) { await s.Process.DisposeAsync(); s.Process = null; }
        if (s.Http is not null) { await s.Http.DisposeAsync(); s.Http = null; }
        s.Tools = []; s.Generation = Guid.NewGuid().ToString("N"); s.Policy.Reset();
    }
    static string SafeError(Exception ex) => ex switch {
        OperationCanceledException => "Connection timed out or was canceled.",
        HttpRequestException h when h.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Authentication failed. Update credentials or use Sign in / Test.",
        _ => "MCP connection failed (" + ex.GetType().Name + "). Check transport, authentication and server compatibility in Connections." };
    public static JsonObject Failure(string code, string summary, string status = "failed") => new() { ["status"] = status, ["verified"] = false, ["summary"] = summary, ["errorCode"] = code, ["data"] = new JsonObject() };
    public static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    string Redact(string id, string text, bool json = false)
    {
        var secrets = new List<string>(); if (Store.ReadSecret(id) is { } bearer) secrets.Add(bearer);
        if (Store.ReadSecret(id, ".oauth") is { } cache)
        {
            void Walk(JsonNode? node)
            {
                if (node is JsonObject obj) foreach (var p in obj) { if (p.Key.Replace("_", "").ToLowerInvariant() is "accesstoken" or "refreshtoken" or "clientsecret" && p.Value is JsonValue) secrets.Add(p.Value.ToString()); else Walk(p.Value); }
                else if (node is JsonArray array) foreach (var child in array) Walk(child);
            }
            Walk(JsonNode.Parse(cache));
        }
        foreach (var secret in secrets.Where(v => v.Length >= 8)) text = text.Replace(json ? JsonSerializer.Serialize(secret)[1..^1] : secret, "[redacted]", StringComparison.Ordinal);
        return text;
    }
    static (long Window, int X, int Y) DesktopState() { GetCursorPos(out var point); return (GetForegroundWindow().ToInt64(), point.X, point.Y); }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct Point { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    public static string Canonical(JsonNode? node) => node switch { JsonObject obj => "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}", JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]", _ => node?.ToJsonString() ?? "null" };
    public async ValueTask DisposeAsync() { lifetime.Cancel(); foreach (var id in live.Keys) await DisconnectAsync(id); lifetime.Dispose(); }
}
