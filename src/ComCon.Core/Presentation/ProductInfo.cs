namespace ComCon.Core.Presentation;

public static class ProductInfo
{
    public const string Name = "ComCon";
    public const string Attribution = "BattleHook (PA1RGX)";
    public const string Copyright = "Copyright (c) 2026 PA1RGX";
    public const string LicenseName = "MIT License";
    public const string RepositoryUrl = "https://github.com/BattleHook/ComCon";
    public const string IssuesUrl = "https://github.com/BattleHook/ComCon/issues";
    public const string KoFiUrl = "https://ko-fi.com/battlehook";
    public const string HelpRelativePath = "Help\\index.html";
    public const string LicenseFileName = "LICENSE.txt";
    public const string NoticesFileName = "THIRD-PARTY-NOTICES.md";

    public const string DonationText =
        "ComCon is free to use. If it helps your station, you can optionally support its " +
        "development with a one-time donation on Ko-fi. You choose the amount. Donations are " +
        "not required and do not unlock features.";

    public const string SafetyText =
        "ComCon is provided as-is, without warranty and without any safety certification. " +
        "You remain responsible for the operation of your transmitter and station.";

    public static string FormatVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return "unknown";
        var plus = informationalVersion.IndexOf('+');
        return plus > 0 ? informationalVersion[..plus] : informationalVersion.Trim();
    }

    public static string ResolveBundledFile(string baseDirectory, string relativePath) =>
        Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
}
