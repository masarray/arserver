using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Ari61850Bridge.Models;

public class RelayEndpointView : ObservableObject
{
    private string _ipAddress = "";
    private int _mmsPort = 102;
    private string _iedName = "Relay";
    private string _status = "Draft";
    private string _mode = "MMS";
    private string _rcbName = "Polling";
    private string _rcbMode = "MMS polling";
    private int _tagCount;
    private string _sclFilePath = string.Empty;
    private string _sclSummary = string.Empty;
    private string _sclIpAddress = string.Empty;
    private string _selectedReportControlReference = string.Empty;
    private string _reportRuntimeMode = "MMS polling";
    private int _dataSetCount;
    private int _reportControlCount;
    private string _heartbeatText = "Idle";
    private bool _isActive;
    private MediaBrush _statusBrush = new SolidColorBrush(MediaColor.FromRgb(148, 163, 184));
    private MediaBrush _activityBrush = new SolidColorBrush(MediaColor.FromRgb(148, 163, 184));

    public string RelayId { get; set; } = Guid.NewGuid().ToString("N");
    public ObservableCollection<SignalDefinition> Signals { get; set; } = new();
    public ObservableCollection<BindingItem> ModbusBindings { get; set; } = new();

    public string IpAddress { get => _ipAddress; set => Set(ref _ipAddress, value); }
    public int MmsPort { get => _mmsPort; set => Set(ref _mmsPort, value); }
    public string IedName { get => _iedName; set => Set(ref _iedName, value); }
    public string Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                Raise(nameof(SummaryText));
                Raise(nameof(IsConnectedLike));
                Raise(nameof(ConnectionActionText));
            }
        }
    }
    public string Mode { get => _mode; set { if (Set(ref _mode, value)) Raise(nameof(RcbSummaryText)); } }
    public string RcbName { get => _rcbName; set { if (Set(ref _rcbName, value)) Raise(nameof(RcbSummaryText)); } }
    public string RcbMode { get => _rcbMode; set { if (Set(ref _rcbMode, value)) Raise(nameof(RcbSummaryText)); } }
    public int TagCount { get => _tagCount; set => Set(ref _tagCount, value); }
    public string SclFilePath { get => _sclFilePath; set => Set(ref _sclFilePath, value); }
    public string SclSummary { get => _sclSummary; set => Set(ref _sclSummary, value); }
    public string SclIpAddress { get => _sclIpAddress; set { if (Set(ref _sclIpAddress, value)) Raise(nameof(SclIpSummaryText)); } }
    public string SelectedReportControlReference { get => _selectedReportControlReference; set => Set(ref _selectedReportControlReference, value); }
    public string ReportRuntimeMode { get => _reportRuntimeMode; set { if (Set(ref _reportRuntimeMode, value)) Raise(nameof(RcbSummaryText)); } }
    public int DataSetCount { get => _dataSetCount; set { if (Set(ref _dataSetCount, value)) Raise(nameof(RcbSummaryText)); } }
    public int ReportControlCount { get => _reportControlCount; set { if (Set(ref _reportControlCount, value)) Raise(nameof(RcbSummaryText)); } }
    public string HeartbeatText { get => _heartbeatText; set => Set(ref _heartbeatText, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    [JsonIgnore]
    public MediaBrush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
    [JsonIgnore]
    public MediaBrush ActivityBrush { get => _activityBrush; set => Set(ref _activityBrush, value); }
    [JsonIgnore]
    public DateTime LastMmsActivityUtc { get; set; } = DateTime.MinValue;

    public string DisplayName => string.IsNullOrWhiteSpace(IedName) ? "Relay" : IedName;
    public string EndpointText => string.IsNullOrWhiteSpace(IpAddress) ? "New relay" : $"{IpAddress}:{MmsPort}";
    public string SclIpSummaryText => string.IsNullOrWhiteSpace(SclIpAddress) ? "SCL IP: not provided" : string.Equals(SclIpAddress, IpAddress, StringComparison.OrdinalIgnoreCase) ? $"SCL IP: {SclIpAddress}" : $"SCL IP: {SclIpAddress} overridden by runtime IP";
    public string SummaryText => $"{Status} • {TagCount} tags • {HeartbeatText}";
    public string RcbSummaryText => ReportControlCount > 0
        ? $"RCB: {RcbName} • {ReportRuntimeMode} • DS {DataSetCount} • RCB {ReportControlCount}"
        : $"RCB: {RcbName} • {RcbMode}";
    public bool IsConnectedLike => Status.Contains("Connected", StringComparison.OrdinalIgnoreCase) || Status.Contains("Live", StringComparison.OrdinalIgnoreCase) || Status.Contains("Transport Ready", StringComparison.OrdinalIgnoreCase);
    public string ConnectionActionText => IsConnectedLike ? "Disconnect" : "Connect";

    public void RefreshComputed()
    {
        Raise(nameof(DisplayName));
        Raise(nameof(EndpointText));
        Raise(nameof(SummaryText));
        Raise(nameof(IsConnectedLike));
        Raise(nameof(ConnectionActionText));
        Raise(nameof(RcbSummaryText));
        Raise(nameof(SclIpSummaryText));
    }

    public static MediaBrush BrushForStatus(string status)
    {
        var color = status.ToLowerInvariant() switch
        {
            var s when s.Contains("live") || s.Contains("connected") || s.Contains("associated") => MediaColor.FromRgb(34, 197, 94),
            var s when s.Contains("connecting") || s.Contains("transport ready") => MediaColor.FromRgb(37, 99, 235),
            var s when s.Contains("stale") || s.Contains("warning") || s.Contains("pending") => MediaColor.FromRgb(245, 158, 11),
            var s when s.Contains("failed") || s.Contains("error") => MediaColor.FromRgb(239, 68, 68),
            _ => MediaColor.FromRgb(148, 163, 184)
        };
        return new SolidColorBrush(color);
    }
}
