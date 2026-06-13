using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Ari61850Bridge.Models;
using Ari61850Bridge.Services;

namespace Ari61850Bridge;

public partial class IedConfigurationWizardWindow : Window, INotifyPropertyChanged
{
    private string _searchText = string.Empty;
    private bool _showRaw;
    private BindingItem? _selectedBinding;
    private string _statusMessage = "Ready.";
    private int _stepIndex;
    private string _validationState = "Not checked";
    private readonly Dictionary<SignalDefinition, bool> _originalSignalSelection;
    private readonly List<BindingItem> _originalBindings;
    private bool _saved;
    private readonly IIec61850Client? _probeClient;
    private CancellationTokenSource? _probeCts;
    private bool _isProbing;
    private NativeReportControlCandidate? _selectedReportControl;
    private NativeDataSetCandidate? _selectedDataSet;
    private string _reportPlanStatus = "Report plan is optional. Choose a DataSet/RCB here once; runtime views stay lightweight.";

    public ObservableCollection<SignalDefinition> Signals { get; }
    public ObservableCollection<BindingItem> Bindings { get; }
    public ObservableCollection<NativeReportControlCandidate> ReportControls { get; } = new();
    public ObservableCollection<NativeDataSetCandidate> DataSets { get; } = new();
    public ObservableCollection<ReportDataSetMemberView> DataSetMembers { get; } = new();
    public ICollectionView SignalsView { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int StepIndex
    {
        get => _stepIndex;
        set
        {
            var next = Math.Max(0, Math.Min(3, value));
            if (_stepIndex == next) return;
            _stepIndex = next;
            Raise(nameof(StepIndex));
            Raise(nameof(Step1Visibility));
            Raise(nameof(Step2Visibility));
            Raise(nameof(StepReportVisibility));
            Raise(nameof(Step3Visibility));
            Raise(nameof(StepTitle));
            Raise(nameof(StepSubtitle));
            Raise(nameof(PrimaryActionText));
        }
    }

    public Visibility Step1Visibility => StepIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Step2Visibility => StepIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StepReportVisibility => StepIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Step3Visibility => StepIndex == 3 ? Visibility.Visible : Visibility.Collapsed;

    public string StepTitle => StepIndex switch
    {
        0 => "Step 1 — Select IEC 61850 SCADA Signals",
        1 => "Step 2 — Build Modbus TCP Binding",
        2 => "Step 3 — Choose Report Plan",
        _ => "Step 4 — Add IED Configuration to Runtime"
    };

    public string StepSubtitle => StepIndex switch
    {
        0 => "Recommended tags are checked by default. Harmonic/statistical MMXU and duplicate instCVal are kept out of default publishing.",
        1 => "Review or edit the Modbus address map. Position uses Input Register, protection uses Discrete Input, measurement uses Holding Register Float32.",
        2 => "Select the DataSet/RCB once during engineering. Runtime will use the saved plan; no RCB write is done in this phase.",
        _ => "Validate once more, then save. Explorer and Modbus Server will show only the saved runtime view."
    };

    public string PrimaryActionText => StepIndex switch
    {
        0 => "Save Selection → Binding",
        1 => "Save Binding → Report Plan",
        2 => "Save Report Plan → Review",
        _ => "Add to Runtime"
    };

    public NativeReportControlCandidate? SelectedReportControl
    {
        get => _selectedReportControl;
        set
        {
            if (ReferenceEquals(_selectedReportControl, value)) return;
            _selectedReportControl = value;
            Raise(nameof(SelectedReportControl));
            MatchSelectedDataSetToReportControl();
            RebuildSelectedDataSetMembers();
            Raise(nameof(SelectedReportControlSummary));
            Raise(nameof(SelectedDataSetSummary));
        }
    }

    public NativeDataSetCandidate? SelectedDataSet
    {
        get => _selectedDataSet;
        set
        {
            if (ReferenceEquals(_selectedDataSet, value)) return;
            _selectedDataSet = value;
            Raise(nameof(SelectedDataSet));
            RebuildSelectedDataSetMembers();
            Raise(nameof(SelectedDataSetSummary));
        }
    }

    public string ReportPlanStatus
    {
        get => _reportPlanStatus;
        set
        {
            if (_reportPlanStatus == value) return;
            _reportPlanStatus = value;
            Raise(nameof(ReportPlanStatus));
        }
    }

    public string SelectedReportControlReference => SelectedReportControl?.Reference ?? string.Empty;
    public string SelectedReportControlName => SelectedReportControl?.Name ?? string.Empty;
    public string SelectedDataSetReference => SelectedDataSet?.Reference ?? SelectedReportControl?.DataSetReference ?? string.Empty;
    public string ReportRuntimeMode => string.IsNullOrWhiteSpace(SelectedReportControlReference) ? "MMS polling only" : "Report preferred + polling fallback (planned)";
    public string SelectedReportControlSummary => SelectedReportControl == null
        ? "No RCB selected. Runtime will use MMS polling only."
        : $"{SelectedReportControl.Mode} • {SelectedReportControl.Reference} • DS: {(string.IsNullOrWhiteSpace(SelectedReportControl.DataSetReference) ? "not confirmed" : SelectedReportControl.DataSetReference)}";
    public string SelectedDataSetSummary => SelectedDataSet == null
        ? "No DataSet selected."
        : $"{SelectedDataSet.Reference} • source: {(string.IsNullOrWhiteSpace(SelectedDataSet.RawMmsName) ? "SCL/static" : SelectedDataSet.RawMmsName)}";

    public bool IsProbing
    {
        get => _isProbing;
        set
        {
            if (_isProbing == value) return;
            _isProbing = value;
            Raise(nameof(IsProbing));
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            SignalsView.Refresh();
            Raise(nameof(VisibleSignalCountText));
        }
    }

    public bool ShowRaw
    {
        get => _showRaw;
        set
        {
            if (_showRaw == value) return;
            _showRaw = value;
            SignalsView.Refresh();
            Raise(nameof(VisibleSignalCountText));
        }
    }

    public BindingItem? SelectedBinding
    {
        get => _selectedBinding;
        set
        {
            if (_selectedBinding == value) return;
            _selectedBinding = value;
            Raise(nameof(SelectedBinding));
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (_statusMessage == value) return;
            _statusMessage = value;
            Raise(nameof(StatusMessage));
        }
    }

    public string ValidationState
    {
        get => _validationState;
        set
        {
            if (_validationState == value) return;
            _validationState = value;
            Raise(nameof(ValidationState));
        }
    }

    public int SelectedSignalCount => Signals.Count(s => s.IsSelected);
    public int BindingCount => Bindings.Count;

    public string VisibleSignalCountText => ShowRaw
        ? $"Showing {SignalsView.Cast<object>().Count()} of {Signals.Count} MMS attributes"
        : $"Showing {SignalsView.Cast<object>().Count()} smart SCADA signals of {Signals.Count}";

    public IedConfigurationWizardWindow(ObservableCollection<SignalDefinition> signals, ObservableCollection<BindingItem> bindings, IIec61850Client? probeClient = null, NativeReportInventory? reportInventory = null, string selectedReportControlReference = "")
    {
        Signals = signals;
        Bindings = bindings;
        _probeClient = probeClient;
        _originalSignalSelection = signals.ToDictionary(s => s, s => s.IsSelected);
        _originalBindings = bindings.Select(CloneBinding).ToList();
        SignalsView = CollectionViewSource.GetDefaultView(Signals);
        SignalsView.Filter = FilterSignal;
        SignalsView.SortDescriptions.Clear();
        SignalsView.SortDescriptions.Add(new SortDescription(nameof(SignalDefinition.SortPriority), ListSortDirection.Ascending));
        SignalsView.SortDescriptions.Add(new SortDescription(nameof(SignalDefinition.LogicalNode), ListSortDirection.Ascending));
        SignalsView.SortDescriptions.Add(new SortDescription(nameof(SignalDefinition.Name), ListSortDirection.Ascending));

        LoadReportInventory(reportInventory, selectedReportControlReference);

        foreach (var signal in Signals)
            signal.PropertyChanged += Signal_PropertyChanged;

        DataContext = this;
        InitializeComponent();

        if (!Signals.Any(s => s.IsSelected))
            SelectRecommendedSignals();
        if (Bindings.Count == 0 && Signals.Any(s => s.IsSelected))
            RebuildBindingFromSelection();

        RefreshCounts();
        StepIndex = 0;
    }

    private void Signal_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SignalDefinition.IsSelected))
            RefreshCounts();
    }

    private bool FilterSignal(object obj)
    {
        if (obj is not SignalDefinition signal) return false;
        var text = SearchText?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var tokens = text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var haystack = $"{signal.Name} {signal.LogicalNode} {signal.LogicalNodeClass} {signal.Category} {signal.DataType} {signal.FunctionalConstraint} {signal.ObjectReference}";
            return tokens.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        return ShowRaw || signal.IsScadaCoreSignal || signal.IsSelected;
    }

    private void StepNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && int.TryParse(button.Tag?.ToString(), out var index))
        {
            if (index > StepIndex && !CanMoveForwardTo(index)) return;
            StepIndex = index;
        }
    }

    private bool CanMoveForwardTo(int targetStep)
    {
        if (targetStep >= 1 && SelectedSignalCount == 0)
        {
            StatusMessage = "Select at least one IEC 61850 signal before moving to Modbus Binding.";
            return false;
        }
        if (targetStep >= 2)
        {
            if (Bindings.Count == 0) RebuildBindingFromSelection();
            var errors = ValidateBindings();
            if (errors.Count > 0)
            {
                ValidationState = "Warning";
                StatusMessage = $"Fix binding before review: {errors[0]}";
                return false;
            }
            ValidationState = "OK";
        }
        return true;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (StepIndex > 0) StepIndex--;
    }

    private void NextOrSave_Click(object sender, RoutedEventArgs e)
    {
        if (StepIndex == 0)
        {
            if (SelectedSignalCount == 0)
            {
                StatusMessage = "Select at least one IEC 61850 signal.";
                return;
            }
            RebuildBindingFromSelection();
            StatusMessage = $"Selection saved. {SelectedSignalCount} signal(s) prepared for Modbus binding.";
            StepIndex = 1;
            return;
        }

        if (StepIndex == 1)
        {
            var errors = ValidateBindings();
            if (errors.Count > 0)
            {
                ValidationState = "Warning";
                StatusMessage = $"Fix binding before report plan: {errors[0]}";
                return;
            }
            ValidationState = "OK";
            StatusMessage = "Binding saved. Choose a report plan or keep MMS polling only.";
            StepIndex = 2;
            return;
        }

        if (StepIndex == 2)
        {
            ApplySelectedReportPlanToWorkspace();
            StatusMessage = string.IsNullOrWhiteSpace(SelectedReportControlReference)
                ? "Report plan saved as polling only. Review the configuration, then add it to runtime."
                : $"Report plan saved: {SelectedReportControlReference}. Runtime will still use polling fallback until report activation is implemented.";
            StepIndex = 3;
            return;
        }

        ApplySelectedReportPlanToWorkspace();
        SaveAndClose();
    }

    private void LoadReportInventory(NativeReportInventory? inventory, string selectedReportControlReference)
    {
        ReportControls.Clear();
        DataSets.Clear();
        DataSetMembers.Clear();

        if (inventory != null)
        {
            foreach (var rcb in inventory.ReportControls.OrderByDescending(x => x.Buffered).ThenBy(x => x.Domain).ThenBy(x => x.LogicalNode).ThenBy(x => x.Name))
                ReportControls.Add(CloneReportControlCandidate(rcb));
            foreach (var ds in inventory.DataSets.OrderBy(x => x.Domain).ThenBy(x => x.LogicalNode).ThenBy(x => x.Name))
                DataSets.Add(CloneDataSetCandidate(ds));
        }

        SelectedReportControl = ReportControls.FirstOrDefault(r => !string.IsNullOrWhiteSpace(selectedReportControlReference) && string.Equals(r.Reference, selectedReportControlReference, StringComparison.OrdinalIgnoreCase))
            ?? ReportControls.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.DataSetReference))
            ?? ReportControls.FirstOrDefault();

        if (SelectedReportControl == null)
        {
            SelectedDataSet = DataSets.FirstOrDefault();
            ReportPlanStatus = "No RCB inventory is available for this IED. Runtime will use MMS polling only.";
        }
        else
        {
            MatchSelectedDataSetToReportControl();
            ReportPlanStatus = $"Report inventory loaded: {ReportControls.Count} RCB(s), {DataSets.Count} DataSet(s). Select one plan or keep polling only.";
        }
        RebuildSelectedDataSetMembers();
    }

    private static NativeDataSetCandidate CloneDataSetCandidate(NativeDataSetCandidate ds) => new()
    {
        Domain = ds.Domain,
        LogicalNode = ds.LogicalNode,
        Name = ds.Name,
        Reference = ds.Reference,
        RawMmsName = ds.RawMmsName
    };

    private static NativeReportControlCandidate CloneReportControlCandidate(NativeReportControlCandidate rcb) => new()
    {
        Domain = rcb.Domain,
        LogicalNode = rcb.LogicalNode,
        FunctionalConstraint = rcb.FunctionalConstraint,
        Name = rcb.Name,
        Reference = rcb.Reference,
        Buffered = rcb.Buffered,
        DataSetReference = rcb.DataSetReference,
        ReportId = rcb.ReportId,
        ConfRev = rcb.ConfRev,
        IntegrityPeriodMs = rcb.IntegrityPeriodMs,
        EnabledState = rcb.EnabledState,
        Status = rcb.Status,
        Attributes = rcb.Attributes.ToList()
    };

    private void MatchSelectedDataSetToReportControl()
    {
        var target = SelectedReportControl?.DataSetReference;
        if (string.IsNullOrWhiteSpace(target))
            return;

        var match = DataSets.FirstOrDefault(ds => ReferencesMatch(ds.Reference, target));
        if (match != null && !ReferenceEquals(match, _selectedDataSet))
        {
            _selectedDataSet = match;
            Raise(nameof(SelectedDataSet));
        }
    }

    private static bool ReferencesMatch(string a, string b)
    {
        static string Clean(string x) => (x ?? string.Empty).Trim().Replace('$', '.').Replace("//", "/");
        var left = Clean(a);
        var right = Clean(b);
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase)) return true;
        return left.EndsWith(right, StringComparison.OrdinalIgnoreCase) || right.EndsWith(left, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildSelectedDataSetMembers()
    {
        DataSetMembers.Clear();
        var selectedDataSet = SelectedDataSet;
        if (selectedDataSet == null)
            return;

        var directMembers = Signals
            .Where(s => !string.IsNullOrWhiteSpace(s.DataSetReference) && ReferencesMatch(s.DataSetReference, selectedDataSet.Reference))
            .OrderBy(s => s.ObjectReference, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (directMembers.Count == 0)
        {
            directMembers = Signals
                .Where(s => s.IsSelected)
                .OrderBy(s => s.ObjectReference, StringComparer.OrdinalIgnoreCase)
                .Take(80)
                .ToList();
        }

        foreach (var signal in directMembers)
        {
            DataSetMembers.Add(new ReportDataSetMemberView
            {
                DataSetReference = selectedDataSet.Reference,
                ObjectReference = signal.ObjectReference,
                FunctionalConstraint = signal.FunctionalConstraint,
                DataType = signal.DataType,
                Coverage = string.IsNullOrWhiteSpace(signal.DataSetReference) ? "Selected signal / awaiting DataSet directory" : "Covered by DataSet",
                Source = string.IsNullOrWhiteSpace(signal.DataSetReference) ? "Runtime selection hint" : "SCL FCDA"
            });
        }
    }

    private async void ProbeSelectedReportControl_Click(object sender, RoutedEventArgs e)
    {
        var rcb = SelectedReportControl;
        if (rcb == null)
        {
            ReportPlanStatus = "Select one RCB before probing.";
            return;
        }

        if (_probeClient is not NativeIec61850Client native || !native.IsMmsReady)
        {
            ReportPlanStatus = "Read-only RCB probe requires native MMS association. Reconnect/discover the IED, then open this wizard again.";
            return;
        }

        try
        {
            ReportPlanStatus = $"Probing {rcb.Reference} read-only...";
            await native.ProbeReportControlAsync(rcb, CancellationToken.None).ConfigureAwait(true);
            MatchSelectedDataSetToReportControl();
            RebuildSelectedDataSetMembers();
            ReportPlanStatus = $"Probe complete: {rcb.Status}. DataSet: {(string.IsNullOrWhiteSpace(rcb.DataSetReference) ? "not confirmed" : rcb.DataSetReference)}.";
            Raise(nameof(SelectedReportControlSummary));
            Raise(nameof(SelectedDataSetSummary));
        }
        catch (Exception ex)
        {
            ReportPlanStatus = $"Probe failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private void UseSelectedReportControl_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedReportControl == null)
        {
            ReportPlanStatus = "Select one RCB first, or choose Polling Only.";
            return;
        }

        ApplySelectedReportPlanToWorkspace();
        ReportPlanStatus = $"Selected {SelectedReportControl.Reference} as the saved report plan. Runtime remains polling-safe until report activation is implemented.";
        SignalsView.Refresh();
    }

    private void UsePollingOnly_Click(object sender, RoutedEventArgs e)
    {
        _selectedReportControl = null;
        _selectedDataSet = null;
        foreach (var signal in Signals)
        {
            signal.ReportControlReference = string.Empty;
            signal.DataSetReference = string.Empty;
            signal.IsReportCapable = false;
        }
        foreach (var binding in Bindings)
        {
            binding.ReportControlReference = string.Empty;
            binding.DataSetReference = string.Empty;
            binding.RcbMode = "MMS polling";
        }
        ReportPlanStatus = "Polling-only plan selected. No RCB/DataSet plan will be saved for this IED.";
        DataSetMembers.Clear();
        Raise(nameof(SelectedReportControl));
        Raise(nameof(SelectedDataSet));
        Raise(nameof(SelectedReportControlSummary));
        Raise(nameof(SelectedDataSetSummary));
        SignalsView.Refresh();
    }

    private void ApplySelectedReportPlanToWorkspace()
    {
        var rcbRef = SelectedReportControl?.Reference ?? string.Empty;
        var dsRef = SelectedReportControl?.DataSetReference;
        if (string.IsNullOrWhiteSpace(dsRef)) dsRef = SelectedDataSet?.Reference ?? string.Empty;

        if (string.IsNullOrWhiteSpace(rcbRef) && string.IsNullOrWhiteSpace(dsRef))
            return;

        foreach (var signal in Signals.Where(s => s.IsSelected))
        {
            signal.ReportControlReference = rcbRef;
            signal.DataSetReference = dsRef ?? string.Empty;
            signal.IsReportCapable = !string.IsNullOrWhiteSpace(rcbRef) || !string.IsNullOrWhiteSpace(dsRef);
        }

        foreach (var binding in Bindings)
        {
            if (!Signals.Any(s => s.IsSelected && string.Equals(s.ObjectReference, binding.IecReference, StringComparison.OrdinalIgnoreCase)))
                continue;
            binding.ReportControlReference = rcbRef;
            binding.DataSetReference = dsRef ?? string.Empty;
            binding.RcbMode = string.IsNullOrWhiteSpace(rcbRef) ? "MMS polling" : "Report planned / polling fallback";
            binding.ReadMode = string.IsNullOrWhiteSpace(rcbRef) ? "MMS polling" : "RCB candidate + MMS polling fallback";
        }
        RebuildSelectedDataSetMembers();
        Raise(nameof(SelectedReportControlReference));
        Raise(nameof(SelectedReportControlName));
        Raise(nameof(SelectedDataSetReference));
        Raise(nameof(ReportRuntimeMode));
    }

    private async void ProbeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (IsProbing) return;

        if (_probeClient == null || !_probeClient.IsConnected)
        {
            StatusMessage = "Live probe requires an associated IEC 61850 client. Connect/discover first, then probe selected signals.";
            return;
        }

        var selected = Signals
            .Where(s => s.IsSelected && !string.Equals(s.DataType, "Directory", StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.SortPriority)
            .ThenBy(s => s.LogicalNode)
            .ThenBy(s => s.Name)
            .Take(120)
            .ToList();

        if (selected.Count == 0)
        {
            StatusMessage = "Select at least one value signal before running live probe.";
            return;
        }

        _probeCts?.Cancel();
        _probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(selected.Count * 2, 8, 60)));
        IsProbing = true;
        var ok = 0;
        var failed = 0;
        StatusMessage = $"Live probe running for {selected.Count} selected signal(s)...";

        try
        {
            foreach (var signal in selected)
            {
                _probeCts.Token.ThrowIfCancellationRequested();
                signal.ProbeStatus = "Reading...";
                signal.Value = "...";
                signal.Quality = "Checking";
                signal.DeviceTimestamp = "-";
                signal.Timestamp = DateTime.Now;

                try
                {
                    var value = await _probeClient.ReadValueAsync(signal.ObjectReference, signal.FunctionalConstraint, signal.DataType, _probeCts.Token).ConfigureAwait(true);
                    if (value == null)
                    {
                        failed++;
                        signal.Value = "-";
                        signal.Quality = "Bad";
                        signal.DeviceTimestamp = "-";
                        signal.ProbeStatus = "Not readable";
                        continue;
                    }

                    signal.Value = MockIec61850Client.Format(value, signal.DataType, signal.Unit);
                    signal.Quality = "Good";
                    signal.ProbeStatus = "Readable";
                    signal.Timestamp = DateTime.Now;
                    await TryProbeCompanionQualityAndTimestampAsync(signal, _probeClient, _probeCts.Token).ConfigureAwait(true);
                    ok++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    signal.Value = "Read failed";
                    signal.Quality = "Bad";
                    signal.DeviceTimestamp = "-";
                    signal.ProbeStatus = ex.GetType().Name;
                }
            }

            StatusMessage = $"Live probe complete: {ok} readable, {failed} failed. Save only signals that are proven useful for runtime.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"Live probe stopped: {ok} readable, {failed} failed/cancelled.";
        }
        finally
        {
            IsProbing = false;
            SignalsView.Refresh();
        }
    }

    private static async Task TryProbeCompanionQualityAndTimestampAsync(SignalDefinition signal, IIec61850Client client, CancellationToken token)
    {
        if (signal.ObjectReference.EndsWith(".q", StringComparison.OrdinalIgnoreCase) ||
            signal.ObjectReference.EndsWith(".t", StringComparison.OrdinalIgnoreCase))
            return;

        if (TryBuildCompanionReference(signal.ObjectReference, "q", out var qRef))
        {
            try
            {
                var q = await client.ReadValueAsync(qRef, signal.FunctionalConstraint, "Quality", token).ConfigureAwait(true);
                var qText = q?.ToString();
                if (!string.IsNullOrWhiteSpace(qText))
                    signal.Quality = qText;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Companion quality is optional. A readable value should not be rejected because q is hidden by the IED.
            }
        }

        if (TryBuildCompanionReference(signal.ObjectReference, "t", out var tRef))
        {
            try
            {
                var t = await client.ReadValueAsync(tRef, signal.FunctionalConstraint, "Timestamp", token).ConfigureAwait(true);
                var tText = t?.ToString();
                if (!string.IsNullOrWhiteSpace(tText))
                    signal.DeviceTimestamp = tText;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Companion timestamp is optional. Runtime will continue with local update time if t is not exposed.
            }
        }
    }

    private static bool TryBuildCompanionReference(string reference, string companion, out string companionReference)
    {
        companionReference = string.Empty;
        if (!companion.Equals("q", StringComparison.OrdinalIgnoreCase) && !companion.Equals("t", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(reference)) return false;

        var normalized = reference.Replace('$', '.').Trim();
        if (normalized.EndsWith(".q", StringComparison.OrdinalIgnoreCase) || normalized.EndsWith(".t", StringComparison.OrdinalIgnoreCase)) return false;

        var parent = normalized;
        if (parent.EndsWith(".stVal", StringComparison.OrdinalIgnoreCase)) parent = parent[..^6];
        else if (parent.EndsWith(".general", StringComparison.OrdinalIgnoreCase)) parent = parent[..^8];
        else if (parent.EndsWith(".cVal.mag.f", StringComparison.OrdinalIgnoreCase)) parent = parent[..^11];
        else if (parent.EndsWith(".mag.f", StringComparison.OrdinalIgnoreCase)) parent = parent[..^6];
        else
        {
            var slash = parent.IndexOf('/');
            var dot = parent.LastIndexOf('.');
            if (dot <= slash) return false;
            parent = parent[..dot];
        }

        if (string.IsNullOrWhiteSpace(parent)) return false;
        companionReference = $"{parent}.{companion.ToLowerInvariant()}";
        return true;
    }

    private void SelectRecommended_Click(object sender, RoutedEventArgs e) => SelectRecommendedSignals();

    private void SelectRecommendedSignals()
    {
        foreach (var signal in Signals)
            signal.IsSelected = signal.IsScadaCoreSignal;
        SignalsView.Refresh();
        RefreshCounts();
        StatusMessage = $"Recommended SCADA selection applied: {SelectedSignalCount} signal(s).";
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var signal in Signals)
            signal.IsSelected = false;
        SignalsView.Refresh();
        RefreshCounts();
        StatusMessage = "Signal selection cleared.";
    }

    private void QuickFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button) return;
        SearchText = button.Tag?.ToString() ?? button.Content?.ToString() ?? string.Empty;
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        SearchText = string.Empty;
        ShowRaw = false;
    }

    private void RebuildBinding_Click(object sender, RoutedEventArgs e)
    {
        RebuildBindingFromSelection();
        StatusMessage = $"Binding rebuilt from {SelectedSignalCount} selected signal(s).";
    }

    private void RebuildBindingFromSelection()
    {
        var selected = Signals.Where(s => s.IsSelected).ToList();
        Bindings.Clear();
        foreach (var item in BindingAutoMapper.CreateBindings(selected))
            Bindings.Add(item);
        ApplySelectedReportPlanToWorkspace();
        SelectedBinding = Bindings.FirstOrDefault();
        RefreshCounts();
    }

    private void RemoveBinding_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBinding == null) return;
        Bindings.Remove(SelectedBinding);
        SelectedBinding = Bindings.FirstOrDefault();
        RefreshCounts();
        StatusMessage = "Selected binding removed. Validate before saving.";
    }

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        var errors = ValidateBindings();
        ValidationState = errors.Count == 0 ? "OK" : "Warning";
        StatusMessage = errors.Count == 0
            ? "Validation OK. No register overlap detected."
            : $"Validation warning: {errors[0]}";
        RefreshCounts();
    }

    private List<string> ValidateBindings()
    {
        var errors = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in Bindings.Where(b => b.IsEnabled))
        {
            if (binding.ModbusAddress <= 0)
                errors.Add($"Invalid address for {binding.SignalName}.");
            var width = binding.ModbusDataType == "Float32" ? 2 : 1;
            for (var i = 0; i < width; i++)
            {
                var key = $"{binding.ModbusArea}:{binding.ModbusAddress + i}";
                if (!used.Add(key))
                    errors.Add($"Register overlap: {key}.");
            }
        }
        return errors;
    }

    private void SaveAndClose()
    {
        if (Bindings.Count == 0 && Signals.Any(s => s.IsSelected))
            RebuildBindingFromSelection();

        var errors = ValidateBindings();
        if (errors.Count > 0)
        {
            ValidationState = "Warning";
            StatusMessage = $"Fix binding before save: {errors[0]}";
            StepIndex = 1;
            return;
        }

        ValidationState = "OK";
        StatusMessage = "IED configuration saved.";
        _saved = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        RestoreOriginalConfiguration();
        DialogResult = false;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        try { _probeCts?.Cancel(); } catch { }
        if (!_saved)
            RestoreOriginalConfiguration();
        base.OnClosing(e);
    }

    private void RestoreOriginalConfiguration()
    {
        foreach (var pair in _originalSignalSelection)
            pair.Key.IsSelected = pair.Value;

        Bindings.Clear();
        foreach (var binding in _originalBindings.Select(CloneBinding))
            Bindings.Add(binding);
    }

    private static BindingItem CloneBinding(BindingItem source)
    {
        return new BindingItem
        {
            IsEnabled = source.IsEnabled,
            PublishToModbus = source.PublishToModbus,
            PublishToMqtt = source.PublishToMqtt,
            SignalName = source.SignalName,
            IecReference = source.IecReference,
            FunctionalConstraint = source.FunctionalConstraint,
            IecDataType = source.IecDataType,
            Category = source.Category,
            Unit = source.Unit,
            ReadMode = source.ReadMode,
            RcbMode = source.RcbMode,
            DataSetReference = source.DataSetReference,
            ReportControlReference = source.ReportControlReference,
            PollingIntervalMs = source.PollingIntervalMs,
            StaleTimeoutMs = source.StaleTimeoutMs,
            ModbusArea = source.ModbusArea,
            ModbusAddress = source.ModbusAddress,
            ModbusDataType = source.ModbusDataType,
            WordOrder = source.WordOrder,
            Scale = source.Scale,
            Offset = source.Offset,
            FuxaTagName = source.FuxaTagName,
            MqttTopic = source.MqttTopic,
            CurrentValue = source.CurrentValue,
            Quality = source.Quality,
            DeviceTimestamp = source.DeviceTimestamp,
            Status = source.Status,
            Sequence = source.Sequence,
            LastUpdate = source.LastUpdate,
            AgeMs = source.AgeMs
        };
    }

    private void RefreshCounts()
    {
        Raise(nameof(SelectedSignalCount));
        Raise(nameof(BindingCount));
        Raise(nameof(VisibleSignalCountText));
        Raise(nameof(SelectedReportControlSummary));
        Raise(nameof(SelectedDataSetSummary));
    }

    private void Raise(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
