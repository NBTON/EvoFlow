using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace EvoFlow.ToolHost;

// Shared source with WinUI. Launch belongs to the long-lived native client, rather
// than the short-lived helper whose redirected handle/process lifetime can end.
public static class ApplicationLauncher
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr state);
    delegate bool EnumWindow(IntPtr window, IntPtr state);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static string ImagePath(uint processId)
    {
        var process = OpenProcess(0x1000, false, processId); if (process == IntPtr.Zero) return "";
        try { var name = new System.Text.StringBuilder(2000); int size = name.Capacity; return QueryFullProcessImageName(process, 0, name, ref size) ? name.ToString() : ""; }
        finally { CloseHandle(process); }
    }
    public static string Resolve(string application)
    {
        if (application.ToLowerInvariant() is not ("chrome" or "edge" or "notepad" or "calculator"))
        {
            var installed = InstalledApplications.Resolve(application);
            return installed.AppId is null ? installed.Executable : "app:" + installed.AppId;
        }
        var exe = application.ToLowerInvariant() switch { "chrome" => "chrome.exe", "edge" => "msedge.exe", "notepad" => "notepad.exe", _ => "calc.exe" };
        if (exe is "notepad.exe" or "calc.exe") return Path.Combine(Environment.SystemDirectory, exe);
        var folder = exe == "chrome.exe" ? @"Google\Chrome\Application\chrome.exe" : @"Microsoft\Edge\Application\msedge.exe";
        // Product identity wins over per-user App Paths entries: Chromium forks can
        // register chrome.exe even when Google Chrome is installed alongside them.
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            if (File.Exists(Path.Combine(root, folder))) return Path.Combine(root, folder);
        string expectedProduct = exe == "chrome.exe" ? "Google Chrome" : "Microsoft Edge";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
            if (key?.GetValue(null) is not string candidate) continue;
            candidate = candidate.Trim('"');
            if (File.Exists(candidate) && string.Equals(Path.GetFileName(candidate), exe, StringComparison.OrdinalIgnoreCase)
                && string.Equals(FileVersionInfo.GetVersionInfo(candidate).ProductName, expectedProduct, StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        throw new ArgumentException("Application is not installed in a registered location.");
    }
    public static async Task<JsonObject> LaunchAsync(string application, CancellationToken ct)
    {
        var executable = Resolve(application);
        if (executable.StartsWith("app:"))
        {
            var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"))!;
            var manager = (IApplicationActivationManager)Activator.CreateInstance(type)!;
            try
            {
                int error = manager.ActivateApplication(executable[4..], null, 2, out uint pid);
                System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(error);
                return await VerifyAsync(application, ct, pid);
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(manager); }
        }
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!, CreateNoWindow = false };
        // A background Chrome/Edge singleton can accept a bare executable launch without
        // retaining a visible window. This fixed application-owned switch requests one.
        if (application is "chrome" or "edge") info.ArgumentList.Add("--new-window");
        info.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMDATA", "HOMEDRIVE", "HOMEPATH", "USERNAME", "USERDOMAIN" })
            if (Environment.GetEnvironmentVariable(key) is { } value) info.Environment[key] = value;
        info.Environment["PATH"] = Environment.SystemDirectory;
        using var launched = Process.Start(info);
        return await VerifyAsync(application, ct);
    }
    public static async Task<JsonObject> VerifyAsync(string application, CancellationToken ct, uint? activatedPid = null)
    {
        var executable = Resolve(application);
        long lastWindow = 0; int consecutive = 0;
        int matchedImages = 0;
        for (int attempt = 0; attempt < 32; attempt++)
        {
            ct.ThrowIfCancellationRequested(); var matches = new List<(IntPtr Window, uint Pid, string Image)>();
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window)) return true;
                GetWindowThreadProcessId(window, out uint pid);
                string actual = ImagePath(pid);
                        string packageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + Path.DirectorySeparatorChar;
                        bool packaged = application is "notepad" or "calculator" && actual.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase)
                            && (actual.Contains("Microsoft.WindowsNotepad_", StringComparison.OrdinalIgnoreCase) || actual.Contains("Microsoft.WindowsCalculator_", StringComparison.OrdinalIgnoreCase));
                bool activated = activatedPid == pid || executable.StartsWith("app:") && actual.Contains(executable[4..].Split('!')[0].Split('_')[0] + "_", StringComparison.OrdinalIgnoreCase);
                if (string.Equals(actual, executable, StringComparison.OrdinalIgnoreCase) || packaged || activated) matches.Add((window, pid, actual));
                return true;
            }, IntPtr.Zero);
            matchedImages = matches.Count;
            if (matches.Count > 0)
            {
                var match = matches.FirstOrDefault(m => m.Window.ToInt64() == lastWindow);
                if (match.Window == IntPtr.Zero) match = matches[0];
                consecutive = lastWindow == match.Window.ToInt64() ? consecutive + 1 : 1; lastWindow = match.Window.ToInt64();
                if (consecutive >= 4) return new() { ["status"] = "succeeded", ["verified"] = true, ["summary"] = $"{application} window is available.", ["data"] = new JsonObject { ["application"] = application, ["pid"] = match.Pid, ["windowHandle"] = lastWindow.ToString(), ["executable"] = match.Image } };
            }
            else consecutive = 0;
            await Task.Delay(300, ct);
        }
        return new() { ["status"] = "failed", ["verified"] = false, ["summary"] = "Launch was requested, but no stable matching application window remained available.", ["errorCode"] = "launch_unverified", ["data"] = new JsonObject { ["matchedWindows"] = matchedImages, ["expectedExecutable"] = executable } };
    }

    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        [System.Runtime.InteropServices.PreserveSig] int ActivateApplication([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string appId, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
    }
}
