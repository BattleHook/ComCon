using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Security;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using ComCon.Core.Configuration;
using ComCon.Core.Models;
using ComCon.Core.Transport;
using ComCon.Core.Ipc;
using ComCon.Core.Routing;
using ComCon.Core.Presentation;
using ComCon.UI.ViewModels;
using ComCon.Windows;
using Microsoft.Win32;

namespace ComCon.UI;

public partial class MainWindow : Window
{
    private static readonly Guid ConfigurationPickerId = new("64cf1357-c77f-4c4b-bcaa-93babf73834f");
    private static readonly Guid RigProfilePickerId = new("cb00df6a-9eb8-4e99-bd7c-aa901c6f8160");
    private readonly WindowPositionStore positions = new("main");
    private readonly MainViewModel model = new();
    private readonly DispatcherTimer bridgeStatusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ConfigurationFileStore? configuration;
    private readonly Dictionary<int, string> lastReportedFaults = [];
    private readonly CommandIpcActivityTracker commandIpcActivity = new(TimeSpan.FromSeconds(2));
    private IReadOnlyList<CommandIpcActivity> visibleCommandIpcActivity = [];
    private bool closeInProgress;
    private bool allowClose;
    private bool operationInProgress;
    private bool suppressSelectionOffer;
    private bool updatingCaptureControls;
    private readonly RawCatCapture rawCatCapture = new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    private readonly DevelopmentFaultLog faultLog = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ComCon", "Logging", "ComCon-development-faults.jsonl"));
    private readonly ComConBridgeRuntime runtime;
    private ComConCommandGroupSupervisor? commandSupervisor => runtime.CommandSupervisor;
    private string? lastReportedCommandFault;
    private readonly UiColorPreferences colorPreferences = UiColorPreferences.Load();

    public MainWindow()
    {
        runtime = new(_ => new SerialPortByteBridgeEndpointFactory(), ReportCommandFailure, rawCatCapture);
        InitializeComponent();
        ApplyColorPreferences();
        DataContext = model;
        model.ConfirmProfileSwitch = (_, _) => ResolveSelectionDraft();
        model.PropertyChanged += SelectedProfileChanged;
        SourceInitialized += (_, _) => ExecutePositionOperation(positions.Restore);
        ContentRendered += ShowWelcomeOnce;
        Closing += OnClosing;
        bridgeStatusTimer.Tick += (_, _) => UpdateBridgeFaultStatus();
        rawCatCapture.StatusChanged += RawCatCaptureStatusChanged;
        bridgeStatusTimer.Start();
        LoadInitialConfiguration();
        UpdateBridgeControls();
        UpdateCommandIpcStatus();
        UpdateRawCaptureControls(rawCatCapture.Snapshot);
    }

    private void ShowHelp(object sender, System.Windows.Input.ExecutedRoutedEventArgs args) => ProductLinks.OpenHelp(this);

    private void ShowAbout(object sender, RoutedEventArgs args) => new AboutWindow { Owner = this }.ShowDialog();

