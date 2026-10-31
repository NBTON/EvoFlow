using System.Diagnostics;

namespace EvoFlow.ToolHost;

public static class ExecutionPolicy
{
    static readonly string[] Hosts = ["cmd", "powershell", "powershell_ise", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "msiexec", "wmic", "wsl", "bash", "sh", "zsh", "perl", "ruby", "lua", "julia", "java", "javaw", "php", "rscript", "csc", "vbc", "msbuild", "reg", "schtasks", "forfiles", "bitsadmin", "certutil", "sqlite3", "psql", "sqlcmd", "mysql", "mariadb", "duckdb"];
    public static bool ScriptHost(string name)
    {
        name = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        return Hosts.Contains(name) || name.StartsWith("python") || name.StartsWith("node") || name.StartsWith("deno") || name.StartsWith("bun") || name.StartsWith("autohotkey");
    }
    public static string Resolve(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 1000 || input.Any(char.IsControl)) throw new ArgumentException("Invalid executable identity.");
        if (ScriptHost(input)) throw new ArgumentException("Script hosts, shell execution, installers and system persistence tools are not available through this interface.");
        IEnumerable<string> candidates;
        if (Path.IsPathFullyQualified(input)) candidates = [input];
        else
        {
            if (input.Contains('/') || input.Contains('\\') || input.Contains(':')) throw new ArgumentException("Use an installed executable name or an absolute local executable path.");
            string name = input.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? input : input + ".exe";
            var paths = new[] { Environment.SystemDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet") }
                .Concat((Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "").Split(';'))
                .Concat((Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "").Split(';'));
            candidates = paths.Where(Path.IsPathFullyQualified).Select(p => Path.Combine(p.Trim('"'), name));
        }
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            string path = Path.GetFullPath(candidate);
            if (path.StartsWith('\\') || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || ScriptHost(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            return path;
        }
        throw new ArgumentException("Executable not found in installed system/user PATH. Supply its absolute installed executable path.");
    }
    public static void ValidateArguments(string executable, string[] args)
    {
        if (File.Exists(executable))
        {
            using var file = File.OpenRead(executable); using var reader = new BinaryReader(file);
            if (file.Length < 128 || reader.ReadUInt16() != 0x5A4D) throw new ArgumentException("Expected a native Windows executable.");
            file.Position = 0x3C; int pe = reader.ReadInt32();
            if (pe < 0 || pe + 94 > file.Length) throw new ArgumentException("Invalid executable header.");
            file.Position = pe + 4 + 20 + 68;
            if (reader.ReadUInt16() != 3) throw new ArgumentException("Use application_launch for GUI applications. Terminal accepts native console executables, not GUI command-line escape routes.");
        }
        if (args.Length > 32 || args.Any(a => a.Length > 2000 || a.Any(char.IsControl))) throw new ArgumentException("Argument array exceeds bounds.");
        string[] codeOptions = ["-c", "/c", "/k", "-e", "--eval", "--command", "-command", "-encodedcommand", "--execute", "--exec", "--require", "--import"];
        string[] scripts = [".ps1", ".psm1", ".bat", ".cmd", ".vbs", ".js", ".mjs", ".cjs", ".py", ".pyw", ".sh", ".dll"];
        if (args.Any(a => codeOptions.Contains(a.ToLowerInvariant()) || scripts.Any(s => a.Trim('"').EndsWith(s, StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("Arguments that execute scripts or inline code are unavailable.");
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && !Diagnostic(executable, args))
            throw new ArgumentException("dotnet supports diagnostic switches here; generated code/build execution is unavailable.");
    }
    public static bool Diagnostic(string executable, string[] args)
    {
        string name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        return name is "hostname" or "whoami" && args.Length == 0
            || name == "dotnet" && args.Length == 1 && args[0] is "--info" or "--version" or "--list-sdks" or "--list-runtimes";
    }
    public static void ValidatePathArguments(Func<string, string> normalize, string directory, string[] args)
    {
        foreach (var argument in args)
        {
            string value = argument.Contains('=') ? argument[(argument.IndexOf('=') + 1)..] : argument;
            value = value.Trim('"'); if (value.StartsWith('@')) value = value[1..];
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") continue;
            if (value.Contains(".env", StringComparison.OrdinalIgnoreCase) || value.Contains(".dpapi", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Credential paths are protected across tool routes.");
            if (value.StartsWith('-') || value.StartsWith('/') && !value.Contains(':') && !File.Exists(value)) continue;
            if (Path.IsPathFullyQualified(value) || value.Contains('\\') || value.StartsWith('.') || Path.HasExtension(value)
                || File.Exists(Path.Combine(directory, value)) || Directory.Exists(Path.Combine(directory, value)))
                normalize(Path.GetFullPath(value, directory));
        }
    }
}
