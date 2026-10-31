using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EvoFlow.Connectors;

/// <summary>Reviewed host contracts are narrower than discovered schemas. Annotations never grant authorization.</summary>
internal sealed class CuaPolicy(ConnectionConfig config)
{
    readonly Dictionary<string, (int Pid, long Window, DateTimeOffset At)> tokens = [];
    readonly HashSet<int> browsers = [];
    readonly HashSet<string> tabs = [];
    readonly HashSet<(int Pid, long Window)> calculatorWindows = [];
    static readonly string[] DesktopTools = ["launch_app", "list_windows", "get_window_state", "click", "verify_state", "get_guide"];
    static readonly string[] BrowserTools = ["browser_prepare", "list_windows", "get_browser_state", "browser_navigate", "browser_click", "browser_type", "get_guide"];
    public static bool ContractAllowed(ConnectionConfig config, string name, JsonObject schema)
    {
        if (config.Kind == "cua_desktop") return DesktopTools.Contains(name);
        if (config.Kind == "cua_browser") return BrowserTools.Contains(name);
        // Generic data connectors may be configured by the user, but capability-bearing
        // routes require a new reviewed native adapter. A server annotation is not one.
        string contract = name + " " + string.Join(" ", (schema["properties"] as JsonObject ?? []).Select(p => p.Key));
        return !Regex.IsMatch(contract, @"shell|terminal|exec|command|script|code|sql|query|file|path|clipboard|browser|desktop|process|window|password|credential|token|secret|url|uri", RegexOptions.IgnoreCase);
    }
    public void Reset() { tokens.Clear(); browsers.Clear(); tabs.Clear(); calculatorWindows.Clear(); }
    public JsonObject AdvertisedSchema(string tool, JsonObject original)
    {
        var schema = original.DeepClone().AsObject();
        if (config.Kind == "generic") return schema;
        var properties = schema["properties"]!.AsObject(); properties.Remove("session");
        if (config.Kind == "cua_desktop")
        {
            if (tool == "launch_app") return JsonNode.Parse("""{"type":"object","properties":{"aumid":{"type":"string","const":"Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"}},"required":["aumid"],"additionalProperties":false}""")!.AsObject();
            string[]? allowed = tool switch { "get_window_state" => ["pid", "window_id", "include_screenshot", "include_accessibility_tree", "max_elements", "max_depth", "max_image_dimension", "tree_format", "query"], "click" => ["target", "element_token", "delivery_mode"], "list_windows" => ["pid", "on_screen_only"], _ => null };
            if (allowed is not null) foreach (var key in properties.Select(p => p.Key).Where(k => !allowed.Contains(k)).ToArray()) properties.Remove(key);
            if (tool == "list_windows") schema["required"] = new JsonArray("pid");
            if (tool == "click")
            {
                properties["target"] = JsonNode.Parse("""{"type":"object","properties":{"kind":{"const":"window","type":"string"},"pid":{"type":"integer"},"window_id":{"type":"integer"}},"required":["kind","pid","window_id"],"additionalProperties":false}""");
                schema["required"] = new JsonArray("target", "element_token");
            }
            if (tool == "get_window_state") { properties["max_elements"]!["maximum"] = 300; properties["max_depth"]!["maximum"] = 10; properties["max_image_dimension"]!["minimum"] = 1; properties["max_image_dimension"]!["maximum"] = 1600; }
        }
        if (tool == "browser_prepare") return JsonNode.Parse("""{"type":"object","properties":{"allow_launch":{"type":"boolean","const":true},"profile":{"type":"object","properties":{"mode":{"type":"string","const":"isolated_new"}},"required":["mode"],"additionalProperties":false}},"required":["allow_launch","profile"],"additionalProperties":false}""")!.AsObject();
        schema["additionalProperties"] = false;
        return schema;
    }
    public string Validate(string tool, JsonObject args)
    {
        if (args.Any(p => p.Key.StartsWith('_') || p.Key.Contains("approv", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Authorization is owned by native UI, never tool arguments.");
        if (config.Kind == "generic")
        {
            if (!ContractAllowed(config, tool, new JsonObject { ["properties"] = JsonNode.Parse(args.ToJsonString()) })) throw new ArgumentException("Generic executable, filesystem, browser, credential and code routes require a reviewed native capability adapter.");
            return "User-registered data connector; exact arguments require native approval.";
        }
        if (tool == "get_guide") return "Read-only bundled Cua guide; content is untrusted data.";
        if (args.ContainsKey("session")) throw new ArgumentException("EvoFlow owns the private transport lifecycle. Omit session.");
        string mode = args["delivery_mode"]?.ToString() ?? "background";
        if (mode is not ("background" or "foreground")) throw new ArgumentException("Choose background or explicitly approved foreground delivery.");
        if (config.Kind == "cua_browser") return Browser(tool, args);
        if (tool == "launch_app")
        {
            if (args.Count != 1 || args["aumid"]?.ToString() != "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")
                throw new ArgumentException("This reviewed desktop capability launches Windows Calculator by its exact AUMID only. Launch arguments, URLs and arbitrary executables are disabled.");
            return "Launch Windows Calculator (Cua background launch).";
        }
        if (tool == "list_windows" && args["pid"] is null)
            throw new ArgumentException("Select the exact PID returned by Calculator launch before listing windows.");
        var target = args["target"] as JsonObject ?? args;
        if (args.ContainsKey("scope") || target["kind"] is not null && target["kind"]!.ToString() != "window") throw new ArgumentException("Desktop and global-coordinate routes are disabled; select an exact window.");
        int pid = target["pid"]?.GetValue<int>() ?? throw new ArgumentException("Exact current PID required.");
        string executable = ProcessPath(pid);
        if (tool == "list_windows")
        {
            if (!executable.Equals(config.TargetExecutable, StringComparison.OrdinalIgnoreCase) && !calculatorWindows.Any(t => t.Pid == pid && CalculatorWindow(pid, t.Window)))
                throw new ArgumentException("Protected or stale PID. First launch Calculator to obtain its exact window.");
            return $"Calculator PID {pid}; native output filters other hosted applications.";
        }
        long window = target["window_id"]?.GetValue<long>() ?? throw new ArgumentException("Exact current window_id required.");
        if (!CalculatorWindow(pid, window))
            throw new ArgumentException("Stale target or mismatched PID/window. Rediscover and observe.");
        if (tool == "get_window_state")
        {
            string[] allowed = ["pid", "window_id", "include_screenshot", "include_accessibility_tree", "max_elements", "max_depth", "max_image_dimension", "tree_format", "query"];
            if (args.Any(p => !allowed.Contains(p.Key)) || (args["max_elements"]?.GetValue<int>() ?? 250) > 300
                || (args["max_depth"]?.GetValue<int>() ?? 8) > 10 || (args["max_image_dimension"]?.GetValue<int>() ?? 1280) is < 1 or > 1600)
                throw new ArgumentException("Observation exceeds reviewed bounds. Screenshot output paths and unrestricted captures are disabled; EvoFlow stores selected image evidence.");
        }
        if (tool == "click")
        {
            string[] allowed = ["target", "element_token", "delivery_mode"];
            string token = args["element_token"]?.ToString() ?? "";
            if (args.Any(p => !allowed.Contains(p.Key)) || !tokens.TryGetValue(token, out var observation)
                || observation.Pid != pid || observation.Window != window || DateTimeOffset.UtcNow - observation.At > TimeSpan.FromSeconds(90))
                throw new ArgumentException("A fresh snapshot-bound Calculator element_token is required. Pixel, modifier and global input routes are disabled.");
        }
        return $"Calculator PID {pid} / window {window} · {mode} delivery. Foreground requires this exact native approval.";
    }
    string Browser(string tool, JsonObject args)
    {
        if (tool == "browser_prepare")
        {
            if (args.Any(p => p.Key is not ("allow_launch" or "profile")) || args["allow_launch"]?.GetValue<bool>() != true
                || args["profile"] is not JsonObject profile || profile.Count != 1 || profile["mode"]?.ToString() != "isolated_new")
                throw new ArgumentException("Only a new isolated Cua browser profile is admitted; existing-profile attachment is disabled.");
            return "Launch an isolated Cua browser test window; personal profiles are excluded.";
        }
        if (tool == "list_windows" || tool == "get_browser_state" && args["target_id"] is null)
        {
            int pid = args["pid"]?.GetValue<int>() ?? throw new ArgumentException("Use the prepared browser PID.");
            if (!browsers.Contains(pid) || ProcessPath(pid).Length == 0) throw new ArgumentException("Browser target was not prepared by this owned Cua runtime.");
            if (tool == "get_browser_state")
            {
                long window = args["window_id"]?.GetValue<long>() ?? 0;
                if (!IsWindow(new IntPtr(window)) || GetWindowThreadProcessId(new IntPtr(window), out uint owner) == 0 || owner != pid) throw new ArgumentException("Stale browser PID/window.");
            }
            return $"Owned isolated browser PID {pid}; exact native binding.";
        }
        string binding = args["target_id"] + ":" + args["tab_id"];
        if (!tabs.Contains(binding)) throw new ArgumentException("Obtain a fresh exact browser target_id/tab_id binding from this connection.");
        if (tool == "browser_navigate")
        {
            if (!Uri.TryCreate(args["url"]?.ToString(), UriKind.Absolute, out var url) || url.GetLeftPart(UriPartial.Authority) != "https://www.selenium.dev")
                throw new ArgumentException("The reviewed browser capability permits https://www.selenium.dev only. Other origins and private/file URLs are disabled.");
        }
        if (tool == "browser_click" && (args["ref"] is null || args.Any(p => p.Key is not ("target_id" or "tab_id" or "ref" or "delivery_mode")))) throw new ArgumentException("Use a fresh semantic browser ref; raw coordinates and native browser chrome are disabled.");
        if (tool == "browser_type" && (args["text"]?.ToString().Length ?? 0) > 200) throw new ArgumentException("Browser test text is limited to 200 characters.");
        return "Cua isolated test browser · https://www.selenium.dev · " + (args["delivery_mode"]?.ToString() ?? "background") + " delivery";
    }
    public void Observe(string tool, JsonNode? data)
    {
        if (tool is "click" or "verify_state") tokens.Clear();
        if (data is null) return;
        if (tool == "launch_app")
        {
            int pid = Find(data, "pid").Select(v => int.TryParse(v, out int p) ? p : 0).FirstOrDefault(p => p > 0);
            foreach (string value in Find(data, "window_id")) if (long.TryParse(value, out long window) && CalculatorWindow(pid, window)) calculatorWindows.Add((pid, window));
        }
        if (tool == "get_window_state")
        {
            tokens.Clear();
            int pid = Find(data, "pid").Select(v => int.TryParse(v, out int p) ? p : 0).FirstOrDefault(p => p > 0);
            long window = Find(data, "window_id").Select(v => long.TryParse(v, out long w) ? w : 0).FirstOrDefault(w => w > 0);
            foreach (string token in Find(data, "element_token")) tokens[token] = (pid, window, DateTimeOffset.UtcNow);
            // Markdown is the default advertised Cua representation; the tokens are minted by Cua.
            string? snapshot = Find(data, "snapshot_id").FirstOrDefault();
            string? markdown = Find(data, "tree_markdown").FirstOrDefault();
            if (snapshot is not null && markdown is not null) foreach (Match m in Regex.Matches(markdown, @"\[(\d+)\]")) tokens[snapshot + ":" + m.Groups[1].Value] = (pid, window, DateTimeOffset.UtcNow);
        }
        if (tool == "browser_prepare") foreach (string value in Find(data, "prepared_pid")) if (int.TryParse(value, out int pid)) browsers.Add(pid);
        if (tool == "get_browser_state" && Find(data, "binding_quality").Contains("exact") && Find(data, "mutation_allowed").Contains("true"))
        {
            string? target = Find(data, "target_id").FirstOrDefault(), tab = Find(data, "tab_id").FirstOrDefault();
            if (target is not null && tab is not null) tabs.Add(target + ":" + tab);
        }
    }
    bool CalculatorWindow(int pid, long window)
    {
        if (!IsWindow(new IntPtr(window)) || GetWindowThreadProcessId(new IntPtr(window), out uint owner) == 0 || owner != pid) return false;
        string image = ProcessPath(pid);
        if (image.Equals(config.TargetExecutable, StringComparison.OrdinalIgnoreCase)) return true;
        if (!image.Equals(Path.Combine(Environment.SystemDirectory, "ApplicationFrameHost.exe"), StringComparison.OrdinalIgnoreCase)) return false;
        bool calculator = false;
        EnumChildWindows(new IntPtr(window), (child, _) => { GetWindowThreadProcessId(child, out uint childPid); if (ProcessPath((int)childPid).Equals(config.TargetExecutable, StringComparison.OrdinalIgnoreCase)) calculator = true; return true; }, IntPtr.Zero);
        return calculator;
    }
    public void FilterWindows(JsonNode? data)
    {
        if (config.Kind != "cua_desktop" || data is null) return;
        if (data is JsonObject obj)
        {
            if (obj["windows"] is JsonArray windows)
                for (int i = windows.Count - 1; i >= 0; i--) { var w = windows[i]; if (w?["pid"] is not null && w["window_id"] is not null && !CalculatorWindow(w["pid"]!.GetValue<int>(), w["window_id"]!.GetValue<long>())) windows.RemoveAt(i); }
            foreach (var child in obj.Select(p => p.Value).Where(v => v is JsonObject or JsonArray).ToArray()) FilterWindows(child);
        }
        else if (data is JsonArray array) foreach (var child in array) FilterWindows(child);
    }
    static IEnumerable<string> Find(JsonNode node, string key)
    {
        if (node is JsonObject obj) foreach (var p in obj) { if (p.Key == key && p.Value is not null) yield return p.Value.ToString(); if (p.Value is JsonObject or JsonArray) foreach (var value in Find(p.Value, key)) yield return value; }
        else if (node is JsonArray array) foreach (var child in array) if (child is not null) foreach (var value in Find(child, key)) yield return value;
    }
    static string ProcessPath(int pid)
    {
        IntPtr process = OpenProcess(0x1000, false, (uint)pid); if (process == IntPtr.Zero) return "";
        try { var path = new System.Text.StringBuilder(2048); int size = path.Capacity; return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : ""; }
        finally { CloseHandle(process); }
    }
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    delegate bool EnumWindow(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindow callback, IntPtr state);
}
