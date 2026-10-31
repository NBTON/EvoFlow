using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using EvoFlow.Connectors;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Client;

if (args.Length > 0 && args[0] == "--native-qa")
{
    await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "EvoFlow-native-qa-arguments.json"), JsonSerializer.Serialize(args));
    if (args.Length != 4) throw new ArgumentException("Native QA expects case, PID and evidence directory.");
    await NativeQa.RunAsync(args[1], int.Parse(args[2]), args[3]); return;
}

if (args.Length > 0 && args[0] == "--witness")
{
    var thread = new Thread(() => { using var form = new System.Windows.Forms.Form { Text = "EvoFlow background witness", Width = 420, Height = 220, Left = 1200, Top = 600, StartPosition = System.Windows.Forms.FormStartPosition.Manual }; form.Controls.Add(new System.Windows.Forms.Label { Text = "This test window stays foreground while Cua operates Calculator.", Dock = System.Windows.Forms.DockStyle.Fill }); System.Windows.Forms.Application.Run(form); });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return;
}

if (args.Length > 0 && args[0] == "--browser-diagnostic")
{
    string home = args[1]; var config = new ConnectionStore(home).Load().Single(c => c.Id == "cua_browser");
    var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
    environment["CUA_DRIVER_PERMISSION_MODE"] = "standard"; environment["CUA_DRIVER_CAPABILITY_MANIFEST_FILE"] = config.Manifest; environment["CUA_DRIVER_CAPABILITY_MANIFEST_APPROVED"] = "1";
    await using var sdk = await McpClient.CreateAsync(new StdioClientTransport(new() { Command = config.Command, Arguments = ["mcp"], InheritEnvironmentVariables = false, EnvironmentVariables = environment }));
    var values = new Dictionary<string, object?> { ["allow_launch"] = true, ["profile"] = new { mode = "isolated_new" } };
    if (args.Length > 2) values["pid"] = int.Parse(args[2]);
    var result = await sdk.CallToolAsync("browser_prepare", values); Console.WriteLine(JsonSerializer.Serialize(result)); return;
}

