namespace ComCon.Core.Models;

public sealed record ProfileConfig
{
    public int Id { get; init; }

    public string Label { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public bool AutoConnect { get; init; }

    public bool CommandIpcEnabled { get; init; }

    public int LegacyClientAddress { get; init; } = 0xE0;

    public int IpcClientAddress { get; init; } = 0xE1;

    public string? PhysicalPort { get; init; }

    public string? VirtualPort { get; init; }

    public string? CommanderPort { get; init; }

    public AdapterKind Adapter { get; init; } = AdapterKind.IcomIc7600;

    public FramingMode Framing { get; init; } = FramingMode.None;

    public int? RadioAddress { get; init; }

    public int? ControllerAddress { get; init; }

    public SerialSettings SerialSettings { get; init; } = new();
}
