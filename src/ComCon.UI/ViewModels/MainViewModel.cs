using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using ComCon.Core.Configuration;
using ComCon.Core.Models;
using ComCon.Core.Ipc;

namespace ComCon.UI.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private AppSettings settings = CreateDraft();
    private IReadOnlyList<ProfileEditor> profiles = [];
    private ObservableCollection<RigProfileMappingEditor> rigProfileMappings = [];
    private ProfileEditor? selectedProfile;
    private string status = "No ports are open. Load a saved profile, then explicitly start one selected bridge.";
    private bool isDirty;
    private IReadOnlyList<string> availablePorts = [];
    private bool refreshingPorts;
    private bool replacingProfiles;
    private bool bridgeActive;
    private IReadOnlyDictionary<int, ProfileConfig> activeProfiles = new Dictionary<int, ProfileConfig>();
    private IReadOnlySet<int> rawProfileIds = new HashSet<int>();
    private string commandIpcStatus = "Command IPC is off. Production startup remains gated pending approved queue bounds.";
    private string commandIpcSessionsStatus = "Sessions:\n  (none)\nFaults:\n  (none)";
    private string telemetryStatus = "Passive telemetry OFF. Live enablement is unavailable in this build; no HSC safety integration.";

    public MainViewModel() => Replace(settings);
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<ProfileEditor> Profiles => profiles;
    public ObservableCollection<RigProfileMappingEditor> RigProfileMappingEditors => rigProfileMappings;
    public IReadOnlyList<string> AvailablePorts => availablePorts;
    public FramingMode[] FramingModes { get; } = Enum.GetValues<FramingMode>();
    public SerialParity[] Parities { get; } = Enum.GetValues<SerialParity>();
    public SerialStopBits[] StopBitOptions { get; } = Enum.GetValues<SerialStopBits>();
    public SerialHandshake[] Handshakes { get; } = Enum.GetValues<SerialHandshake>();
    public bool IsDirty => isDirty;
    public AppSettings SavedSettings => settings;
    public bool SelectedProfileEditable => selectedProfile is { } profile && !activeProfiles.ContainsKey(profile.Id);
    public bool SelectedProfileCanEnableIpc => SelectedProfileEditable ||
        (selectedProfile is { } profile && rawProfileIds.Contains(profile.Id) && !profile.CommandIpcEnabled);
    public string Status { get => status; set { status = value; Notify(nameof(Status)); } }
    public string TelemetryStatus => telemetryStatus;
    public string CommandIpcStatus => commandIpcStatus;
    public string CommandIpcSessionsStatus => commandIpcSessionsStatus;
    public IReadOnlyList<ComConRigProfileMapping> RigProfileMappings =>
        Array.AsReadOnly(rigProfileMappings.Select(mapping => mapping.Export()).ToArray());
    public Func<ProfileEditor, ProfileEditor, bool>? ConfirmProfileSwitch { get; set; }

    public static string FormatRigProfileMapping(ComConRigProfileMapping mapping, string? profileLabel = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(mapping.RigModelName))
            details.Add(mapping.RigModelName.Trim());
        if (!string.IsNullOrWhiteSpace(mapping.CivAddress))
            details.Add($"CI-V address {mapping.CivAddress.Trim().ToUpperInvariant()}");
        details.Add($"UUID {mapping.RigProfileId}");

        var line = $"{string.Join("; ", details)} -> Profile {mapping.ProfileId}";
        return string.IsNullOrEmpty(profileLabel) ? line : $"{line} '{profileLabel}'";
    }

    public ProfileEditor? SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (ReferenceEquals(value, selectedProfile)) return;
            if (!replacingProfiles && selectedProfile is not null && value is not null)
            {
                if (ConfirmProfileSwitch is not null &&
                    !ConfirmProfileSwitch(selectedProfile, value))
                {
                    Notify(nameof(SelectedProfile));
                    return;
                }
                value = profiles.Single(profile => profile.Id == value.Id);
            }
            selectedProfile = value;
            telemetryStatus = $"Profile {value?.Id}: passive telemetry OFF. Old readings must clear; " +
                "new profile requires explicit enablement and fresh model-matched data. Live enablement unavailable.";
            Notify(nameof(SelectedProfile));
            Notify(nameof(TelemetryStatus));
            Notify(nameof(SelectedProfileEditable));
            Notify(nameof(SelectedProfileCanEnableIpc));
        }
    }

    public void UpdateTelemetry(PassiveTelemetrySnapshot snapshot, bool isBridgeActive)
    {
        bridgeActive = isBridgeActive;
        var shownProfile = snapshot.State == "Off" ? SelectedProfile?.Id : snapshot.ProfileId;
        telemetryStatus = $"Profile {shownProfile?.ToString(CultureInfo.InvariantCulture) ?? "none"}: " +
            $"passive telemetry {snapshot.State.ToUpperInvariant()}. " +
            (snapshot.Fault is { } fault ? $"Unavailable: {fault}. " : "") +
            "Live enablement unavailable; display-only, not safety confirmation.";
        Notify(nameof(TelemetryStatus));
    }

    public void SetCommandIpcStatus(string value, string sessionsValue = "")
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(sessionsValue);
        commandIpcStatus = value;
        commandIpcSessionsStatus = sessionsValue;
        Notify(nameof(CommandIpcStatus));
        Notify(nameof(CommandIpcSessionsStatus));
    }

    public void UpdateProfileRuntimeStates(IEnumerable<(int ProfileId, bool IsRunning, bool IsFaulted)> states)
    {
        var stateMap = states.ToDictionary(s => s.ProfileId);
        foreach (var profile in profiles)
        {
            if (stateMap.TryGetValue(profile.Id, out var state))
            {
                profile.IsRunning = state.IsRunning;
                profile.IsFaulted = state.IsFaulted;
            }
            else
            {
                profile.IsRunning = false;
                profile.IsFaulted = false;
            }
        }
    }

    public void ValidateClientRigImport(ClientRigProfile imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        if (!SelectedProfileEditable) throw new InvalidOperationException("Stop this profile before importing a rig mapping.");
        var profile = SelectedProfile
            ?? throw new InvalidOperationException("Select a ComCon profile before importing a rig mapping.");
        if (!profile.CommandIpcEnabled)
            throw new InvalidOperationException("Enable Command IPC for the selected profile before importing.");
        if (imported.Mapping.ProfileId != profile.Id || imported.Framing != profile.Framing)
            throw new InvalidOperationException("Imported rig transport and target must match the selected profile's framing and ID.");
        if (rigProfileMappings.Any(mapping => mapping.RigProfileId == imported.Mapping.RigProfileId &&
                                              mapping.ProfileId != profile.Id))
            throw new InvalidOperationException("This rig UUID is already mapped to another profile. Import cannot reroute it.");
    }

    public void ImportClientRigProfile(ClientRigProfile imported)
    {
        ValidateClientRigImport(imported);
        var editor = new RigProfileMappingEditor(imported.Mapping);
        editor.SetSupportsCivAddress(imported.Framing == FramingMode.Civ);
        var existing = rigProfileMappings.FirstOrDefault(mapping =>
            mapping.RigProfileId == imported.Mapping.RigProfileId);
        if (existing is not null)
        {
            existing.PropertyChanged -= DraftChanged;
            rigProfileMappings[rigProfileMappings.IndexOf(existing)] = editor;
        }
        else rigProfileMappings.Add(editor);
        editor.PropertyChanged += DraftChanged;
        DraftChanged(editor, new(nameof(RigProfileMappings)));
    }

    public AppSettings Export()
    {
        var result = settings with
        {
            Profiles = Profiles.Select((p, i) => p.Export(i)).ToArray(),
            RigProfileMappings = rigProfileMappings.Select(mapping => mapping.Export()).ToArray()
        };
        var errors = ConfigurationValidator.Validate(result);
        if (errors.Count != 0) throw new ConfigurationValidationException(errors);
        ValidateRunningConfiguration(result);
        return result with
        {
            RigProfileMappings = result.RigProfileMappings.Select(mapping =>
                activeProfiles.ContainsKey(mapping.ProfileId)
                    ? settings.RigProfileMappings.Single(saved => saved.RigProfileId == mapping.RigProfileId)
                    : mapping).ToArray()
        };
    }

    public void Load(AppSettings loaded)
    {
        if (bridgeActive) throw new InvalidOperationException("Stop the bridge before replacing profile configuration.");
        var errors = ConfigurationValidator.Validate(loaded);
        if (errors.Count != 0) throw new ConfigurationValidationException(errors);
        Replace(loaded);
    }

    public void Saved(AppSettings saved)
    {
        ValidateRunningConfiguration(saved);
        settings = saved;
        isDirty = false;
        Notify(nameof(IsDirty));
    }

    public void SetActiveProfiles(IEnumerable<ProfileConfig> active, IEnumerable<int> rawIds)
    {
        activeProfiles = active.ToDictionary(profile => profile.Id);
        rawProfileIds = rawIds.ToHashSet();
        bridgeActive = activeProfiles.Count != 0;
        foreach (var mapping in rigProfileMappings)
            mapping.IsRuntimeLocked = activeProfiles.ContainsKey(mapping.ProfileId);
        Notify(nameof(SelectedProfileEditable));
        Notify(nameof(SelectedProfileCanEnableIpc));
    }

    public void DiscardDraft()
    {
        var id = SelectedProfile?.Id;
        Replace(settings);
        replacingProfiles = true;
        try { SelectedProfile = profiles.FirstOrDefault(profile => profile.Id == id) ?? profiles.FirstOrDefault(); }
        finally { replacingProfiles = false; }
        SetActiveProfiles(activeProfiles.Values.ToArray(), rawProfileIds);
    }

    private void ValidateRunningConfiguration(AppSettings candidate)
    {
        foreach (var (id, active) in activeProfiles)
        {
            var draft = candidate.Profiles.Single(profile => profile.Id == id);
            if (rawProfileIds.Contains(id) &&
                settings.Profiles.Single(profile => profile.Id == id).CommandIpcEnabled &&
                !draft.CommandIpcEnabled)
                throw new InvalidOperationException($"Stop profile {id} before disabling its saved IPC mode.");
            var allowed = rawProfileIds.Contains(id) && draft.CommandIpcEnabled
                ? draft with { CommandIpcEnabled = active.CommandIpcEnabled } : draft;
            if (allowed != active)
                throw new InvalidOperationException($"Profile {id} is running; stop it before changing its settings.");
            var currentMappings = settings.RigProfileMappings.Where(mapping => mapping.ProfileId == id)
                .Select(mapping => new RigProfileMappingEditor(mapping).Export());
            var candidateMappings = candidate.RigProfileMappings.Where(mapping => mapping.ProfileId == id)
                .Select(mapping => new RigProfileMappingEditor(mapping).Export());
            if (!currentMappings.SequenceEqual(candidateMappings))
                throw new InvalidOperationException($"Profile {id} is running; its rig mappings are locked.");
        }
        if (activeProfiles.Count != 0 &&
            (candidate.SchemaVersion != settings.SchemaVersion || candidate.RoutingLimits != settings.RoutingLimits))
            throw new InvalidOperationException("Stop all bridges before changing shared settings.");
    }

    public static ConfigurationFileStore SelectSaveStore(string path, ConfigurationFileStore? current)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        if (current is not null &&
            string.Equals(current.FilePath, fullPath, StringComparison.OrdinalIgnoreCase))
            return current;
        if (System.IO.File.Exists(fullPath))
            throw new ConfigurationConcurrencyException(
                "This configuration file already exists and has not been loaded in this window. " +
                "Your current edits have not been saved or discarded. Choose a new filename to save " +
                "this draft, or cancel Save and use Load configuration before updating the existing file. " +
                $"Loading may replace your draft after confirmation. File: {fullPath}");
        return new ConfigurationFileStore(fullPath);
    }

    public void RefreshPorts(IEnumerable<string> registeredNames)
    {
        ArgumentNullException.ThrowIfNull(registeredNames);
        var normalized = new SortedDictionary<int, string>();
        foreach (var name in registeredNames)
        {
            if (name is null || name.Length < 4 ||
                !name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                name[3] is < '1' or > '9' ||
                !name.AsSpan(3).ToString().All(character => character is >= '0' and <= '9') ||
                !int.TryParse(name.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                throw new ArgumentException("Registered port list contains an invalid COM name.", nameof(registeredNames));
            normalized[number] = $"COM{number.ToString(CultureInfo.InvariantCulture)}";
        }
        availablePorts = Array.AsReadOnly(normalized.Values.ToArray());
        var drafts = profiles.Select(profile =>
            (Profile: profile, profile.PhysicalPort, profile.VirtualPort, profile.CommanderPort)).ToArray();
        refreshingPorts = true;
        try
        {
            Notify(nameof(AvailablePorts));
        }
        finally
        {
            // Editable controls can clear bound text when their item source changes.
            foreach (var draft in drafts)
            {
                draft.Profile.PhysicalPort = draft.PhysicalPort;
                draft.Profile.VirtualPort = draft.VirtualPort;
                draft.Profile.CommanderPort = draft.CommanderPort;
            }
            refreshingPorts = false;
        }
        Status = availablePorts.Count == 0
            ? "No registered COM names found. Manual entry remains available; no ports were opened."
            : $"Listed {availablePorts.Count} registered COM names. Identity, availability and com0com pairing are not verified.";
    }

    private void Replace(AppSettings loaded)
    {
        foreach (var profile in profiles) profile.PropertyChanged -= DraftChanged;
        foreach (var mapping in rigProfileMappings) mapping.PropertyChanged -= DraftChanged;
        settings = loaded;
        profiles = loaded.Profiles.Select(p => new ProfileEditor(p)).ToArray();
        foreach (var profile in profiles) profile.PropertyChanged += DraftChanged;
        rigProfileMappings = new ObservableCollection<RigProfileMappingEditor>(
            loaded.RigProfileMappings.Select(mapping =>
            {
                var editor = new RigProfileMappingEditor(mapping);
                editor.PropertyChanged += DraftChanged;
                editor.SetSupportsCivAddress(IsCivMappingProfile(mapping.ProfileId));
                return editor;
            }));
        replacingProfiles = true;
        try { SelectedProfile = profiles.FirstOrDefault(); }
        finally { replacingProfiles = false; }
        isDirty = false;
        Notify(nameof(Profiles));
        Notify(nameof(RigProfileMappingEditors));
        Notify(nameof(RigProfileMappings));
        Notify(nameof(IsDirty));
    }

    private void DraftChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (refreshingPorts || args.PropertyName == nameof(RigProfileMappingEditor.IsRuntimeLocked) ||
            args.PropertyName == nameof(ProfileEditor.IsRunning) ||
            args.PropertyName == nameof(ProfileEditor.IsFaulted)) return;
        if (sender is ProfileEditor)
        {
            foreach (var mapping in rigProfileMappings)
                mapping.SetSupportsCivAddress(IsCivMappingProfile(mapping.ProfileId));
        }
        isDirty = true;
        Notify(nameof(SelectedProfileCanEnableIpc));
        Notify(nameof(RigProfileMappings));
        Notify(nameof(IsDirty));
        Status = "Unsaved offline changes. Save validates configuration; it does not connect any port.";
    }

    private bool IsCivMappingProfile(int profileId) =>
        profiles.FirstOrDefault(profile => profile.Id == profileId)?.Framing == FramingMode.Civ;

    private void Notify(string property) => PropertyChanged?.Invoke(this, new(property));

    private static AppSettings CreateDraft()
    {
        var defaults = AppSettings.CreateDefault();
        return defaults with
        {
            Profiles = defaults.Profiles.Select(p => p with { Adapter = AdapterKind.Passthrough }).ToArray()
        };
    }
}

