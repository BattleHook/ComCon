using System;
using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace ComCon.UI;

public sealed class UiColorPreferences
{
    public string RunningColorHex { get; set; } = "#C8E6C9";
    public string RunningTextColorHex { get; set; } = "#000000";
    public string FaultColorHex { get; set; } = "#FFCDD2";
    public string FaultTextColorHex { get; set; } = "#000000";
    public string? SelectedThemePreset { get; set; } = "Windows";
    public string? LastConfigurationFilePath { get; set; }

    public static UiColorPreferences Load()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComCon");
            var file = Path.Combine(dir, "UiColors.json");
            if (File.Exists(file))
            {
                var json = File.ReadAllText(file);
                var loaded = JsonSerializer.Deserialize<UiColorPreferences>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch { }
        return new UiColorPreferences();
    }

    public void Save()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComCon");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "UiColors.json");
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(file, json);
        }
        catch { }
    }

    public static Color ParseColor(string? hex, Color fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            if (!hex.StartsWith('#')) hex = "#" + hex;
            var obj = ColorConverter.ConvertFromString(hex);
            return obj is Color color ? color : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
