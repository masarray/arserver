namespace Ari61850Bridge.Models;

public sealed class RuntimeValueSnapshot
{
    public string RelayId { get; set; } = string.Empty;
    public string IecReference { get; set; } = string.Empty;
    public string Value { get; set; } = "-";
    public string Quality { get; set; } = "Unknown";
    public string DeviceTimestamp { get; set; } = "-";
    public DateTime Timestamp { get; set; } = DateTime.MinValue;
    public string Status { get; set; } = string.Empty;
    public long Sequence { get; set; }
}
