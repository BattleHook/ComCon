using ComCon.Core.Ipc;

namespace ComCon.Core.Models;

public sealed record AppSettings
{
    private IReadOnlyList<ProfileConfig> _profiles = Array.AsReadOnly(Array.Empty<ProfileConfig>());
    private IReadOnlyList<ComConRigProfileMapping> _rigProfileMappings =
        Array.AsReadOnly(Array.Empty<ComConRigProfileMapping>());

    public int SchemaVersion { get; init; } = 1;

    public IReadOnlyList<ProfileConfig> Profiles
    {
        get => _profiles;
        init => _profiles = value is null
            ? throw new ArgumentNullException(nameof(value))
            : Array.AsReadOnly(value.ToArray());
    }

    public RoutingLimits RoutingLimits { get; init; } = new();

    public IReadOnlyList<ComConRigProfileMapping> RigProfileMappings
    {
        get => _rigProfileMappings;
        init => _rigProfileMappings = value is null
            ? throw new ArgumentNullException(nameof(value))
            : Array.AsReadOnly(value.ToArray());
    }

    public static AppSettings CreateDefault() =>
        new()
        {
            SchemaVersion = 1,
            Profiles = Enumerable.Range(1, 4)
                .Select(id => new ProfileConfig
                {
                    Id = id,
                    Label = $"Profile {id}"
                })
                .ToArray(),
            RoutingLimits = new RoutingLimits()
        };
}
