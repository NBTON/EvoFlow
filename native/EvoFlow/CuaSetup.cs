using EvoFlow.Connectors;
using System.Text.Json.Nodes;
using Windows.Management.Deployment;

namespace EvoFlow;

internal static class CuaSetup
{
    public static async Task ConfigureAsync(McpBroker broker, string home)
    {
        string runtime = Path.Combine(AppContext.BaseDirectory, "runtime", "cua", "cua-driver.exe");
        if (!File.Exists(runtime)) throw new IOException("Built-in Cua payload is missing. Run scripts/Sync-CuaRuntime.ps1 and rebuild the native delivery.");
        var integrity = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "config", "cua-runtime.json")))!;
        foreach (var file in integrity["files"]!.AsObject())
            if (McpBroker.FileHash(Path.Combine(Path.GetDirectoryName(runtime)!, file.Key)) != file.Value!.ToString()) throw new IOException("Cua payload integrity check failed. Rebuild from the pinned runtime.");
        string driverHash = integrity["files"]!["cua-driver.exe"]!.ToString();
        string directory = Path.Combine(home, "connections"); Directory.CreateDirectory(directory);
        var manager = new PackageManager();
        var package = manager.FindPackagesForUser("").FirstOrDefault(p => p.Id.Name == "Microsoft.WindowsCalculator" && p.Id.PublisherId == "8wekyb3d8bbwe");
        string calculator = package is null ? "" : Path.Combine(package.InstalledLocation.Path, "CalculatorApp.exe");
        if (!File.Exists(calculator)) throw new IOException("Install Windows Calculator to enable the reviewed Cua desktop capability.");
        string desktop = Path.Combine(directory, "cua-desktop.yaml"), browser = Path.Combine(directory, "cua-browser.yaml");
        string[] desktopTools = ["launch_app", "list_windows", "get_window_state", "click", "verify_state", "get_guide"];
        string[] browserTools = ["browser_prepare", "list_windows", "get_browser_state", "browser_navigate", "browser_click", "browser_type", "get_guide"];
        await File.WriteAllTextAsync(desktop, "version: 3\nallow:\n  tools:\n" + string.Concat(desktopTools.Select(t => "    - " + t + "\n")) +
            "resources:\n  apps:\n    - executable: '" + calculator.Replace("'", "''") + "'\n      launch: true\n      windows: all\n    - executable: '" + Path.Combine(Environment.SystemDirectory, "ApplicationFrameHost.exe") + "'\n      windows: all\n  desktop:\n    display: false\n");
        await File.WriteAllTextAsync(browser, "version: 3\nallow:\n  tools:\n" + string.Concat(browserTools.Select(t => "    - " + t + "\n")) +
            "resources:\n  browser:\n    profiles:\n      - kind: isolated\n    origins:\n      - https://www.selenium.dev\n  desktop:\n    display: false\n");
        foreach (var entry in new[] {
            new ConnectionConfig { Id = "cua_desktop", Name = "Cua · Calculator", Kind = "cua_desktop", Command = runtime, Arguments = ["mcp"], ExecutableHash = driverHash, Manifest = desktop, TargetExecutable = calculator, Enabled = true, ReviewedTools = desktopTools },
            new ConnectionConfig { Id = "cua_browser", Name = "Cua · isolated browser", Kind = "cua_browser", Command = runtime, Arguments = ["mcp"], ExecutableHash = driverHash, Manifest = browser, Enabled = true, ReviewedTools = browserTools } })
        {
            var prior = broker.Configurations.FirstOrDefault(c => c.Id == entry.Id);
            // App-owned paths and package versions can change across deliveries. Keep the user's enabled preference.
            if (prior is null) await broker.SaveAsync(entry);
            else if (prior.Command != entry.Command || prior.TargetExecutable != entry.TargetExecutable || prior.ExecutableHash != entry.ExecutableHash)
                await broker.UpdateBuiltinAsync(entry with { Enabled = prior.Enabled, RetainEvidence = prior.RetainEvidence });
        }
    }
}
