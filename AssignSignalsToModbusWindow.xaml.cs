using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Ari61850Bridge.Models;

namespace Ari61850Bridge;

public partial class AssignSignalsToModbusWindow : Window, INotifyPropertyChanged
{
    private RelayEndpointView? _selectedRelay;
    private string _searchText = string.Empty;

    public ObservableCollection<RelayEndpointView> Relays { get; }
    public ObservableCollection<AssignmentSignalRow> Rows { get; } = new();
    public ICollectionView RowsView { get; }
    public IReadOnlyList<SignalDefinition> SelectedSignals { get; private set; } = Array.Empty<SignalDefinition>();

    public event PropertyChangedEventHandler? PropertyChanged;

    public RelayEndpointView? SelectedRelay
    {
        get => _selectedRelay;
        set
        {
            if (ReferenceEquals(_selectedRelay, value)) return;
            _selectedRelay = value;
            Raise();
            RebuildRows();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            Raise();
            RowsView.Refresh();
            Raise(nameof(SelectionSummary));
        }
    }

    public string SelectionSummary => $"{Rows.Count(r => r.IsChecked && r.CanAssign)} selected · {Rows.Count(r => r.IsAlreadyAssigned)} already assigned · {RowsView.Cast<object>().Count()} visible";

    public AssignSignalsToModbusWindow(IEnumerable<RelayEndpointView> relays, RelayEndpointView? selectedRelay)
    {
        Relays = new ObservableCollection<RelayEndpointView>(relays.Where(r => r.Signals.Any(s => s.IsSelected && s.CanPublishToRuntime)));
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = FilterRow;
        DataContext = this;
        InitializeComponent();
        SelectedRelay = Relays.FirstOrDefault(r => ReferenceEquals(r, selectedRelay)) ?? Relays.FirstOrDefault();
    }

    private void RebuildRows()
    {
        Rows.Clear();
        if (SelectedRelay != null)
        {
            var assigned = SelectedRelay.ModbusBindings
                .Select(b => b.IecReference)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var signal in SelectedRelay.Signals
                         .Where(s => s.IsSelected && s.CanPublishToRuntime)
                         .OrderBy(s => s.SortPriority)
                         .ThenBy(s => s.ObjectReference))
            {
                var row = new AssignmentSignalRow(signal, assigned.Contains(signal.ObjectReference));
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(AssignmentSignalRow.IsChecked))
                        Raise(nameof(SelectionSummary));
                };
                Rows.Add(row);
            }
        }
        RowsView.Refresh();
        Raise(nameof(SelectionSummary));
    }

    private bool FilterRow(object item)
    {
        if (item is not AssignmentSignalRow row) return false;
        var text = SearchText.Trim();
        if (string.IsNullOrWhiteSpace(text)) return true;
        var haystack = $"{row.Signal.Name} {row.Signal.ObjectReference} {row.Signal.LogicalNode} {row.Signal.DataType} {row.Signal.Category} {row.Signal.Value} {row.Signal.Quality}";
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private void SignalsGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindVisualParent<CheckBox>(source) != null || FindVisualParent<DataGridColumnHeader>(source) != null)
            return;
        var row = FindVisualParent<DataGridRow>(source);
        if (row?.Item is not AssignmentSignalRow item || !item.CanAssign) return;
        item.IsChecked = !item.IsChecked;
        row.IsSelected = true;
        e.Handled = true;
    }

    private void AssignmentCheckBox_Click(object sender, RoutedEventArgs e)
    {
        Raise(nameof(SelectionSummary));
        e.Handled = true;
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) => SearchText = string.Empty;

    private void Assign_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRelay == null) return;
        SelectedSignals = Rows.Where(r => r.IsChecked && r.CanAssign).Select(r => r.Signal).ToList();
        if (SelectedSignals.Count == 0)
        {
            MessageBox.Show(this, "Select at least one unassigned Explorer signal.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
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

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class AssignmentSignalRow : INotifyPropertyChanged
{
    private bool _isChecked;
    public SignalDefinition Signal { get; }
    public bool IsAlreadyAssigned { get; }
    public bool CanAssign => !IsAlreadyAssigned;
    public string AssignmentStatus => IsAlreadyAssigned ? "Assigned" : "Available";
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!CanAssign || _isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public AssignmentSignalRow(SignalDefinition signal, bool isAlreadyAssigned)
    {
        Signal = signal;
        IsAlreadyAssigned = isAlreadyAssigned;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
