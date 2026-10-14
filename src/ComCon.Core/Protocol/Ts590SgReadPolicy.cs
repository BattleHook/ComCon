using System.Globalization;

namespace ComCon.Core.Protocol;

public enum Ts590SgReadKind { IfStatus, VfoAFrequency, VfoBFrequency, Mode }
public enum Ts590SgReadResultKind { Value, RadioError, Unrelated, Anomaly }

public sealed record Ts590SgReadResult(
    Ts590SgReadResultKind Kind,
    Ts590SgReadKind? ReadKind = null,
    string? Value = null,
    string? Error = null);

public sealed class Ts590SgModelIdentity
{
    private Ts590SgModelIdentity() { }

    public static Ts590SgModelIdentity ValidateResponse(ReadOnlySpan<byte> record)
    {
        if (record.SequenceEqual("ID023;"u8)) return new();
        if (record.SequenceEqual("ID021;"u8))
            throw new InvalidOperationException("The connected model identifier is TS-590S, not TS-590SG.");
        throw new ArgumentException("Expected the manual-documented TS-590SG model response ID023;.", nameof(record));
    }
}

public sealed class Ts590SgReadContract
{
    private Ts590SgReadContract(Ts590SgReadKind kind, string command)
    {
        Kind = kind;
        Command = command;
    }

    public Ts590SgReadKind Kind { get; }
    public string Command { get; }

    public static Ts590SgReadContract ValidateRequest(
        ReadOnlySpan<byte> record, Ts590SgModelIdentity modelIdentity)
    {
        ArgumentNullException.ThrowIfNull(modelIdentity);
        if (record.SequenceEqual("IF;"u8)) return new(Ts590SgReadKind.IfStatus, "IF");
        if (record.SequenceEqual("FA;"u8)) return new(Ts590SgReadKind.VfoAFrequency, "FA");
        if (record.SequenceEqual("FB;"u8)) return new(Ts590SgReadKind.VfoBFrequency, "FB");
        if (record.SequenceEqual("MD;"u8)) return new(Ts590SgReadKind.Mode, "MD");
        throw new ArgumentException("Only manual-documented read-only TS-590SG requests are admitted.", nameof(record));
    }

    public Ts590SgReadResult Classify(ReadOnlySpan<byte> record)
    {
        if (record.SequenceEqual("?;"u8) || record.SequenceEqual("E;"u8) || record.SequenceEqual("O;"u8))
            return new(Ts590SgReadResultKind.RadioError, Kind, Error: "Radio returned a documented error record.");
        if (record.Length < 3 || record[^1] != (byte)';')
            return new(Ts590SgReadResultKind.Anomaly, Kind, Error: "Reply is not a complete semicolon-terminated record.");
        foreach (var value in record[..^1])
        {
            if (value is < 0x20 or > 0x7E)
                return new(Ts590SgReadResultKind.Anomaly, Kind, Error: "Reply contains a nonprintable/non-ASCII byte.");
        }
        if (!record[..2].SequenceEqual(System.Text.Encoding.ASCII.GetBytes(Command)))
            return new(Ts590SgReadResultKind.Unrelated, Kind);

        return Kind switch
        {
            Ts590SgReadKind.VfoAFrequency or Ts590SgReadKind.VfoBFrequency => ClassifyFrequency(record),
            Ts590SgReadKind.Mode => ClassifyMode(record),
            Ts590SgReadKind.IfStatus => ClassifyIfStatus(record),
            _ => throw new InvalidOperationException("Unknown read contract.")
        };
    }

    private Ts590SgReadResult ClassifyFrequency(ReadOnlySpan<byte> record)
    {
        if (record.Length != 14 || !IsDigits(record[2..13]))
            return Invalid("Frequency answer must contain exactly eleven decimal digits.");
        var hz = long.Parse(System.Text.Encoding.ASCII.GetString(record[2..13]), CultureInfo.InvariantCulture);
        return new(Ts590SgReadResultKind.Value, Kind, hz.ToString(CultureInfo.InvariantCulture));
    }

    private Ts590SgReadResult ClassifyMode(ReadOnlySpan<byte> record)
    {
        if (record.Length != 4 || !IsMode(record[2]))
            return Invalid("Mode answer must contain one supported TS-590SG mode digit.");
        return new(Ts590SgReadResultKind.Value, Kind, ((char)record[2]).ToString());
    }

    private Ts590SgReadResult ClassifyIfStatus(ReadOnlySpan<byte> record)
    {
        if (record.Length != 38 || !IsDigits(record[2..13]) ||
            !record[13..18].SequenceEqual("     "u8) ||
            record[18] is not ((byte)'+' or (byte)'-') || !IsDigits(record[19..23]) ||
            !IsBit(record[23]) || !IsBit(record[24]) ||
            !IsDigits(record[25..27]) || !IsBit(record[27]) ||
            !IsMode(record[28]) || !IsDigits(record[29..31]) ||
            record[31] is not ((byte)'0' or (byte)'1' or (byte)'4' or (byte)'5' or (byte)'7') ||
            record[32] is not ((byte)'0' or (byte)'1') ||
            record[33] is < (byte)'0' or > (byte)'3' ||
            !IsDigits(record[34..36]) || record[34] > (byte)'4' ||
            record[35] > (byte)'2' || record[36] != (byte)'0' ||
            record[37] != (byte)';')
        {
            return Invalid("IF answer does not match the documented fixed-field shape.");
        }

        var offset = int.Parse(System.Text.Encoding.ASCII.GetString(record[19..23]), CultureInfo.InvariantCulture);
        if (offset > 9990)
            return Invalid("IF RIT/XIT offset is outside the documented +/-9990 Hz range.");
        var tone = int.Parse(System.Text.Encoding.ASCII.GetString(record[35..37]), CultureInfo.InvariantCulture);
        if (tone > 42)
            return Invalid("IF tone frequency code is outside the documented 00-42 range.");
        return new(Ts590SgReadResultKind.Value, Kind, System.Text.Encoding.ASCII.GetString(record));
    }

    private static bool IsBit(byte value) => value is (byte)'0' or (byte)'1';
    private static bool IsMode(byte value) =>
        value is >= (byte)'1' and <= (byte)'7' or (byte)'9';

    private static bool IsDigits(ReadOnlySpan<byte> values)
    {
        foreach (var value in values)
        {
            if (value is < (byte)'0' or > (byte)'9') return false;
        }
        return true;
    }
    private Ts590SgReadResult Invalid(string error) => new(Ts590SgReadResultKind.Anomaly, Kind, Error: error);
}
