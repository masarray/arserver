using System.Collections.ObjectModel;
using System.ComponentModel;
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

    public ObservableCollection<SignalDefinition> Signals { get; }
    public ObservableCollection<BindingItem> Bindings { get; }
    public ICollectionView SignalsView { get; }

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
            Raise(nameof(Step3Visibility));
            Raise(nameof(StepTitle));
            Raise(nameof(StepSubtitle));
            Raise(nameof(PrimaryActionText));
        }
    }

    public Visibility Step1Visibility => StepIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Step2Visibility => StepIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Step3Visibility => StepIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

    public string StepTitle => StepIndex switch
    {
        0 => "Step 1 — Select IEC 61850 SCADA Signals",
        1 => "Step 2 — Build Modbus TCP Binding",
        _ => "Step 3 — Add IED Configuration to Runtime"
    };

    public string StepSubtitle => StepIndex switch
    {
        0 => "Recommended tags are checked by default. Harmonic/statistical MMXU and duplicate instCVal are kept out of default publishing.",
        1 => "Review or edit the Modbus address map. Position uses Input Register, protection uses Discrete Input, measurement uses Holding Register Float32.",
        _ => "Validate once more, then save. Explorer and Modbus Server will show only the saved runtime view."
    };

    public string PrimaryActionText => StepIndex switch
    {
        0 => "Save Selection → Binding",
        1 => "Save Binding → Review",
        _ => "Add to Runtime"
    };

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

    public IedConfigurationWizardWindow(ObservableCollection<SignalDefinition> signals, ObservableCollection<BindingItem> bindings)
    {
        Signals = signals;
        Bindings = bindings;
        _originalSignalSelection = signals.ToDictionary(s => s, s => s.IsSelected);
        _originalBindings = bindings.Select(CloneBinding).ToList();
        SignalsView = CollectionViewSource.GetDefaultView(Signals);
        SignalsView.Filter = FilterSignal;
        SignalsView.SortDescriptions.Clear();
        SignalsView.SortDescriptions.Add(new SortDescription(nameof(SignalDefinition.SortPriority), ListSortDirection.Ascending));
        SignalsView.SortDescriptions.Add(new SortDescription(nameof(SignalDefinition.LogicalNode), ListSortDirection.Ascending));
        SignalsView.SortDescriptions.Add(new SortDescription(nameof(SignalDefinition.Name), ListSortDirection.Ascending));

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
                StatusMessage = $"Fix binding before review: {errors[0]}";
                return;
            }
            ValidationState = "OK";
            StatusMessage = "Binding saved. Review the configuration, then add it to runtime.";
            StepIndex = 2;
            return;
        }

        SaveAndClose();
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
            CurrentValue = source.CurrentValue,
            Quality = source.Quality,
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
    }

    private void Raise(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
