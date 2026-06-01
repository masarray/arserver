using System.IO;
using System.Collections.ObjectModel;

namespace Ari61850Bridge.Models;

public sealed class SclImportResult
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName => string.IsNullOrWhiteSpace(FilePath) ? string.Empty : Path.GetFileName(FilePath);
    public string IedName { get; set; } = string.Empty;
    public string AccessPointName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int MmsPort { get; set; } = 102;
    public string RuntimeIpAddress { get; set; } = string.Empty;
    public string SelectedReportControlReference { get; set; } = string.Empty;
    public string SelectedReportControlName { get; set; } = string.Empty;
    public string ReportRuntimeMode { get; set; } = "Static RCB candidate";
    public ObservableCollection<SignalDefinition> Signals { get; set; } = new();
    public List<SclDataSetModel> DataSets { get; set; } = new();
    public List<SclReportControlModel> ReportControls { get; set; } = new();
    public string Summary => $"{IedName} • {Signals.Count} signals • {DataSets.Count} DataSets • {ReportControls.Count} RCBs";
}

public sealed class SclDataSetModel
{
    public string Reference { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string LogicalDevice { get; set; } = string.Empty;
    public string LogicalNode { get; set; } = string.Empty;
    public List<SclDataSetMember> Members { get; set; } = new();
}

public sealed class SclDataSetMember
{
    public string ObjectReference { get; set; } = string.Empty;
    public string FunctionalConstraint { get; set; } = string.Empty;
    public string OriginalText { get; set; } = string.Empty;
}

public sealed class SclReportControlModel
{
    public string Reference { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DataSetName { get; set; } = string.Empty;
    public string DataSetReference { get; set; } = string.Empty;
    public string ReportId { get; set; } = string.Empty;
    public bool Buffered { get; set; }
    public int IntegrityPeriodMs { get; set; }
    public string TriggerOptions { get; set; } = string.Empty;
    public string OptionalFields { get; set; } = string.Empty;
    public string DisplayText => $"{Name} • {(Buffered ? "BRCB" : "URCB")} • DS: {DataSetName} • {Reference}";
}
