namespace ComCon.Core.Models;

public enum FramingMode { None, Civ, KenwoodAscii, YaesuAscii }

public static class FramingModeExtensions
{
    public static string ToDisplayName(this FramingMode mode) => mode switch
    {
        FramingMode.Civ => "CI-V",
        FramingMode.KenwoodAscii => "Kenwood ASCII",
        FramingMode.YaesuAscii => "YAESU ASCII",
        FramingMode.None => "None",
        _ => mode.ToString()
    };
}

