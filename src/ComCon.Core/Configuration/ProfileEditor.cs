using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using ComCon.Core.Models;

namespace ComCon.Core.Configuration;

/// <summary>Editable offline draft; exports preserve legacy metadata without interpreting it.</summary>
public sealed class ProfileEditor : INotifyPropertyChanged
{
    private readonly ProfileConfig original;
    private string label;
    private bool enabled;
    private bool autoConnect;
    private bool commandIpcEnabled;
    private string legacyClientAddress;
    private string ipcClientAddress;
    private string physicalPort;
    private string virtualPort;
    private string commanderPort;
    private string baudRate;
    private string dataBits;
    private FramingMode framing;
    private SerialParity parity;
    private SerialStopBits stopBits;
    private SerialHandshake handshake;
    private bool dtrEnable;
    private bool rtsEnable;
    private bool isRunning;
    private bool isFaulted;

    public ProfileEditor(ProfileConfig profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        original = profile;
        label = profile.Label;
        enabled = profile.Enabled;
        autoConnect = profile.AutoConnect;
        commandIpcEnabled = profile.CommandIpcEnabled;
        legacyClientAddress = profile.LegacyClientAddress.ToString("X2", CultureInfo.InvariantCulture);
        ipcClientAddress = profile.IpcClientAddress.ToString("X2", CultureInfo.InvariantCulture);
        physicalPort = profile.PhysicalPort ?? "";
        virtualPort = profile.VirtualPort ?? "";
        commanderPort = profile.CommanderPort ?? "";
        baudRate = profile.SerialSettings.BaudRate.ToString(CultureInfo.InvariantCulture);
        dataBits = profile.SerialSettings.DataBits.ToString(CultureInfo.InvariantCulture);
        framing = profile.Framing;
        parity = profile.SerialSettings.Parity;
        stopBits = profile.SerialSettings.StopBits;
        handshake = profile.SerialSettings.Handshake;
        dtrEnable = profile.SerialSettings.DtrEnable;
        rtsEnable = profile.SerialSettings.RtsEnable;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public int Id => original.Id;
    public string MetadataNotice => original.Adapter == AdapterKind.Passthrough
        ? "Model-neutral draft. Radio command selection belongs to the client application."
        : "Legacy adapter/address metadata is preserved. Command-mode client addresses are separate.";
    public string Label { get => label; set => Set(ref label, value); }
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public bool AutoConnect { get => autoConnect; set => Set(ref autoConnect, value); }
    public bool CommandIpcEnabled { get => commandIpcEnabled; set => Set(ref commandIpcEnabled, value); }
    public string LegacyClientAddress { get => legacyClientAddress; set => Set(ref legacyClientAddress, value); }
    public string IpcClientAddress { get => ipcClientAddress; set => Set(ref ipcClientAddress, value); }
    public string PhysicalPort { get => physicalPort; set => Set(ref physicalPort, value); }
    public string VirtualPort { get => virtualPort; set => Set(ref virtualPort, value); }
    public string CommanderPort { get => commanderPort; set => Set(ref commanderPort, value); }
    public string BaudRate { get => baudRate; set => Set(ref baudRate, value); }
    public string DataBits { get => dataBits; set => Set(ref dataBits, value); }
    public FramingMode Framing { get => framing; set => Set(ref framing, value); }
    public SerialParity Parity { get => parity; set => Set(ref parity, value); }
    public SerialStopBits StopBits { get => stopBits; set => Set(ref stopBits, value); }
    public SerialHandshake Handshake { get => handshake; set => Set(ref handshake, value); }
    public bool DtrEnable { get => dtrEnable; set => Set(ref dtrEnable, value); }
    public bool RtsEnable { get => rtsEnable; set => Set(ref rtsEnable, value); }
    public bool IsRunning { get => isRunning; set => Set(ref isRunning, value); }
    public bool IsFaulted { get => isFaulted; set => Set(ref isFaulted, value); }

    public ProfileConfig Export(int index)
    {
        var errors = new List<ConfigurationError>();
        var baud = Parse(BaudRate, $"profiles[{index}].serialSettings.baudRate", errors);
        var bits = Parse(DataBits, $"profiles[{index}].serialSettings.dataBits", errors);
        var legacyAddress = ParseHexAddress(
            LegacyClientAddress, $"profiles[{index}].legacyClientAddress", errors);
        var ipcAddress = ParseHexAddress(
            IpcClientAddress, $"profiles[{index}].ipcClientAddress", errors);
        if (errors.Count != 0) throw new ConfigurationValidationException(errors);
        return original with
        {
            Label = Label,
            Enabled = Enabled,
            AutoConnect = AutoConnect,
            CommandIpcEnabled = CommandIpcEnabled,
            LegacyClientAddress = legacyAddress,
            IpcClientAddress = ipcAddress,
            PhysicalPort = EmptyToNull(PhysicalPort),
            VirtualPort = EmptyToNull(VirtualPort),
            CommanderPort = EmptyToNull(CommanderPort),
            Framing = Framing,
            SerialSettings = original.SerialSettings with
            {
                BaudRate = baud, DataBits = bits, Parity = Parity, StopBits = StopBits,
                Handshake = Handshake, DtrEnable = DtrEnable, RtsEnable = RtsEnable
            }
        };
    }

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    private static int Parse(string text, string path, List<ConfigurationError> errors)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return value;
        errors.Add(new(path, "Must be an unsigned decimal integer."));
        return 0;
    }

    private static int ParseHexAddress(string text, string path, List<ConfigurationError> errors)
    {
        if (text.Length == 2 &&
            byte.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            return value;
        errors.Add(new(path, "Must contain exactly two hexadecimal digits (00-FF)."));
        return 0;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}
