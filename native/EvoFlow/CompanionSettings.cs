using System.Text.Json;

namespace EvoFlow;

/// <summary>Only device-local presentation choices. Credentials remain in the worker's protected store.</summary>
internal sealed class CompanionSettings
{
    public bool AlwaysOnTop { get; set; } = true;
    public bool ExpandOnHover { get; set; } = true;
    public int ShortcutIndex { get; set; }
    public ulong DisplayId { get; set; }

    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EvoFlow", "companion-settings.json");

    public static CompanionSettings Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<CompanionSettings>(File.ReadAllText(SettingsPath)) ?? new();
            value.ShortcutIndex = Math.Clamp(value.ShortcutIndex, 0, 1);
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string pending = SettingsPath + ".tmp";
        File.WriteAllText(pending, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(pending, SettingsPath, overwrite: true);
    }
}
