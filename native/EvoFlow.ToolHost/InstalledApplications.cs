using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Text.Json.Nodes;

namespace EvoFlow.ToolHost;

public sealed record InstalledApplication(string Name, string Executable, string? AppId = null);

public static class InstalledApplications
{
    public static IReadOnlyList<InstalledApplication> Discover()
    {
        var found = new Dictionary<string, InstalledApplication>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string exe, string? appId = null)
        {
            if (string.IsNullOrWhiteSpace(name) || ExecutionPolicy.ScriptHost(exe) || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("setup", StringComparison.OrdinalIgnoreCase)) return;
            if (appId is null && (!File.Exists(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) return;
            found.TryAdd(appId ?? Path.GetFullPath(exe), new(name, exe, appId));
        }
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var paths = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            foreach (var keyName in paths?.GetSubKeyNames() ?? [])
            {
                try
                {
                    using var key = paths!.OpenSubKey(keyName);
                    if (key?.GetValue(null) is not string candidate) continue;
                    candidate = Environment.ExpandEnvironmentVariables(candidate.Trim('"'));
                    if (!File.Exists(candidate)) continue;
                    var metadata = FileVersionInfo.GetVersionInfo(candidate);
                    Add(metadata.FileDescription ?? metadata.ProductName ?? keyName, candidate);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
        // Standard shell catalog includes Store/MSIX applications. Start-menu shortcuts
        // cover desktop applications that do not publish an App Paths registration.
        object? shell = null; object? folder = null; object? items = null; object? shortcuts = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            folder = ((dynamic)shell!).NameSpace("shell:AppsFolder"); items = ((dynamic)folder!).Items();
            for (int i = 0; i < Math.Min((int)((dynamic)items).Count, 1500); i++)
            {
                dynamic item = ((dynamic)items).Item(i);
                try
                {
                    string id = item.Path; string name = item.Name;
                    if (id.Contains('!')) Add(name, "", id);
                    else
                    {
                        string target = item.ExtendedProperty("System.Link.TargetParsingPath") as string ?? "";
                        if (File.Exists(target)) Add(name, target);
                    }
                }
                finally { Marshal.FinalReleaseComObject(item); }
            }
            shortcuts = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            foreach (var menu in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) })
            foreach (var path in Directory.EnumerateFiles(menu, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Take(1500))
            {
                dynamic link = ((dynamic)shortcuts!).CreateShortcut(path);
                try { if (string.IsNullOrEmpty((string)link.Arguments)) Add(Path.GetFileNameWithoutExtension(path), (string)link.TargetPath); }
                finally { Marshal.FinalReleaseComObject(link); }
            }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally { foreach (var obj in new[] { shortcuts, items, folder, shell }) if (obj is not null && Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj); }
        return found.Values.OrderBy(a => a.Name).ToArray();
    }
    static string Key(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    public static IEnumerable<InstalledApplication> Search(string query)
    {
        string key = Key(query);
        return Discover().Where(a => Key(a.Name).Contains(key) || Key(Path.GetFileNameWithoutExtension(a.Executable)).Contains(key) || a.AppId?.Equals(query, StringComparison.OrdinalIgnoreCase) == true);
    }
    public static InstalledApplication Resolve(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200 || query.Any(char.IsControl) || query.Contains('/') || query.Contains('\\') || query.Contains(':'))
            throw new ArgumentException("Choose an installed application name or catalog app ID, not a path or command.");
        if (ExecutionPolicy.ScriptHost(query)) throw new ArgumentException("Use terminal tools for approved commands; shells and script hosts are unavailable.");
        var candidates = Search(query).ToArray(); string key = Key(query);
        var exact = candidates.Where(a => Key(a.Name) == key || Key(Path.GetFileNameWithoutExtension(a.Executable)) == key || a.AppId == query).ToArray();
        if (exact.Length == 1) return exact[0];
        var words = candidates.Where(a => System.Text.RegularExpressions.Regex.IsMatch(a.Name, @"\b" + System.Text.RegularExpressions.Regex.Escape(query) + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();
        if (words.Length == 1) return words[0];
        // Multiple registrations of one desktop program are deduplicated by executable.
        if (candidates.Length == 1) return candidates[0];
        if (candidates.Length == 0) throw new ArgumentException("No installed application matches. Use application_search to inspect the catalog.");
        throw new ArgumentException("Ambiguous application. Choose a full name or app ID from application_search: " + string.Join(", ", candidates.Take(8).Select(a => a.Name)));
    }
    public static JsonObject Catalog(string query) => new() { ["applications"] = new JsonArray(Search(query).Take(100).Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["application"] = a.AppId ?? a.Name, ["executable"] = a.Executable, ["packaged"] = a.AppId is not null }).ToArray()) };
}
