using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SunumBuyuteci;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FollowMode
{
    Edge,
    Center,
}

public sealed class Settings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SunumBuyuteci", "settings.json");

    public string? LastMonitorDevice { get; set; }

    public FollowMode FollowMode { get; set; } = FollowMode.Edge;

    public bool ClipCursor { get; set; } = true;

    /// <summary>Last zoom above 1x; used by the tray double-click toggle.</summary>
    public double LastZoom { get; set; } = 2.0;

    /// <summary>Keep the control window above other windows.</summary>
    public bool ControlTopmost { get; set; }

    /// <summary>Control window position in physical pixels; null = default (top center of the primary screen).</summary>
    public int? ControlLeft { get; set; }
    public int? ControlTop { get; set; }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception)
        {
            // Missing or corrupt file: use defaults.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Settings are best-effort.
        }
    }
}
