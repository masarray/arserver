namespace Ari61850Bridge.Models;

public class BindingItem : ObservableObject
{
    private bool _isEnabled = true;
    private bool _publishToModbus = true;
    private bool _publishToMqtt = true;
    private string _currentValue = "-";
    private string _quality = "Unknown";
    private string _deviceTimestamp = "-";
    private string _status = "Idle";
    private int _sequence;
    private DateTime _lastUpdate = DateTime.MinValue;
    private int _ageMs;
    private int _pollingIntervalMs = 1000;
    private int _staleTimeoutMs = 3000;

    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }
    public bool PublishToModbus { get => _publishToModbus; set => Set(ref _publishToModbus, value); }
    public bool PublishToMqtt { get => _publishToMqtt; set => Set(ref _publishToMqtt, value); }
    public string RelayId { get; set; } = "";
    public string IedName { get; set; } = "";
    public string RelayIpAddress { get; set; } = "";
    public string SignalName { get; set; } = "";
    public string IecReference { get; set; } = "";
    public string FunctionalConstraint { get; set; } = "";
    public string IecDataType { get; set; } = "";
    public string Category { get; set; } = "";
    public string Unit { get; set; } = "";
    public string ReadMode { get; set; } = "Auto";
    public string RcbMode { get; set; } = "Auto";
    public string DataSetReference { get; set; } = "";
    public string ReportControlReference { get; set; } = "";
    public int PollingIntervalMs { get => _pollingIntervalMs; set => Set(ref _pollingIntervalMs, value); }
    public int StaleTimeoutMs { get => _staleTimeoutMs; set => Set(ref _staleTimeoutMs, value); }

    public string ModbusArea { get; set; } = "HoldingRegister";
    public int ModbusAddress { get; set; }
    public string ModbusDataType { get; set; } = "Float32";
    public string WordOrder { get; set; } = "ABCD";
    public double Scale { get; set; } = 1.0;
    public double Offset { get; set; } = 0.0;
    public string FuxaTagName { get; set; } = "";
    public string MqttTopic { get; set; } = "";

    public string CurrentValue { get => _currentValue; set => Set(ref _currentValue, value); }
    public string Quality { get => _quality; set => Set(ref _quality, value); }
    public string DeviceTimestamp { get => _deviceTimestamp; set => Set(ref _deviceTimestamp, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public int Sequence { get => _sequence; set => Set(ref _sequence, value); }
    public DateTime LastUpdate { get => _lastUpdate; set => Set(ref _lastUpdate, value); }
    public int AgeMs { get => _ageMs; set => Set(ref _ageMs, value); }
}
