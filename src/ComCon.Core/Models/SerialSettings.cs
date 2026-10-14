namespace ComCon.Core.Models;

public enum SerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space
}

public enum SerialStopBits
{
    One,
    OnePointFive,
    Two
}

public enum SerialHandshake
{
    None,
    XOnXOff,
    RequestToSend,
    RequestToSendXOnXOff
}

public sealed record SerialSettings
{
    public int BaudRate { get; init; } = 19_200;

    public int DataBits { get; init; } = 8;

    public SerialParity Parity { get; init; } = SerialParity.None;

    public SerialStopBits StopBits { get; init; } = SerialStopBits.One;

    public SerialHandshake Handshake { get; init; } = SerialHandshake.None;

    public bool DtrEnable { get; init; }

    public bool RtsEnable { get; init; }
}
