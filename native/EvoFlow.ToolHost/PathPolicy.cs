namespace EvoFlow.ToolHost;

public sealed class PathPolicy
{
    public string Workspace { get; }
    private readonly string appHome;
    private static readonly HashSet<string> ProtectedParts = new(StringComparer.OrdinalIgnoreCase)
    { ".ssh", ".aws", ".azure", ".codex", ".agents", ".git", "Agent Skills", "Credentials", "Protect", "Vault", "Cookies", "Login Data", "User Data", "Bitwarden", "MicrosoftEdge", "Firefox" };
    public PathPolicy(string workspace, string appHome)
    {
        Workspace = Path.GetFullPath(workspace); this.appHome = Path.GetFullPath(appHome);
    }
    public static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public string Normalize(string input, bool write = false)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 1000 || input.Any(char.IsControl)
            || input.StartsWith('\\') || input.IndexOf(':', Math.Min(2, input.Length)) >= 0 || input.Contains('*') || input.Contains('?'))
            throw new ArgumentException("Use a local file path without device paths, streams or wildcards.");
        var path = Path.GetFullPath(input, Workspace);
        if (path.StartsWith('\\') || new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
            throw new ArgumentException("Network locations are not local tool paths.");
        if (path.Split(Path.DirectorySeparatorChar).Any(p => p.EndsWith(' ') || p.EndsWith('.')))
            throw new ArgumentException("Ambiguous Windows path.");
        var name = Path.GetFileName(path);
        var reserved = name.Split('.')[0].ToUpperInvariant();
        if (reserved is "CON" or "PRN" or "AUX" or "NUL" || System.Text.RegularExpressions.Regex.IsMatch(reserved, "^(COM|LPT)[0-9]$"))
            throw new ArgumentException("Reserved Windows device name.");
        if (path.Split(Path.DirectorySeparatorChar).Any(ProtectedParts.Contains)
            || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || name.Contains("credential", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || new[] { ".env", ".dpapi", ".pfx", ".p12", ".key", ".pem", ".db" }.Contains(Path.GetExtension(path).ToLowerInvariant())
            || Within(path, appHome) || Within(path, AppContext.BaseDirectory)
            || Within(path, Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!))
            throw new ArgumentException("Credential, application and shared instruction paths are protected.");
        // Fail closed on every existing component, including ancestors of nonexistent destinations.
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Junctions and symbolic links are not permitted by local tool policy.");
        if (write && Within(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
            throw new ArgumentException("Windows system files are protected.");
        return path;
    }
    public void WritableFile(string path)
    {
        string[] allowed = [".txt", ".md", ".json", ".csv", ".tsv", ".docx", ".pptx", ".xlsx", ".pdf"];
        if (!allowed.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new ArgumentException("Only text, data and supported document files may be written or transferred.");
    }
}
