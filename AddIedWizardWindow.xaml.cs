using System.Collections.ObjectModel;
using System.Windows;
using Ari61850Bridge.Models;
using Ari61850Bridge.Services;
using Microsoft.Win32;

namespace Ari61850Bridge;

public partial class AddIedWizardWindow : Window
{
    public static readonly DependencyProperty RelayIpAddressProperty = DependencyProperty.Register(
        nameof(RelayIpAddress), typeof(string), typeof(AddIedWizardWindow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty MmsPortProperty = DependencyProperty.Register(
        nameof(MmsPort), typeof(int), typeof(AddIedWizardWindow), new PropertyMetadata(102));


    public static readonly DependencyProperty SclSummaryProperty = DependencyProperty.Register(
        nameof(SclSummary), typeof(string), typeof(AddIedWizardWindow), new PropertyMetadata("No CID/SCD file imported yet."));

    public static readonly DependencyProperty SclFilePathProperty = DependencyProperty.Register(
        nameof(SclFilePath), typeof(string), typeof(AddIedWizardWindow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SclIpAddressProperty = DependencyProperty.Register(
        nameof(SclIpAddress), typeof(string), typeof(AddIedWizardWindow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SelectedReportControlProperty = DependencyProperty.Register(
        nameof(SelectedReportControl), typeof(SclReportControlModel), typeof(AddIedWizardWindow), new PropertyMetadata(null));

    public static readonly DependencyProperty ReportRuntimeModeProperty = DependencyProperty.Register(
        nameof(ReportRuntimeMode), typeof(string), typeof(AddIedWizardWindow), new PropertyMetadata("Static RCB candidate"));

    public ObservableCollection<SclReportControlModel> AvailableReportControls { get; } = new();
    public ObservableCollection<string> ReportRuntimeModes { get; } = new(new[] { "Static RCB candidate", "Dynamic RCB candidate", "MMS polling only" });

    public string RelayIpAddress
    {
        get => (string)GetValue(RelayIpAddressProperty);
        set => SetValue(RelayIpAddressProperty, value);
    }

    public int MmsPort
    {
        get => (int)GetValue(MmsPortProperty);
        set => SetValue(MmsPortProperty, value);
    }


    public string SclSummary
    {
        get => (string)GetValue(SclSummaryProperty);
        set => SetValue(SclSummaryProperty, value);
    }

    public string SclFilePath
    {
        get => (string)GetValue(SclFilePathProperty);
        set => SetValue(SclFilePathProperty, value);
    }

    public string SclIpAddress
    {
        get => (string)GetValue(SclIpAddressProperty);
        set => SetValue(SclIpAddressProperty, value);
    }

    public SclReportControlModel? SelectedReportControl
    {
        get => (SclReportControlModel?)GetValue(SelectedReportControlProperty);
        set => SetValue(SelectedReportControlProperty, value);
    }

    public string ReportRuntimeMode
    {
        get => (string)GetValue(ReportRuntimeModeProperty);
        set => SetValue(ReportRuntimeModeProperty, value);
    }

    public SclImportResult? ImportedScl { get; private set; }

    public AddIedWizardWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RelayIpBox.Focus();
    }

    private void ImportScl_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import IEC 61850 SCL/CID/SCD/ICD",
            Filter = "IEC 61850 SCL files (*.cid;*.scd;*.icd;*.iid;*.sed;*.xml)|*.cid;*.scd;*.icd;*.iid;*.sed;*.xml|All files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var result = SclImportService.Import(dialog.FileName);
            ImportedScl = result;
            SclFilePath = dialog.FileName;
            SclIpAddress = result.IpAddress;
            SclSummary = $"{result.Summary} • SCL IP {(string.IsNullOrWhiteSpace(result.IpAddress) ? "not found" : result.IpAddress)}:{result.MmsPort}";

            // Runtime IP is intentionally editable. The imported SCL/CID is not modified.
            // In real FAT/site work, the SCL IP is often an engineering/default address while
            // the actual relay endpoint is assigned by the test network.
            if (!string.IsNullOrWhiteSpace(result.IpAddress) && string.IsNullOrWhiteSpace(RelayIpAddress))
                RelayIpAddress = result.IpAddress;
            if (result.MmsPort > 0)
                MmsPort = result.MmsPort;

            AvailableReportControls.Clear();
            foreach (var rcb in result.ReportControls.OrderBy(r => r.Buffered ? 0 : 1).ThenBy(r => r.Reference))
                AvailableReportControls.Add(rcb);
            SelectedReportControl = AvailableReportControls.FirstOrDefault();

            WizardStatusText.Text = $"SCL imported: {result.IedName}. DataSets={result.DataSets.Count}, RCB={result.ReportControls.Count}. Set the Runtime IP and choose RCB strategy before saving.";
        }
        catch (Exception ex)
        {
            ImportedScl = null;
            SclFilePath = string.Empty;
            SclSummary = "SCL import failed.";
            SclIpAddress = string.Empty;
            AvailableReportControls.Clear();
            SelectedReportControl = null;
            WizardStatusText.Text = $"SCL import failed: {ex.Message}";
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        RelayIpAddress = (RelayIpAddress ?? string.Empty).Trim();
        if (ImportedScl != null)
        {
            ImportedScl.RuntimeIpAddress = RelayIpAddress;
            ImportedScl.MmsPort = MmsPort;
            ImportedScl.SelectedReportControlReference = SelectedReportControl?.Reference ?? string.Empty;
            ImportedScl.SelectedReportControlName = SelectedReportControl?.Name ?? string.Empty;
            ImportedScl.ReportRuntimeMode = ReportRuntimeMode;
        }
        if (string.IsNullOrWhiteSpace(RelayIpAddress) && ImportedScl == null)
        {
            WizardStatusText.Text = "Enter the IED IP address or import CID/SCD first.";
            RelayIpBox.Focus();
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
}