if (args.Length > 0 && args[0].StartsWith("--fixture-"))
{
    string marker = args.ElementAtOrDefault(2) ?? "";
    IMcpServerBuilder Configure(IMcpServerBuilder builder) => builder
        .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [
            new Tool { Name = "echo", Description = "Deterministic data echo", InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new Dictionary<string, object> { [File.Exists(marker) ? "changed" : "value"] = new { type = "string", maxLength = 300 } }, additionalProperties = false }) },
            new Tool { Name = "failure", InputSchema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false }) },
            new Tool { Name = "slow", InputSchema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false }) },
            new Tool { Name = "image", InputSchema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false }) },
            new Tool { Name = "malformed", InputSchema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false }) },
            new Tool { Name = "invalid_data", InputSchema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false }), OutputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { count = new { type = "integer" } }, required = new[] { "count" } }) },
            new Tool { Name = "run_shell", InputSchema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false }) }
        ] }))
        .WithCallToolHandler(async (request, ct) =>
        {
            string name = request.Params!.Name;
            if (name == "invalid_data") return new CallToolResult { Content = [new TextContentBlock { Text = "Malformed structured fixture" }], StructuredContent = JsonSerializer.SerializeToElement(new { count = "invalid" }) };
            if (name == "slow") await Task.Delay(TimeSpan.FromMinutes(2), ct);
            if (name is "image" or "malformed") return new CallToolResult { Content = [new ImageContentBlock { MimeType = "image/png", Data = System.Text.Encoding.UTF8.GetBytes(name == "malformed" ? "broken" : "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=") }] };
            return new CallToolResult { IsError = name == "failure", Content = [new TextContentBlock { Text = name == "failure" ? "retained fixture refusal" : (request.Params.Arguments?.TryGetValue("value", out var value) == true ? value.ToString() : "ok") }],
                StructuredContent = JsonSerializer.SerializeToElement(new { leaked = Environment.GetEnvironmentVariable("EVOFLOW_WORKER_TOKEN") is not null || Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") is not null, status = name == "failure" ? "refused" : "ok" }) };
        });
    if (args[0] == "--fixture-http")
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls(args[1]);
        Configure(builder.Services.AddMcpServer().WithHttpTransport());
        var app = builder.Build();
        app.Use(async (context, next) => { if (context.Request.Headers.Authorization != "Bearer fixture-secret") { context.Response.StatusCode = 401; return; } await next(); });
        app.MapMcp("/mcp"); await app.RunAsync();
    }
    else
    {
        var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
        Configure(builder.Services.AddMcpServer().WithStdioServerTransport()); await builder.Build().RunAsync();
    }
    return;
}

if (args.Length > 0 && args[0] == "--cua-probe")
{
    string home = args[1]; await using var broker = new McpBroker(home);
    broker.Approve = (_, _) => Task.FromResult(true);
    await broker.StartEnabledAsync();
    Console.WriteLine(JsonSerializer.Serialize(broker.States));
    var config = broker.Configurations.Single(c => c.Id == "cua_desktop");
    var export = broker.ExportTools().Single(t => t!["serverId"]!.ToString() == config.Id && t["remoteName"]!.ToString() == "launch_app")!;
    var call = Call(export, new JsonObject { ["aumid"] = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" });
    var result = await broker.ExecuteAsync(call, CancellationToken.None); Console.WriteLine(result.ToJsonString());
    var stateTool = broker.ExportTools().Single(t => t!["serverId"]!.ToString() == config.Id && t["remoteName"]!.ToString() == "get_window_state")!;
    var state = await broker.ExecuteAsync(Call(stateTool, new() { ["pid"] = result["data"]!["structuredContent"]!["pid"]!.DeepClone(), ["window_id"] = result["data"]!["structuredContent"]!["windows"]![0]!["window_id"]!.DeepClone(), ["tree_format"] = "elements" }), CancellationToken.None);
    state["data"]?.AsObject().Remove("visionImages"); Console.WriteLine(state.ToJsonString());
    return;
}

string root = Path.Combine(Path.GetTempPath(), "EvoFlow-mcp-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks.Add(message); Console.WriteLine("PASS " + message); }
void Denied(Func<string> action, string message) { try { action(); throw new Exception("Policy unexpectedly admitted " + message); } catch (ArgumentException) { Check(true, message); } }
var desktopPolicy = new CuaPolicy(new ConnectionConfig { Id = "cua_desktop", Name = "test", Kind = "cua_desktop", TargetExecutable = "C:/reviewed/CalculatorApp.exe" });
Denied(() => desktopPolicy.Validate("launch_app", new() { ["path"] = "C:/Windows/System32/cmd.exe" }), "Cua cannot launch shells or arbitrary executable arguments");
Denied(() => desktopPolicy.Validate("click", new() { ["target"] = new JsonObject { ["kind"] = "desktop", ["display_id"] = "primary" }, ["x"] = 10, ["y"] = 10 }), "desktop and pixel authority cannot bypass exact-window policy");
Denied(() => desktopPolicy.Validate("get_window_state", new() { ["pid"] = Environment.ProcessId, ["window_id"] = 0L }), "protected and stale PID/window targets are refused");
var browserPolicy = new CuaPolicy(new ConnectionConfig { Id = "cua_browser", Name = "test", Kind = "cua_browser" });
Denied(() => browserPolicy.Validate("browser_prepare", new() { ["allow_launch"] = true, ["profile"] = new JsonObject { ["mode"] = "existing_profile" } }), "existing personal browser profiles cannot be attached");
Denied(() => browserPolicy.Validate("browser_navigate", new() { ["target_id"] = "forged", ["tab_id"] = "forged", ["url"] = "http://127.0.0.1" }), "forged browser capabilities and private URLs are denied");
Check(!CuaPolicy.ContractAllowed(new() { Id = "x", Name = "x" }, "query_sql", new()) && !CuaPolicy.ContractAllowed(new() { Id = "x", Name = "x" }, "read", new() { ["properties"] = new JsonObject { ["password"] = new JsonObject() } }), "generic SQL and credential routes stay disabled");
await using var client = new McpBroker(root);
string exe = Environment.ProcessPath!, markerFile = Path.Combine(root, "schema.changed");
ConnectionConfig Config(string id) => new() { Id = id, Name = "Fixture " + id, Command = exe, Arguments = ["--fixture-stdio", "unused", markerFile], Enabled = true, ReviewedTools = ["echo", "failure", "slow", "image", "malformed", "invalid_data", "run_shell"] };
Environment.SetEnvironmentVariable("EVOFLOW_WORKER_TOKEN", "must-not-leak"); Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", "must-not-leak");
await client.SaveAsync(Config("alpha")); await client.SaveAsync(Config("beta")); await client.StartEnabledAsync();
Check(client.States.All(s => s.Status == "ready"), "stdio initialization and tool discovery");
JsonNode Tool(string id, string name) => client.ExportTools().Single(t => t!["serverId"]!.ToString() == id && t["remoteName"]!.ToString() == name)!;
Check(Tool("alpha", "echo")["name"]!.ToString() != Tool("beta", "echo")["name"]!.ToString(), "namespace collisions remain distinct");
Check(!client.ExportTools().Any(t => t!["remoteName"]!.ToString() == "run_shell"), "unsafe generic execution contract disabled despite configuration");
int approvals = 0; client.Approve = (_, _) => { approvals++; return Task.FromResult(true); };
var echo = await client.ExecuteAsync(Call(Tool("alpha", "echo"), new() { ["value"] = "hello" }), CancellationToken.None);
Check(echo["status"]!.ToString() == "succeeded" && echo["data"]!["text"]!.ToString() == "hello" && echo["verified"]!.GetValue<bool>() == false, "stdio call preserves content without claiming effect verification");
Check(echo["data"]!["structuredContent"]!["leaked"]!.GetValue<bool>() == false, "child environment excludes provider keys and worker tokens");
var invalid = await client.ExecuteAsync(Call(Tool("alpha", "echo"), new() { ["value"] = 12 }), CancellationToken.None);
Check(invalid["errorCode"]!.ToString() == "invalid_arguments" && approvals == 1, "schema validation rejects malformed input before approval");
client.Approve = (_, _) => Task.FromResult(false);
var denied = await client.ExecuteAsync(Call(Tool("alpha", "echo"), new() { ["value"] = "denied" }), CancellationToken.None);
Check(denied["status"]!.ToString() == "denied", "native approval denial prevents dispatch");
client.Approve = (_, _) => Task.FromResult(true);
var failure = await client.ExecuteAsync(Call(Tool("alpha", "failure"), new()), CancellationToken.None);
Check(failure["status"]!.ToString() == "failed" && failure["data"]!["text"]!.ToString().Contains("refusal"), "MCP isError content retained");
var image = await client.ExecuteAsync(Call(Tool("alpha", "image"), new()), CancellationToken.None);
Check(File.Exists(image["data"]!["images"]![0]!["path"]!.ToString()) && !image["data"]!["images"]![0]!["modelInspected"]!.GetValue<bool>(), "multimodal image preserved locally with honest inspection state");
var malformedData = await client.ExecuteAsync(Call(Tool("alpha", "invalid_data"), new()), CancellationToken.None);
Check(malformedData["status"]!.ToString() == "failed", "declared output schema rejects malformed structured data");
await client.ConnectAsync("alpha");
var malformed = await client.ExecuteAsync(Call(Tool("alpha", "malformed"), new()), CancellationToken.None);
Check(malformed["status"]!.ToString() == "failed" && client.States.Single(s => s.Id == "alpha").OwnedPid is null, "malformed image closes failed owned runtime");
await client.ConnectAsync("alpha");
var old = Call(Tool("alpha", "echo"), new() { ["value"] = "old" }); await File.WriteAllTextAsync(markerFile, "changed");
var stale = await client.ExecuteAsync(old, CancellationToken.None);
Check(stale["errorCode"]!.ToString() == "schema_changed", "remote schema changes invalidate old contracts");
File.Delete(markerFile); await client.ConnectAsync("alpha");
client.Approve = async (_, _) => { await File.WriteAllTextAsync(markerFile, "changed during review"); return true; };
var race = await client.ExecuteAsync(Call(Tool("alpha", "echo"), new() { ["value"] = "race" }), CancellationToken.None);
Check(race["errorCode"]!.ToString() == "approval_invalidated", "approval bound to exact schema and arguments across review");
File.Delete(markerFile); await client.ConnectAsync("alpha"); client.Approve = (_, _) => Task.FromResult(true);
int owned = client.States.Single(s => s.Id == "alpha").OwnedPid!.Value;
using (var cancel = new CancellationTokenSource(350))
{
    var canceled = await client.ExecuteAsync(Call(Tool("alpha", "slow"), new()), cancel.Token);
    Check(canceled["status"]!.ToString() == "canceled", "cancellation returns structured failure without replay");
}
Check(!Alive(owned), "cancellation kills owned stdio process");
await using (var bounded = new McpBroker(Path.Combine(root, "timeout"), TimeSpan.FromMilliseconds(200)))
{
    await bounded.SaveAsync(Config("timeout")); bounded.Approve = (_, _) => Task.FromResult(true); await bounded.ConnectAsync("timeout");
    var slow = bounded.ExportTools().Single(t => t!["remoteName"]!.ToString() == "slow")!;
    var timedOut = await bounded.ExecuteAsync(Call(slow, new()), CancellationToken.None);
    Check(timedOut["status"]!.ToString() == "timed_out" && bounded.States.Single().OwnedPid is null, "execution deadline terminates uncertain mutation and owned process");
}
await client.ConnectAsync("alpha");
int disconnectedPid = client.States.Single(s => s.Id == "alpha").OwnedPid!.Value; using (var p = Process.GetProcessById(disconnectedPid)) p.Kill(true);
var disconnected = await client.ExecuteAsync(Call(Tool("alpha", "echo"), new() { ["value"] = "no replay" }), CancellationToken.None);
Check(disconnected["status"]!.ToString() == "failed", "runtime disconnect surfaces failure");
await client.ConnectAsync("alpha"); Check(client.States.Single(s => s.Id == "alpha").Status == "ready", "explicit restart restores discovery");
int port; var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
using var httpProcess = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--fixture-http", $"http://127.0.0.1:{port}" }, RedirectStandardOutput = true, RedirectStandardError = true })!;
try
{
    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    for (;;) { try { using var http = new HttpClient(); await http.GetAsync($"http://127.0.0.1:{port}/", wait.Token); break; } catch (HttpRequestException) { await Task.Delay(100, wait.Token); } }
    await client.SaveAsync(new ConnectionConfig { Id = "remote", Name = "HTTP fixture", Transport = "http", Endpoint = $"http://127.0.0.1:{port}/mcp", Authentication = "bearer", ReviewedTools = ["echo"], Enabled = true }, "wrong");
    try { await client.ConnectAsync("remote"); } catch (InvalidOperationException) { }
    Check(client.States.Single(s => s.Id == "remote").Status == "failed", "HTTP authentication failure visible");
    client.Store.WriteSecret("remote", "fixture-secret"); await client.ConnectAsync("remote");
    var remote = await client.ExecuteAsync(Call(Tool("remote", "echo"), new() { ["value"] = "streamable" }), CancellationToken.None);
    Check(remote["data"]!["text"]!.ToString() == "streamable", "Streamable HTTP negotiation discovery bearer auth and call");
    var redacted = await client.ExecuteAsync(Call(Tool("remote", "echo"), new() { ["value"] = "fixture-secret" }), CancellationToken.None);
    Check(redacted["data"]!["text"]!.ToString() == "[redacted]", "assigned server credentials are redacted from returned tool content");
    Check(!File.ReadAllText(Path.Combine(root, "connections", "servers.json")).Contains("fixture-secret") && !System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "connections", "remote.dpapi"))).Contains("fixture-secret"), "credentials DPAPI protected and absent from config");
}
finally { if (!httpProcess.HasExited) httpProcess.Kill(true); }
int[] ownedBeforeShutdown = client.States.Where(s => s.OwnedPid.HasValue).Select(s => s.OwnedPid!.Value).ToArray();
foreach (var config in client.Configurations) await client.DisconnectAsync(config.Id);
Check(ownedBeforeShutdown.All(p => !Alive(p)), "clean disconnect has no owned orphan processes");
await using (var restarted = new McpBroker(root)) Check(restarted.Configurations.Count == 3 && restarted.Store.ReadSecret("remote") == "fixture-secret", "app restart restores configuration and protected credentials");
await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { checks, count = checks.Count }));
Console.WriteLine("Evidence: " + root);

static JsonObject Call(JsonNode tool, JsonObject arguments) => new() { ["callId"] = Guid.NewGuid().ToString("N"), ["runId"] = "fixture-run", ["tool"] = tool["name"]!.DeepClone(), ["arguments"] = arguments,
    ["binding"] = new JsonObject { ["schemaHash"] = tool["schemaHash"]!.DeepClone(), ["serverId"] = tool["serverId"]!.DeepClone(), ["generation"] = tool["generation"]!.DeepClone() } };
static bool Alive(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }


