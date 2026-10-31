using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Authentication;

namespace EvoFlow.Connectors;

public sealed record ConnectionConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Transport { get; init; } = "stdio";
    public string Command { get; init; } = "";
    public string[] Arguments { get; init; } = [];
    public string Endpoint { get; init; } = "";
    public string Authentication { get; init; } = "none";
    public string OAuthClientId { get; init; } = "";
    public bool Enabled { get; init; }
    public bool RetainEvidence { get; init; }
    public string[] ReviewedTools { get; init; } = [];
    public string Kind { get; init; } = "generic";
    public string ExecutableHash { get; init; } = "";
    public string Manifest { get; init; } = "";
    public string TargetExecutable { get; init; } = "";

    public void Validate()
    {
        if (!Regex.IsMatch(Id, "^[a-z][a-z0-9_]{0,23}$") || Name.Length is < 1 or > 100)
            throw new ArgumentException("Use a unique lowercase connection ID (up to 24 letters, digits and underscores).");
        if (Transport is not ("stdio" or "http") || Authentication is not ("none" or "bearer" or "oauth"))
            throw new ArgumentException("Unsupported transport or authentication. Use stdio or Streamable HTTP, and none, bearer or OAuth.");
        if (Kind is not ("generic" or "cua_desktop" or "cua_browser")) throw new ArgumentException("Unsupported connector kind.");
        if (Transport == "stdio" && (!Path.IsPathFullyQualified(Command) || !File.Exists(Command)
            || !Command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Arguments.Length > 32
            || Arguments.Any(a => a.Length > 2000 || a.Any(char.IsControl))))
            throw new ArgumentException("Select an installed absolute .exe path and a JSON argument array. Server registration is trusted native configuration.");
        if (Transport == "stdio" && Authentication == "oauth") throw new ArgumentException("OAuth is supported for HTTP connections.");
        if (Transport == "http")
        {
            if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
                throw new ArgumentException("Use HTTPS for remote MCP; HTTP is allowed only for a local test server. Do not put credentials in the URL.");
            if (uri.Query.Length != 0) throw new ArgumentException("MCP endpoint query strings are disabled to avoid credentials in configuration.");
        }
        if (ReviewedTools.Length > 128 || ReviewedTools.Any(t => !Regex.IsMatch(t, "^[A-Za-z0-9_.-]{1,100}$")))
            throw new ArgumentException("Reviewed tool names exceed limits.");
    }
}

public sealed class ConnectionStore
{
    readonly string directory;
    public ConnectionStore(string home) { directory = Path.Combine(home, "connections"); Directory.CreateDirectory(directory); }
    public IReadOnlyList<ConnectionConfig> Load()
    {
        string file = Path.Combine(directory, "servers.json");
        if (!File.Exists(file)) return [];
        if (new FileInfo(file).Length > 256000) throw new IOException("Connection configuration exceeds size budget.");
        return JsonSerializer.Deserialize<ConnectionConfig[]>(File.ReadAllText(file)) ?? [];
    }
    public void Save(IEnumerable<ConnectionConfig> configs)
    {
        var values = configs.ToArray();
        if (values.Length > 16 || values.Select(c => c.Id).Distinct().Count() != values.Length) throw new ArgumentException("At most 16 unique connections are supported.");
        foreach (var config in values) config.Validate();
        string file = Path.Combine(directory, "servers.json"), temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, file, true);
    }
    string SecretPath(string id, string suffix)
    {
        if (!Regex.IsMatch(id, "^[a-z][a-z0-9_]{0,23}$")) throw new ArgumentException("Invalid server identity.");
        return Path.Combine(directory, id + suffix + ".dpapi");
    }
    public string? ReadSecret(string id, string suffix = "")
    {
        var file = SecretPath(id, suffix);
        return File.Exists(file) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(file), Encoding.UTF8.GetBytes("EvoFlow.MCP." + id), DataProtectionScope.CurrentUser)) : null;
    }
    public void WriteSecret(string id, string? secret, string suffix = "")
    {
        string file = SecretPath(id, suffix);
        if (string.IsNullOrEmpty(secret)) { if (File.Exists(file)) File.Delete(file); return; }
        if (secret.Length > 64000) throw new ArgumentException("Credential exceeds size budget.");
        File.WriteAllBytes(file + ".tmp", ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("EvoFlow.MCP." + id), DataProtectionScope.CurrentUser));
        File.Move(file + ".tmp", file, true);
    }
    public ITokenCache TokenCache(string id) => new ProtectedTokenCache(this, id);
    sealed class ProtectedTokenCache(ConnectionStore store, string id) : ITokenCache
    {
        readonly SemaphoreSlim gate = new(1);
        public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            try { store.WriteSecret(id, JsonSerializer.Serialize(tokens), ".oauth"); } finally { gate.Release(); }
        }
        public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            try { var json = store.ReadSecret(id, ".oauth"); return json is null ? null : JsonSerializer.Deserialize<TokenContainer>(json); } finally { gate.Release(); }
        }
    }
}

