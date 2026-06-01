using System.Collections.ObjectModel;
using System.Windows;
using Ari61850Bridge.Models;

namespace Ari61850Bridge;

public partial class SclImportWizardWindow : Window
{
    public static readonly DependencyProperty RuntimeIpAddressProperty = DependencyProperty.Register(nameof(RuntimeIpAddress), typeof(string), typeof(SclImportWizardWindow), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty MmsPortProperty = DependencyProperty.Register(nameof(MmsPort), typeof(int), typeof(SclImportWizardWindow), new PropertyMetadata(102));
    public static readonly DependencyProperty SelectedReportControlProperty = DependencyProperty.Register(nameof(SelectedReportControl), typeof(SclReportControlModel), typeof(SclImportWizardWindow), new PropertyMetadata(null));
    public static readonly DependencyProperty ReportRuntimeModeProperty = DependencyProperty.Register(nameof(ReportRuntimeMode), typeof(string), typeof(SclImportWizardWindow), new PropertyMetadata("Static RCB candidate"));

    public ObservableCollection<SclReportControlModel> AvailableReportControls { get; } = new();
    public ObservableCollection<string> ReportRuntimeModes { get; } = new(new[] { "Static RCB candidate", "Dynamic RCB candidate", "MMS polling only" });
    public SclImportResult ImportedScl { get; }

    public string RuntimeIpAddress { get => (string)GetValue(RuntimeIpAddressProperty); set => SetValue(RuntimeIpAddressProperty, value); }
    public int MmsPort { get => (int)GetValue(MmsPortProperty); set => SetValue(MmsPortProperty, value); }
    public SclReportControlModel? SelectedReportControl { get => (SclReportControlModel?)GetValue(SelectedReportControlProperty); set => SetValue(SelectedReportControlProperty, value); }
    public string ReportRuntimeMode { get => (string)GetValue(ReportRuntimeModeProperty); set => SetValue(ReportRuntimeModeProperty, value); }

    public string FilePath => ImportedScl.FilePath;
    public string ImportSummary => $"IED {ImportedScl.IedName} • AP {ImportedScl.AccessPointName} • {ImportedScl.Signals.Count} signals • {ImportedScl.DataSets.Count} DataSets • {ImportedScl.ReportControls.Count} RCBs";
    public string SclConnectionText => $"AP: {ImportedScl.AccessPointName} • SCL IP: {(string.IsNullOrWhiteSpace(ImportedScl.IpAddress) ? "not provided" : ImportedScl.IpAddress)}:{ImportedScl.MmsPort}";

    public SclImportWizardWindow(SclImportResult import)
    {
        ImportedScl = import;
        RuntimeIpAddress = string.IsNullOrWhiteSpace(import.RuntimeIpAddress) ? import.IpAddress : import.RuntimeIpAddress;
        MmsPort = import.MmsPort <= 0 ? 102 : import.MmsPort;
        foreach (var rcb in import.ReportControls.OrderBy(r => r.Buffered ? 0 : 1).ThenBy(r => r.Reference))
            AvailableReportControls.Add(rcb);
        SelectedReportControl = AvailableReportControls.FirstOrDefault();
        InitializeComponent();
        Loaded += (_, _) => RuntimeIpBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        RuntimeIpAddress = (RuntimeIpAddress ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(RuntimeIpAddress))
        {
            WizardStatusText.Text = "Set the actual Runtime IP address before saving the SCL plan.";
            RuntimeIpBox.Focus();
            return;
        }
        ImportedScl.RuntimeIpAddress = RuntimeIpAddress;
        ImportedScl.MmsPort = MmsPort;
        ImportedScl.SelectedReportControlReference = SelectedReportControl?.Reference ?? string.Empty;
        ImportedScl.SelectedReportControlName = SelectedReportControl?.Name ?? string.Empty;
        ImportedScl.ReportRuntimeMode = ReportRuntimeMode;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
