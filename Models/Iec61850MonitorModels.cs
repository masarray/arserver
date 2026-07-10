using System.Collections.ObjectModel;

namespace Ari61850Bridge.Models;

public sealed class Iec61850MonitorDevice : ObservableObject
{
    private string _name = "IED";
    private string _ipAddress = "192.168.1.10";
    private int _port = 102;
    private string _status = "Disconnected";
    private string _detail = "Enter an IP address, then Connect & Scan.";
    private bool _isBusy;
    private bool _isMonitoring;

    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public ObservableCollection<SignalDefinition> Signals { get; } = new();
    public ObservableCollection<Iec61850MonitorPoint> Points { get; } = new();

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, string.IsNullOrWhiteSpace(value) ? "IED" : value.Trim()))
                RefreshComputed();
        }
    }

    public string IpAddress
    {
        get => _ipAddress;
        set
        {
            if (Set(ref _ipAddress, value?.Trim() ?? string.Empty))
                RefreshComputed();
        }
    }

    public int Port
    {
        get => _port;
        set
        {
            if (Set(ref _port, value <= 0 ? 102 : value))
                RefreshComputed();
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, string.IsNullOrWhiteSpace(value) ? "Disconnected" : value))
                RefreshComputed();
        }
    }

    public string Detail
    {
        get => _detail;
        set
        {
            if (Set(ref _detail, value ?? string.Empty))
                RefreshComputed();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
                RefreshComputed();
        }
    }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        set
        {
            if (Set(ref _isMonitoring, value))
                RefreshComputed();
        }
    }

    public string EndpointText => string.IsNullOrWhiteSpace(IpAddress) ? "No endpoint" : $"{IpAddress}:{Port}";
    public int SignalCount => Signals.Count;
    public int SelectedSignalCount => Signals.Count(signal => signal.IsSelected);
    public int PointCount => Points.Count;
    public string ActivityText => IsBusy ? "Working…" : IsMonitoring ? "Monitoring" : Status;
    public string SummaryText => $"{EndpointText} • {SignalCount} discovered • {SelectedSignalCount} selected • {PointCount} live";

    public void RefreshComputed()
    {
        Raise(nameof(EndpointText));
        Raise(nameof(SignalCount));
        Raise(nameof(SelectedSignalCount));
        Raise(nameof(PointCount));
        Raise(nameof(ActivityText));
        Raise(nameof(SummaryText));
    }
}

public sealed class Iec61850MonitorPoint : ObservableObject
{
    private string _value = "-";
    private string _quality = "Unknown";
    private string _deviceTimestamp = "-";
    private DateTime _localTimestamp = DateTime.MinValue;
    private string _sourceMode = "Waiting";
    private string _reason = "-";
    private string _status = "Queued";
    private long _sequence;
    private int _ageMs;

    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string SignalName { get; set; } = string.Empty;
    public string ObjectReference { get; set; } = string.Empty;
    public string FunctionalConstraint { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public string DataSetReference { get; set; } = string.Empty;
    public string ReportControlReference { get; set; } = string.Empty;
    public int PollingIntervalMs { get; set; } = 1000;

    public string PointKey => $"{DeviceId}|{ObjectReference}";
    public string Value { get => _value; set => Set(ref _value, string.IsNullOrWhiteSpace(value) ? "-" : value); }
    public string Quality { get => _quality; set => Set(ref _quality, string.IsNullOrWhiteSpace(value) ? "Unknown" : value); }
    public string DeviceTimestamp { get => _deviceTimestamp; set => Set(ref _deviceTimestamp, string.IsNullOrWhiteSpace(value) ? "-" : value); }
    public DateTime LocalTimestamp { get => _localTimestamp; set => Set(ref _localTimestamp, value); }
    public string LocalTimestampText => LocalTimestamp == DateTime.MinValue ? "-" : LocalTimestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
    public string SourceMode { get => _sourceMode; set => Set(ref _sourceMode, string.IsNullOrWhiteSpace(value) ? "Unknown" : value); }
    public string Reason { get => _reason; set => Set(ref _reason, string.IsNullOrWhiteSpace(value) ? "-" : value); }
    public string Status { get => _status; set => Set(ref _status, string.IsNullOrWhiteSpace(value) ? "Unknown" : value); }
    public long Sequence { get => _sequence; set => Set(ref _sequence, value); }
    public int AgeMs { get => _ageMs; set => Set(ref _ageMs, value); }

    public void Touch(DateTime timestamp)
    {
        LocalTimestamp = timestamp;
        Raise(nameof(LocalTimestampText));
    }
}

public sealed class Iec61850EventEntry
{
    public long Sequence { get; init; }
    public DateTime LocalTimestamp { get; init; } = DateTime.Now;
    public string LocalTimestampText => LocalTimestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
    public string DeviceTimestamp { get; init; } = "-";
    public string DeviceName { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public string SignalName { get; init; } = string.Empty;
    public string ObjectReference { get; init; } = string.Empty;
    public string OldValue { get; init; } = "-";
    public string NewValue { get; init; } = "-";
    public string Quality { get; init; } = "Unknown";
    public string SourceMode { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string ChangeText => $"{OldValue} → {NewValue}";
}
