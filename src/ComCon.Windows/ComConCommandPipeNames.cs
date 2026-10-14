namespace ComCon.Windows;

public sealed class ComConCommandPipeNames
{
    private ComConCommandPipeNames(string catPipeName, bool production)
    {
        CatPipeName = catPipeName;
        IsProduction = production;
    }

    public string CatPipeName { get; }
    public bool IsProduction { get; }

    public static ComConCommandPipeNames Production() => new("ComConCmd_Cat", true);

    public static ComConCommandPipeNames IsolatedTest(string catPipeName)
    {
        ArgumentNullException.ThrowIfNull(catPipeName);
        if (!catPipeName.StartsWith("ComConTest_", StringComparison.Ordinal) ||
            catPipeName.Length > 80 ||
            catPipeName.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("Only bounded ComConTest_ names are allowed.", nameof(catPipeName));
        return new(catPipeName, false);
    }

    public string ControlPipeName(int profileId)
    {
        if (profileId is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(profileId));
        return IsProduction
            ? $"ComConCmd_Cat_Control_Profile{profileId}"
            : $"{CatPipeName}_Control_Profile{profileId}";
    }
}
