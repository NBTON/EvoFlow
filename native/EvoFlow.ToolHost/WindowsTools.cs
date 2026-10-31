using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using System.Text.Json.Nodes;

namespace EvoFlow.ToolHost;

public static class WindowsTools
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    public static ToolResult Execute(JsonObject a, CancellationToken ct)
    {
        using var automation = new UIA3Automation();
        var desktop = automation.GetDesktop(); var op = LocalTools.Text(a, "operation");
        var windows = desktop.FindAllChildren();
        if (op == "windows")
        {
            var rows = new JsonArray();
            foreach (var w in windows.Take(100))
            { ct.ThrowIfCancellationRequested(); try { rows.Add(new JsonObject { ["title"] = w.Name, ["windowHandle"] = w.Properties.NativeWindowHandle.Value.ToInt64().ToString(), ["pid"] = w.Properties.ProcessId.Value }); } catch (Exception) { } }
            return Wire.Success("Available desktop windows inspected.", new() { ["windows"] = rows });
        }
        var handle = new IntPtr(long.Parse(LocalTools.Text(a, "windowHandle")));
        var window = automation.FromHandle(handle);
        if (window is null || !window.IsAvailable) return Wire.Failure("inaccessible", "Window is no longer available or accessible.");
        // UI automation cannot be used as an alternate terminal, credential reader or approval route.
        using var owner = System.Diagnostics.Process.GetProcessById(window.Properties.ProcessId.Value);
        if (new[] { "EvoFlow", "EvoFlow.ToolHost", "cmd", "powershell", "pwsh", "WindowsTerminal", "conhost", "python", "Code", "regedit", "CredentialUIBroker", "consent", "Taskmgr", "msedge", "chrome", "firefox" }.Contains(owner.ProcessName, StringComparer.OrdinalIgnoreCase))
            return Wire.Failure("protected_application", "Credential prompts, command/script hosts, developer consoles and EvoFlow approval controls are protected. Use the browser tool for web interaction.");
        if (op == "activate")
        {
            window.SetForeground(); bool focused = GetForegroundWindow() == handle;
            return new(focused ? "succeeded" : "failed", focused, focused ? "Window is foreground." : "Windows did not grant foreground activation.", new() { ["focused"] = focused });
        }
        if (op == "inspect")
        {
            var rows = new JsonArray();
            void Walk(AutomationElement element, int depth, string controlPath)
            {
                ct.ThrowIfCancellationRequested(); if (rows.Count >= 150 || depth > 8) return;
                if (element.Properties.IsPassword.ValueOrDefault) { rows.Add(new JsonObject { ["name"] = "[protected control]" }); return; }
                rows.Add(new JsonObject { ["name"] = element.Properties.Name.ValueOrDefault, ["automationId"] = element.Properties.AutomationId.ValueOrDefault, ["controlPath"] = controlPath, ["controlType"] = element.Properties.ControlType.ValueOrDefault.ToString(), ["enabled"] = element.Properties.IsEnabled.ValueOrDefault,
                    ["invoke"] = element.Patterns.Invoke.IsSupported, ["value"] = element.Patterns.Value.IsSupported, ["toggle"] = element.Patterns.Toggle.IsSupported, ["select"] = element.Patterns.SelectionItem.IsSupported });
                var children = element.FindAllChildren();
                for (int i = 0; i < Math.Min(children.Length, 150); i++) Walk(children[i], depth + 1, string.IsNullOrEmpty(controlPath) ? i.ToString() : controlPath + "." + i);
            }
            Walk(window, 0, ""); return Wire.Success("Controls inspected (depth 8, up to 150).", new() { ["controls"] = rows });
        }
        AutomationElement target;
        if (a["controlPath"] is not null)
        {
            target = window;
            foreach (var index in LocalTools.Text(a, "controlPath").Split('.').Select(int.Parse))
            { var children = target.FindAllChildren(); if (index >= children.Length) return Wire.Failure("stale_control", "Control tree changed; inspect again."); target = children[index]; }
        }
        else
        {
            var matches = window.FindAllDescendants(cf => cf.ByAutomationId(LocalTools.Text(a, "automationId")));
            if (matches.Length != 1) return Wire.Failure("ambiguous_control", "Control must have one unique AutomationId; inspect again.");
            target = matches[0];
        }
        if (!target.IsEnabled || target.Properties.IsPassword.ValueOrDefault) return Wire.Failure("inaccessible", "Control is disabled or protected.");
        if (op == "invoke" && target.Patterns.Invoke.IsSupported) { target.Patterns.Invoke.Pattern.Invoke(); return new("succeeded", false, "Invoke dispatched; inspect the resulting application state to verify its effect.", new()); }
        if (op == "set_value" && target.Patterns.Value.IsSupported)
        { var value = LocalTools.Text(a, "value"); target.Patterns.Value.Pattern.SetValue(value); return new("succeeded", target.Patterns.Value.Pattern.Value.Value == value, "Value interaction completed; value read back.", new()); }
        if (op == "toggle" && target.Patterns.Toggle.IsSupported)
        { var before = target.Patterns.Toggle.Pattern.ToggleState.Value; target.Patterns.Toggle.Pattern.Toggle(); return new("succeeded", target.Patterns.Toggle.Pattern.ToggleState.Value != before, "Toggle state read back.", new()); }
        if (op == "select" && target.Patterns.SelectionItem.IsSupported)
        { target.Patterns.SelectionItem.Pattern.Select(); return new("succeeded", target.Patterns.SelectionItem.Pattern.IsSelected.Value, "Selection state read back.", new()); }
        return Wire.Failure("unsupported_control", "This control does not expose the requested UI Automation pattern. No interaction performed.");
    }
}
