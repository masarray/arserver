using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Data;
using Ari61850Bridge.Models;
using Ari61850Bridge.Services;
using Microsoft.Win32;

namespace Ari61850Bridge;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private IIec61850Client? _iecClient;
    private string _iecClientEndpointKey = "";
    private BridgeRuntime? _runtime;
    private BindingItem? _selectedBinding;
    private RelayEndpointView? _selectedRelay;
    private string _projectName = "ArServer Project";
    private string _relayIpAddress = "192.168.1.10";
    private int _mmsPort = 102;
    private string _modbusBindAddress = "0.0.0.0";
    private int _modbusPort = 502;
    private int _modbusUnitId = 1;
    private int _mmsPollingIntervalMs = BridgeRuntime.DefaultMmsPollingIntervalMs;
    private bool _fastAcquisitionEnabled = true;
    private bool _enableModbusTcp = true;
    private MqttGatewaySettings _mqttSettings = new();
    private string _iedConnectionStatus = "Disconnected";
    private string _runtimeStatusText = "Stopped";
    private string _lastStatusLevel = "INFO";
    private string _lastStatusText = "Ready. Connect relay or load an existing project.";
    private string _eventStrategyStatus = "Not scanned";
    private int _modbusClientCount;
    private long _modbusReadCount;
    private string _modbusLastClientText = "-";
    private long _mqttPublishedCount;
    private long _mqttDroppedCount;
    private bool _mqttConnected;
    private bool _useRealIecEngine;
    private bool _useNativeCleanRoomEngine;
    private string _discoverySearchText = "";
    private bool _showRawEngineeringAttributes;
    private long _lastObservedModbusReadCount;
    private DateTime _lastActivityPulse = DateTime.MinValue;
    private bool _openConfigWizardAfterDiscovery;
    private Window? _activeWizardWindow;
    private bool _navigatingToDiagnosticsForException;
    private readonly List<System.Windows.Controls.Button> _navButtons = new();
    private readonly DispatcherTimer _activityResetTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
    private readonly DispatcherTimer _runtimeSnapshotTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly ConcurrentDictionary<string, RuntimeValueSnapshot> _pendingRuntimeSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastIecActivityAt = DateTime.MinValue;
    private DateTime _lastModbusActivityAt = DateTime.MinValue;

    public ObservableCollection<SignalDefinition> Signals { get; } = new();
    public ObservableCollection<BindingItem> Bindings { get; } = new(); // Legacy/mirror collection. Do not bind Modbus grid/runtime directly to relay workspace state.
    public ObservableCollection<BindingItem> PublishedModbusBindings { get; } = new();
    public ObservableCollection<DiagnosticEntry> Logs { get; } = new();
    public ObservableCollection<RelayEndpointView> Relays { get; } = new();
    public ObservableCollection<string> RecentRelayIps { get; } = new();
    public ICollectionView SignalsView { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ProjectName { get => _projectName; set => Set(ref _projectName, value); }
    public string RelayIpAddress { get => _relayIpAddress; set => Set(ref _relayIpAddress, value); }
    public int MmsPort { get => _mmsPort; set => Set(ref _mmsPort, value); }
    public string ModbusBindAddress { get => _modbusBindAddress; set { if (Set(ref _modbusBindAddress, value)) Raise(nameof(ModbusEndpointText)); } }
    public int ModbusPort { get => _modbusPort; set { if (Set(ref _modbusPort, value)) Raise(nameof(ModbusEndpointText)); } }
    public int ModbusUnitId { get => _modbusUnitId; set { if (Set(ref _modbusUnitId, value)) Raise(nameof(ModbusEndpointText)); } }
    public int MmsPollingIntervalMs
    {
        get => _mmsPollingIntervalMs;
        set
        {
            if (Set(ref _mmsPollingIntervalMs, value))
            {
                Raise(nameof(MmsPollingText));
                Raise(nameof(MmsPollingHintText));
                Raise(nameof(FastAcquisitionText));
            }
        }
    }
    public bool FastAcquisitionEnabled
    {
        get => _fastAcquisitionEnabled;
        set
        {
            if (Set(ref _fastAcquisitionEnabled, value))
            {
                Raise(nameof(FastAcquisitionText));
                Raise(nameof(MmsPollingHintText));
            }
        }
    }
    public bool EnableModbusTcp
    {
        get => _enableModbusTcp;
        set
        {
            if (Set(ref _enableModbusTcp, value))
            {
                Raise(nameof(ModbusEndpointText));
                Raise(nameof(RuntimeInsightText));
            }
        }
    }
    public MqttGatewaySettings MqttSettings
    {
        get => _mqttSettings;
        set
        {
            value ??= new MqttGatewaySettings();
            if (ReferenceEquals(_mqttSettings, value))
                return;

            _mqttSettings.PropertyChanged -= MqttSettings_PropertyChanged;
            _mqttSettings = value;
            _mqttSettings.PropertyChanged += MqttSettings_PropertyChanged;
            Raise(nameof(MqttSettings));
            Raise(nameof(MqttEndpointText));
            Raise(nameof(MqttTrafficText));
            Raise(nameof(RuntimeInsightText));
        }
    }
    public string IedConnectionStatus
    {
        get => _iedConnectionStatus;
        set
        {
            if (Set(ref _iedConnectionStatus, value))
                Raise(nameof(IecInsightText));
        }
    }

    public string RuntimeStatusText
    {
        get => _runtimeStatusText;
        set
        {
            if (Set(ref _runtimeStatusText, value))
            {
                Raise(nameof(RuntimeInsightText));
                Raise(nameof(IsRuntimeRunning));
                Raise(nameof(IsProjectEditable));
            }
        }
    }
    public string LastStatusLevel { get => _lastStatusLevel; set => Set(ref _lastStatusLevel, value); }
    public string LastStatusText { get => _lastStatusText; set => Set(ref _lastStatusText, value); }
    public string EventStrategyStatus { get => _eventStrategyStatus; set => Set(ref _eventStrategyStatus, value); }
    public int SignalCount => Signals.Count;
    public int BindingCount => PublishedModbusBindings.Count;
    public int ModbusClientCount
    {
        get => _modbusClientCount;
        set
        {
            if (Set(ref _modbusClientCount, value))
                Raise(nameof(ModbusTrafficText));
        }
    }

    public long ModbusReadCount
    {
        get => _modbusReadCount;
        set
        {
            if (Set(ref _modbusReadCount, value))
                Raise(nameof(ModbusTrafficText));
        }
    }

    public string ModbusLastClientText { get => _modbusLastClientText; set => Set(ref _modbusLastClientText, value); }
    public long MqttPublishedCount
    {
        get => _mqttPublishedCount;
        set
        {
            if (Set(ref _mqttPublishedCount, value))
                Raise(nameof(MqttTrafficText));
        }
    }
    public long MqttDroppedCount
    {
        get => _mqttDroppedCount;
        set
        {
            if (Set(ref _mqttDroppedCount, value))
                Raise(nameof(MqttTrafficText));
        }
    }
    public bool MqttConnected
    {
        get => _mqttConnected;
        set
        {
            if (Set(ref _mqttConnected, value))
            {
                Raise(nameof(MqttEndpointText));
                Raise(nameof(MqttTrafficText));
            }
        }
    }
    public bool UseRealIecEngine { get => _useRealIecEngine; set => Set(ref _useRealIecEngine, value); }
    public bool UseNativeCleanRoomEngine { get => _useNativeCleanRoomEngine; set => Set(ref _useNativeCleanRoomEngine, value); }
    public string DiscoverySearchText
    {
        get => _discoverySearchText;
        set
        {
            if (Set(ref _discoverySearchText, value))
            {
                SignalsView.Refresh();
                Raise(nameof(VisibleSignalCountText));
            }
        }
    }

    public bool ShowRawEngineeringAttributes
    {
        get => _showRawEngineeringAttributes;
        set
        {
            if (Set(ref _showRawEngineeringAttributes, value))
            {
                SignalsView.Refresh();
                Raise(nameof(VisibleSignalCountText));
            }
        }
    }

    public string VisibleSignalCountText => ShowRawEngineeringAttributes
        ? $"Showing {SignalsView.Cast<object>().Count()} of {Signals.Count} discovered MMS attributes"
        : $"Showing {SignalsView.Cast<object>().Count()} smart SCADA signals of {Signals.Count} discovered attributes";
    public string ModbusEndpointText => EnableModbusTcp ? $"{ModbusBindAddress}:{ModbusPort} / UID {ModbusUnitId}" : "Modbus disabled";
    public string MqttEndpointText => MqttSettings.IsEnabled ? $"{MqttSettings.BrokerHost}:{MqttSettings.BrokerPort} / {MqttSettings.TopicRoot}" : "MQTT disabled";
    public string MmsPollingText => $"MMS {BridgeRuntime.NormalizeMmsPollingIntervalMs(MmsPollingIntervalMs)} ms";
    public string FastAcquisitionText => FastAcquisitionEnabled
        ? $"Fast CB ON / {BridgeRuntime.NormalizeMmsPollingIntervalMs(MmsPollingIntervalMs)} ms target"
        : "Fast CB OFF";
    public string MmsPollingHintText => FastAcquisitionEnabled
        ? "Fast CB lane prioritizes breaker/status points before measurements. Use RCB/GOOSE for protection-event evidence when available."
        : BridgeRuntime.NormalizeMmsPollingIntervalMs(MmsPollingIntervalMs) <= 50
            ? "Fast polling: benchmark/monitoring mode, not protection-event substitute."
            : "Runtime reads IEC MMS into cache; Modbus/MQTT clients read cached values.";
    public string RuntimeInsightText => RuntimeStatusText == "Running" ? $"Publishing {ActiveOutputBindingCount} routed binding(s) via {RuntimeOutputText}" : $"Ready: {RuntimeOutputText}";
    public string IecInsightText => $"{IedConnectionStatus} / {SignalCount} discovered";
    public string ModbusTrafficText => $"{ModbusClientCount} client(s), {ModbusReadCount} read(s)";
    public string MqttTrafficText => MqttSettings.IsEnabled ? $"{(MqttConnected ? "connected" : "offline")}, {MqttPublishedCount} pub(s), {MqttDroppedCount} drop(s)" : "disabled";
    public bool IsRuntimeRunning => RuntimeStatusText.Equals("Running", StringComparison.OrdinalIgnoreCase) || RuntimeStatusText.Equals("Starting", StringComparison.OrdinalIgnoreCase);
    public bool IsProjectEditable => !IsRuntimeRunning;
    private string RuntimeOutputText => (EnableModbusTcp, MqttSettings.IsEnabled) switch
    {
        (true, true) => "Modbus + MQTT",
        (true, false) => "Modbus",
        (false, true) => "MQTT",
        _ => "no output"
    };
    private int ActiveOutputBindingCount => PublishedModbusBindings.Count(IsRoutedBinding);
    private int ModbusBindingCount => PublishedModbusBindings.Count(b => b.IsEnabled && b.PublishToModbus);
    private int MqttBindingCount => PublishedModbusBindings.Count(b => b.IsEnabled && b.PublishToMqtt);
    private static bool IsRoutedBinding(BindingItem binding) => binding.IsEnabled && (binding.PublishToModbus || binding.PublishToMqtt);

    public BindingItem? SelectedBinding
    {
        get => _selectedBinding;
        set => Set(ref _selectedBinding, value);
    }


    public RelayEndpointView? SelectedRelay
    {
        get => _selectedRelay;
        set
        {
            if (Set(ref _selectedRelay, value))
            {
                Raise(nameof(EmptyExplorerVisibility));
                Raise(nameof(SelectedExplorerVisibility));
                Raise(nameof(ActiveRelayTitle));
                Raise(nameof(ActiveRelaySubtitle));
            }
        }
    }

    public Visibility EmptyExplorerVisibility => SelectedRelay == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectedExplorerVisibility => SelectedRelay != null ? Visibility.Visible : Visibility.Collapsed;
    public string ActiveRelayTitle => SelectedRelay == null ? "No IED selected" : $"{SelectedRelay.DisplayName}";
    public string ActiveRelaySubtitle => SelectedRelay == null
        ? "Add an IED to start IEC 61850 discovery."
        : $"{SelectedRelay.EndpointText} • {SelectedRelay.Status} • {SelectedRelay.TagCount} tags • {SelectedRelay.HeartbeatText}";

    public MainWindow()
    {
        InitializeComponent();
        SignalsView = CollectionViewSource.GetDefaultView(Signals);
        SignalsView.Filter = FilterSignal;
        SignalsView.SortDescriptions.Add(new SortDescription(nameof(SignalDefinition.SortPriority), ListSortDirection.Ascending));
        SignalsView.SortDescriptions.Add(new SortDescription(nameof(SignalDefinition.LogicalNode), ListSortDirection.Ascending));
        SignalsView.SortDescriptions.Add(new SortDescription(nameof(SignalDefinition.Name), ListSortDirection.Ascending));
        DataContext = this;
        MqttSettings.PropertyChanged += MqttSettings_PropertyChanged;
        Relays.CollectionChanged += Relays_CollectionChanged;
        Loaded += MainWindow_Loaded;
        _activityResetTimer.Tick += ActivityResetTimer_Tick;
        _activityResetTimer.Start();
        _runtimeSnapshotTimer.Tick += RuntimeSnapshotTimer_Tick;
        _runtimeSnapshotTimer.Start();
        AddLog("INFO", "System", "Application loaded. Ready for Add IED wizard or project load.");
    }

    private void Relays_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Raise(nameof(EmptyExplorerVisibility));
        Raise(nameof(SelectedExplorerVisibility));
        Raise(nameof(ActiveRelayTitle));
        Raise(nameof(ActiveRelaySubtitle));
    }

    private void MqttSettings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Raise(nameof(MqttEndpointText));
        Raise(nameof(MqttTrafficText));
        Raise(nameof(RuntimeInsightText));
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _navButtons.Clear();
        _navButtons.AddRange(new[] { NavConnectButton, NavRuntimeButton, NavMqttButton, NavDiagnosticsButton });
        WorkflowNavShell.SizeChanged += (_, _) => MoveWorkflowPill(MainTabs.SelectedIndex, false);
        RuntimeToggleShell.SizeChanged += (_, _) => MoveRuntimeToggle(RuntimeStatusText == "Running", false);

        LoadRecentRelayIps();
        MainTabs.SelectedIndex = 0;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            MainTabs.SelectedIndex = 0;
            UpdateNavigationVisuals(0);
            MoveWorkflowPill(0, false);
        }));

        if (RealLibIec61850Client.IsRuntimeLibraryAvailable())
        {
            UseRealIecEngine = true;
            UseNativeCleanRoomEngine = false;
            AddLog("INFO", "IEC61850", "IEC 61850 MMS runtime detected beside EXE. Real IEC 61850 mode enabled automatically.");
        }
        else
        {
            AddLog("INFO", "IEC61850", "External IEC 61850 runtime not detected. Native clean-room IP/SCL workflow is available; mock mode remains available for UI/Modbus testing.");
        }

        UpdateNavigationVisuals(MainTabs.SelectedIndex);
        MoveWorkflowPill(MainTabs.SelectedIndex, false);
        MoveRuntimeToggle(false, false);
    }

    private async void ConnectDiscover_Click(object sender, RoutedEventArgs e)
    {
        var relayIp = GetRelayIpForOperation();
        try
        {
            if (IsRuntimeRunning)
            {
                AddLog("WARN", "Runtime", "IEC discovery/reconnect is blocked while runtime is running. Stop Runtime before changing the active IED/MMS session.");
                NavigateToTab(1);
                return;
            }

            if (string.IsNullOrWhiteSpace(relayIp))
            {
                IedConnectionStatus = "Missing IP";
                EventStrategyStatus = "Waiting for relay IP";
                AddLog("ERROR", "IEC61850", "Relay IP address is empty. Type or select a relay IP before Connect & Discover.");
                NavigateToTab(0);
                return;
            }

            RelayIpAddress = relayIp;
            if (RelayIpTextBox != null && string.IsNullOrWhiteSpace(RelayIpTextBox.Text))
                RelayIpTextBox.Text = relayIp;

            var existingRelay = Relays.FirstOrDefault(r => string.Equals(r.IpAddress, relayIp, StringComparison.OrdinalIgnoreCase));
            if (existingRelay != null)
            {
                SetActiveRelay(existingRelay);
                AddLog("INFO", "Relay", $"Existing IED endpoint selected: {relayIp}:{MmsPort}. ArServer will reconnect/update this device, not create a duplicate.");
            }

            IedConnectionStatus = "Connecting...";
            EventStrategyStatus = "Scanning...";
            AddLog("INFO", "IEC61850", $"Connecting to {relayIp}:{MmsPort} using {(UseNativeCleanRoomEngine ? "native clean-room IEC 61850 transport" : UseRealIecEngine ? "real IEC 61850 MMS" : "mock")} IEC 61850 engine.");

            // The Modbus gateway lifecycle is independent from per-IED connection lifecycle.
            // When runtime is running, keep the Modbus server alive and refresh only the active IEC session.
            // The runtime will receive the new IEC client after successful discovery.
            if (IsRuntimeRunning)
            {
                AddLog("INFO", "Runtime", "Modbus server remains running. Refreshing active IEC 61850 session for this IED only.");
            }

            await ConnectActiveIecClientAsync(relayIp, MmsPort, CancellationToken.None);
            var activeClient = _iecClient ?? throw new InvalidOperationException("IEC client was not created.");
            IedConnectionStatus = GetClientConnectedDisplayStatus(activeClient);

            if (!activeClient.IsConnected)
            {
                Signals.Clear();
                SignalsView.Refresh();
                Raise(nameof(SignalCount));
                Raise(nameof(VisibleSignalCountText));
                EventStrategyStatus = "Connection failed";

                if (activeClient is RealLibIec61850Client realFailed)
                    AddLog("ERROR", "IEC61850", string.IsNullOrWhiteSpace(realFailed.LastErrorMessage) ? "Real IEC61850 connection failed. No mock fallback was used." : realFailed.LastErrorMessage);
                else if (activeClient is NativeCleanRoomIec61850Client nativeFailed)
                    AddLog("ERROR", "Native IEC61850", string.IsNullOrWhiteSpace(nativeFailed.LastErrorMessage) ? "Native clean-room IEC61850 ACSE/MMS association failed." : nativeFailed.LastErrorMessage);
                else
                    AddLog("ERROR", "IEC61850", "IEC61850 connection failed.");

                AddLog("INFO", "Operator Hint", "No mock signals are shown in Real mode. Fix network/MMS endpoint first, then Connect & Discover again.");
                NavigateToTab(3);
                return;
            }

            var discovered = await activeClient.DiscoverSignalsAsync(CancellationToken.None);
            Signals.Clear();
            foreach (var signal in discovered)
                AddSignal(signal);

            if (Signals.Count > 0)
            {
                if (_openConfigWizardAfterDiscovery)
                {
                    AddLog("INFO", "IP Discovery", $"Online MMS browse found {Signals.Count} attributes for {relayIp}:{MmsPort}. Result is held as wizard draft only until Save to Runtime.");
                }
                else
                {
                    await ReadInitialSignalSnapshotAsync("discovery snapshot");
                    UpsertRelayChip(relayIp, "Connected", Signals.Count, activeClient.ConnectionMode);
                    if (IsRuntimeRunning && _runtime != null)
                        _runtime.ReplaceIecClient(activeClient);
                    PulseIecActivity();
                    await RememberRelayIpAsync(relayIp);
                    AddLog("INFO", "Preferences", $"Successful relay endpoint saved: {relayIp}:{MmsPort}");
                }
            }
            else
            {
                AddLog("WARN", "Preferences", $"Relay endpoint was not saved because online model discovery returned no signal: {relayIp}:{MmsPort}");
            }

            if (_iecClient is RealLibIec61850Client realOk && !string.IsNullOrWhiteSpace(realOk.LastDiscoverySummary))
                AddLog("INFO", "Discovery", realOk.LastDiscoverySummary);
            if (_iecClient is NativeCleanRoomIec61850Client nativeOk && !string.IsNullOrWhiteSpace(nativeOk.LastDiscoverySummary))
                AddLog("INFO", "Native Discovery", nativeOk.LastDiscoverySummary);

            EventStrategyStatus = _iecClient is NativeCleanRoomIec61850Client nativeStatus
                ? nativeStatus.IsMmsReady
                    ? "Native ACSE/MMS associated / read pending"
                    : nativeStatus.IsMmsInitiateFailed
                        ? "Native transport ready / MMS initiate failed"
                        : "Native transport ready / MMS pending"
                : _iecClient.ConnectionMode.Contains("Mock", StringComparison.OrdinalIgnoreCase)
                    ? "Mock event simulation"
                    : "Real MMS polling";
            SignalsView.Refresh();
            Raise(nameof(SignalCount));
            Raise(nameof(VisibleSignalCountText));
            AddLog("INFO", "Discovery", $"Discovered {Signals.Count} MMS attributes. Smart workspace now prioritizes position, protection, then MMXU/MMXN cVal current/voltage. Statistical LNs such as HarMMXU/MinMMXU/MaxMMXU/MeanMMXU are excluded from recommendations.");

            if (_openConfigWizardAfterDiscovery)
            {
                _openConfigWizardAfterDiscovery = false;
                var suggestedIedName = InferIedNameFromSignals() ?? "IED";
                var draftSignals = CloneSignals(Signals);
                RestoreWorkspaceAfterDraftCancel();
                var saved = OpenDraftIedConfigurationWizardAndCommit(
                    context: $"IP-only discovered IED {relayIp}:{MmsPort}",
                    draftSignals: draftSignals,
                    draftBindings: new ObservableCollection<BindingItem>(),
                    runtimeIpAddress: relayIp,
                    runtimePort: MmsPort,
                    useRealEngine: UseRealIecEngine,
                    useNativeCleanRoomEngine: UseNativeCleanRoomEngine,
                    suggestedIedName: suggestedIedName,
                    mode: _iecClient.ConnectionMode,
                    status: "Connected",
                    heartbeat: "MMS polling ready",
                    rcbMode: "MMS polling");

                if (saved)
                {
                    PulseIecActivity();
                    await RememberRelayIpAsync(relayIp);
                    AddLog("INFO", "Preferences", $"Successful relay endpoint saved after Save to Runtime: {relayIp}:{MmsPort}");
                }
                else
                {
                    AddLog("INFO", "Wizard", "IP-only discovery wizard cancelled before Save to Runtime. No relay/session/binding was committed.");
                    RestoreWorkspaceAfterDraftCancel();
                }
            }
            else
            {
                AddLog("INFO", "UX Flow", "IEC 61850 Explorer updated in viewing mode. Use Edit IED Wizard to change selected signals or binding.");
            }

            NavigateToTab(0);
        }
        catch (Exception ex)
        {
            _openConfigWizardAfterDiscovery = false;
            IedConnectionStatus = "Failed";
            UpsertRelayChip(relayIp, "Failed", 0, "MMS");
            EventStrategyStatus = "Unavailable";
            AddExceptionLog("IEC61850", ex, $"Connect/Discover failed for {relayIp}:{MmsPort}");
            AddLog("INFO", "Operator Hint", "Cek IP relay, VLAN/subnet, firewall Windows, port TCP 102, MMS server di relay, dan pastikan belum ada client lain yang mengunci association/RCB.");
        }
    }

    private static string GetClientConnectedDisplayStatus(IIec61850Client client)
    {
        if (client is NativeCleanRoomIec61850Client native)
        {
            if (native.IsMmsReady) return "MMS Associated";
            if (native.IsTransportReady) return native.IsMmsInitiateFailed ? "MMS Initiate Failed" : "Transport Ready";
            return "Failed";
        }

        if (!client.IsConnected) return "Failed";
        return "Connected";
    }

    private void SelectRecommended_Click(object sender, RoutedEventArgs e)
    {
        foreach (var signal in Signals)
            signal.IsSelected = signal.IsScadaCoreSignal;
        SignalsView.Refresh();
        Raise(nameof(VisibleSignalCountText));
        AddLog("INFO", "Discovery", "Selected smart SCADA-core signals only: protection operate/trip, CSWI/XCBR/XSWI Pos, and MMXU/MMXN cVal current/voltage. Statistic/harmonic/mean/min/max signals are intentionally excluded.");
    }

    private void QuickFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button) return;
        var token = button.Tag?.ToString() ?? button.Content?.ToString() ?? string.Empty;
        DiscoverySearchText = token;
        AddLog("INFO", "Discovery", $"Quick filter applied: {token}. Search matches Signal, LN, category, IEC object reference, and type.");
    }

    private void ClearDiscoveryFilter_Click(object sender, RoutedEventArgs e)
    {
        DiscoverySearchText = string.Empty;
        ShowRawEngineeringAttributes = false;
        AddLog("INFO", "Discovery", "Discovery search cleared. Smart SCADA workspace restored.");
    }

    private bool FilterSignal(object obj)
    {
        if (obj is not SignalDefinition signal) return false;

        var text = DiscoverySearchText?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Search is intentional operator action. Do not hide valid non-core points just
            // because they are outside the default smart SCADA shortlist. This prevents
            // a connected IED from looking "empty" when the first smart filter is too strict.
            var tokens = text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0) return true;

            var haystack = $"{signal.Name} {signal.LogicalNode} {signal.LogicalNodeClass} {signal.Category} {signal.DataType} {signal.FunctionalConstraint} {signal.ObjectReference}";
            return tokens.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        // Explorer is runtime viewing, not configuration. Show only saved/selected signals here.
        // Raw browsing and add/remove selection lives inside the IED Configuration Wizard.
        if (!ShowRawEngineeringAttributes)
            return signal.IsSelected;

        return signal.IsSelected || signal.IsScadaCoreSignal;
    }


    private void AddSignal(SignalDefinition signal)
    {
        signal.PropertyChanged += Signal_PropertyChanged;
        Signals.Add(signal);
    }

    private void Signal_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SignalDefinition.IsSelected) or nameof(SignalDefinition.ReportPlan))
        {
            SignalsView.Refresh();
            Raise(nameof(VisibleSignalCountText));
        }
    }

    private void AutoMap_Click(object sender, RoutedEventArgs e)
    {
        if (IsRuntimeRunning)
        {
            AddLog("WARN", "Runtime", "Auto Map is locked while runtime is running. Stop Runtime before changing Modbus binding.");
            return;
        }

        var relay = SelectedRelay;
        var selected = Signals.Where(s => s.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Belum ada signal dipilih. Buka Edit IED Wizard lalu pilih signal IEC 61850 yang akan dipublish ke Modbus.", "No signal selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var newBindings = BindingAutoMapper.CreateBindings(selected, relay == null ? 0 : GetRelayBlockIndex(relay));
        foreach (var item in newBindings)
            ApplyDefaultTimingToBinding(item);

        if (relay != null)
        {
            relay.ModbusBindings.Clear();
            foreach (var item in newBindings)
            {
                item.RelayId = relay.RelayId;
                item.IedName = relay.DisplayName;
                item.RelayIpAddress = relay.IpAddress;
                relay.ModbusBindings.Add(item);
            }
            SaveWorkspaceToRelay(relay, Signals, relay.ModbusBindings);
            RebuildPublishedBindingsFromRelays();
        }
        else
        {
            Bindings.Clear();
            PublishedModbusBindings.Clear();
            foreach (var item in newBindings)
            {
                Bindings.Add(item);
                PublishedModbusBindings.Add(item);
            }
        }

        SelectedBinding = PublishedModbusBindings.FirstOrDefault();
        Raise(nameof(BindingCount));
        AddLog("INFO", "Binding", $"Auto-mapped {selected.Count} selected IEC 61850 signal(s). Published Modbus map now has {PublishedModbusBindings.Count} binding(s) from all IEDs.");
        ValidateBindings(showMessage: false);
        AddLog("INFO", "UX Flow", "Modbus Server tab opened. Review/edit address, validate, then start runtime.");
        NavigateToTab(1);
    }

    private void AddSelectedToBinding_Click(object sender, RoutedEventArgs e)
    {
        if (IsRuntimeRunning)
        {
            AddLog("WARN", "Runtime", "Add binding is locked while runtime is running. Stop Runtime before changing Modbus binding.");
            return;
        }

        var selected = Signals.Where(s => s.IsSelected).ToList();
        if (selected.Count == 0)
        {
            AddLog("WARN", "Binding", "No selected IEC 61850 signal to add. Select signals in the IED Wizard first.");
            return;
        }

        var relay = SelectedRelay;
        var ownerBindings = relay?.ModbusBindings ?? Bindings;
        var added = 0;
        foreach (var item in BindingAutoMapper.CreateBindings(selected, relay == null ? 0 : GetRelayBlockIndex(relay)))
        {
            ApplyDefaultTimingToBinding(item);
            if (ownerBindings.Any(b => string.Equals(b.IecReference, item.IecReference, StringComparison.OrdinalIgnoreCase)))
                continue;

            item.ModbusAddress = FindNextFreeModbusAddress(item.ModbusArea, item.ModbusDataType);
            if (relay != null)
            {
                item.RelayId = relay.RelayId;
                item.IedName = relay.DisplayName;
                item.RelayIpAddress = relay.IpAddress;
                relay.ModbusBindings.Add(item);
            }
            else
            {
                Bindings.Add(item);
                PublishedModbusBindings.Add(item);
            }
            added++;
        }

        if (relay != null)
            RebuildPublishedBindingsFromRelays();

        SelectedBinding = PublishedModbusBindings.LastOrDefault();
        Raise(nameof(BindingCount));
        ValidateBindings(showMessage: false);
        AddLog("INFO", "Binding", $"Added {added} new Modbus binding(s). Published map total: {PublishedModbusBindings.Count}.");
        NavigateToTab(1);
    }

    private void RemoveSelectedBinding_Click(object sender, RoutedEventArgs e)
    {
        if (IsRuntimeRunning)
        {
            AddLog("WARN", "Runtime", "Remove binding is locked while runtime is running. Stop Runtime before changing Modbus binding.");
            return;
        }

        if (SelectedBinding == null) return;
        var relay = FindRelayForBinding(SelectedBinding);
        if (relay != null)
        {
            var owned = relay.ModbusBindings.FirstOrDefault(b => ReferenceEquals(b, SelectedBinding) ||
                (string.Equals(b.IecReference, SelectedBinding.IecReference, StringComparison.OrdinalIgnoreCase) && b.ModbusAddress == SelectedBinding.ModbusAddress && string.Equals(b.ModbusArea, SelectedBinding.ModbusArea, StringComparison.OrdinalIgnoreCase)));
            if (owned != null)
                relay.ModbusBindings.Remove(owned);
            RebuildPublishedBindingsFromRelays();
        }
        else
        {
            Bindings.Remove(SelectedBinding);
            PublishedModbusBindings.Remove(SelectedBinding);
        }

        SelectedBinding = PublishedModbusBindings.FirstOrDefault();
        Raise(nameof(BindingCount));
        ValidateBindings(showMessage: false);
    }

    private int FindNextFreeModbusAddress(string area, string dataType)
    {
        var start = area switch
        {
            "DiscreteInput" => 10001,
            "InputRegister" => 30001,
            "HoldingRegister" => 40001,
            "Coil" => 1,
            _ => 40001
        };
        var width = string.Equals(dataType, "Float32", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        var used = new HashSet<int>();
        foreach (var binding in PublishedModbusBindings.Where(b => string.Equals(b.ModbusArea, area, StringComparison.OrdinalIgnoreCase)))
        {
            var bindingWidth = string.Equals(binding.ModbusDataType, "Float32", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            for (var i = 0; i < bindingWidth; i++)
                used.Add(binding.ModbusAddress + i);
        }

        var address = start;
        while (Enumerable.Range(address, width).Any(used.Contains))
            address += width;
        return address;
    }

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        ValidateBindings(showMessage: true);
    }

    private void ApplyDefaultTimingToBinding(BindingItem binding)
    {
        binding.PollingIntervalMs = BridgeRuntime.NormalizeMmsPollingIntervalMs(MmsPollingIntervalMs);
        binding.StaleTimeoutMs = Math.Max(binding.StaleTimeoutMs, Math.Max(3000, binding.PollingIntervalMs * 10));
    }

    private void ApplyMmsPollingToMap_Click(object sender, RoutedEventArgs e)
    {
        if (IsRuntimeRunning)
        {
            AddLog("WARN", "Runtime", "MMS polling time is locked while runtime is running. Stop Runtime before changing polling timing.");
            return;
        }

        ApplyProjectPollingIntervalToPublishedMap(logChanges: true);
        NormalizePublishedBindingTiming(logChanges: true);
        ValidateBindings(showMessage: false);
    }

    private void ApplyProjectPollingIntervalToPublishedMap(bool logChanges)
    {
        var requested = MmsPollingIntervalMs;
        var normalized = BridgeRuntime.NormalizeMmsPollingIntervalMs(requested);
        if (requested != normalized)
            MmsPollingIntervalMs = normalized;

        var touched = 0;
        foreach (var binding in EnumerateAllKnownBindings().Where(IsRoutedBinding))
        {
            if (binding.PollingIntervalMs != normalized)
            {
                binding.PollingIntervalMs = normalized;
                touched++;
            }

            binding.StaleTimeoutMs = Math.Max(binding.StaleTimeoutMs, Math.Max(3000, normalized * 10));
        }

        if (logChanges)
        {
            var level = normalized <= 50 ? "WARN" : "INFO";
            AddLog(level, "Runtime", $"IEC 61850 MMS polling target set to {normalized} ms for {touched} routed binding(s). Minimum allowed target is {BridgeRuntime.MinimumMmsPollingIntervalMs} ms.");
            if (normalized <= 50)
                AddLog("WARN", "Runtime", "10-50 ms MMS polling is best treated as expert fast monitoring/bench mode. For protection-grade event capture, prefer Report/RCB or GOOSE/SV path when available.");
            if (FastAcquisitionEnabled)
                AddLog("INFO", "Runtime", "Fast CB acquisition is enabled. CB position/status/Boolean/protection flags will be scheduled before measurement tags at runtime.");
        }
    }

    private void NormalizePublishedBindingTiming(bool logChanges)
    {
        var clamped = 0;
        foreach (var binding in EnumerateAllKnownBindings().Where(IsRoutedBinding))
        {
            var normalized = BridgeRuntime.NormalizeMmsPollingIntervalMs(binding.PollingIntervalMs);
            if (binding.PollingIntervalMs != normalized)
            {
                binding.PollingIntervalMs = normalized;
                clamped++;
            }

            binding.StaleTimeoutMs = Math.Max(binding.StaleTimeoutMs, Math.Max(3000, normalized * 10));
        }

        if (logChanges && clamped > 0)
            AddLog("WARN", "Runtime", $"Clamped {clamped} polling interval value(s) into safe range {BridgeRuntime.MinimumMmsPollingIntervalMs}..{BridgeRuntime.MaximumMmsPollingIntervalMs} ms.");
    }

    private IEnumerable<BindingItem> EnumerateAllKnownBindings()
    {
        var seen = new HashSet<BindingItem>(ReferenceEqualityComparer.Instance);

        foreach (var binding in PublishedModbusBindings)
            if (seen.Add(binding))
                yield return binding;

        foreach (var binding in Bindings)
            if (seen.Add(binding))
                yield return binding;

        foreach (var relayBinding in Relays.SelectMany(r => r.ModbusBindings))
            if (seen.Add(relayBinding))
                yield return relayBinding;
    }

    private bool ValidateBindings(bool showMessage)
    {
        // Always normalize the global Modbus map before validation. This makes multi-IED
        // projects self-healing: users add IEDs and select signals, ArServer arranges the
        // address blocks automatically instead of forcing manual register planning.
        NormalizePublishedBindingTiming(logChanges: false);
        ArrangeAllRelayModbusBlocks();

        var errors = new List<string>();
        var used = new Dictionary<string, BindingItem>(StringComparer.OrdinalIgnoreCase);

        if (!EnableModbusTcp && !MqttSettings.IsEnabled)
            errors.Add("At least one output must be enabled: Modbus TCP or MQTT.");
        if (EnableModbusTcp && (ModbusPort <= 0 || ModbusPort > 65535))
            errors.Add("Modbus TCP port must be 1..65535.");
        if (EnableModbusTcp && (ModbusUnitId < 1 || ModbusUnitId > 247))
            errors.Add("Modbus Unit ID must be 1..247.");
        if (EnableModbusTcp && ModbusBindingCount == 0)
            errors.Add("Modbus TCP is enabled, but no active signal is routed to Modbus.");
        if (MqttSettings.IsEnabled && string.IsNullOrWhiteSpace(MqttSettings.BrokerHost))
            errors.Add("MQTT broker host is required when MQTT is enabled.");
        if (MqttSettings.IsEnabled && (MqttSettings.BrokerPort <= 0 || MqttSettings.BrokerPort > 65535))
            errors.Add("MQTT broker port must be 1..65535.");
        if (MqttSettings.IsEnabled && MqttBindingCount == 0)
            errors.Add("MQTT is enabled, but no active signal is routed to MQTT.");

        foreach (var binding in PublishedModbusBindings.Where(IsRoutedBinding))
        {
            if (string.IsNullOrWhiteSpace(binding.SignalName)) errors.Add("There is a binding with an empty signal label.");
            if (string.IsNullOrWhiteSpace(binding.IecReference)) errors.Add($"{binding.SignalName}: IEC reference is empty.");

            if (!binding.PublishToModbus)
                continue;

            if (binding.ModbusAddress <= 0) errors.Add($"{binding.SignalName}: invalid Modbus address.");
            if (binding.ModbusDataType == "Float32" && binding.ModbusArea is "Coil" or "DiscreteInput")
                errors.Add($"{binding.SignalName}: Float32 is not valid for {binding.ModbusArea}. Use HoldingRegister/InputRegister.");

            var width = binding.ModbusDataType == "Float32" ? 2 : 1;
            for (var i = 0; i < width; i++)
            {
                var key = $"{binding.ModbusArea}:{binding.ModbusAddress + i}";
                if (used.TryGetValue(key, out var existing))
                {
                    errors.Add($"Register overlap: {key} used by {existing.IedName}/{existing.SignalName} and {binding.IedName}/{binding.SignalName}.");
                }
                else
                {
                    used[key] = binding;
                }
            }
        }

        if (errors.Count == 0)
        {
            AddLog("INFO", "Validation", $"Binding validation OK. Modbus routed: {ModbusBindingCount}, MQTT routed: {MqttBindingCount}.");
            return true;
        }

        var grouped = errors.GroupBy(e => e).Select(g => g.Key).Take(12).ToList();
        foreach (var error in grouped) AddLog("WARN", "Validation", error);
        if (errors.Count > grouped.Count)
            AddLog("WARN", "Validation", $"{errors.Count - grouped.Count} additional validation warning(s) suppressed. Fix the first warnings or rebuild binding.");

        if (showMessage)
            NavigateToTab(3);

        return false;
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyProjectPollingIntervalToPublishedMap(logChanges: false);
            NormalizePublishedBindingTiming(logChanges: false);
            var project = new BridgeProject
            {
                ProjectName = ProjectName,
                RelayIpAddress = RelayIpAddress,
                MmsPort = MmsPort,
                UseRealIecEngine = UseRealIecEngine,
                UseNativeCleanRoomEngine = UseNativeCleanRoomEngine,
                ModbusBindAddress = ModbusBindAddress,
                EnableModbusTcp = EnableModbusTcp,
                ModbusPort = ModbusPort,
                ModbusUnitId = ModbusUnitId,
                MmsPollingIntervalMs = BridgeRuntime.NormalizeMmsPollingIntervalMs(MmsPollingIntervalMs),
                FastAcquisitionEnabled = FastAcquisitionEnabled,
                Mqtt = CloneMqttSettings(MqttSettings),
                Signals = Signals.ToList(),
                Bindings = PublishedModbusBindings.ToList(),
                Relays = Relays.ToList()
            };
            await ProjectStore.SaveAsync(project);
            AddLog("INFO", "Storage", $"Project saved to {ProjectStore.DefaultProjectPath}");
            if (PublishedModbusBindings.Count > 0)
            {
                AddLog("INFO", "UX Flow", "Modbus Server tab opened automatically. Start runtime when ready, then point FUXA to the endpoint.");
                NavigateToTab(1);
            }
            else if (Signals.Count > 0)
            {
                NavigateToTab(0);
            }
            AddLog("INFO", "Storage", "Profile saved. Runtime will use the saved binding profile.");
        }
        catch (Exception ex)
        {
            AddExceptionLog("Storage", ex, "Save project failed");
        }
    }

    private async void LoadProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var project = await ProjectStore.LoadAsync();
            if (project == null)
            {
                AddLog("WARN", "Storage", $"Project file not found: {ProjectStore.DefaultProjectPath}");
                return;
            }

            ProjectName = project.ProjectName;
            RelayIpAddress = project.RelayIpAddress;
            MmsPort = project.MmsPort;
            UseRealIecEngine = project.UseRealIecEngine;
            UseNativeCleanRoomEngine = project.UseNativeCleanRoomEngine;
            ModbusBindAddress = string.IsNullOrWhiteSpace(project.ModbusBindAddress) ? "0.0.0.0" : project.ModbusBindAddress;
            EnableModbusTcp = project.EnableModbusTcp;
            ModbusPort = project.ModbusPort <= 0 ? 502 : project.ModbusPort;
            ModbusUnitId = project.ModbusUnitId <= 0 ? 1 : project.ModbusUnitId;
            MmsPollingIntervalMs = BridgeRuntime.NormalizeMmsPollingIntervalMs(project.MmsPollingIntervalMs <= 0 ? BridgeRuntime.DefaultMmsPollingIntervalMs : project.MmsPollingIntervalMs);
            FastAcquisitionEnabled = project.FastAcquisitionEnabled;
            MqttSettings = CloneMqttSettings(project.Mqtt ?? new MqttGatewaySettings());

            Signals.Clear();
            foreach (var signal in project.Signals) AddSignal(signal);
            Bindings.Clear();
            PublishedModbusBindings.Clear();
            foreach (var binding in project.Bindings)
            {
                Bindings.Add(binding);
                PublishedModbusBindings.Add(binding);
            }
            SelectedBinding = PublishedModbusBindings.FirstOrDefault();

            Relays.Clear();
            foreach (var relay in project.Relays)
            {
                relay.StatusBrush = RelayEndpointView.BrushForStatus(relay.Status);
                relay.RefreshComputed();
                Relays.Add(relay);
            }
            SelectedRelay = Relays.FirstOrDefault(r => r.IsActive) ?? Relays.FirstOrDefault();
            RebuildPublishedBindingsFromRelays();
            if (SelectedRelay != null)
                SetActiveRelay(SelectedRelay);

            SignalsView.Refresh();
            Raise(nameof(SignalCount));
            Raise(nameof(VisibleSignalCountText));
            Raise(nameof(BindingCount));
            AddLog("INFO", "Storage", $"Project loaded from {ProjectStore.DefaultProjectPath}");
            NavigateToTab(PublishedModbusBindings.Count > 0 ? 1 : 0);
        }
        catch (Exception ex)
        {
            AddExceptionLog("Storage", ex, "Load project failed");
        }
    }

    private async void StartRuntime_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (IsRuntimeRunning)
            {
                AddLog("INFO", "Runtime", "Start ignored because runtime is already running.");
                return;
            }

            RuntimeStatusText = "Starting";
            MoveRuntimeToggle(true, true);

            PreparePublishedModbusMapForRuntime();
            ApplyProjectPollingIntervalToPublishedMap(logChanges: true);
            NormalizePublishedBindingTiming(logChanges: true);
            if (!EnableModbusTcp && !MqttSettings.IsEnabled)
            {
                RuntimeStatusText = "Stopped";
                MoveRuntimeToggle(false, true);
                AddLog("ERROR", "Runtime", "Runtime blocked. Enable Modbus TCP, MQTT, or both before starting.");
                NavigateToTab(1);
                return;
            }

            if (PublishedModbusBindings.Count == 0)
            {
                AutoMap_Click(sender, e);
                PreparePublishedModbusMapForRuntime();
                ApplyProjectPollingIntervalToPublishedMap(logChanges: true);
                NormalizePublishedBindingTiming(logChanges: true);
                if (PublishedModbusBindings.Count == 0)
                {
                    RuntimeStatusText = "Stopped";
                    MoveRuntimeToggle(false, true);
                    AddLog("ERROR", "Runtime", "Runtime blocked. No published Modbus binding exists after rebuilding from all IED sessions.");
                    return;
                }
            }

            if (!ValidateBindings(showMessage: false))
            {
                MessageBox.Show("Binding masih punya error. Buka log/validation dulu sebelum runtime.", "Runtime blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
                RuntimeStatusText = "Stopped";
                MoveRuntimeToggle(false, true);
                return;
            }

            var runtimeRelayCount = PublishedModbusBindings
                .Where(IsRoutedBinding)
                .Select(b => string.IsNullOrWhiteSpace(b.RelayId) ? "__single__" : b.RelayId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            var runtimeEndpoint = ResolveSingleRuntimeEndpoint();
            if (runtimeRelayCount <= 1 && !IsActiveIecClientFor(runtimeEndpoint.IpAddress, runtimeEndpoint.Port))
            {
                var relayIp = runtimeEndpoint.IpAddress;
                var relayPort = runtimeEndpoint.Port;
                if (string.IsNullOrWhiteSpace(relayIp))
                {
                    AddLog("ERROR", "Runtime", "Runtime blocked. Relay IP address is empty; cannot reconnect IEC 61850 downstream.");
                    RuntimeStatusText = "Stopped";
                    MoveRuntimeToggle(false, true);
                    NavigateToTab(0);
                    return;
                }

                RelayIpAddress = relayIp;
                MmsPort = relayPort;
                if (RelayIpTextBox != null)
                    RelayIpTextBox.Text = relayIp;

                await ConnectActiveIecClientAsync(relayIp, relayPort, CancellationToken.None);
                var activeClient = _iecClient ?? throw new InvalidOperationException("IEC client was not created.");
                IedConnectionStatus = activeClient.IsConnected ? "Connected" : "Failed";

                if (!activeClient.IsConnected)
                {
                    if (activeClient is RealLibIec61850Client realFailed)
                        AddLog("ERROR", "IEC61850", string.IsNullOrWhiteSpace(realFailed.LastErrorMessage) ? "Runtime cannot start because IEC61850 is not connected." : realFailed.LastErrorMessage);
                    else if (activeClient is NativeCleanRoomIec61850Client nativeFailed)
                        AddLog("ERROR", "Native IEC61850", string.IsNullOrWhiteSpace(nativeFailed.LastErrorMessage) ? "Runtime cannot start because native ACSE/MMS association is not ready." : nativeFailed.LastErrorMessage);
                    AddLog("WARN", "Runtime", "Runtime blocked. IEC61850 downstream is not connected. Modbus server is not started to avoid publishing stale/fake values.");
                    RuntimeStatusText = "Stopped";
                    MoveRuntimeToggle(false, true);
                    return;
                }
            }
            else if (_iecClient == null)
            {
                // Multi-IED runtime uses isolated per-IED MMS clients inside BridgeRuntime.
                // Do not pre-connect a single global client to the selected relay; that was
                // the root cause of last-added/selected IED sessions stealing the first IED.
                _iecClient = CreateConfiguredIecClient();
            }

            if (_runtime != null)
                await _runtime.DisposeAsync();

            _runtime = new BridgeRuntime(
                _iecClient,
                PublishedModbusBindings,
                Relays,
                () => CreateConfiguredIecClient(),
                EnableModbusTcp,
                CloneMqttSettings(MqttSettings),
                FastAcquisitionEnabled);
            _runtime.Diagnostic += entry => Dispatcher.Invoke(() => AddLog(entry.Level, entry.Source, entry.Message));
            _runtime.BindingUpdated += binding => Dispatcher.Invoke(() =>
            {
                QueueRuntimeSnapshot(binding);
            });
            _runtime.RuntimeTick += () => Dispatcher.Invoke(() =>
            {
                if (_runtime == null) return;
                ModbusClientCount = _runtime.ClientCount;
                ModbusReadCount = _runtime.ModbusReadCount;
                ModbusLastClientText = _runtime.LastClientEndpoint;
                MqttConnected = _runtime.MqttIsConnected;
                MqttPublishedCount = _runtime.MqttPublishedCount;
                MqttDroppedCount = _runtime.MqttDroppedCount;

                if (EnableModbusTcp && ModbusReadCount > _lastObservedModbusReadCount)
                {
                    _lastObservedModbusReadCount = ModbusReadCount;
                    PulseModbusActivity();
                }
            });

            await _runtime.StartAsync(ModbusBindAddress, ModbusPort, ModbusUnitId);
            NavigateToTab(1);
            RuntimeStatusText = "Running";
            MoveRuntimeToggle(true, true);
            EventStrategyStatus = _runtime.EventMode;
            if (EnableModbusTcp)
                AddLog("INFO", "Runtime", $"FUXA can connect by Modbus TCP to this PC IP, port {ModbusPort}, Unit ID {ModbusUnitId}.");
            if (MqttSettings.IsEnabled)
                AddLog("INFO", "Runtime", $"FUXA can subscribe by MQTT via broker {MqttSettings.BrokerHost}:{MqttSettings.BrokerPort}, topic root {MqttSettings.TopicRoot}.");
        }
        catch (Exception ex)
        {
            RuntimeStatusText = "Failed";
            MoveRuntimeToggle(false, true);
            AddExceptionLog("Runtime", ex, "Runtime start failed");
            AddLog("INFO", "Operator Hint", "Runtime tidak dihentikan diam-diam. Detail error masuk ke Diagnostics Panel dan status bar, tanpa pop-up modal yang mengganggu flow kerja.");
        }
    }

    private async void StopRuntime_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_runtime == null) return;
            await _runtime.StopAsync();
            RuntimeStatusText = "Stopped";
            MoveRuntimeToggle(false, true);
            ModbusClientCount = 0;
            ModbusLastClientText = "-";
            MqttConnected = false;
        }
        catch (Exception ex)
        {
            AddExceptionLog("Runtime", ex, "Runtime stop failed");
        }
    }


    private async void OpenConfigurationWizardInternal(string context)
    {
        if (IsRuntimeRunning)
        {
            AddLog("WARN", "Runtime", "Configuration wizard is locked while runtime is running. Stop Runtime before editing IEC signals or Modbus binding.");
            return;
        }

        var relay = SelectedRelay;
        if (Signals.Count == 0 && (relay == null || relay.Signals.Count == 0))
        {
            AddLog("WARN", "Wizard", "Cannot open signal/binding wizard because no IEC 61850 model is currently loaded for the selected IED.");
            return;
        }

        if (relay != null && relay.Signals.Count > 0)
            LoadWorkspaceFromRelay(relay, loadBindings: false);

        var wizardSignals = CloneSignals(Signals);
        var sourceBindings = relay != null ? relay.ModbusBindings : new ObservableCollection<BindingItem>(PublishedModbusBindings.Where(b => string.IsNullOrWhiteSpace(b.RelayId)));
        var wizardBindings = CloneBindings(sourceBindings);

        var wizard = new IedConfigurationWizardWindow(wizardSignals, wizardBindings)
        {
            Owner = this
        };

        AddLog("INFO", "Wizard", $"Configuration wizard opened for {context}. Selection, filtering, binding and save are isolated inside the popup window.");
        TrackActiveWizard(wizard);
        if (wizard.ShowDialog() == true)
        {
            if (relay == null)
            {
                EnsureActiveRelayChip();
                relay = SelectedRelay ?? Relays.FirstOrDefault(r => r.IsActive);
            }

            if (relay != null)
            {
                SaveWorkspaceToRelay(relay, wizardSignals, wizardBindings);
                SetActiveRelay(relay);
                RebuildPublishedBindingsFromRelays();
            }
            else
            {
                Signals.Clear();
                foreach (var signal in wizardSignals) Signals.Add(signal);
                Bindings.Clear();
                foreach (var binding in wizardBindings) Bindings.Add(binding);
            }

            SignalsView.Refresh();
            SelectedBinding = Bindings.FirstOrDefault();
            Raise(nameof(BindingCount));
            Raise(nameof(VisibleSignalCountText));
            ValidateBindings(showMessage: false);
            await ReadInitialSignalSnapshotAsync("wizard save snapshot");
            if (relay != null)
                SyncLiveWorkspaceToRelay(relay);
            SignalsView.Refresh();
            AddLog("INFO", "Wizard", $"IED configuration saved. Selected signals: {Signals.Count(s => s.IsSelected)}. Published Modbus bindings: {PublishedModbusBindings.Count}.");
        }
        else
        {
            AddLog("INFO", "Wizard", "Configuration wizard closed without applying a new save action.");
        }
    }

    private async Task ReadInitialSignalSnapshotAsync(string context)
    {
        if (_iecClient == null || !_iecClient.IsConnected)
        {
            AddLog("WARN", "IEC61850", $"Skipped {context}: IEC61850 client is not connected.");
            return;
        }

        var candidates = Signals
            .Where(s => s.IsSelected && s.DataType != "Directory")
            .OrderBy(s => s.SortPriority)
            .ThenBy(s => s.LogicalNode)
            .ThenBy(s => s.Name)
            .Take(180)
            .ToList();

        if (candidates.Count == 0) return;

        var ok = 0;
        var failed = 0;
        foreach (var signal in candidates)
        {
            try
            {
                var value = await _iecClient.ReadValueAsync(signal.ObjectReference, signal.FunctionalConstraint, signal.DataType, CancellationToken.None);
                if (value == null)
                {
                    signal.Value = "-";
                    signal.Quality = "Bad";
                    signal.Timestamp = DateTime.Now;
                    failed++;
                    if (failed <= 3)
                        AddLog("WARN", "IEC61850", $"Initial read skipped for {signal.Name}: object not readable/directly accessible by MMS.");
                    continue;
                }

                signal.Value = MockIec61850Client.Format(value, signal.DataType, signal.Unit);
                signal.Quality = "Good";
                signal.Timestamp = DateTime.Now;
                UpdateBindingFromSignal(signal);
                ok++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                signal.Value = "Read failed";
                signal.Quality = "Bad";
                signal.Timestamp = DateTime.Now;
                if (failed <= 3)
                    AddLog("WARN", "IEC61850", $"Initial read failed for {signal.Name}: {ex.Message}");
            }
        }

        SignalsView.Refresh();
        AddLog("INFO", "IEC61850", $"Initial value snapshot complete ({context}): {ok} value(s) read, {failed} failed. The Explorer now shows real values where the relay allows direct read.");
    }

    private void UpdateBindingFromSignal(SignalDefinition signal)
    {
        var relayId = SelectedRelay?.RelayId;
        foreach (var binding in PublishedModbusBindings.Where(b => string.Equals(b.IecReference, signal.ObjectReference, StringComparison.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(relayId) || string.IsNullOrWhiteSpace(b.RelayId) || b.RelayId == relayId)))
        {
            binding.CurrentValue = signal.Value;
            binding.Quality = signal.Quality;
            binding.LastUpdate = signal.Timestamp;
            binding.AgeMs = 0;
            binding.Status = signal.Quality == "Good" ? "Mapped/Live" : "Mapped/Bad";
        }

        if (SelectedRelay != null)
            SyncLiveWorkspaceToRelay(SelectedRelay);
    }

    private void UpdateSignalFromBinding(BindingItem binding)
    {
        var relay = FindRelayForBinding(binding);
        if (relay != null)
        {
            var relaySignal = relay.Signals.FirstOrDefault(s => string.Equals(s.ObjectReference, binding.IecReference, StringComparison.OrdinalIgnoreCase));
            if (relaySignal != null)
            {
                relaySignal.Value = binding.CurrentValue;
                relaySignal.Quality = binding.Quality;
                relaySignal.Timestamp = binding.LastUpdate == DateTime.MinValue ? DateTime.Now : binding.LastUpdate;
            }

            ApplyRelayRuntimeReadState(relay, binding);
        }

        if (SelectedRelay != null && relay != null && relay.RelayId != SelectedRelay.RelayId)
            return;
        if (SelectedRelay != null && !string.IsNullOrWhiteSpace(binding.RelayId) && binding.RelayId != SelectedRelay.RelayId)
            return;

        var signal = Signals.FirstOrDefault(s => string.Equals(s.ObjectReference, binding.IecReference, StringComparison.OrdinalIgnoreCase));
        if (signal == null) return;
        signal.Value = binding.CurrentValue;
        signal.Quality = binding.Quality;
        signal.Timestamp = binding.LastUpdate == DateTime.MinValue ? DateTime.Now : binding.LastUpdate;

        if (SelectedRelay != null)
            SyncLiveWorkspaceToRelay(SelectedRelay);
    }

    private static void ApplyRelayRuntimeReadState(RelayEndpointView relay, BindingItem binding)
    {
        var statusText = binding.Status ?? string.Empty;
        var good = binding.Quality.Equals("Good", StringComparison.OrdinalIgnoreCase);
        var nativeAssociated = statusText.Contains("Native MMS associated", StringComparison.OrdinalIgnoreCase) ||
                               statusText.Contains("Confirmed-Read", StringComparison.OrdinalIgnoreCase);
        var nativeFailed = statusText.Contains("Native MMS initiate failed", StringComparison.OrdinalIgnoreCase);
        var nativePending = statusText.Contains("Native transport", StringComparison.OrdinalIgnoreCase) ||
                            statusText.Contains("Native MMS pending", StringComparison.OrdinalIgnoreCase) ||
                            statusText.Contains("ACSE/MMS", StringComparison.OrdinalIgnoreCase) ||
                            statusText.Contains("Not readable", StringComparison.OrdinalIgnoreCase) ||
                            nativeAssociated ||
                            nativeFailed;

        if (good)
        {
            relay.Status = "Connected";
            relay.HeartbeatText = "MMS stream active";
            relay.StatusBrush = RelayEndpointView.BrushForStatus(relay.Status);
            relay.ActivityBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94));
        }
        else if (nativePending)
        {
            relay.Status = nativeAssociated ? "MMS Associated" : "Transport Ready";
            relay.HeartbeatText = nativeAssociated
                ? "Confirmed-Read pending"
                : nativeFailed
                    ? "MMS initiate failed"
                    : "Native MMS pending";
            relay.StatusBrush = RelayEndpointView.BrushForStatus(relay.Status);
            relay.ActivityBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
        }
        else
        {
            relay.Status = string.IsNullOrWhiteSpace(relay.Status) || relay.Status.Contains("Connected", StringComparison.OrdinalIgnoreCase)
                ? "Read warning"
                : relay.Status;
            relay.HeartbeatText = "MMS read warning";
            relay.StatusBrush = RelayEndpointView.BrushForStatus(relay.Status);
            relay.ActivityBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
        }

        relay.RefreshComputed();
    }

    private RelayEndpointView? FindRelayForBinding(BindingItem binding)
    {
        if (!string.IsNullOrWhiteSpace(binding.RelayId))
        {
            var byId = Relays.FirstOrDefault(r => string.Equals(r.RelayId, binding.RelayId, StringComparison.OrdinalIgnoreCase));
            if (byId != null) return byId;
        }

        if (!string.IsNullOrWhiteSpace(binding.RelayIpAddress))
        {
            var byIp = Relays.FirstOrDefault(r => string.Equals(r.IpAddress, binding.RelayIpAddress, StringComparison.OrdinalIgnoreCase));
            if (byIp != null) return byIp;
        }

        if (!string.IsNullOrWhiteSpace(binding.IedName))
            return Relays.FirstOrDefault(r => string.Equals(r.DisplayName, binding.IedName, StringComparison.OrdinalIgnoreCase));

        return SelectedRelay;
    }

    private void OpenModbusServer_Click(object sender, RoutedEventArgs e)
    {
        NavigateToTab(1);
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        var window = new HelpWindow { Owner = this };
        window.ShowDialog();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var window = new AboutWindow { Owner = this };
        window.ShowDialog();
    }


    private void AddRelay_Click(object sender, RoutedEventArgs e)
    {
        if (IsRuntimeRunning)
        {
            AddLog("WARN", "Runtime", "Add IED is locked while runtime is running. Stop Runtime before changing IED topology.");
            NavigateToTab(1);
            return;
        }

        var choice = new AddIedSourceChoiceWindow { Owner = this };
        TrackActiveWizard(choice);
        if (choice.ShowDialog() != true)
        {
            AddLog("INFO", "Relay", "Add IED cancelled. No relay slot was created.");
            return;
        }

        if (string.Equals(choice.SelectedFlow, "SCL", StringComparison.OrdinalIgnoreCase))
        {
            OpenSclImportFlow();
            return;
        }

        OpenIpOnlyAddFlow(sender, e);
    }

    private void OpenIpOnlyAddFlow(object sender, RoutedEventArgs e)
    {
        var wizard = new IpConnectWizardWindow
        {
            Owner = this,
            RelayIpAddress = string.IsNullOrWhiteSpace(RelayIpAddress) ? (RecentRelayIps.FirstOrDefault() ?? string.Empty) : RelayIpAddress,
            MmsPort = MmsPort,
            UseRealIecEngine = UseRealIecEngine,
            UseNativeCleanRoomEngine = true
        };

        TrackActiveWizard(wizard);
        if (wizard.ShowDialog() != true)
        {
            AddLog("INFO", "IP Discovery", "IP-only IED setup cancelled. No draft or empty relay slot was created.");
            return;
        }

        var ip = NormalizeRelayIp(wizard.RelayIpAddress);
        if (string.IsNullOrWhiteSpace(ip))
        {
            AddLog("ERROR", "IP Discovery", "IP-only wizard returned no IP. Operation cancelled.");
            return;
        }

        var duplicate = Relays.FirstOrDefault(r => string.Equals(r.IpAddress, ip, StringComparison.OrdinalIgnoreCase) && r.MmsPort == wizard.MmsPort);
        if (duplicate != null)
        {
            SetActiveRelay(duplicate);
            AddLog("WARN", "Relay", $"IED endpoint already exists: {duplicate.EndpointText}. ArServer selected the existing IED instead of creating a duplicate. Use Edit IED Wizard to change its mapping.");
            NavigateToTab(0);
            return;
        }

        RelayIpAddress = ip;
        MmsPort = wizard.MmsPort;
        UseNativeCleanRoomEngine = wizard.UseNativeCleanRoomEngine;
        UseRealIecEngine = !UseNativeCleanRoomEngine && wizard.UseRealIecEngine;
        if (RelayIpTextBox != null)
            RelayIpTextBox.Text = ip;

        AddLog("INFO", "IP Discovery", $"IP-only flow accepted endpoint {ip}:{MmsPort}. Starting {(UseNativeCleanRoomEngine ? "native clean-room" : UseRealIecEngine ? "external runtime" : "mock")} online MMS browse before Signal Map and Modbus Binding.");
        _openConfigWizardAfterDiscovery = true;
        ConnectDiscover_Click(sender, e);
    }

    private void OpenSclImportFlow()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open IEC 61850 SCL/CID/SCD/ICD",
            Filter = "IEC 61850 SCL files (*.cid;*.scd;*.icd;*.iid;*.sed;*.xml)|*.cid;*.scd;*.icd;*.iid;*.sed;*.xml|All files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            AddLog("INFO", "SCL Import", "Open SCL cancelled before importing file.");
            return;
        }

        try
        {
            var import = SclImportService.Import(dialog.FileName);
            var wizard = new SclImportWizardWindow(import) { Owner = this };
            TrackActiveWizard(wizard);
            if (wizard.ShowDialog() != true)
            {
                AddLog("INFO", "SCL Import", "SCL plan window cancelled. Imported file was not saved to runtime.");
                return;
            }

            ApplySclImport(wizard.ImportedScl, UseRealIecEngine, wizard.RuntimeIpAddress, wizard.MmsPort, wizard.SelectedReportControl, wizard.ReportRuntimeMode);
        }
        catch (Exception ex)
        {
            AddExceptionLog("SCL Import", ex, "Failed to import SCL/CID file");
        }
    }

    private void ApplySclImport(SclImportResult import, bool useRealEngine, string runtimeIpAddress, int runtimePort, SclReportControlModel? selectedRcb, string reportRuntimeMode)
    {
        if (IsRuntimeRunning)
        {
            AddLog("WARN", "Runtime", "SCL import is blocked while runtime is running. Stop Runtime before changing IED topology.");
            NavigateToTab(1);
            return;
        }

        var sclIp = NormalizeRelayIp(import.IpAddress);
        var ip = NormalizeRelayIp(string.IsNullOrWhiteSpace(runtimeIpAddress) ? import.RuntimeIpAddress : runtimeIpAddress);
        if (string.IsNullOrWhiteSpace(ip)) ip = sclIp;
        var runtimePortToUse = runtimePort > 0 ? runtimePort : (import.MmsPort <= 0 ? 102 : import.MmsPort);
        var chosenRcb = selectedRcb ?? import.ReportControls.FirstOrDefault();

        AddLog("INFO", "SCL Import", $"Imported {Path.GetFileName(import.FilePath)} as draft only: IED={import.IedName}, AP={import.AccessPointName}, SCL IP={(string.IsNullOrWhiteSpace(sclIp) ? "not in SCL" : sclIp)}, Runtime IP={(string.IsNullOrWhiteSpace(ip) ? "not configured" : ip)}, Signals={import.Signals.Count}, DataSets={import.DataSets.Count}, RCB={import.ReportControls.Count}.");
        AddLog("INFO", "SCL Import", "Workspace is not changed yet. The imported SCL model will be committed only after Add to Runtime in the Signal/Binding wizard.");

        var draftSignals = CloneSignals(import.Signals.OrderBy(s => s.SortPriority).ThenBy(s => s.LogicalNode).ThenBy(s => s.Name));
        var draftBindings = new ObservableCollection<BindingItem>();
        var displayName = string.IsNullOrWhiteSpace(import.IedName) ? "SCL-IED" : import.IedName;

        var saved = OpenDraftIedConfigurationWizardAndCommit(
            context: $"SCL imported IED {displayName}",
            draftSignals: draftSignals,
            draftBindings: draftBindings,
            runtimeIpAddress: ip,
            runtimePort: runtimePortToUse,
            useRealEngine: useRealEngine,
            useNativeCleanRoomEngine: true,
            suggestedIedName: displayName,
            mode: "CID/SCD model / Native clean-room runtime",
            status: "Configured",
            heartbeat: "Ready for runtime verification",
            sclIpAddress: sclIp,
            sclFilePath: import.FilePath,
            sclSummary: import.Summary,
            dataSetCount: import.DataSets.Count,
            reportControlCount: import.ReportControls.Count,
            selectedRcbName: chosenRcb?.Name ?? "Polling",
            selectedRcbReference: chosenRcb?.Reference ?? string.Empty,
            reportRuntimeMode: string.IsNullOrWhiteSpace(reportRuntimeMode) ? "Static RCB candidate" : reportRuntimeMode,
            rcbMode: import.ReportControls.Count > 0 ? "RCB candidate / MMS polling fallback" : "MMS polling");

        if (!saved)
        {
            AddLog("INFO", "SCL Import", "SCL wizard cancelled before Save to Runtime. No signal, binding, or relay session was committed.");
            RestoreWorkspaceAfterDraftCancel();
            return;
        }

        AddLog("INFO", "SCL Import", $"Selected RCB plan committed: {(chosenRcb == null ? "None" : chosenRcb.Reference)} / {reportRuntimeMode}. SCL file is not modified; only ArServer runtime endpoint is overridden.");
        NavigateToTab(0);
    }

    private bool OpenDraftIedConfigurationWizardAndCommit(
        string context,
        ObservableCollection<SignalDefinition> draftSignals,
        ObservableCollection<BindingItem> draftBindings,
        string runtimeIpAddress,
        int runtimePort,
        bool useRealEngine,
        bool useNativeCleanRoomEngine,
        string suggestedIedName,
        string mode,
        string status,
        string heartbeat,
        string sclIpAddress = "",
        string sclFilePath = "",
        string sclSummary = "",
        int dataSetCount = 0,
        int reportControlCount = 0,
        string selectedRcbName = "Polling",
        string selectedRcbReference = "",
        string reportRuntimeMode = "MMS polling only",
        string rcbMode = "MMS polling")
    {
        var wizard = new IedConfigurationWizardWindow(draftSignals, draftBindings)
        {
            Owner = this
        };

        AddLog("INFO", "Wizard", $"Draft configuration wizard opened for {context}. Workspace IEC/Modbus remains unchanged until Add to Runtime is pressed.");
        TrackActiveWizard(wizard);
        if (wizard.ShowDialog() != true)
            return false;

        RelayIpAddress = runtimeIpAddress;
        MmsPort = runtimePort > 0 ? runtimePort : 102;
        UseRealIecEngine = useRealEngine;
        UseNativeCleanRoomEngine = useNativeCleanRoomEngine;
        if (UseNativeCleanRoomEngine)
            AddLog("INFO", "Native IEC61850", string.IsNullOrWhiteSpace(sclFilePath)
                ? "IP-only workflow committed to native clean-room runtime path. Online discovery is based on MMS GetNameList; polling uses the native Confirmed-Read path."
                : "SCL workflow committed to native clean-room runtime path. Open SCL remains the most deterministic route when engineering files are available.");
        if (RelayIpTextBox != null)
            RelayIpTextBox.Text = RelayIpAddress;

        var relay = Relays.FirstOrDefault(r => !string.IsNullOrWhiteSpace(runtimeIpAddress) && string.Equals(r.IpAddress, runtimeIpAddress, StringComparison.OrdinalIgnoreCase) && r.MmsPort == MmsPort);
        if (relay == null)
        {
            relay = new RelayEndpointView();
            Relays.Add(relay);
        }

        relay.IedName = MakeUniqueRelayName(suggestedIedName, runtimeIpAddress, relay);
        relay.IpAddress = runtimeIpAddress;
        relay.MmsPort = MmsPort;
        relay.Status = status;
        relay.Mode = mode;
        relay.HeartbeatText = heartbeat;
        relay.SclIpAddress = sclIpAddress;
        relay.SclFilePath = sclFilePath;
        relay.SclSummary = sclSummary;
        relay.DataSetCount = dataSetCount;
        relay.ReportControlCount = reportControlCount;
        relay.RcbName = string.IsNullOrWhiteSpace(selectedRcbName) ? "Polling" : selectedRcbName;
        relay.RcbMode = string.IsNullOrWhiteSpace(rcbMode) ? "MMS polling" : rcbMode;
        relay.ReportRuntimeMode = string.IsNullOrWhiteSpace(reportRuntimeMode) ? "MMS polling only" : reportRuntimeMode;
        relay.SelectedReportControlReference = selectedRcbReference ?? string.Empty;
        relay.StatusBrush = RelayEndpointView.BrushForStatus(status);
        relay.ActivityBrush = RelayEndpointView.BrushForStatus(status);

        SaveWorkspaceToRelay(relay, wizard.Signals, wizard.Bindings);
        relay.RcbName = string.IsNullOrWhiteSpace(selectedRcbName) ? relay.RcbName : selectedRcbName;
        relay.RcbMode = string.IsNullOrWhiteSpace(rcbMode) ? relay.RcbMode : rcbMode;
        relay.ReportRuntimeMode = string.IsNullOrWhiteSpace(reportRuntimeMode) ? relay.ReportRuntimeMode : reportRuntimeMode;
        relay.SelectedReportControlReference = selectedRcbReference ?? relay.SelectedReportControlReference;
        relay.SclIpAddress = sclIpAddress;
        relay.SclFilePath = sclFilePath;
        relay.SclSummary = sclSummary;
        relay.DataSetCount = dataSetCount;
        relay.ReportControlCount = reportControlCount;
        relay.RefreshComputed();

        SetActiveRelay(relay);
        LoadWorkspaceFromRelay(relay, loadBindings: false);
        RebuildPublishedBindingsFromRelays();
        SignalsView.Refresh();
        Raise(nameof(SignalCount));
        Raise(nameof(VisibleSignalCountText));
        Raise(nameof(BindingCount));
        AddLog("INFO", "Wizard", $"{relay.DisplayName} committed to runtime. Selected IEC signals: {relay.Signals.Count(s => s.IsSelected)}. Published Modbus map: {PublishedModbusBindings.Count} binding(s) from all saved IEDs.");
        return true;
    }

    private void RestoreWorkspaceAfterDraftCancel()
    {
        if (SelectedRelay != null && Relays.Contains(SelectedRelay))
        {
            LoadWorkspaceFromRelay(SelectedRelay, loadBindings: false);
        }
        else
        {
            Signals.Clear();
        }
        SignalsView.Refresh();
        Raise(nameof(SignalCount));
        Raise(nameof(VisibleSignalCountText));
        RebuildPublishedBindingsFromRelays();
    }

    private void RelayCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is RelayEndpointView relay)
        {
            SetActiveRelay(relay);
            NavigateToTab(0);
        }
    }

    private void OpenRelay_Click(object sender, RoutedEventArgs e)
    {
        var relay = GetRelayFromSender(sender);
        if (relay == null) return;
        SetActiveRelay(relay);
        AddLog("INFO", "Relay", $"Opened IED workspace: {relay.DisplayName} {relay.EndpointText}.");
        NavigateToTab(0);
    }

    private void EditRelay_Click(object sender, RoutedEventArgs e)
    {
        var relay = GetRelayFromSender(sender);
        if (relay == null) return;
        EditRelayInternal(relay, sender, e);
    }

    private void EditActiveRelay_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRelay == null)
        {
            AddRelay_Click(sender, e);
            return;
        }

        EditRelayInternal(SelectedRelay, sender, e);
    }

    private void EditRelayInternal(RelayEndpointView relay, object sender, RoutedEventArgs e)
    {
        SetActiveRelay(relay);

        if (IsRuntimeRunning)
        {
            AddLog("WARN", "Runtime", "Edit IED Wizard is blocked while runtime is running. Stop Runtime first before changing selected signals or Modbus mapping.");
            NavigateToTab(1);
            return;
        }

        if (Signals.Count > 0)
        {
            OpenConfigurationWizardInternal($"existing IED {relay.DisplayName}");
            NavigateToTab(0);
            return;
        }

        // If the IED has no current online model loaded, reopen the IP-only connect step first.
        var wizard = new IpConnectWizardWindow
        {
            Owner = this,
            RelayIpAddress = relay.IpAddress,
            MmsPort = relay.MmsPort,
            UseRealIecEngine = UseRealIecEngine
        };

        TrackActiveWizard(wizard);
        if (wizard.ShowDialog() == true)
        {
            RelayIpAddress = NormalizeRelayIp(wizard.RelayIpAddress);
            MmsPort = wizard.MmsPort;
            UseRealIecEngine = wizard.UseRealIecEngine;
            if (RelayIpTextBox != null)
                RelayIpTextBox.Text = RelayIpAddress;
            AddLog("INFO", "Relay", $"Re-discovering edited IED endpoint {RelayIpAddress}:{MmsPort}. Configuration wizard will open after discovery.");
            _openConfigWizardAfterDiscovery = true;
            ConnectDiscover_Click(sender, e);
            return;
        }

        AddLog("INFO", "Relay", $"Edit wizard closed for {relay.DisplayName}. Existing binding remains unchanged.");
        NavigateToTab(0);
    }

    private async void ToggleRelayConnection_Click(object sender, RoutedEventArgs e)
    {
        var relay = GetRelayFromSender(sender);
        if (relay == null) return;
        SetActiveRelay(relay);

        if (relay.IsConnectedLike)
        {
            relay.Status = "Disconnected";
            relay.HeartbeatText = "Disabled by user";
            relay.StatusBrush = RelayEndpointView.BrushForStatus("Disconnected");
            relay.RefreshComputed();

            // There is currently one active IEC client instance. Disconnecting the active IED
            // closes the IEC session but intentionally does NOT stop the Modbus TCP server/runtime.
            if (_iecClient != null && string.Equals(RelayIpAddress, relay.IpAddress, StringComparison.OrdinalIgnoreCase))
            {
                await DisposeActiveIecClientAsync();
                IedConnectionStatus = "Disconnected";
                foreach (var binding in Bindings)
                {
                    binding.Status = "IED disabled";
                    binding.Quality = "Disabled";
                }
            }

            AddLog("INFO", "Relay", $"IED disconnected individually: {relay.DisplayName} {relay.EndpointText}. Modbus gateway runtime state was not changed.");
            Raise(nameof(IecInsightText));
            Raise(nameof(ActiveRelaySubtitle));
            return;
        }

        if (string.IsNullOrWhiteSpace(relay.IpAddress))
        {
            AddLog("WARN", "Relay", "Selected IED has no IP. Use Add IED or Edit IED Wizard first.");
            return;
        }

        relay.Status = "Connecting";
        relay.HeartbeatText = "MMS reconnecting";
        relay.StatusBrush = RelayEndpointView.BrushForStatus("Connecting");
        relay.RefreshComputed();

        RelayIpAddress = NormalizeRelayIp(relay.IpAddress);
        MmsPort = relay.MmsPort;
        if (RelayIpTextBox != null)
            RelayIpTextBox.Text = RelayIpAddress;
        AddLog("INFO", "Relay", $"Reconnecting IED individually: {relay.DisplayName} {relay.EndpointText}. Modbus server remains under the main Runtime Start/Stop control.");
        ConnectDiscover_Click(sender, e);
    }

    private async void DisconnectRelay_Click(object sender, RoutedEventArgs e)
    {
        var relay = GetRelayFromSender(sender);
        if (relay == null) return;
        relay.Status = "Paused";
        relay.HeartbeatText = "Disconnected by user";
        relay.StatusBrush = RelayEndpointView.BrushForStatus("Warning");
        relay.RefreshComputed();

        if (relay.IsActive)
        {
            if (IsRuntimeRunning)
                await StopRuntimeSafeAsync();
            if (_iecClient != null)
            {
                await DisposeActiveIecClientAsync();
            }
            IedConnectionStatus = "Paused";
        }

        AddLog("INFO", "Relay", $"IED paused/disconnected: {relay.DisplayName} {relay.EndpointText}. Configuration remains available for reconnect/edit.");
    }

    private async void DeleteRelay_Click(object sender, RoutedEventArgs e)
    {
        var relay = GetRelayFromSender(sender);
        if (relay == null) return;

        if (IsRuntimeRunning)
        {
            AddLog("WARN", "Runtime", $"Delete IED is locked while runtime is running: {relay.DisplayName}. Stop Runtime first so Modbus publishing cannot keep stale bindings.");
            NavigateToTab(1);
            return;
        }

        var wasActive = relay.IsActive;
        var relayId = relay.RelayId;
        var relayEndpoint = relay.IpAddress;
        var removedBindingCount = relay.ModbusBindings.Count;
        var removedSignalCount = relay.Signals.Count;

        relay.ModbusBindings.Clear();
        relay.Signals.Clear();

        for (var i = Bindings.Count - 1; i >= 0; i--)
        {
            var binding = Bindings[i];
            var sameRelayId = !string.IsNullOrWhiteSpace(relayId) && string.Equals(binding.RelayId, relayId, StringComparison.OrdinalIgnoreCase);
            var sameEndpoint = !string.IsNullOrWhiteSpace(relayEndpoint) && string.Equals(binding.RelayIpAddress, relayEndpoint, StringComparison.OrdinalIgnoreCase);
            var sameName = string.Equals(binding.IedName, relay.DisplayName, StringComparison.OrdinalIgnoreCase);
            if (sameRelayId || sameEndpoint || sameName)
                Bindings.RemoveAt(i);
        }

        Relays.Remove(relay);
        RebuildPublishedBindingsFromRelays();

        if (wasActive)
        {
            Signals.Clear();
            RelayIpAddress = string.Empty;
            if (RelayIpTextBox != null) RelayIpTextBox.Text = string.Empty;
            SelectedBinding = null;
            IedConnectionStatus = Relays.Count > 0 ? "Connected" : "Disconnected";
            if (Relays.Count > 0)
                SetActiveRelay(Relays[0]);
            else
                SelectedRelay = null;
        }

        SignalsView.Refresh();
        Raise(nameof(SignalCount));
        Raise(nameof(BindingCount));
        Raise(nameof(VisibleSignalCountText));
        Raise(nameof(RuntimeInsightText));
        AddLog("WARN", "Relay", $"IED removed: {relay.DisplayName} {relay.EndpointText}. Cleared {removedSignalCount} IEC signals and {removedBindingCount} Modbus bindings owned by this IED.");
    }

    private RelayEndpointView? GetRelayFromSender(object sender)
    {
        return sender is FrameworkElement element ? element.DataContext as RelayEndpointView : null;
    }

    private void SetActiveRelay(RelayEndpointView relay)
    {
        foreach (var item in Relays)
        {
            item.IsActive = ReferenceEquals(item, relay);
            item.RefreshComputed();
        }

        SelectedRelay = relay;
        RelayIpAddress = NormalizeRelayIp(relay.IpAddress);
        MmsPort = relay.MmsPort;
        if (RelayIpTextBox != null)
            RelayIpTextBox.Text = RelayIpAddress;

        LoadWorkspaceFromRelay(relay, loadBindings: false);
        SignalsView.Refresh();
        Raise(nameof(SignalCount));
        Raise(nameof(VisibleSignalCountText));
        Raise(nameof(ActiveRelayTitle));
        Raise(nameof(ActiveRelaySubtitle));
    }

    private async Task StopRuntimeSafeAsync()
    {
        try
        {
            if (_runtime != null)
                await _runtime.StopAsync();
            RuntimeStatusText = "Stopped";
            MoveRuntimeToggle(false, true);
        }
        catch (Exception ex)
        {
            AddExceptionLog("Runtime", ex, "Runtime stop during IED lifecycle action failed");
        }
    }

    private void RecentRelay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is string ip && !string.IsNullOrWhiteSpace(ip))
        {
            RelayIpAddress = NormalizeRelayIp(ip);
            if (RelayIpTextBox != null)
                RelayIpTextBox.Text = RelayIpAddress;
            AddLog("INFO", "Preferences", $"Selected successful relay endpoint: {RelayIpAddress}:{MmsPort}");
        }
    }


    private void EnsureActiveRelayChip()
    {
        if (Relays.Count > 0) return;
        var relay = new RelayEndpointView
        {
            IedName = "Relay Session",
            IpAddress = RelayIpAddress,
            MmsPort = MmsPort,
            Status = IedConnectionStatus,
            Mode = UseRealIecEngine ? "Real MMS" : "Mock/MMS",
            TagCount = Signals.Count,
            HeartbeatText = "Idle",
            IsActive = true,
            StatusBrush = RelayEndpointView.BrushForStatus(IedConnectionStatus)
        };
        relay.RefreshComputed();
        Relays.Add(relay);
    }

    private void UpsertRelayChip(string ipAddress, string status, int tagCount, string mode)
    {
        if (string.IsNullOrWhiteSpace(ipAddress)) return;
        var relay = Relays.FirstOrDefault(r => string.Equals(r.IpAddress, ipAddress, StringComparison.OrdinalIgnoreCase));
        relay ??= Relays.FirstOrDefault(r => r.IsActive && string.Equals(r.Status, "Draft", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(r.IpAddress));
        if (relay == null)
        {
            relay = new RelayEndpointView();
            Relays.Add(relay);
        }

        var detectedName = InferIedNameFromSignals() ?? "IED";
        relay.IpAddress = ipAddress;
        relay.MmsPort = MmsPort;
        relay.IedName = MakeUniqueRelayName(detectedName, ipAddress, relay);
        relay.Status = status;
        relay.Mode = mode;
        relay.TagCount = tagCount;
        relay.HeartbeatText = status.Contains("Connected", StringComparison.OrdinalIgnoreCase) ? "MMS live" : status;
        relay.RcbName = DetectRcbNameForRelay();
        relay.RcbMode = "MMS polling";
        relay.StatusBrush = RelayEndpointView.BrushForStatus(status);
        relay.ActivityBrush = RelayEndpointView.BrushForStatus(status.Contains("Connected", StringComparison.OrdinalIgnoreCase) ? "Connecting" : status);
        SaveWorkspaceToRelay(relay, Signals, relay.ModbusBindings.Count > 0 ? relay.ModbusBindings : new ObservableCollection<BindingItem>());
        relay.RefreshComputed();
        SetActiveRelay(relay);
    }

    private string MakeUniqueRelayName(string baseName, string ipAddress, RelayEndpointView currentRelay)
    {
        var candidate = string.IsNullOrWhiteSpace(baseName) ? "IED" : baseName.Trim();
        var duplicate = Relays.Any(r => !ReferenceEquals(r, currentRelay) && string.Equals(r.IedName, candidate, StringComparison.OrdinalIgnoreCase));
        if (!duplicate) return candidate;

        var suffix = ipAddress.Split('.').LastOrDefault();
        return string.IsNullOrWhiteSpace(suffix) ? $"{candidate} Alias" : $"{candidate}-{suffix}";
    }

    private string? InferIedNameFromSignals()
    {
        var firstRef = Signals.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.ObjectReference))?.ObjectReference;
        if (string.IsNullOrWhiteSpace(firstRef)) return null;
        var slash = firstRef.IndexOf('/');
        return slash > 0 ? firstRef[..slash] : null;
    }

    private void UpdateActiveRelayHeartbeat(string text)
    {
        var relay = Relays.FirstOrDefault(r => r.IsActive) ?? Relays.FirstOrDefault();
        if (relay == null) return;
        relay.HeartbeatText = text;
        relay.Status = IedConnectionStatus;
        relay.StatusBrush = RelayEndpointView.BrushForStatus(relay.Status);
        relay.ActivityBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94));
        _lastIecActivityAt = DateTime.Now;
        relay.RefreshComputed();
        Raise(nameof(ActiveRelaySubtitle));
    }

    private void PulseIecActivity(BindingItem? binding = null)
    {
        _lastIecActivityAt = DateTime.Now;
        AnimateDot(RelayActivityDot, System.Windows.Media.Color.FromRgb(37, 99, 235), System.Windows.Media.Color.FromRgb(34, 197, 94));

        var relay = binding != null ? FindRelayForBinding(binding) : (Relays.FirstOrDefault(r => r.IsActive) ?? Relays.FirstOrDefault());
        if (relay != null)
        {
            relay.LastMmsActivityUtc = DateTime.Now;

            if (binding != null && !binding.Quality.Equals("Good", StringComparison.OrdinalIgnoreCase))
            {
                ApplyRelayRuntimeReadState(relay, binding);
            }
            else
            {
                relay.ActivityBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94));
                relay.HeartbeatText = "MMS stream active";
                if (!relay.Status.Contains("failed", StringComparison.OrdinalIgnoreCase) && !relay.Status.Contains("error", StringComparison.OrdinalIgnoreCase))
                {
                    relay.Status = "Connected";
                    relay.StatusBrush = RelayEndpointView.BrushForStatus(relay.Status);
                }
                relay.RefreshComputed();
            }

            if (relay.IsActive)
                Raise(nameof(ActiveRelaySubtitle));
        }
    }

    private void PulseModbusActivity()
    {
        _lastModbusActivityAt = DateTime.Now;
        AnimateDot(ModbusActivityDot, System.Windows.Media.Color.FromRgb(37, 99, 235), System.Windows.Media.Color.FromRgb(34, 197, 94));
    }

    private void AnimateDot(Border dot, System.Windows.Media.Color idleColor, System.Windows.Media.Color activeColor)
    {
        try
        {
            if (dot.Background is not SolidColorBrush brush || brush.IsFrozen)
            {
                brush = new SolidColorBrush(idleColor);
                dot.Background = brush;
            }

            var animation = new ColorAnimationUsingKeyFrames();
            animation.KeyFrames.Add(new EasingColorKeyFrame(idleColor, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new EasingColorKeyFrame(activeColor, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
            animation.KeyFrames.Add(new EasingColorKeyFrame(idleColor, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520))));
            brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        }
        catch
        {
            // Activity animation must never affect gateway runtime.
        }
    }

    private void QueueRuntimeSnapshot(BindingItem binding)
    {
        PulseIecActivity(binding);
        UpdateSignalFromBinding(binding);
        var key = $"{binding.RelayId}|{binding.IecReference}";
        _pendingRuntimeSnapshots[key] = new RuntimeValueSnapshot
        {
            RelayId = binding.RelayId,
            IecReference = binding.IecReference,
            Value = binding.CurrentValue,
            Quality = binding.Quality,
            Timestamp = binding.LastUpdate,
            Status = binding.Status,
            Sequence = binding.Sequence
        };
    }

    private void RuntimeSnapshotTimer_Tick(object? sender, EventArgs e)
    {
        if (_pendingRuntimeSnapshots.IsEmpty) return;

        var selectedRelayId = SelectedRelay?.RelayId ?? string.Empty;
        var applied = 0;
        foreach (var item in _pendingRuntimeSnapshots.ToArray())
        {
            if (!_pendingRuntimeSnapshots.TryRemove(item.Key, out var snapshot))
                continue;

            // The IEC Explorer grid is intentionally snapshot-buffered.
            // Only the selected IED grid is rendered; other IEDs update cache/card status only.
            if (!string.IsNullOrWhiteSpace(selectedRelayId) &&
                !string.Equals(snapshot.RelayId, selectedRelayId, StringComparison.OrdinalIgnoreCase))
                continue;

            var signal = Signals.FirstOrDefault(s => string.Equals(s.ObjectReference, snapshot.IecReference, StringComparison.OrdinalIgnoreCase));
            if (signal == null) continue;
            signal.Value = snapshot.Value;
            signal.Quality = snapshot.Quality;
            signal.Timestamp = snapshot.Timestamp;
            applied++;
        }

        if (applied > 0)
        {
            Raise(nameof(VisibleSignalCountText));
            if (SelectedRelay != null)
                Raise(nameof(ActiveRelaySubtitle));
        }
    }

    private void ActivityResetTimer_Tick(object? sender, EventArgs e)
    {
        foreach (var relay in Relays)
        {
            if ((DateTime.Now - relay.LastMmsActivityUtc).TotalMilliseconds <= 650)
                continue;

            if (relay.Status.Contains("failed", StringComparison.OrdinalIgnoreCase) || relay.Status.Contains("error", StringComparison.OrdinalIgnoreCase) || relay.Status.Contains("disconnected", StringComparison.OrdinalIgnoreCase))
                relay.ActivityBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
            else if (relay.Status.Contains("connected", StringComparison.OrdinalIgnoreCase) || relay.Status.Contains("live", StringComparison.OrdinalIgnoreCase))
                relay.ActivityBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235));
            else
                relay.ActivityBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184));
            relay.RefreshComputed();
        }

        if ((DateTime.Now - _lastModbusActivityAt).TotalMilliseconds > 650 && ModbusActivityDot.Background is SolidColorBrush mb && !mb.IsFrozen)
            mb.Color = System.Windows.Media.Color.FromRgb(148, 163, 184);
    }

    private ObservableCollection<SignalDefinition> CloneSignals(IEnumerable<SignalDefinition> source)
    {
        var result = new ObservableCollection<SignalDefinition>();
        foreach (var s in source)
        {
            result.Add(new SignalDefinition
            {
                IsSelected = s.IsSelected,
                IsReportCapable = s.IsReportCapable,
                Name = s.Name,
                ObjectReference = s.ObjectReference,
                FunctionalConstraint = s.FunctionalConstraint,
                DataType = s.DataType,
                Category = s.Category,
                Unit = s.Unit,
                Confidence = s.Confidence,
                DataSetReference = s.DataSetReference,
                ReportControlReference = s.ReportControlReference,
                Source = s.Source,
                Value = s.Value,
                Quality = s.Quality,
                Timestamp = s.Timestamp
            });
        }
        return result;
    }

    private ObservableCollection<BindingItem> CloneBindings(IEnumerable<BindingItem> source)
    {
        var result = new ObservableCollection<BindingItem>();
        foreach (var b in source)
            result.Add(CloneBinding(b));
        return result;
    }

    private BindingItem CloneBinding(BindingItem b)
    {
        return new BindingItem
        {
            RelayId = b.RelayId,
            IedName = b.IedName,
            RelayIpAddress = b.RelayIpAddress,
            IsEnabled = b.IsEnabled,
            PublishToModbus = b.PublishToModbus,
            PublishToMqtt = b.PublishToMqtt,
            SignalName = b.SignalName,
            IecReference = b.IecReference,
            FunctionalConstraint = b.FunctionalConstraint,
            IecDataType = b.IecDataType,
            Category = b.Category,
            Unit = b.Unit,
            ReadMode = b.ReadMode,
            RcbMode = b.RcbMode,
            DataSetReference = b.DataSetReference,
            ReportControlReference = b.ReportControlReference,
            PollingIntervalMs = b.PollingIntervalMs,
            StaleTimeoutMs = b.StaleTimeoutMs,
            ModbusArea = b.ModbusArea,
            ModbusAddress = b.ModbusAddress,
            ModbusDataType = b.ModbusDataType,
            WordOrder = b.WordOrder,
            Scale = b.Scale,
            Offset = b.Offset,
            FuxaTagName = b.FuxaTagName,
            MqttTopic = b.MqttTopic,
            CurrentValue = b.CurrentValue,
            Quality = b.Quality,
            Status = b.Status,
            Sequence = b.Sequence,
            LastUpdate = b.LastUpdate,
            AgeMs = b.AgeMs
        };
    }

    private void LoadWorkspaceFromRelay(RelayEndpointView relay, bool loadBindings)
    {
        Signals.Clear();
        foreach (var signal in relay.Signals.OrderBy(s => s.SortPriority).ThenBy(s => s.LogicalNode).ThenBy(s => s.Name))
            Signals.Add(signal);

        if (loadBindings)
        {
            Bindings.Clear();
            foreach (var binding in relay.ModbusBindings)
                Bindings.Add(binding);
            SelectedBinding = Bindings.FirstOrDefault();
            Raise(nameof(BindingCount));
        }
    }

    private void SaveWorkspaceToRelay(RelayEndpointView relay, IEnumerable<SignalDefinition> signals, IEnumerable<BindingItem> bindings)
    {
        relay.Signals.Clear();
        foreach (var signal in signals)
            relay.Signals.Add(signal);

        var bindingList = bindings.Select(CloneBinding).ToList();
        BindingAutoMapper.ArrangeExistingBindings(bindingList, GetRelayBlockIndex(relay));

        relay.ModbusBindings.Clear();
        foreach (var binding in bindingList)
        {
            binding.RelayId = relay.RelayId;
            binding.IedName = relay.DisplayName;
            binding.RelayIpAddress = relay.IpAddress;
            relay.ModbusBindings.Add(binding);
        }

        relay.TagCount = relay.Signals.Count(s => s.IsSelected);
        relay.RcbName = DetectRcbNameForRelay(relay.Signals);
        relay.RcbMode = relay.Signals.Any(s => s.IsReportCapable) ? "SCL report-aware / MMS polling" : "MMS polling";
        relay.ReportControlCount = Math.Max(relay.ReportControlCount, relay.Signals.Select(s => s.ReportControlReference).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        relay.DataSetCount = Math.Max(relay.DataSetCount, relay.Signals.Select(s => s.DataSetReference).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        relay.RefreshComputed();
    }

    private void SyncLiveWorkspaceToRelay(RelayEndpointView relay)
    {
        if (relay.Signals.Count == 0) return;
        foreach (var signal in Signals)
        {
            var target = relay.Signals.FirstOrDefault(s => string.Equals(s.ObjectReference, signal.ObjectReference, StringComparison.OrdinalIgnoreCase));
            if (target == null) continue;
            target.Value = signal.Value;
            target.Quality = signal.Quality;
            target.Timestamp = signal.Timestamp;
        }
    }

    private void RebuildPublishedBindingsFromRelays()
    {
        ArrangeAllRelayModbusBlocks();

        // Authoritative Modbus publication map. This is deliberately rebuilt from
        // every RelayEndpointView.ModbusBindings so the Modbus Server tab/runtime never
        // follows the selected IEC Explorer card by accident.
        PublishedModbusBindings.Clear();
        Bindings.Clear(); // legacy mirror for older helper methods/project compatibility

        foreach (var relay in Relays)
        {
            foreach (var binding in relay.ModbusBindings.Where(b => b.IsEnabled || !string.IsNullOrWhiteSpace(b.IecReference)))
            {
                binding.RelayId = relay.RelayId;
                binding.IedName = relay.DisplayName;
                binding.RelayIpAddress = relay.IpAddress;
                PublishedModbusBindings.Add(binding);
                Bindings.Add(binding);
            }
        }

        SelectedBinding = PublishedModbusBindings.FirstOrDefault();
        Raise(nameof(BindingCount));
        Raise(nameof(RuntimeInsightText));
        Raise(nameof(IecInsightText));
        AddLog("INFO", "Binding", $"Published Modbus map rebuilt from {Relays.Count} IED session(s): {PublishedModbusBindings.Count} binding(s). The Modbus grid is now decoupled from selected IEC workspace.");
    }

    private void PreparePublishedModbusMapForRuntime()
    {
        // Keep each relay owning its own binding list. If an old project still has
        // legacy global bindings only, attach them to the selected/first relay once.
        if (Relays.Count > 0 && Relays.All(r => r.ModbusBindings.Count == 0) && Bindings.Count > 0)
        {
            var owner = SelectedRelay ?? Relays.FirstOrDefault();
            if (owner != null)
            {
                foreach (var legacy in Bindings.ToList())
                    owner.ModbusBindings.Add(CloneBinding(legacy));
                AddLog("WARN", "Migration", $"Legacy global Modbus bindings were attached to {owner.DisplayName}. Save project once to persist the per-IED model.");
            }
        }

        RebuildPublishedBindingsFromRelays();
    }

    private string DetectRcbNameForRelay(IEnumerable<SignalDefinition>? signals = null)
    {
        var source = signals ?? Signals;
        var rcb = source.Select(s => s.ReportControlReference)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (string.IsNullOrWhiteSpace(rcb)) return "Polling";
        var lastDot = rcb.LastIndexOf('.');
        return lastDot >= 0 && lastDot < rcb.Length - 1 ? rcb[(lastDot + 1)..] : rcb;
    }

    private void ArrangeAllRelayModbusBlocks()
    {
        for (var index = 0; index < Relays.Count; index++)
        {
            var relay = Relays[index];
            if (relay.ModbusBindings.Count == 0) continue;
            BindingAutoMapper.ArrangeExistingBindings(relay.ModbusBindings, index);
            foreach (var binding in relay.ModbusBindings)
            {
                binding.RelayId = relay.RelayId;
                binding.IedName = relay.DisplayName;
                binding.RelayIpAddress = relay.IpAddress;
            }
        }

        // If the project still contains legacy/global bindings without relay ownership,
        // arrange them in the first block so old projects remain usable.
        var orphanBindings = PublishedModbusBindings.Where(b => string.IsNullOrWhiteSpace(b.RelayId)).ToList();
        if (Relays.Count == 0 && orphanBindings.Count > 0)
            BindingAutoMapper.ArrangeExistingBindings(orphanBindings, 0);
    }

    private int GetRelayBlockIndex(RelayEndpointView relay)
    {
        var index = Relays.IndexOf(relay);
        return index < 0 ? Math.Max(0, Relays.Count) : index;
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        Logs.Clear();
        AddLog("INFO", "System", "Logs cleared.");
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl) return;
        UpdateNavigationVisuals(MainTabs.SelectedIndex);
        MoveWorkflowPill(MainTabs.SelectedIndex, true);
        AnimatePageTransition();
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && int.TryParse(button.Tag?.ToString(), out var index))
            NavigateToTab(index);
    }

    private void NavigateToTab(int index)
    {
        if (index < 0 || index >= MainTabs.Items.Count) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            MainTabs.SelectedIndex = index;
            UpdateNavigationVisuals(index);
            MoveWorkflowPill(index, true);
            AnimatePageTransition();
        }), DispatcherPriority.Background);
    }

    private void AnimatePageTransition()
    {
        var fade = new DoubleAnimation
        {
            From = 0.96,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        MainTabs.BeginAnimation(OpacityProperty, fade);
        PageTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        PageTranslate.Y = 0;
    }

    private void UpdateNavigationVisuals(int selectedIndex)
    {
        if (_navButtons.Count == 0) return;
        for (var i = 0; i < _navButtons.Count; i++)
        {
            var selected = i == selectedIndex;
            _navButtons[i].Foreground = selected ? System.Windows.Media.Brushes.White : (System.Windows.Media.Brush)FindResource("Muted");
            _navButtons[i].FontWeight = selected ? FontWeights.SemiBold : FontWeights.Medium;
        }
    }

    private void MoveWorkflowPill(int index, bool animate)
    {
        if (WorkflowNavGrid.ActualWidth <= 0) return;
        var segmentWidth = WorkflowNavGrid.ActualWidth / Math.Max(1.0, _navButtons.Count);
        WorkflowPill.Width = Math.Max(0, segmentWidth - 8);
        var target = index * segmentWidth + 4;
        var duration = animate ? TimeSpan.FromMilliseconds(280) : TimeSpan.Zero;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        WorkflowPillTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(target, duration) { EasingFunction = easing });
        WorkflowPillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(animate ? 1.035 : 1.0, TimeSpan.FromMilliseconds(90)) { AutoReverse = true, EasingFunction = easing });
    }

    private void MoveRuntimeToggle(bool running, bool animate)
    {
        if (RuntimeToggleGrid.ActualWidth <= 0) return;
        var segmentWidth = RuntimeToggleGrid.ActualWidth / 2.0;
        RuntimeTogglePill.Width = Math.Max(0, segmentWidth - 8);
        var target = running ? segmentWidth + 4 : 4;
        var duration = animate ? TimeSpan.FromMilliseconds(260) : TimeSpan.Zero;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        RuntimeToggleTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(target, duration) { EasingFunction = easing });
        RuntimeToggleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RuntimeToggleScale.ScaleX = 1.0;
        RuntimeTogglePill.Background = new SolidColorBrush(running ? System.Windows.Media.Color.FromRgb(220, 38, 38) : System.Windows.Media.Color.FromRgb(22, 163, 74));
        StartRuntimeToggleButton.Foreground = running ? (System.Windows.Media.Brush)FindResource("Muted") : System.Windows.Media.Brushes.White;
        StopRuntimeToggleButton.Foreground = running ? System.Windows.Media.Brushes.White : (System.Windows.Media.Brush)FindResource("Muted");
    }

    private string GetRelayIpForOperation()
    {
        var comboText = string.Empty;
        try
        {
            comboText = RelayIpTextBox?.Text ?? string.Empty;
        }
        catch
        {
            // Control may not be ready during early initialization.
        }

        var candidate = !string.IsNullOrWhiteSpace(comboText) ? comboText : RelayIpAddress;
        return NormalizeRelayIp(candidate);
    }

    private (string IpAddress, int Port) ResolveSingleRuntimeEndpoint()
    {
        var binding = PublishedModbusBindings.FirstOrDefault(IsRoutedBinding) ?? PublishedModbusBindings.FirstOrDefault();
        var relay = binding == null
            ? SelectedRelay
            : Relays.FirstOrDefault(r => !string.IsNullOrWhiteSpace(binding.RelayId) && string.Equals(r.RelayId, binding.RelayId, StringComparison.OrdinalIgnoreCase))
              ?? Relays.FirstOrDefault(r => !string.IsNullOrWhiteSpace(binding.RelayIpAddress) && string.Equals(r.IpAddress, binding.RelayIpAddress, StringComparison.OrdinalIgnoreCase))
              ?? SelectedRelay;

        var ip = NormalizeRelayIp(binding?.RelayIpAddress);
        if (string.IsNullOrWhiteSpace(ip))
            ip = NormalizeRelayIp(relay?.IpAddress);
        if (string.IsNullOrWhiteSpace(ip))
            ip = GetRelayIpForOperation();

        var port = relay?.MmsPort > 0 ? relay.MmsPort : MmsPort;
        return (ip, port <= 0 ? 102 : port);
    }

    private bool IsActiveIecClientFor(string ipAddress, int port)
    {
        return _iecClient != null &&
               _iecClient.IsConnected &&
               string.Equals(_iecClientEndpointKey, BuildIecEndpointKey(ipAddress, port), StringComparison.OrdinalIgnoreCase);
    }

    private IIec61850Client CreateConfiguredIecClient()
    {
        if (UseNativeCleanRoomEngine)
            return new NativeCleanRoomIec61850Client();

        return UseRealIecEngine
            ? new RealLibIec61850Client()
            : new MockIec61850Client();
    }

    private async Task ConnectActiveIecClientAsync(string ipAddress, int port, CancellationToken cancellationToken)
    {
        await DisposeActiveIecClientAsync();
        _iecClient = CreateConfiguredIecClient();
        await _iecClient.ConnectAsync(ipAddress, port, cancellationToken);
        _iecClientEndpointKey = _iecClient.IsConnected ? BuildIecEndpointKey(ipAddress, port) : "";
    }

    private async Task DisposeActiveIecClientAsync()
    {
        if (_iecClient != null)
        {
            try { await _iecClient.DisposeAsync(); } catch { }
        }
        _iecClient = null;
        _iecClientEndpointKey = "";
    }

    private static string BuildIecEndpointKey(string ipAddress, int port) => $"{NormalizeRelayIp(ipAddress)}:{(port <= 0 ? 102 : port)}";

    private static MqttGatewaySettings CloneMqttSettings(MqttGatewaySettings settings)
    {
        return new MqttGatewaySettings
        {
            IsEnabled = settings.IsEnabled,
            BrokerHost = string.IsNullOrWhiteSpace(settings.BrokerHost) ? "127.0.0.1" : settings.BrokerHost.Trim(),
            BrokerPort = settings.BrokerPort is > 0 and <= 65535 ? settings.BrokerPort : 1883,
            ClientId = string.IsNullOrWhiteSpace(settings.ClientId) ? $"arserver-{Environment.MachineName}".ToLowerInvariant() : settings.ClientId.Trim(),
            TopicRoot = string.IsNullOrWhiteSpace(settings.TopicRoot) ? "arserver" : settings.TopicRoot.Trim(),
            QualityOfService = Math.Clamp(settings.QualityOfService, 0, 1),
            RetainLastValue = settings.RetainLastValue,
            PublishJsonState = settings.PublishJsonState,
            Username = settings.Username?.Trim() ?? "",
            Password = settings.Password ?? ""
        };
    }

    private static string NormalizeRelayIp(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            text = text[7..];
        if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            text = text[8..];
        if (text.Contains('/'))
            text = text.Split('/')[0];
        if (text.Contains(':'))
            text = text.Split(':')[0];
        return text.Trim();
    }

    private void LoadRecentRelayIps()
    {
        try
        {
            RecentRelayIps.Clear();
            foreach (var ip in UserPreferenceStore.LoadRecentRelayIps())
                RecentRelayIps.Add(ip);

            if (RecentRelayIps.Count > 0 && (string.IsNullOrWhiteSpace(RelayIpAddress) || RelayIpAddress == "192.168.1.10"))
                RelayIpAddress = RecentRelayIps[0];
        }
        catch (Exception ex)
        {
            AddExceptionLog("Preferences", ex, "Load recent relay IP list failed");
        }
    }

    private async Task RememberRelayIpAsync(string ipAddress)
    {
        var ip = NormalizeRelayIp(ipAddress);
        if (string.IsNullOrWhiteSpace(ip)) return;

        try
        {
            if (Dispatcher.CheckAccess())
            {
                if (RecentRelayIps.Contains(ip)) RecentRelayIps.Remove(ip);
                RecentRelayIps.Insert(0, ip);
                while (RecentRelayIps.Count > 12) RecentRelayIps.RemoveAt(RecentRelayIps.Count - 1);
                RelayIpAddress = ip;
            }

            await UserPreferenceStore.SaveRecentRelayIpsAsync(RecentRelayIps.ToList());
            AddLog("INFO", "Preferences", $"Relay IP remembered: {ip}");
        }
        catch (Exception ex)
        {
            AddExceptionLog("Preferences", ex, "Save recent relay IP list failed");
        }
    }


    public void HandleGlobalException(Exception exception, string source, string context)
    {
        try
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => HandleGlobalException(exception, source, context)), DispatcherPriority.Send);
                return;
            }

            CloseActiveWizardForException();
            AddExceptionLog(source, exception, context);
            OpenDiagnosticsForException();
        }
        catch
        {
            // Last safety net: never throw from the exception handler itself.
        }
    }

    private void TrackActiveWizard(Window wizard)
    {
        _activeWizardWindow = wizard;
        wizard.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeWizardWindow, wizard))
                _activeWizardWindow = null;
        };
    }

    private void CloseActiveWizardForException()
    {
        try
        {
            var wizard = _activeWizardWindow;
            if (wizard == null) return;
            _activeWizardWindow = null;
            if (wizard.IsVisible)
            {
                wizard.Close();
                AddLog("WARN", "Safety", "Open wizard was closed because an application exception was captured. See Diagnostics for details.");
            }
        }
        catch
        {
            // Wizard close must not throw while handling an exception.
        }
    }

    private void OpenDiagnosticsForException()
    {
        if (_navigatingToDiagnosticsForException) return;
        _navigatingToDiagnosticsForException = true;
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    NavigateToTab(3);
                }
                finally
                {
                    _navigatingToDiagnosticsForException = false;
                }
            }), DispatcherPriority.Background);
        }
        catch
        {
            _navigatingToDiagnosticsForException = false;
        }
    }

    private void AddExceptionLog(string source, Exception exception, string context)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => AddExceptionLog(source, exception, context)), DispatcherPriority.Send);
            return;
        }

        CloseActiveWizardForException();
        var root = UnwrapException(exception);
        AddLog("ERROR", source, $"{context}: {root.GetType().Name} — {root.Message}");

        var details = BuildExceptionDetails(exception);
        foreach (var line in details.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Take(8))
            AddLog("DEBUG", source, line);

        OpenDiagnosticsForException();
    }

    private static Exception UnwrapException(Exception exception)
    {
        return exception switch
        {
            TargetInvocationException { InnerException: not null } tie => UnwrapException(tie.InnerException!),
            AggregateException { InnerException: not null } ae => UnwrapException(ae.InnerException!),
            InvalidOperationException { InnerException: not null } ioe when ioe.Message.StartsWith("IEC 61850", StringComparison.OrdinalIgnoreCase) => UnwrapException(ioe.InnerException!),
            _ => exception
        };
    }

    private static string BuildExceptionDetails(Exception exception)
    {
        var builder = new StringBuilder();
        var cursor = exception;
        var level = 0;
        while (cursor != null && level < 4)
        {
            builder.AppendLine($"Exception[{level}]: {cursor.GetType().FullName}: {cursor.Message}");
            cursor = cursor.InnerException;
            level++;
        }
        return builder.ToString();
    }

    private void AddLog(string level, string source, string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() => AddLog(level, source, message)), DispatcherPriority.Background);
            }
            catch
            {
                // The UI may already be closing. Do not throw from diagnostics/logging.
            }
            return;
        }

        var normalizedLevel = string.IsNullOrWhiteSpace(level) ? "INFO" : level.ToUpperInvariant();
        Logs.Insert(0, new DiagnosticEntry { Time = DateTime.Now, Level = normalizedLevel, Source = source, Message = message });
        LastStatusLevel = normalizedLevel;
        LastStatusText = $"{DateTime.Now:HH:mm:ss}  {source}: {message}";
        while (Logs.Count > 500) Logs.RemoveAt(Logs.Count - 1);
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (_runtime != null) await _runtime.DisposeAsync();
        if (_iecClient != null) await _iecClient.DisposeAsync();
        base.OnClosed(e);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }

    private void Raise([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
