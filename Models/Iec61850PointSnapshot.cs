namespace Ari61850Bridge.Models;

public sealed class Iec61850PointSnapshot
{
    public required Iec61850MonitorPoint Point { get; init; }
    public string Value { get; init; } = "-";
    public string Quality { get; init; } = "Unknown";
    public string DeviceTimestamp { get; init; } = "-";
    public DateTime LocalTimestamp { get; init; } = DateTime.Now;
    public string SourceMode { get; init; } = "MMS Polling";
    public string Reason { get; init; } = "cyclic";
    public string Status { get; init; } = "Live";
    public long Sequence { get; init; }
    public int AgeMs { get; init; }
}