public sealed class RigProfileMappingEditor : INotifyPropertyChanged
{
    private string modelName;
    private string civAddress;
    private bool supportsCivAddress;
    private bool isRuntimeLocked;

    public RigProfileMappingEditor(ComConRigProfileMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        RigProfileId = mapping.RigProfileId;
        ProfileId = mapping.ProfileId;
        modelName = mapping.RigModelName ?? "";
        civAddress = mapping.CivAddress ?? "";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string RigProfileId { get; }
    public int ProfileId { get; }
    public bool SupportsCivAddress => supportsCivAddress;
    public bool IsRuntimeLocked
    {
        get => isRuntimeLocked;
        internal set
        {
            if (isRuntimeLocked == value) return;
            isRuntimeLocked = value;
            Notify(nameof(IsRuntimeLocked));
        }
    }
    public string CivAddressDisplay => supportsCivAddress ? civAddress : "Not applicable";

    public string ModelName
    {
        get => modelName;
        set
        {
            if (modelName == value) return;
            modelName = value;
            Notify(nameof(ModelName));
        }
    }

    public string CivAddress
    {
        get => civAddress;
        set
        {
            if (civAddress == value) return;
            civAddress = value;
            Notify(nameof(CivAddress));
            Notify(nameof(CivAddressDisplay));
        }
    }

    public ComConRigProfileMapping Export() => new(RigProfileId, ProfileId)
    {
        RigModelName = string.IsNullOrWhiteSpace(ModelName) ? null : ModelName.Trim(),
        CivAddress = string.IsNullOrWhiteSpace(CivAddress) ? null : CivAddress.Trim()
    };

    internal void SetSupportsCivAddress(bool value)
    {
        if (supportsCivAddress == value) return;
        supportsCivAddress = value;
        if (!value && civAddress.Length != 0)
        {
            civAddress = "";
            Notify(nameof(CivAddress));
        }
        Notify(nameof(SupportsCivAddress));
        Notify(nameof(CivAddressDisplay));
    }

    private void Notify(string property) => PropertyChanged?.Invoke(this, new(property));
}
