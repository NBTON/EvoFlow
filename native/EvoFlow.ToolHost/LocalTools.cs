using System.Text;
using System.Text.Json.Nodes;

namespace EvoFlow.ToolHost;

public sealed class LocalTools(PathPolicy paths, string python, string documentHandler) : ILocalTool
{
    public string Name => "local-tools-v1";
    public static readonly string[] Names = ["filesystem", "application_search", "application_launch", "terminal_run", "windows_ui", "document", "browser"];
    public static string Text(JsonObject args, string key, string? fallback = null) => args[key]?.GetValue<string>() ?? fallback ?? throw new ArgumentException($"Missing {key}.");
    public static string[] Strings(JsonObject args, string key) => args[key]?.AsArray().Select(x => x!.GetValue<string>()).ToArray() ?? [];
    static void Keys(JsonObject args, params string[] allowed)
    {
        if (args.Any(p => !allowed.Contains(p.Key)) || args.ToJsonString().Length > 100000)
            throw new ArgumentException("Unsupported arguments or input size.");
    }
    public ToolAction Validate(ToolRequest request)
    {
        if (!Names.Contains(request.Tool) || request.Version != 1 || request.CallId.Length > 100 || request.RunId.Length > 100)
            throw new ArgumentException("Unknown tool or contract version.");
        var a = request.Arguments; bool approval = false; ToolRisk risk = ToolRisk.Read; string summary;
        switch (request.Tool)
        {
            case "filesystem":
                Keys(a, "operation", "path", "destination", "content");
                var op = Text(a, "operation");
                if (!new[] { "list", "inspect", "read", "write", "mkdir", "move", "copy" }.Contains(op)) throw new ArgumentException("Unsupported filesystem operation.");
                bool write = op is "write" or "mkdir" or "move" or "copy";
                var path = paths.Normalize(Text(a, "path"), write);
                if (op is "write" or "move" or "copy") paths.WritableFile(path);
                if (op is "move" or "copy")
                {
                    var dest = paths.Normalize(Text(a, "destination"), true); paths.WritableFile(dest);
                    if (File.Exists(dest) || Directory.Exists(dest)) throw new ArgumentException("Transfer destination must not exist.");
                    if (!File.Exists(path)) throw new ArgumentException("Transfers support existing files only.");
                    approval |= !PathPolicy.Within(dest, paths.Workspace);
                }
                if (op == "write" && Text(a, "content").Length > 64000) throw new ArgumentException("Text exceeds write limit.");
                if (write) risk = ToolRisk.LocalWrite;
                if (op == "move" || op == "write" && File.Exists(path)) { approval = true; risk = ToolRisk.Destructive; }
                if (!PathPolicy.Within(path, paths.Workspace)) { approval = true; risk = ToolRisk.OutsideWorkspace; }
                summary = $"{op}: {path}" + (a["destination"] is null ? "" : $" → {paths.Normalize(Text(a, "destination"), true)}");
                break;
            case "application_search":
                Keys(a, "query"); summary = "Search installed applications: " + Text(a, "query", ""); break;
            case "application_launch":
                Keys(a, "application", "arguments");
                _ = ResolveApplication(Text(a, "application"));
                if (Strings(a, "arguments").Length != 0) throw new ArgumentException("Application launch v1 accepts no arguments; browser switches, URLs and script hosts are unavailable.");
                risk = ToolRisk.ApplicationLaunch; summary = $"Open {Text(a, "application")} · {ResolveApplication(Text(a, "application"))}";
                break;
            case "terminal_run":
                Keys(a, "executable", "arguments", "workingDirectory", "timeoutSeconds");
                var commandPath = TerminalCommand(a);
                paths.Normalize(commandPath);
                var directory = paths.Normalize(Text(a, "workingDirectory", paths.Workspace));
                if (!Directory.Exists(directory)) throw new ArgumentException("Working directory does not exist.");
                ExecutionPolicy.ValidatePathArguments(p => paths.Normalize(p), directory, Strings(a, "arguments"));
                approval = !ExecutionPolicy.Diagnostic(commandPath, Strings(a, "arguments")) || !PathPolicy.Within(directory, paths.Workspace);
                risk = approval ? ToolRisk.ProcessExecution : ToolRisk.Read;
                summary = $"Run {commandPath}\nArguments: {System.Text.Json.JsonSerializer.Serialize(Strings(a, "arguments"))}\nDirectory: {directory}";
                if (a["timeoutSeconds"] is not null && (a["timeoutSeconds"]!.GetValue<int>() < 1 || a["timeoutSeconds"]!.GetValue<int>() > 45)) throw new ArgumentException("Timeout must be 1–45 seconds.");
                break;
            case "windows_ui":
                Keys(a, "operation", "windowHandle", "automationId", "controlPath", "value");
                var uiOp = Text(a, "operation");
                if (!new[] { "windows", "inspect", "activate", "invoke", "set_value", "toggle", "select" }.Contains(uiOp)) throw new ArgumentException("Unsupported UI operation.");
                if (uiOp != "windows" && (!long.TryParse(Text(a, "windowHandle"), out var handle) || handle <= 0)) throw new ArgumentException("Select a window handle returned by windows.");
                if (uiOp is "invoke" or "set_value" or "toggle" or "select")
                {
                    if (a["controlPath"] is not null)
                    { if (!System.Text.RegularExpressions.Regex.IsMatch(Text(a, "controlPath"), @"^\d{1,3}(\.\d{1,3}){0,7}$")) throw new ArgumentException("Use a controlPath returned by inspect."); }
                    else if (Text(a, "automationId").Length is < 1 or > 300) throw new ArgumentException("Select a unique AutomationId or controlPath from inspect.");
                }
                if (uiOp == "set_value" && Text(a, "value").Length > 4000) throw new ArgumentException("UI value too long.");
                approval = uiOp != "windows"; risk = approval ? ToolRisk.DesktopInteraction : ToolRisk.Read;
                summary = $"Windows UI {uiOp} · window {a["windowHandle"]} · control {a["automationId"]}";
                break;
            case "browser":
                BrowserService.Validate(a, paths);
                approval = true; risk = ToolRisk.DesktopInteraction;
                summary = $"Browser {Text(a, "operation")}";
                foreach (var field in new[] { "url", "reference", "value", "key", "path" }) if (a[field] is not null) summary += "\n" + field + ": " + a[field];
                break;
            case "document":
                Keys(a, "operation", "format", "path", "title", "paragraphs", "slides", "sheets", "edits");
                var docOp = Text(a, "operation"); var format = Text(a, "format");
                if (!new[] { "create", "inspect", "edit" }.Contains(docOp) || !new[] { "docx", "pptx", "xlsx", "pdf" }.Contains(format)) throw new ArgumentException("Unsupported document operation or format.");
                var docPath = paths.Normalize(Text(a, "path"), docOp != "inspect"); paths.WritableFile(docPath);
                if (!docPath.EndsWith('.' + format, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Document extension must match its format.");
                if (docOp != "create" && !File.Exists(docPath)) throw new ArgumentException("Document does not exist.");
                approval = !PathPolicy.Within(docPath, paths.Workspace) || docOp != "inspect" && File.Exists(docPath);
                if (a["edits"] is JsonArray edits)
                    foreach (var edit in edits.OfType<JsonObject>())
                        if (edit["source"] is not null)
                        {
                            var source = paths.Normalize(Text(edit, "source"));
                            if (!source.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || !File.Exists(source)) throw new ArgumentException("PDF source is missing or invalid.");
                            approval |= !PathPolicy.Within(source, paths.Workspace);
                            summary = source;
                        }
                risk = docOp == "inspect" ? ToolRisk.Read : File.Exists(docPath) ? ToolRisk.Destructive : ToolRisk.LocalWrite;
                summary = $"{docOp} {format}: {docPath}";
                if (a["edits"] is JsonArray sourceEdits) foreach (var e in sourceEdits.OfType<JsonObject>()) if (e["source"] is not null) summary += "\nSource: " + paths.Normalize(Text(e, "source"));
                break;
            default: throw new ArgumentException("Unregistered tool.");
        }
        return new(request.CallId, request.Tool, 1, risk, approval, summary, Wire.Hash(a.ToJsonString()));
    }
    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Validate(request); // Revalidate immediately before the effect, after approval.
        var a = request.Arguments;
        switch (request.Tool)
        {
            case "filesystem": return await Files(a, cancellationToken);
            case "application_search": return Wire.Success("Installed application catalog searched.", InstalledApplications.Catalog(Text(a, "query", "")));
            case "application_launch": return await Launch(a, cancellationToken);
            case "terminal_run":
                var executable = TerminalCommand(a);
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(a["timeoutSeconds"]?.GetValue<int>() ?? 30));
                    var result = await ProcessRunner.RunAsync(ProcessRunner.StartInfo(executable, Strings(a, "arguments"), paths.Normalize(Text(a, "workingDirectory", paths.Workspace))), null, deadline.Token);
                    return new(result.ExitCode == 0 ? "succeeded" : "failed", result.ExitCode == 0, $"Process exited with code {result.ExitCode}.",
                        new() { ["exitCode"] = result.ExitCode, ["stdout"] = result.Stdout, ["stderr"] = result.Stderr, ["truncated"] = result.Truncated });
                }
            case "windows_ui": return WindowsTools.Execute(a, cancellationToken);
            case "document":
                if (!File.Exists(python) || !File.Exists(documentHandler)) return Wire.Failure("runtime_missing", "Bundled document runtime is missing. Rebuild delivery.");
                var input = (JsonObject)a.DeepClone(); input["path"] = paths.Normalize(Text(a, "path"), Text(a, "operation") != "inspect");
                if (input["edits"] is JsonArray inputEdits) foreach (var e in inputEdits.OfType<JsonObject>()) if (e["source"] is not null) e["source"] = paths.Normalize(Text(e, "source"));
                var output = await ProcessRunner.RunAsync(ProcessRunner.StartInfo(python, ["-I", documentHandler], paths.Workspace), input.ToJsonString(), cancellationToken);
                if (output.ExitCode != 0) return Wire.Failure("document_failed", "Document handler rejected or could not process the document.");
                var data = JsonNode.Parse(output.Stdout)!.AsObject();
                if (data["verified"]?.GetValue<bool>() != true) return Wire.Failure("unverified", "Document output could not be verified.");
                return Wire.Success($"Document {Text(a, "operation")} verified: {input["path"]}", data);
        }
        return Wire.Failure("unknown_tool", "No registered implementation.");
    }
    async Task<ToolResult> Files(JsonObject a, CancellationToken ct)
    {
        var op = Text(a, "operation"); var path = paths.Normalize(Text(a, "path"), op is "write" or "mkdir" or "move" or "copy");
        if (op == "list")
        {
            var items = new JsonArray();
            foreach (var entry in Directory.EnumerateFileSystemEntries(path).Take(201))
            {
                if (items.Count == 200) break;
                try { var safe = paths.Normalize(entry); items.Add(new JsonObject { ["name"] = Path.GetFileName(safe), ["directory"] = Directory.Exists(safe) }); }
                catch (ArgumentException) { /* Protected entries are omitted rather than disclosed. */ }
            }
            return Wire.Success("Directory listed (up to 200 entries).", new() { ["path"] = path, ["entries"] = items });
        }
        if (op == "inspect") return Wire.Success("Path inspected.", new() { ["path"] = path, ["exists"] = File.Exists(path) || Directory.Exists(path), ["directory"] = Directory.Exists(path), ["bytes"] = File.Exists(path) ? new FileInfo(path).Length : 0 });
        if (op == "read")
        {
            if (new FileInfo(path).Length > 64000) throw new ArgumentException("Read limit is 64000 bytes; use document inspection for Office/PDF files.");
            if (new[] { ".docx", ".pptx", ".xlsx", ".pdf", ".exe", ".dll" }.Contains(Path.GetExtension(path).ToLowerInvariant())) throw new ArgumentException("Use structured document inspection; binary reads are unavailable.");
            return Wire.Success("File read.", new() { ["path"] = path, ["content"] = await File.ReadAllTextAsync(path, ct) });
        }
        if (op == "mkdir") { Directory.CreateDirectory(path); return Wire.Success("Directory created and verified.", new() { ["path"] = path }); }
        if (op == "write")
        {
            var bytes = Encoding.UTF8.GetBytes(Text(a, "content"));
            // Open with sharing disabled; no partial overwrite is published.
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temporary, bytes, ct); paths.Normalize(path, true); ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return Wire.Success("File written and hash verified.", new() { ["path"] = path, ["sha256"] = Wire.Hash(await File.ReadAllTextAsync(path, ct)), ["bytes"] = bytes.Length });
        }
        var destination = paths.Normalize(Text(a, "destination"), true);
        if (new FileInfo(path).Length > 32_000_000) throw new ArgumentException("Transfer limit is 32 MB.");
        var before = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path, ct)));
        if (op == "copy") File.Copy(path, destination, false); else File.Move(path, destination, false);
        var after = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(destination, ct)));
        if (before != after || op == "move" && File.Exists(path)) return Wire.Failure("verification_failed", "File transfer verification failed.");
        return Wire.Success("File transfer verified.", new() { ["path"] = destination, ["sha256"] = after });
    }
    public static string ResolveApplication(string application) => ApplicationLauncher.Resolve(application);
    async Task<ToolResult> Launch(JsonObject args, CancellationToken cancellationToken)
        => System.Text.Json.JsonSerializer.Deserialize<ToolResult>((await ApplicationLauncher.LaunchAsync(Text(args, "application"), cancellationToken)).ToJsonString(), Wire.Json)!;
    public static string TerminalCommand(JsonObject a)
    {
        var path = ExecutionPolicy.Resolve(Text(a, "executable"));
        ExecutionPolicy.ValidateArguments(path, Strings(a, "arguments"));
        return path;
    }
}