    private void ShowWelcomeOnce(object? sender, EventArgs args)
    {
        ContentRendered -= ShowWelcomeOnce;
        var store = FirstUseStore.CreateDefault();
        if (!store.ShouldShowWelcome()) return;
        new WelcomeWindow { Owner = this }.ShowDialog();
        var error = store.MarkWelcomeDismissed(DateTimeOffset.UtcNow);
        if (error is not null)
        {
            MessageBox.Show(this, error, "ComCon - welcome setting not saved",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadInitialConfiguration()
    {
        try
        {
            var configPath = colorPreferences.LastConfigurationFilePath;
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            {
                var defaultPath = Path.Combine(AppContext.BaseDirectory, "comcon.settings.json");
                if (File.Exists(defaultPath))
                {
                    configPath = defaultPath;
                }
            }

            if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
            {
                var store = new ConfigurationFileStore(configPath);
                var settings = store.Load();
                suppressSelectionOffer = true;
                try { model.Load(settings); }
                finally { suppressSelectionOffer = false; }
                configuration = store;
                colorPreferences.LastConfigurationFilePath = configPath;
                colorPreferences.Save();
                model.Status = $"Loaded configuration: {store.FilePath}. No connections started.";
            }
        }
        catch (Exception ex)
        {
            model.Status = $"Could not auto-load configuration: {ex.Message}";
        }
    }

    private void LoadConfiguration(object sender, RoutedEventArgs args)
    {
        if (operationInProgress || runtime.Profiles.Count != 0)
        {
            ShowBridgeError("Stop all bridges before loading another configuration.");
            return;
        }
        if (!CanDiscardDraft()) return;
        var dialog = new OpenFileDialog
        {
            Filter = "ComCon configuration (*.json)|*.json",
            ClientGuid = ConfigurationPickerId,
            DefaultDirectory = AppContext.BaseDirectory,
            InitialDirectory = configuration is null ? "" : Path.GetDirectoryName(configuration.FilePath),
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != true) return;
        ExecuteConfigurationOperation(() =>
        {
            var candidate = new ConfigurationFileStore(dialog.FileName);
            var settings = candidate.Load();
            var next = settings.Profiles.First();
            if (model.SelectedProfile is { } previous &&
                MessageBox.Show(this,
                    $"Load configuration and select profile {next.Id} '{next.Label}'?\n\n" +
                    $"Previous: profile {previous.Id} '{previous.Label}'\n" +
                    $"New mapping: LegacyClient {next.CommanderPort}, ComCon {next.VirtualPort}, radio {next.PhysicalPort}.\n\n" +
                    "Old telemetry readings must clear. New telemetry stays OFF and requires explicit " +
                    "enablement and fresh model-matched data. Live enablement is unavailable. " +
                    "Loading does not start a bridge or change the safety reader.",
                    "Load transceiver profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            suppressSelectionOffer = true;
            try { model.Load(settings); }
            finally { suppressSelectionOffer = false; }
            configuration = candidate;
            colorPreferences.LastConfigurationFilePath = candidate.FilePath;
            colorPreferences.Save();
            model.Status = $"Loaded offline configuration: {candidate.FilePath}. No connections started.";
            UpdateBridgeControls();
            UpdateCommandIpcStatus();
        });
    }

    private void ValidateConfiguration(object sender, RoutedEventArgs args) =>
        ExecuteConfigurationOperation(() =>
        {
            model.Export();
            model.Status = "Offline configuration is valid. Port existence, com0com pairing and hardware compatibility are not verified.";
        });

    private void SaveConfiguration(object sender, RoutedEventArgs args) => SaveDraft();

    private void ImportClientRigProfile(object sender, RoutedEventArgs args)
    {
        var profile = model.SelectedProfile;
        if (profile is null)
        {
            ShowBridgeError("Select a ComCon profile before importing a client rig mapping.");
            return;
        }
        var dialog = new OpenFileDialog
        {
            Filter = "Client rig-profile JSON (*.json)|*.json",
            ClientGuid = RigProfilePickerId,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = ClientRigProfileJson.Load(dialog.FileName, profile.Id);
            model.ValidateClientRigImport(imported);
            if (MessageBox.Show(this,
                    $"Import this client rig mapping?\n\n{MainViewModel.FormatRigProfileMapping(imported.Mapping, profile.Label)}\n\n" +
                    $"Source: {dialog.FileName}\n\n" +
                    "The client keeps the same UUID. Its JSON will not be modified. Other mappings remain intact; " +
                    "an existing mapping for this UUID/profile has its descriptions refreshed.\n\n" +
                    "This changes only the offline draft. Use Save configuration to persist it. No ports will be opened.",
                    "Confirm client rig mapping", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            model.ImportClientRigProfile(imported);
            model.Status = "Client rig mapping imported into the offline draft. Save configuration to persist it; client JSON unchanged.";
            UpdateCommandIpcStatus();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or SecurityException or
            JsonException or ConfigurationValidationException or InvalidOperationException)
        {
            model.Status = $"Client rig-profile import failed: {exception.Message}";
            MessageBox.Show(this, model.Status, "Client rig-profile import", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool SaveDraft()
    {
        if (operationInProgress && !closeInProgress) return false;
        var dialog = new SaveFileDialog
        {
            Filter = "ComCon configuration (*.json)|*.json",
            ClientGuid = ConfigurationPickerId,
            DefaultDirectory = AppContext.BaseDirectory,
            InitialDirectory = configuration is null ? "" : Path.GetDirectoryName(configuration.FilePath),
            RestoreDirectory = true,
            FileName = configuration?.FilePath ?? "comcon.settings.json",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true) return false;
        return ExecuteConfigurationOperation(() =>
        {
            var candidate = MainViewModel.SelectSaveStore(dialog.FileName, configuration);
            var settings = model.Export();
            candidate.Save(settings);
            configuration = candidate;
            colorPreferences.LastConfigurationFilePath = candidate.FilePath;
            colorPreferences.Save();
            model.Saved(settings);
            model.Status = $"Saved offline configuration: {candidate.FilePath}. No live settings changed.";
        });
    }

    private bool CanDiscardDraft() => !model.IsDirty ||
        MessageBox.Show(this, "Discard unsaved offline configuration changes?", "ComCon configuration",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private bool ResolveSelectionDraft()
    {
        if (operationInProgress || closeInProgress) return false;
        if (!model.IsDirty) return true;
        var choice = MessageBox.Show(this, "Save configuration changes before selecting another profile?\n\n" +
            "Yes: Save; No: discard the unsaved draft; Cancel: keep the current selection.",
            "Unsaved profile changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes) return SaveDraft();
        suppressSelectionOffer = true;
        try { model.DiscardDraft(); }
        finally { suppressSelectionOffer = false; }
        return true;
    }

    private async void SelectedProfileChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.SelectedProfile) || suppressSelectionOffer) return;
        UpdateBridgeControls();
        var id = model.SelectedProfile?.Id;
        if (id is null || configuration is null || operationInProgress || closeInProgress || model.IsDirty) return;
        var profile = model.SavedSettings.Profiles.Single(item => item.Id == id);
        if (!profile.Enabled || !profile.AutoConnect || !profile.CommandIpcEnabled ||
            runtime.Profiles.Any(item => item.ProfileId == id)) return;
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (model.SelectedProfile?.Id == id && !operationInProgress && !closeInProgress)
            await ShowStartListAsync(true);
    }

    private void RefreshPorts(object sender, RoutedEventArgs args)
    {
        try
        {
            model.RefreshPorts(RegisteredPortReader.Read());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            SecurityException or ArgumentException)
        {
            model.Status = $"Port refresh failed: {exception.Message}";
            MessageBox.Show(this, exception.Message, "ComCon registered ports",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StartBridge(object sender, RoutedEventArgs args)
    {
        if (operationInProgress || closeInProgress) return;
        if (configuration is null || model.IsDirty)
        {
            ShowBridgeError("Load a saved configuration and save or discard edits before starting.");
            return;
        }
        var selected = model.SavedSettings.Profiles.SingleOrDefault(item => item.Id == model.SelectedProfile?.Id);
        if (selected is null || !selected.Enabled)
        {
            ShowBridgeError("Select an enabled saved profile before starting.");
            return;
        }
        var current = runtime.Profiles.SingleOrDefault(item => item.ProfileId == selected.Id);
        if (current is null)
        {
            await ShowStartListAsync(false);
            return;
        }
        if (!current.IsRaw || !selected.CommandIpcEnabled)
        {
            ShowBridgeError("This profile is already active. Stop it before restarting.");
            return;
        }
        if (commandSupervisor is { IpcAvailable: false, IpcFault: not null } unavailable)
        {
            ShowBridgeError($"Recover IPC service before replacing RAW: {unavailable.IpcFault}. RAW remains running.");
            return;
        }
        if (MessageBox.Show(this,
                $"Replace only profile {selected.Id} '{selected.Label}' from RAW to IPC?\n\n" +
                $"{current.ActiveConfiguration.CommanderPort} <-> {current.ActiveConfiguration.VirtualPort} " +
                $"<-> {current.ActiveConfiguration.PhysicalPort}\n\n" +
                "The RAW bridge must close successfully before IPC starts. Other profiles remain untouched. " +
                "A RAW transfer can have an unknown outcome. If IPC fails, this profile stays stopped/faulted; " +
                "the saved IPC setting remains enabled and RAW is not restarted automatically.",
                "Confirm RAW to IPC", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        operationInProgress = true;
        UpdateBridgeControls();
        try
        {
            await runtime.StopProfileAsync(selected.Id, "Confirmed RAW to IPC replacement.");
            await runtime.StartProfileAsync(model.SavedSettings, selected.Id);
            model.Status = $"Profile {selected.Id} now runs IPC; other bridges unchanged.";
        }
        catch (Exception exception) when (IsBridgeFailure(exception)) { ShowBridgeError(exception.Message); }
        finally { operationInProgress = false; UpdateBridgeControls(); UpdateCommandIpcStatus(); }
    }

    private async void StartList(object sender, RoutedEventArgs args) => await ShowStartListAsync(false);

    private IReadOnlyList<BridgeStartRow> CreateStartRows(bool autoconnectOnly) =>
        BridgeStartSelection.Create(model.SavedSettings, runtime.Profiles.Select(profile =>
            new BridgeActiveProfile(profile.ActiveConfiguration, profile.IsRaw, profile.ActualState,
                profile.Fault, profile.IpcAvailable,
                visibleCommandIpcActivity.Any(session => session.ProfileId == profile.ProfileId))),
            model.SelectedProfile?.Id, autoconnectOnly, ComConCommandPipeNames.Production().CatPipeName,
            commandSupervisor?.IpcFault);

    private async Task ShowStartListAsync(bool autoconnectOnly)
    {
        if (operationInProgress || closeInProgress) return;
        if (configuration is null || model.IsDirty)
        {
            ShowBridgeError("Load a saved configuration and save or discard edits before starting.");
            return;
        }
        operationInProgress = true;
        UpdateBridgeControls();
        try
        {
            var dialog = new BridgeStartDialog(model.SavedSettings, CreateStartRows(autoconnectOnly)) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            var succeeded = new List<int>();
            var failed = new List<string>();
            foreach (var id in dialog.SelectedProfileIds)
            {
                try
                {
                    await runtime.StartProfileAsync(model.SavedSettings, id);
                    succeeded.Add(id);
                }
                catch (Exception exception) when (IsBridgeFailure(exception))
                {
                    LogDevelopmentFault("startProfile", $"Profile {id}: {exception.Message}");
                    failed.Add($"Profile {id}: {exception.Message}");
                }
                UpdateBridgeControls();
            }
            model.Status = $"Started profiles: {(succeeded.Count == 0 ? "(none)" : string.Join(", ", succeeded))}. " +
                $"Failed: {failed.Count}. Existing bridges unchanged; no automatic retry.";
            if (failed.Count != 0) ShowBridgeError(string.Join(Environment.NewLine, failed));
        }
        finally { operationInProgress = false; UpdateBridgeControls(); UpdateCommandIpcStatus(); }
    }

    private async void StopBridge(object sender, RoutedEventArgs args)
    {
        if (operationInProgress || closeInProgress) return;
        var profile = runtime.Profiles.SingleOrDefault(item => item.ProfileId == model.SelectedProfile?.Id);
        if (profile is null || !ConfirmStop([profile])) return;
        operationInProgress = true;
        UpdateBridgeControls();
        try
        {
            await runtime.StopProfileAsync(profile.ProfileId, "Operator stopped selected profile.");
            model.Status = $"Profile {profile.ProfileId} stopped. Other bridges and their client sessions remain unchanged.";
        }
        catch (Exception exception) when (IsBridgeFailure(exception)) { ShowBridgeError(exception.Message); }
        finally { operationInProgress = false; UpdateBridgeControls(); UpdateCommandIpcStatus(); }
    }

    private async void StopAll(object sender, RoutedEventArgs args)
    {
        if (operationInProgress || closeInProgress || runtime.Profiles.Count == 0 ||
            !ConfirmStop(runtime.Profiles)) return;
        operationInProgress = true;
        UpdateBridgeControls();
        try
        {
            var failures = await runtime.StopAllAsync("Operator requested Stop all.");
            model.Status = failures.Count == 0 ? "All bridges stopped." :
                $"Stop all completed with {failures.Count} failure(s); uncertain ports remain reserved.";
            if (failures.Count != 0) ShowBridgeError(string.Join(Environment.NewLine, failures));
        }
        finally { operationInProgress = false; UpdateBridgeControls(); UpdateCommandIpcStatus(); }
    }

    private async void RecoverLegacy(object sender, RoutedEventArgs args)
    {
        if (operationInProgress || closeInProgress ||
            sender is not System.Windows.Controls.Button { Tag: int profileId } ||
            commandSupervisor is not { } supervisor)
            return;

        var profile = supervisor.Profiles.SingleOrDefault(item => item.ProfileId == profileId);
        if (profile is null || !profile.CanRecoverLegacy) return;
        if (MessageBox.Show(this,
                $"Recover Legacy for profile {profile.ProfileId} '{profile.Label}'?\n\n" +
                $"First close the Legacy client for {profile.Label}. " +
                "ComCon will reopen only this profile's Legacy virtual endpoint and discard its " +
                "startup backlog. The shared radio and IPC service will remain running. Wait for Ready, " +
                "then manually start the Legacy client and verify a fresh radio response. No client is restarted automatically.",
                "Recover Legacy connection", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        operationInProgress = true;
        UpdateBridgeControls();
        try
        {
            await supervisor.RecoverLegacyAsync(profileId);
            model.Status = $"Legacy recovery completed for profile {profileId}. Status is Ready; " +
                "manually start the Legacy client after confirming the client is closed.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            AggregateException or InvalidOperationException or TimeoutException or
            System.ComponentModel.Win32Exception or ArgumentOutOfRangeException)
        {
            ShowBridgeError($"Legacy recovery failed for profile {profileId}: {exception.Message}");
        }
        finally { operationInProgress = false; UpdateBridgeControls(); UpdateCommandIpcStatus(); }
    }

    private bool ConfirmStop(IReadOnlyList<ComConBridgeProfileStatus> profiles) =>
        MessageBox.Show(this,
            "Stop these bridges only?\n\n" + string.Join(Environment.NewLine, profiles.Select(profile =>
                $"Profile {profile.ProfileId} '{profile.ActiveConfiguration.Label}' / {profile.ActualState}: " +
                $"{profile.ActiveConfiguration.CommanderPort} <-> {profile.ActiveConfiguration.VirtualPort} " +
                $"<-> {profile.ActiveConfiguration.PhysicalPort}")) +
            "\n\nRAW transfers may have an unknown outcome; IPC outcomes will be finalized. " +
            "Failures retain uncertain port reservations and will be reported. No automatic restart or resynchronization.",
            "Confirm bridge stop", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private async void RecoverIpcService(object sender, RoutedEventArgs args)
    {
        if (operationInProgress || closeInProgress || commandSupervisor is not { } supervisor) return;
        if (MessageBox.Show(this,
                "Recover the unavailable shared IPC service?\n\nOld client sessions will be closed before " +
                "fresh sessions can attach. Healthy Legacy/radio bridges remain running. No command is replayed " +
                "and clients must reconnect explicitly.",
                "Recover IPC service", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        operationInProgress = true;
        UpdateBridgeControls();
        try
        {
            await supervisor.RecoverIpcServiceAsync();
            model.Status = "IPC service recovered. Healthy Legacy bridges were not restarted; clients can reconnect.";
        }
        catch (Exception exception) when (IsBridgeFailure(exception)) { ShowBridgeError(exception.Message); }
        finally { operationInProgress = false; UpdateBridgeControls(); UpdateCommandIpcStatus(); }
    }

    private static bool IsBridgeFailure(Exception exception) => exception is IOException or
        UnauthorizedAccessException or ConfigurationValidationException or AggregateException or
        ArgumentException or InvalidOperationException or TimeoutException or
        System.ComponentModel.Win32Exception or SecurityException;

    private void UpdateBridgeFaultStatus()
    {
        UpdateBridgeControls();
        UpdateCommandIpcStatus();
        var commandFault = commandSupervisor?.Faults.LastOrDefault();
        if (commandFault is not null && commandFault != lastReportedCommandFault)
        {
            lastReportedCommandFault = commandFault;
            LogDevelopmentFault("commandSupervisor", commandFault);
        }
        var faulted = runtime.Profiles.Where(item => item.IsRaw && item.Fault is not null).ToArray();
        foreach (var id in lastReportedFaults.Keys.Except(faulted.Select(item => item.ProfileId)).ToArray())
            lastReportedFaults.Remove(id);
        foreach (var profile in faulted)
        {
            if (lastReportedFaults.TryGetValue(profile.ProfileId, out var previous) && previous == profile.Fault) continue;
            lastReportedFaults[profile.ProfileId] = profile.Fault!;
            ShowBridgeError($"Profile {profile.ProfileId} faulted; no automatic restart. " +
                $"An in-flight RAW command may have an unknown outcome. {profile.Fault}");
        }
    }

    private void UpdateBridgeControls()
    {
        runtime.Refresh();
        var profiles = runtime.Profiles;
        visibleCommandIpcActivity = commandIpcActivity.Observe(
            commandSupervisor?.ActiveSessions.Select(session =>
                new CommandIpcActivity(session.ProfileId, session.RigProfileId)) ?? [],
            profiles.Where(profile => !profile.IsRaw).Select(profile => profile.ProfileId),
            DateTimeOffset.UtcNow);
        model.SetActiveProfiles(profiles.Select(item => item.ActiveConfiguration),
            profiles.Where(item => item.IsRaw).Select(item => item.ProfileId));
        model.UpdateProfileRuntimeStates(profiles.Select(item => (
            item.ProfileId,
            IsRunning: item.Fault is null && (string.Equals(item.State, "Running", StringComparison.OrdinalIgnoreCase) || item.State.StartsWith("Running", StringComparison.OrdinalIgnoreCase)),
            IsFaulted: item.Fault is not null || string.Equals(item.State, "Faulted", StringComparison.OrdinalIgnoreCase)
        )));
        UpdateRawCaptureControls(rawCatCapture.Snapshot);
        var idle = !operationInProgress && !closeInProgress;
        var selected = profiles.SingleOrDefault(item => item.ProfileId == model.SelectedProfile?.Id);
        LoadConfigurationButton.IsEnabled = idle && profiles.Count == 0;
        ValidateConfigurationButton.IsEnabled = idle;
        SaveConfigurationButton.IsEnabled = idle;
        RefreshPortsButton.IsEnabled = idle;
        ProfilesGrid.IsEnabled = idle;
        ProfileEditorPanel.IsEnabled = idle;
        StartBridgeButton.IsEnabled = idle && model.SelectedProfile?.Enabled == true &&
            (selected is null || (selected.IsRaw && model.SelectedProfile.CommandIpcEnabled));
        StartListButton.IsEnabled = idle && configuration is not null;
        StopBridgeButton.IsEnabled = idle && selected is not null;
        StopAllButton.IsEnabled = idle && profiles.Count != 0;
        RecoverIpcButton.IsEnabled = idle && commandSupervisor is { IpcAvailable: false, IpcFault: not null };
        LegacyRecoveryProfilesList.IsEnabled = idle;
        BridgeOverview.ItemsSource = CreateStartRows(false);
        if (ProfilesGrid.ItemsSource != null) ProfilesGrid.Items.Refresh();
    }

    private void ApplyColorPreferences()
    {
        var runningColor = UiColorPreferences.ParseColor(colorPreferences.RunningColorHex, Color.FromRgb(200, 230, 201));
        var runningTextColor = UiColorPreferences.ParseColor(colorPreferences.RunningTextColorHex, Colors.Black);
        var faultColor = UiColorPreferences.ParseColor(colorPreferences.FaultColorHex, Color.FromRgb(255, 205, 210));
        var faultTextColor = UiColorPreferences.ParseColor(colorPreferences.FaultTextColorHex, Colors.Black);

        Application.Current.Resources["RunningRowBackgroundBrush"] = new SolidColorBrush(runningColor);
        Application.Current.Resources["RunningRowForegroundBrush"] = new SolidColorBrush(runningTextColor);
        Application.Current.Resources["RunningSelectedRowBackgroundBrush"] = new SolidColorBrush(runningColor);
        Application.Current.Resources["FaultRowBackgroundBrush"] = new SolidColorBrush(faultColor);
        Application.Current.Resources["FaultRowForegroundBrush"] = new SolidColorBrush(faultTextColor);
        Application.Current.Resources["FaultSelectedRowBackgroundBrush"] = new SolidColorBrush(faultColor);

        if (ProfilesGrid?.ItemsSource != null) ProfilesGrid.Items.Refresh();
        if (BridgeOverview?.ItemsSource != null) BridgeOverview.Items.Refresh();
    }

    private void OpenStylingSettings(object sender, RoutedEventArgs args)
    {
        var dialog = new StylingSettingsDialog(
            colorPreferences.RunningColorHex, colorPreferences.RunningTextColorHex,
            colorPreferences.FaultColorHex, colorPreferences.FaultTextColorHex)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            colorPreferences.RunningColorHex = dialog.RunningColorHex;
            colorPreferences.RunningTextColorHex = dialog.RunningTextColorHex;
            colorPreferences.FaultColorHex = dialog.FaultColorHex;
            colorPreferences.FaultTextColorHex = dialog.FaultTextColorHex;
            colorPreferences.Save();
            ApplyColorPreferences();
        }
    }

    private void UpdateCommandIpcStatus()
    {
        var lines = new List<string>();
        var optedIn = model.Profiles.Where(profile => profile.Enabled && profile.CommandIpcEnabled).ToArray();
        lines.Add(commandSupervisor is { IpcAvailable: false, IpcFault: not null } unavailable
            ? $"Command IPC: UNAVAILABLE — {unavailable.IpcFault}. Healthy Legacy paths remain running; manual recovery required."
            : commandSupervisor is null || commandSupervisor.Profiles.Count == 0
            ? optedIn.Length == 0
                ? "Command IPC: stopped; no enabled profiles opted in."
                : "Command IPC: stopped; opted-in profiles are available for manual start."
            : commandSupervisor.IpcAvailable
                ? $"Command IPC: available; shared CAT pipe {commandSupervisor.CatPipeName}."
                : $"Command IPC: UNAVAILABLE — {commandSupervisor.IpcFault}. Healthy Legacy paths remain running; manual recovery required.");
        var sessionLines = new List<string>();
        sessionLines.Add("Mappings:");
        var mappings = commandSupervisor?.Mappings ?? model.RigProfileMappings;
        sessionLines.AddRange(mappings.Select(mapping =>
        {
            var profile = model.Profiles.FirstOrDefault(item => item.Id == mapping.ProfileId);
            return $"  {MainViewModel.FormatRigProfileMapping(mapping, profile?.Label)}";
        }));
        if (mappings.Count == 0) sessionLines.Add("  (none)");
        sessionLines.Add("Hosts:");
        if (commandSupervisor is null || commandSupervisor.Profiles.Count == 0)
            sessionLines.Add("  (no active profile hosts)");
        else
            sessionLines.AddRange(commandSupervisor.Profiles.Select(profile =>
                $"  Profile {profile.ProfileId} '{profile.Label}': {profile.State}" +
                $"; Legacy {profile.LegacyStatus.State}" +
                (profile.LegacyStatus.Reason is { Length: > 0 } reason ? $" — {reason}" : string.Empty) +
                $"; control pipe {commandSupervisor.ControlPipeName(profile.ProfileId)}" +
                (profile.Fault is null ? string.Empty : $" — {profile.Fault}")));
        LegacyRecoveryProfilesList.ItemsSource = commandSupervisor?.Profiles;

        sessionLines.Add("Sessions:");
        var sessions = visibleCommandIpcActivity;
        if (sessions.Count == 0) sessionLines.Add("  (none)");
        else
            sessionLines.AddRange(sessions.Select(session =>
                $"  Profile {session.ProfileId}, {session.RigProfileId}: Connected / polling"));
        var faults = commandSupervisor?.Faults ?? [];
        sessionLines.Add("Faults:");
        if (faults.Count == 0) sessionLines.Add("  (none)");
        else sessionLines.AddRange(faults.Select(fault => $"  {fault}"));
        model.SetCommandIpcStatus(string.Join(Environment.NewLine, lines), string.Join(Environment.NewLine, sessionLines));
    }

    private void ReportCommandFailure(int profileId, Exception exception)
    {
        var message = profileId is < 1 or > 4
            ? exception.Message
            : $"Profile {profileId}: {exception.Message}";
        _ = Dispatcher.BeginInvoke(() => LogDevelopmentFault("commandSupervisor", message));
    }

    private void RawCaptureChanged(object sender, RoutedEventArgs args)
    {
        if (updatingCaptureControls) return;
        try
        {
            rawCatCapture.SetEnabled(RawCaptureCheckBox.IsChecked == true);
            UpdateRawCaptureControls(rawCatCapture.Snapshot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            SecurityException or InvalidOperationException)
        {
            model.Status = $"Raw CAT capture setting failed: {exception.Message}. Bridge forwarding is unchanged.";
            MessageBox.Show(this, model.Status, "ComCon raw CAT capture",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RawCatCaptureStatusChanged(object? sender, RawCatCaptureSnapshot snapshot)
    {
        if (Dispatcher.CheckAccess())
            UpdateRawCaptureControls(snapshot);
        else
            Dispatcher.BeginInvoke(() => UpdateRawCaptureControls(snapshot));
    }

    private void UpdateRawCaptureControls(RawCatCaptureSnapshot snapshot)
    {
        updatingCaptureControls = true;
        try
        {
            RawCaptureCheckBox.IsChecked = snapshot.Enabled;
            var status = snapshot.Status switch
            {
                RawCatCaptureStatus.Armed => "Armed while stopped",
                RawCatCaptureStatus.Capturing => "Capturing",
                RawCatCaptureStatus.Error => "Error",
                _ => "Off"
            };
            RawCaptureStatusText.Text =
                $"{status} — {snapshot.LogPath} ({snapshot.LogSizeBytes} active bytes; rolling: " +
                $"{RawCatCapture.MaximumLogFiles} files maximum, 1 MiB each)" +
                (snapshot.Error is null ? string.Empty : $"\n{snapshot.Error}");
        }
        finally { updatingCaptureControls = false; }
    }

    private void ShowBridgeError(string message)
    {
        LogDevelopmentFault("bridgeOperation", message);
        model.Status = $"Bridge operation failed: {message}";
        MessageBox.Show(this, message, "ComCon bridge",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void LogDevelopmentFault(string component, string message)
    {
        try { faultLog.Append(component, model.SelectedProfile?.Id, message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            model.Status = $"Development fault log failed: {exception.Message}. Original fault: {message}";
            MessageBox.Show(this, model.Status, "ComCon fault logging",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (allowClose)
        {
            bridgeStatusTimer.Stop();
            ExecutePositionOperation(positions.Save);
            rawCatCapture.StatusChanged -= RawCatCaptureStatusChanged;
            await rawCatCapture.DisposeAsync();
            return;
        }

        args.Cancel = true;
        if (closeInProgress || operationInProgress) return;

        if (model.IsDirty)
        {
            var choice = MessageBox.Show(this, "Save offline configuration changes before closing?",
                "ComCon configuration", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Cancel || (choice == MessageBoxResult.Yes && !SaveDraft()))
                return;
        }

        if (runtime.Profiles.Count != 0 && !ConfirmStop(runtime.Profiles)) return;
        closeInProgress = true;
        UpdateBridgeControls();
        try
        {
            // StopAsync can complete synchronously; let the cancelled Closing event unwind first.
            await Dispatcher.Yield(DispatcherPriority.Background);
            await runtime.DisposeAsync();
            allowClose = true;
            Close();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            AggregateException or InvalidOperationException or TimeoutException or
            System.ComponentModel.Win32Exception)
        {
            closeInProgress = false;
            ShowBridgeError(exception.Message);
            UpdateBridgeControls();
        }
    }

    private bool ExecuteConfigurationOperation(Action operation)
    {
        try
        {
            operation();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ConfigurationValidationException or ConfigurationSerializationException or
            ConfigurationConcurrencyException or ConfigurationSizeLimitExceededException or
            AggregateException or ArgumentException or InvalidOperationException)
        {
            model.Status = exception is ConfigurationValidationException validationEx
                ? $"Configuration validation failed with {validationEx.Errors.Count} error(s). See dialog for details."
                : $"Configuration operation failed: {exception.Message}";
            MessageBox.Show(this, exception.Message, "ComCon configuration",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void ExecutePositionOperation(Action<nint> operation)
    {
        try
        {
            operation(new WindowInteropHelper(this).Handle);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or Win32Exception)
        {
            MessageBox.Show(this, exception.Message, "ComCon window position",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}