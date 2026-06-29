using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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
    private SignalDefinition? _selectedSignal;
    private bool _manualReportPlanOverride;
    private int _lastClickedSignalIndex = -1;
    private string _reportPlanStatus = "Auto report planner is ready. Select SCADA signals; ARServer will map RCB/DataSet lanes automatically.";

    public ObservableCollection<SignalDefinition> Signals { get; }
    public ObservableCollection<BindingItem> Bindings { get; }
    public ObservableCollection<NativeReportControlCandidate> ReportControls { get; } = new();
    public ObservableCollection<NativeDataSetCandidate> DataSets { get; } = new();
    public ObservableCollection<ReportDataSetMemberView> DataSetMembers { get; } = new();
    public ICollectionView SignalsView { get; }
    public ICollectionView AutoPlanSignalsView { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int StepIndex
    {
        get => _stepIndex;
        set
        {
            var next = Math.Max(0, Math.Min(2, value));
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
    public Visibility Step2Visibility => Visibility.Collapsed;
    public Visibility StepReportVisibility => StepIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Step3Visibility => StepIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

    public string StepTitle => StepIndex switch
    {
        0 => "Step 1 — Select IEC 61850 SCADA Signals",
        1 => "Step 2 — Auto Report Plan Review",
        _ => "Step 3 — Add IEC Signals to Explorer"
    };

    public string StepSubtitle => StepIndex switch
    {
        0 => "Select SCADA signals only. q/t, Health, Beh and engineering attributes stay out of selection and are shown as Quality/Timestamp columns.",
        1 => "No manual RCB/DataSet choice is required. Review the read-only auto transport plan per selected signal.",
        _ => "Save the Explorer selection. Modbus routes are assigned later from the Modbus Server tab."
    };

    public string PrimaryActionText => StepIndex switch
    {
        0 => "Review Auto Plan →",
        1 => "Review Selection →",
        _ => "Add to Explorer"
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
            Raise(nameof(ReportPlanStatus));
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
            Raise(nameof(ReportPlanStatus));
        }
    }

    public SignalDefinition? SelectedSignal
    {
        get => _selectedSignal;
        set
        {
            if (ReferenceEquals(_selectedSignal, value)) return;
            _selectedSignal = value;
            Raise(nameof(SelectedSignal));
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

    public string SelectedReportControlReference => _manualReportPlanOverride ? SelectedReportControl?.Reference ?? string.Empty : string.Empty;
    public string SelectedReportControlName => _manualReportPlanOverride ? SelectedReportControl?.Name ?? string.Empty : string.Empty;
    public string SelectedDataSetReference => _manualReportPlanOverride ? SelectedDataSet?.Reference ?? SelectedReportControl?.DataSetReference ?? string.Empty : string.Empty;
    public string ReportRuntimeMode => "Auto report planner + MMS polling fallback";
    public string SelectedReportControlSummary => SelectedReportControl == null
        ? "No RCB selected manually. Auto planner will use per-signal RCB hints and polling fallback."
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
            Raise(nameof(SearchPlaceholderVisibility));
            Raise(nameof(SearchClearVisibility));
        }
    }

    public Visibility SearchPlaceholderVisibility => string.IsNullOrWhiteSpace(SearchText) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SearchClearVisibility => string.IsNullOrWhiteSpace(SearchText) ? Visibility.Collapsed : Visibility.Visible;

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

    public int SelectedSignalCount => Signals.Count(s => s.IsSelected && s.CanPublishToRuntime);
    public int BindingCount => Bindings.Count;

    public string VisibleSignalCountText => ShowRaw
        ? $"Showing {SignalsView.Cast<object>().Count()} of {Signals.Count} MMS leaves/attributes"
        : $"Showing {SignalsView.Cast<object>().Count()} smart SCADA signals ({Signals.Count(s => s.IsRawAttribute || !s.CanPublishAsSignal)} raw attributes hidden)";

    public IedConfigurationWizardWindow(ObservableCollection<SignalDefinition> signals, ObservableCollection<BindingItem> bindings, IIec61850Client? probeClient = null, NativeReportInventory? reportInventory = null, string selectedReportControlReference = "", bool isNewIed = false)
    {
        Signals = signals;
        Bindings = bindings;
        _probeClient = probeClient;
        if (isNewIed)
        {
            foreach (var signal in signals)
                signal.IsSelected = false;
        }
        _originalSignalSelection = signals.ToDictionary(s => s, s => s.IsSelected);
        _originalBindings = bindings.Select(CloneBinding).ToList();
        SignalsView = CollectionViewSource.GetDefaultView(Signals);
        SignalsView.Filter = FilterSignal;
        SignalsView.SortDescriptions.Clear();
        SignalsView.SortDescriptions.Add(new SortDescription(nameof(SignalDefinition.SortPriority), ListSortDirection.Ascending));
        SignalsView.SortDescriptions.Add(new SortDescription(nameof(SignalDefinition.LogicalNode), ListSortDirection.Ascending));
        SignalsView.SortDescriptions.Add(new SortDescription(nameof(SignalDefinition.Name), ListSortDirection.Ascending));

        var autoPlanSource = new CollectionViewSource { Source = Signals };
        AutoPlanSignalsView = autoPlanSource.View;
        AutoPlanSignalsView.Filter = item => item is SignalDefinition signal && signal.IsSelected && signal.CanPublishToRuntime;
        AutoPlanSignalsView.SortDescriptions.Add(new SortDescription(nameof(SignalDefinition.ReportPlan), ListSortDirection.Ascending));
        AutoPlanSignalsView.SortDescriptions.Add(new SortDescription(nameof(SignalDefinition.LogicalNode), ListSortDirection.Ascending));
        AutoPlanSignalsView.SortDescriptions.Add(new SortDescription(nameof(SignalDefinition.ObjectReference), ListSortDirection.Ascending));

        LoadReportInventory(reportInventory, selectedReportControlReference);

        foreach (var signal in Signals)
        {
            if (!signal.CanPublishAsSignal)
                signal.IsSelected = false;
            signal.PropertyChanged += Signal_PropertyChanged;
        }

        DataContext = this;
        InitializeComponent();

        RefreshCounts();
        StepIndex = 0;
    }

    private void Signal_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SignalDefinition.IsSelected))
        {
            AutoPlanSignalsView.Refresh();
            RefreshCounts();
        }
    }

    private bool FilterSignal(object obj)
    {
        if (obj is not SignalDefinition signal) return false;

        // Normal wizard mode is signal-centric: value leaves only. Quality/timestamp/health/behaviour
        // remain available in Advanced raw for diagnostics, but they are not selectable runtime signals.
        var visibleByMode = ShowRaw || signal.IsScadaCoreSignal || (signal.IsSelected && signal.CanPublishAsSignal);
        if (!visibleByMode) return false;

        var text = SearchText?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var tokens = text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var haystack = $"{signal.Name} {signal.LogicalNode} {signal.LogicalNodeClass} {signal.Category} {signal.DataType} {signal.FunctionalConstraint} {signal.ObjectReference} {signal.Value} {signal.Quality} {signal.DeviceTimestamp} {signal.ReportPlan} {signal.ReportPlanReason}";
            return tokens.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        return true;
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
            StatusMessage = "Select at least one IEC 61850 signal before reviewing the auto report plan.";
            return false;
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
            ApplySelectedReportPlanToWorkspace();
            StatusMessage = $"{SelectedSignalCount} Explorer signal(s) selected. Review the automatic transport plan.";
            StepIndex = 1;
            return;
        }

        if (StepIndex == 1)
        {
            ApplySelectedReportPlanToWorkspace();
            StatusMessage = BuildAutoReportPlanStatus();
            StepIndex = 2;
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
            ReportPlanStatus = BuildAutoReportPlanStatus();
        }
        else
        {
            MatchSelectedDataSetToReportControl();
            ReportPlanStatus = BuildAutoReportPlanStatus();
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
            .Where(s => s.CanPublishAsSignal && !string.IsNullOrWhiteSpace(s.DataSetReference) && ReferencesMatch(s.DataSetReference, selectedDataSet.Reference))
            .OrderBy(s => s.ObjectReference, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (directMembers.Count == 0)
        {
            directMembers = Signals
                .Where(s => s.IsSelected && s.CanPublishToRuntime)
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
            Raise(nameof(ReportPlanStatus));
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
            ReportPlanStatus = "Select one RCB first, or choose Force Polling Only.";
            return;
        }

        _manualReportPlanOverride = true;
        ApplySelectedReportPlanToWorkspace(allowManualFallback: true);
        ReportPlanStatus = $"Advanced override saved: {SelectedReportControl.Reference}. Only matching uncovered signals will be forced to this RCB; polling fallback remains active.";
        SignalsView.Refresh();
    }

    private void UsePollingOnly_Click(object sender, RoutedEventArgs e)
    {
        _manualReportPlanOverride = false;
        _selectedReportControl = null;
        _selectedDataSet = null;
        foreach (var signal in Signals)
        {
            signal.ReportControlReference = string.Empty;
            signal.DataSetReference = string.Empty;
            signal.IsReportCapable = false;
            signal.ReportCoverage = signal.CanPublishAsSignal ? "Polling fallback" : "Hidden attribute";
            signal.ReportCoverageReason = "Polling-only mode was forced for this IED configuration.";
        }
        foreach (var binding in Bindings)
        {
            binding.ReportControlReference = string.Empty;
            binding.DataSetReference = string.Empty;
            binding.RcbMode = "MMS polling";
            binding.ReadMode = "MMS polling";
            binding.Status = "MMS polling fallback";
        }
        ReportPlanStatus = "Polling-only mode forced. No RCB/DataSet lane will be used; runtime will read selected signals by MMS polling only.";
        DataSetMembers.Clear();
        Raise(nameof(SelectedReportControl));
        Raise(nameof(SelectedDataSet));
        Raise(nameof(SelectedReportControlSummary));
        Raise(nameof(SelectedDataSetSummary));
        Raise(nameof(ReportPlanStatus));
        SignalsView.Refresh();
    }

    private void ApplySelectedReportPlanToWorkspace(bool allowManualFallback = false)
    {
        var selectedRcbRef = SelectedReportControl?.Reference ?? string.Empty;
        var selectedDsRef = SelectedReportControl?.DataSetReference;
        if (string.IsNullOrWhiteSpace(selectedDsRef)) selectedDsRef = SelectedDataSet?.Reference ?? string.Empty;

        // Preserve per-signal report hints created by discovery/SCL.  Some IEDs expose GGIO and
        // MMXU through separate DataSet + RCB lanes.  A single visible selection in this page is
        // now used only as a fallback for uncovered signals that look like members of that lane.
        var selectedSignals = Signals.Where(s => s.IsSelected && s.CanPublishToRuntime).ToList();
        foreach (var signal in selectedSignals)
        {
            var hasNativeHint = !string.IsNullOrWhiteSpace(signal.ReportControlReference) ||
                                !string.IsNullOrWhiteSpace(signal.DataSetReference);

            if (!hasNativeHint && (allowManualFallback || _manualReportPlanOverride) && (!string.IsNullOrWhiteSpace(selectedRcbRef) || !string.IsNullOrWhiteSpace(selectedDsRef)) &&
                SignalLikelyCoveredByReportPlan(signal, SelectedReportControl, selectedDsRef ?? string.Empty))
            {
                signal.ReportControlReference = selectedRcbRef;
                signal.DataSetReference = selectedDsRef ?? string.Empty;
                hasNativeHint = true;
                signal.ReportCoverage = "Advanced forced RCB + polling fallback";
                signal.ReportCoverageReason = "User forced the selected RCB/DataSet for this uncovered signal. Runtime keeps polling fallback if static DataSet membership does not match.";
            }

            signal.IsReportCapable = hasNativeHint;
            if (!hasNativeHint && signal.CanPublishAsSignal)
            {
                signal.ReportCoverage = "Polling fallback";
                signal.ReportCoverageReason = "No confirmed or candidate RCB/DataSet lane is mapped to this signal. Runtime will use MMS polling.";
            }
        }

        var signalByReference = selectedSignals
            .GroupBy(s => NormalizeReference(s.ObjectReference), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var binding in Bindings)
        {
            if (!signalByReference.TryGetValue(NormalizeReference(binding.IecReference), out var signal))
                continue;

            binding.ReportControlReference = signal.ReportControlReference;
            binding.DataSetReference = signal.DataSetReference;
            var reportCapable = !string.IsNullOrWhiteSpace(binding.ReportControlReference) ||
                                !string.IsNullOrWhiteSpace(binding.DataSetReference);
            binding.RcbMode = reportCapable ? "Auto report planner" : "MMS polling";
            binding.ReadMode = reportCapable ? signal.ReportCoverage : "MMS polling";
            binding.Status = reportCapable ? signal.ReportCoverage : "MMS polling fallback";
        }
        RebuildSelectedDataSetMembers();
        Raise(nameof(SelectedReportControlReference));
        Raise(nameof(SelectedReportControlName));
        Raise(nameof(SelectedDataSetReference));
        Raise(nameof(ReportRuntimeMode));
    }

    private static bool SignalLikelyCoveredByReportPlan(SignalDefinition signal, NativeReportControlCandidate? rcb, string dataSetReference)
    {
        var signalLn = signal.LogicalNode ?? string.Empty;
        var signalClass = signal.LogicalNodeClass ?? string.Empty;
        var dataSetLn = ExtractLogicalNodeFromDataSetReference(dataSetReference);

        if (!string.IsNullOrWhiteSpace(dataSetLn) && !dataSetLn.Equals("LLN0", StringComparison.OrdinalIgnoreCase))
            return LogicalNodeMatches(signalLn, signalClass, dataSetLn);

        if (rcb != null && !string.IsNullOrWhiteSpace(rcb.LogicalNode) && !rcb.LogicalNode.Equals("LLN0", StringComparison.OrdinalIgnoreCase))
            return LogicalNodeMatches(signalLn, signalClass, rcb.LogicalNode);

        // Generic LLN0-hosted DataSets may legitimately cover many LNs.  Keep the older behavior
        // only for this generic case; LN-specific DataSets stay isolated.
        return true;
    }

    private static bool LogicalNodeMatches(string signalLn, string signalClass, string candidateLn)
    {
        if (string.IsNullOrWhiteSpace(candidateLn)) return false;
        if (!string.IsNullOrWhiteSpace(signalLn) && candidateLn.Equals(signalLn, StringComparison.OrdinalIgnoreCase))
            return true;
        // Manual override must not accidentally route one logical-node instance through
        // another instance just because the LN class is the same. Class-level report candidates are produced by
        // the smart discovery planner only, then verified by DataSet directory at runtime.
        return false;
    }

    private static string ExtractLogicalNodeFromDataSetReference(string reference)
    {
        var text = (reference ?? string.Empty).Trim().Replace('$', '.');
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var slash = text.IndexOf('/');
        var item = slash >= 0 && slash < text.Length - 1 ? text[(slash + 1)..] : text;
        var dot = item.IndexOf('.');
        return dot > 0 ? item[..dot] : string.Empty;
    }

    private static string NormalizeReference(string? reference)
        => (reference ?? string.Empty).Trim().Replace('$', '.').Replace("..", ".").ToLowerInvariant();

    private string BuildAutoReportPlanStatus()
    {
        var selected = Signals.Where(s => s.IsSelected && s.CanPublishToRuntime).ToList();
        var total = selected.Count;
        var covered = selected.Count(s => s.ReportCoverage.Contains("covered", StringComparison.OrdinalIgnoreCase));
        var candidate = selected.Count(s => s.ReportCoverage.Contains("candidate", StringComparison.OrdinalIgnoreCase));
        var polling = selected.Count(s => !s.IsReportCapable);
        var lanes = selected
            .Where(s => !string.IsNullOrWhiteSpace(s.ReportControlReference) || !string.IsNullOrWhiteSpace(s.DataSetReference))
            .GroupBy(s => string.IsNullOrWhiteSpace(s.ReportControlReference) ? s.DataSetReference : s.ReportControlReference, StringComparer.OrdinalIgnoreCase)
            .Count();

        return $"Auto plan: {total} selected, {covered} report-covered, {candidate} report-candidate/static-verify, {polling} polling fallback, {lanes} RCB/DataSet lane(s).";
    }

    private void SignalsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row != null)
        {
            row.IsSelected = true;
            SelectedSignal = row.Item as SignalDefinition;
        }
    }

    private void SignalsGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindVisualParent<CheckBox>(source) != null || FindVisualParent<DataGridColumnHeader>(source) != null)
            return;

        var row = FindVisualParent<DataGridRow>(source);
        if (row?.Item is not SignalDefinition signal || !signal.CanPublishAsSignal)
            return;

        signal.IsSelected = !signal.IsSelected;
        row.IsSelected = true;
        SelectedSignal = signal;
        SignalsView.Refresh();
        AutoPlanSignalsView.Refresh();
        RefreshCounts();
        StatusMessage = signal.IsSelected ? "Signal selected." : "Signal deselected.";
        e.Handled = true;
    }


    private void SignalsGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && SignalsGrid.SelectedItems.Count > 0)
        {
            var selectedSignals = SignalsGrid.SelectedItems
                .OfType<SignalDefinition>()
                .Where(s => s.CanPublishAsSignal)
                .ToList();
            if (selectedSignals.Count == 0) return;

            var target = selectedSignals.Any(s => !s.IsSelected);
            foreach (var signal in selectedSignals)
                signal.IsSelected = target;

            SignalsView.Refresh();
            AutoPlanSignalsView.Refresh();
            RefreshCounts();
            StatusMessage = target
                ? $"Selected {selectedSignals.Count} highlighted signal(s)."
                : $"Deselected {selectedSignals.Count} highlighted signal(s).";
            e.Handled = true;
        }
    }


    private void SignalUseHeaderCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox)
            return;

        var target = checkBox.IsChecked == true;
        var affected = 0;
        foreach (var signal in GetVisibleSignals().Where(s => s.CanPublishAsSignal))
        {
            signal.IsSelected = target;
            affected++;
        }

        SignalsView.Refresh();
        AutoPlanSignalsView.Refresh();
        RefreshCounts();
        StatusMessage = target
            ? $"Selected {affected} visible publishable signal(s)."
            : $"Deselected {affected} visible publishable signal(s).";
    }

    private void SignalUseCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not SignalDefinition clicked || !clicked.CanPublishAsSignal)
            return;

        var target = checkBox.IsChecked == true;
        var visible = GetVisibleSignals().ToList();
        var clickedIndex = visible.FindIndex(s => ReferenceEquals(s, clicked));
        var applied = 0;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift && _lastClickedSignalIndex >= 0 && clickedIndex >= 0)
        {
            var start = Math.Min(_lastClickedSignalIndex, clickedIndex);
            var end = Math.Max(_lastClickedSignalIndex, clickedIndex);
            for (var i = start; i <= end; i++)
            {
                if (!visible[i].CanPublishAsSignal) continue;
                visible[i].IsSelected = target;
                applied++;
            }
            StatusMessage = $"Block selection updated: {applied} signal(s).";
        }
        else if (SignalsGrid?.SelectedItems.Count > 1 && SignalsGrid.SelectedItems.Contains(clicked))
        {
            foreach (var item in SignalsGrid.SelectedItems.OfType<SignalDefinition>().Where(s => s.CanPublishAsSignal))
            {
                item.IsSelected = target;
                applied++;
            }
            StatusMessage = $"Multi-selection updated: {applied} signal(s).";
        }
        else
        {
            clicked.IsSelected = target;
            StatusMessage = target ? "Signal selected." : "Signal deselected.";
        }

        _lastClickedSignalIndex = clickedIndex;
        SignalsView.Refresh();
        AutoPlanSignalsView.Refresh();
        RefreshCounts();
        e.Handled = true;
    }

    private IEnumerable<SignalDefinition> GetVisibleSignals()
        => SignalsView.Cast<object>().OfType<SignalDefinition>();

    private void ShowSignalProperties_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSignal == null)
        {
            StatusMessage = "Select a signal row first.";
            return;
        }

        MessageBox.Show(this, SelectedSignal.SignalPropertiesSummary, "Signal Properties", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T typed) return typed;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
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
            .Where(s => s.IsSelected && s.CanPublishToRuntime && !string.Equals(s.DataType, "Directory", StringComparison.OrdinalIgnoreCase))
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
                        signal.IsSelected = false;
                        continue;
                    }

                    ApplyReadValueToSignal(signal, value);
                    signal.ProbeStatus = "Readable";
                    signal.Timestamp = DateTime.Now;
                    if (value is not Iec61850ReadValue rich || !rich.HasQuality || !rich.HasDeviceTimestamp)
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
                    signal.IsSelected = false;
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

    private static void ApplyReadValueToSignal(SignalDefinition signal, object value)
    {
        if (value is Iec61850ReadValue rich)
        {
            signal.Value = rich.Value is string || rich.Value == null
                ? rich.ToString()
                : MockIec61850Client.Format(rich.Value, signal.DataType, signal.Unit);
            signal.Quality = rich.HasQuality ? rich.Quality : "Good";
            signal.DeviceTimestamp = rich.HasDeviceTimestamp ? rich.DeviceTimestamp : "-";
            return;
        }

        signal.Value = MockIec61850Client.Format(value, signal.DataType, signal.Unit);
        signal.Quality = "Good";
    }

    private static async Task TryProbeCompanionQualityAndTimestampAsync(SignalDefinition signal, IIec61850Client client, CancellationToken token)
    {
        if (signal.FunctionalConstraint is not ("ST" or "MX"))
            return;

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
        if (parent.EndsWith(".valWTr.posVal", StringComparison.OrdinalIgnoreCase)) parent = parent[..^14];
        else if (parent.EndsWith(".stVal", StringComparison.OrdinalIgnoreCase)) parent = parent[..^6];
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
            signal.IsSelected = signal.IsScadaCoreSignal && signal.CanPublishToRuntime;
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
        var selected = Signals.Where(s => s.IsSelected && s.CanPublishToRuntime).ToList();
        Bindings.Clear();
        foreach (var item in BindingAutoMapper.CreateBindings(selected))
            Bindings.Add(item);
        ApplySelectedReportPlanToWorkspace();
        SelectedBinding = Bindings.FirstOrDefault();
        RefreshCounts();
    }

    private void PruneBindingsToRuntimeReadySelection()
    {
        var allowed = Signals
            .Where(s => s.IsSelected && s.CanPublishToRuntime)
            .Select(s => s.ObjectReference)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = Bindings.Count - 1; i >= 0; i--)
        {
            if (!allowed.Contains(Bindings[i].IecReference))
                Bindings.RemoveAt(i);
        }

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
        ValidationState = "OK";
        StatusMessage = "IEC Explorer selection saved. Modbus assignment remains unchanged.";
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
        AutoPlanSignalsView.Refresh();
        Raise(nameof(SelectedSignalCount));
        Raise(nameof(BindingCount));
        Raise(nameof(VisibleSignalCountText));
        Raise(nameof(SelectedReportControlSummary));
        Raise(nameof(SelectedDataSetSummary));
        Raise(nameof(ReportPlanStatus));
    }

    private void Raise(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
