using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

internal static class NativeQa
{
    public static async Task RunAsync(string mode, int appPid, string directory)
    {
        Directory.CreateDirectory(directory);
        var result = new JsonObject { ["case"] = mode, ["appPid"] = appPid, ["status"] = "running", ["started"] = DateTimeOffset.UtcNow.ToString("O") };
        string output = Path.Combine(directory, mode + "-native-result.json");
        await File.WriteAllTextAsync(output, result.ToJsonString());
        System.Windows.Forms.Form? witness = null; Thread? witnessThread = null;
        try
        {
            if (mode == "regression")
            {
                string executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../EvoFlow.ToolHost.Tests/bin/Debug/net10.0-windows/win-x64/EvoFlow.ToolHost.Tests.exe"));
                var checks = new JsonArray(); result["checks"] = checks;
                foreach (string test in new[] { "--ui-smoke", "--browser-smoke" })
                {
                    using var p = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { test } })!;
                    var text = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
                    await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(70)); string stdout = await text, stderr = await error;
                    checks.Add(new JsonObject { ["test"] = test, ["exitCode"] = p.ExitCode, ["summary"] = stdout, ["error"] = stderr });
                    if (p.ExitCode != 0 || !stdout.Contains("PASS:")) throw new IOException("Existing native tool smoke failed: " + test);
                }
                result["status"] = "PASS"; return;
            }
            async Task<string> Winapp(params string[] arguments)
            {
                var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winapp.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                info.Environment["WINAPP_UI_WORKFLOW_ID"] = "evoflow-native-qa";
                foreach (string argument in arguments) info.ArgumentList.Add(argument);
                using var p = Process.Start(info)!;
                var text = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                string data = await text;
                if (p.ExitCode != 0) throw new IOException("WinApp failed: " + string.Join(' ', arguments.Take(3)) + " " + await error);
                await error; return data;
            }
            var windows = JsonNode.Parse(await Winapp("ui", "list-windows", "-a", appPid.ToString(), "--json"))!.AsArray();
            var window = windows.Single(w => w!["title"]?.ToString() == "EvoFlow")!;
            string hwnd = window["hwnd"]!.ToString(); result["windowId"] = hwnd;
            async Task<string> UI(params string[] values) => await Winapp(["ui", .. values, "-w", hwnd]);
            string cua = Path.Combine(Path.GetDirectoryName(Process.GetProcessById(appPid).MainModule!.FileName)!, "runtime", "cua", "cua-driver.exe");
            string cuaSession = "evoflow-witness-" + Environment.ProcessId;
            async Task<JsonNode> Cua(string tool, JsonObject arguments)
            {
                arguments["session"] = cuaSession;
                var info = new ProcessStartInfo(cua) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string a in new[] { "call", tool, arguments.ToJsonString() }) info.ArgumentList.Add(a);
                using var p = Process.Start(info)!; var text = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); string body = await text; await error;
                if (p.ExitCode != 0) throw new IOException("Cua approval controller failed.");
                var value = JsonNode.Parse(body)!; if (value["isError"]?.GetValue<bool>() == true || value["status"]?.ToString() == "refused") throw new IOException("Cua background approval route refused."); return value;
            }
            static IEnumerable<JsonNode> Flatten(JsonNode? node)
            {
                if (node is JsonArray a) foreach (var child in a) { if (child is not null) foreach (var item in Flatten(child)) yield return item; }
                else if (node is JsonObject obj) { if (obj["automationId"] is not null || obj["type"] is not null) yield return obj; foreach (var field in new[] { "windows", "elements", "children" }) if (obj[field] is { } content) foreach (var item in Flatten(content)) yield return item; }
            }
            async Task<JsonNode[]> Tree() => Flatten(JsonNode.Parse(await UI("inspect", "-d", "18", "--json"))).ToArray();
            if (mode == "shutdown")
            {
                var processTree = Process.GetProcesses().Select(p => (p.Id, Parent: Parent(p.Id))).ToArray();
                var owned = new HashSet<int> { appPid }; bool expanded;
                do { expanded = false; foreach (var p in processTree) if (owned.Contains(p.Parent)) expanded |= owned.Add(p.Id); } while (expanded);
                var children = owned.Where(p => p != appPid).ToArray();
                result["ownedBefore"] = new JsonArray(children.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
                if (!PostMessage(new IntPtr(long.Parse(hwnd)), 0x10, IntPtr.Zero, IntPtr.Zero)) throw new IOException("WM_CLOSE refused.");
                for (int i = 0; i < 100 && (Alive(appPid) || children.Any(Alive)); i++) await Task.Delay(100);
                var remaining = children.Where(Alive).ToArray();
                result["appAlive"] = Alive(appPid); result["remainingOwned"] = new JsonArray(remaining.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
                if (Alive(appPid) || remaining.Length != 0) throw new IOException("Shutdown left owned processes alive.");
                result["status"] = "PASS"; return;
            }
            var initial = await Tree();
            if (!initial.Any(n => n["automationId"]?.ToString() == "TaskText")) { await UI("invoke", "Pill"); await Task.Delay(400); }
            if (mode == "history")
            {
                await UI("invoke", "ChatHistoryButton"); await Task.Delay(400);
                var menu = await Tree();
                var saved = menu.First(n => n["automationId"]?.ToString().StartsWith("Chat_") == true && n["name"]?.ToString().StartsWith("Open Calculator, compute 6") == true);
                await UI("invoke", saved["automationId"]!.ToString());
                string restoredText = "";
                for (int i = 0; i < 30; i++)
                {
                    restoredText = string.Join('\n', (await Tree()).Where(n => n["type"]?.ToString() == "Text").Select(n => n["name"]?.ToString()));
                    if (restoredText.Contains("42") && restoredText.Contains("stable")) break;
                    await Task.Delay(200);
                }
                if (!restoredText.Contains("42") || !restoredText.Contains("stable") || restoredText.Contains("browser_endpoint_owner_mismatch")) throw new IOException("Durable Calculator conversation was not restored in isolation.");
                result["loadedVerifiedCalculator"] = true; result["sessionControl"] = saved["automationId"]!.ToString();
                await UI("screenshot", "--output", Path.Combine(directory, "history-native-evoflow.png"), "--json");
                result["status"] = "PASS"; return;
            }
            if (mode == "reconnect")
            {
                await UI("invoke", "SettingsButton"); await Task.Delay(500);
                var settings = JsonNode.Parse(await Winapp("ui", "list-windows", "-a", appPid.ToString(), "--json"))!.AsArray().Single(w => w?["title"]?.ToString() == "EvoFlow Settings")!;
                string settingsHandle = settings["hwnd"]!.ToString();
                await Winapp("ui", "scroll-into-view", "McpConnections", "-w", settingsHandle);
                await Winapp("ui", "invoke", "McpConnections", "-w", settingsHandle); await Task.Delay(200);
                var expanded = Flatten(JsonNode.Parse(await Winapp("ui", "inspect", "-d", "22", "--json", "-w", settingsHandle))).ToArray();
                var choice = expanded.First(n => n["name"]?.ToString() == "Cua · Calculator" && n["type"]?.ToString() is "ListItem" or "ComboBoxItem");
                await Winapp("ui", "invoke", choice["selector"]!.ToString(), "-w", settingsHandle);
                await Winapp("ui", "scroll-into-view", "McpTest", "-w", settingsHandle);
                await Winapp("ui", "invoke", "McpTest", "-w", settingsHandle);
                string status = "";
                for (int i = 0; i < 80; i++)
                {
                    var nodes = Flatten(JsonNode.Parse(await Winapp("ui", "inspect", "-d", "18", "--json", "-w", settingsHandle))).ToArray();
                    status = nodes.FirstOrDefault(n => n["automationId"]?.ToString() == "McpStatus")?["name"]?.ToString() ?? "";
                    if (status.StartsWith("Cua · Calculator: ready") && !status.Contains("owned PID none")) break;
                    await Task.Delay(250);
                }
                result["connectionStatus"] = status;
                if (!status.StartsWith("Cua · Calculator: ready") || status.Contains("owned PID none")) throw new IOException("Native Test did not restore the failed connector.");
                await Winapp("ui", "screenshot", "--output", Path.Combine(directory, "reconnect-native-settings.png"), "--json", "-w", settingsHandle);
                PostMessage(new IntPtr(long.Parse(settingsHandle)), 0x10, IntPtr.Zero, IntPtr.Zero); result["status"] = "PASS"; return;
            }
            if (mode == "memory")
            {
                await UI("invoke", "BrainButton");
                JsonNode? memory = null;
                for (int i = 0; i < 30 && memory is null; i++)
                {
                    memory = JsonNode.Parse(await Winapp("ui", "list-windows", "-a", appPid.ToString(), "--json"))!.AsArray().FirstOrDefault(w => w?["title"]?.ToString() == "EvoFlow · Memory");
                    if (memory is null) await Task.Delay(100);
                }
                if (memory is null) throw new IOException("Native memory window did not open.");
                string handle = memory["hwnd"]!.ToString(); JsonNode[] nodes = [];
                for (int i = 0; i < 30; i++)
                {
                    nodes = Flatten(JsonNode.Parse(await Winapp("ui", "inspect", "-d", "14", "--json", "-w", handle))).ToArray();
                    if (nodes.Any(n => n["automationId"]?.ToString().StartsWith("MemoryList_") == true)) break;
                    await Task.Delay(200);
                }
                int recordedNodes = nodes.Count(n => n["automationId"]?.ToString().StartsWith("MemoryList_") == true);
                if (recordedNodes == 0 || !nodes.Any(n => n["automationId"]?.ToString() == "MemoryTypeFilter")) throw new IOException("Recorded native memory graph was not loaded.");
                result["visibleRecordedNodes"] = recordedNodes; result["nativeFilterAvailable"] = true;
                PostMessage(new IntPtr(long.Parse(handle)), 0x10, IntPtr.Zero, IntPtr.Zero); result["status"] = "PASS"; return;
            }
            await UI("invoke", "NewChatButton");
            string nativeFile = "EvoFlow-native-MCP-check-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ".txt";
            string message = mode switch {
                "deny" => "Use Cua to open Calculator and inspect its current display. If native approval is declined, stop and report the denial.",
                "cancel" => "Use Cua to open Calculator and inspect its current display.",
                "stale" => "Use Cua to launch a new Calculator window. Observe it, click its Close Calculator button using a fresh element token, then request get_window_state for that same now-closed pid/window_id to verify that a stale target is refused. Do not relaunch or use another backend.",
                "browser" => "Use Cua to open an isolated browser, navigate to https://www.selenium.dev/selenium/web/web-form.html, fill the Text input with EvoFlow MCP verified, submit, and verify the page says Received! If preparation is refused, stop and report the refusal without retrying or another backend.",
                "native" => $"Use the native filesystem tool to create a new text file named {nativeFile} in the default EvoFlow workspace containing exactly EvoFlow native tool verified. Then read it back to verify its contents.",
                _ => "Open Calculator, compute 6 × 7, and verify that Calculator displays 42." };
            await UI("set-value", "TaskText", message); await UI("invoke", "RunButton");
            if (mode is "calculator" or "background")
            {
                var ready = new TaskCompletionSource<System.Windows.Forms.Form>();
                witnessThread = new Thread(() => { var f = new System.Windows.Forms.Form { Text = "EvoFlow background witness", Width = 420, Height = 200, Left = 1450, Top = 650, StartPosition = System.Windows.Forms.FormStartPosition.Manual }; f.Controls.Add(new System.Windows.Forms.Button { Text = "Foreground witness while Cua operates Calculator", Name = "WitnessFocusTarget", Dock = System.Windows.Forms.DockStyle.Fill }); f.Shown += (_, _) => ready.SetResult(f); System.Windows.Forms.Application.Run(f); });
                witnessThread.SetApartmentState(ApartmentState.STA); witnessThread.Start(); witness = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                // Explicit foreground setup for the synthetic witness only. Product
                // Calculator calls and subsequent approvals remain background actions.
                witness.Invoke(() => {
                    uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), ownThread = GetCurrentThreadId();
                    bool attached = foregroundThread != ownThread && AttachThreadInput(ownThread, foregroundThread, true);
                    try { witness.Activate(); SetForegroundWindow(witness.Handle); }
                    finally { if (attached) AttachThreadInput(ownThread, foregroundThread, false); }
                });
                await Winapp("ui", "focus", "WitnessFocusTarget", "-w", witness.Handle.ToInt64().ToString()); result["witnessWindow"] = witness.Handle.ToInt64();
            }
            var approvals = new JsonArray(); result["approvals"] = approvals;
            int attempt;
            for (attempt = 0; attempt < 400; attempt++)
            {
                var tree = await Tree();
                if (tree.Any(n => n["automationId"]?.ToString() == "ApproveToolButton" && n["isEnabled"]?.GetValue<bool>() == true))
                {
                    string description = tree.Single(n => n["automationId"]?.ToString() == "ToolApprovalDescription")["name"]!.ToString();
                    string prefix = mode == "browser" ? "MCP · Cua · isolated browser / " : "MCP · Cua · Calculator / ";
                    if (!description.StartsWith(prefix)) throw new IOException("Approval outside the authorized test scope.");
                    approvals.Add(description.Split('\n')[0]);
                    if (mode == "background")
                    {
                        var fresh = await Cua("get_window_state", new() { ["pid"] = appPid, ["window_id"] = long.Parse(hwnd), ["include_screenshot"] = false, ["tree_format"] = "elements", ["max_elements"] = 250, ["max_depth"] = 16 });
                        var control = fresh["elements"]!.AsArray().Single(n => n?["role"]?.ToString() == "Button" && n["label"]?.ToString() == "Approve action")!;
                        await Cua("click", new() { ["target"] = new JsonObject { ["kind"] = "window", ["pid"] = appPid, ["window_id"] = long.Parse(hwnd) }, ["element_token"] = control["element_token"]!.DeepClone(), ["delivery_mode"] = "background" });
                    }
                    else await UI("invoke", mode == "deny" ? "DeclineToolButton" : mode == "cancel" ? "CancelButton" : "ApproveToolButton");
                }
                if (attempt > 0 && tree.Any(n => n["automationId"]?.ToString() == "RunButton" && n["isEnabled"]?.GetValue<bool>() == true)) break;
                await File.WriteAllTextAsync(output, result.ToJsonString()); await Task.Delay(400);
            }
            if (attempt == 400) throw new IOException("Native acceptance deadline exceeded.");
            var final = await Tree(); string transcript = string.Join('\n', final.Where(n => n["type"]?.ToString() == "Text").Select(n => n["name"]?.ToString()));
            await File.WriteAllTextAsync(Path.Combine(directory, mode + "-native-transcript.txt"), transcript);
            await UI("screenshot", "--output", Path.Combine(directory, mode + "-native-evoflow.png"), "--json");
            string nativePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EvoFlow Files", nativeFile);
            bool valid = mode switch { "calculator" or "background" => transcript.Contains("42") && approvals.Any(p => p!.ToString().EndsWith("/ click")), "native" => File.Exists(nativePath) && File.ReadAllText(nativePath).Trim() == "EvoFlow native tool verified", "browser" => transcript.Contains("Received") && approvals.Any(p => p!.ToString().EndsWith("/ browser_click")), "deny" => transcript.Contains("denied", StringComparison.OrdinalIgnoreCase) || transcript.Contains("declined", StringComparison.OrdinalIgnoreCase), "cancel" => transcript.Contains("cancel", StringComparison.OrdinalIgnoreCase), "stale" => transcript.Contains("stale", StringComparison.OrdinalIgnoreCase) && approvals.Any(p => p!.ToString().EndsWith("/ click")), _ => false };
            if (mode == "native") { result["artifact"] = nativePath; result["independentReadback"] = valid; }
            if (!valid) throw new IOException("Requested acceptance postcondition was not established; inspect retained transcript.");
            result["status"] = "PASS";
        }
        catch (Exception ex) { result["status"] = "FAIL"; result["error"] = ex.Message; }
        finally
        {
            if (witness is not null) witness.BeginInvoke(() => witness.Close());
            witnessThread?.Join(TimeSpan.FromSeconds(3)); await File.WriteAllTextAsync(output, result.ToJsonString());
        }
    }
    static bool Alive(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }
    // Process enumeration is test metadata only; no process is terminated by this controller.
    static int Parent(int pid)
    {
        try { using var p = Process.GetProcessById(pid); var info = new BasicInfo(); if (NtQueryInformationProcess(p.Handle, 0, ref info, Marshal.SizeOf<BasicInfo>(), out _) == 0) return (int)info.Parent; } catch (Exception) { } return 0;
    }
    [StructLayout(LayoutKind.Sequential)] struct BasicInfo { public IntPtr Reserved1, Peb, Reserved2a, Reserved2b, Id, Parent; }
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref BasicInfo info, int length, out int returned);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
}
