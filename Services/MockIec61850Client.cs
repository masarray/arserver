using Ari61850Bridge.Models;

namespace Ari61850Bridge.Services;

/// <summary>
/// Mock IEC 61850 client for MVP testing without relay/CID.
/// Replace this adapter with libiec61850.NET implementation in the next stage.
/// </summary>
public sealed class MockIec61850Client : IIec61850Client
{
    private readonly Random _random = new();
    private readonly Dictionary<string, object> _values = new();
    private IReadOnlyList<SignalDefinition> _signals = Array.Empty<SignalDefinition>();

    public bool IsConnected { get; private set; }
    public string ConnectionMode => "Mock IEC61850 Discovery";

    public async Task ConnectAsync(string ipAddress, int port, CancellationToken cancellationToken)
    {
        await Task.Delay(350, cancellationToken);
        IsConnected = true;
    }

    public async Task<IReadOnlyList<SignalDefinition>> DiscoverSignalsAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(500, cancellationToken);

        var now = DateTime.Now;
        _signals = new List<SignalDefinition>
        {
            New("Phase A Current", "LD0/MMXU1.A.phsA.cVal.mag.f", "MX", "Float32", "Measurement", "A", "High", true, 125.2, now),
            New("Phase B Current", "LD0/MMXU1.A.phsB.cVal.mag.f", "MX", "Float32", "Measurement", "A", "High", true, 124.8, now),
            New("Phase C Current", "LD0/MMXU1.A.phsC.cVal.mag.f", "MX", "Float32", "Measurement", "A", "High", true, 126.1, now),
            New("Phase A Voltage", "LD0/MMXU1.PhV.phsA.cVal.mag.f", "MX", "Float32", "Measurement", "kV", "High", true, 20.1, now),
            New("Phase B Voltage", "LD0/MMXU1.PhV.phsB.cVal.mag.f", "MX", "Float32", "Measurement", "kV", "High", true, 20.0, now),
            New("Phase C Voltage", "LD0/MMXU1.PhV.phsC.cVal.mag.f", "MX", "Float32", "Measurement", "kV", "High", true, 20.2, now),
            New("Frequency", "LD0/MMXU1.Hz.mag.f", "MX", "Float32", "Measurement", "Hz", "High", true, 50.01, now),
            New("Breaker Position", "LD0/XCBR1.Pos.stVal", "ST", "Enum", "Breaker", "", "High", true, 2, now),
            New("Trip General", "LD0/PTRC1.Tr.general", "ST", "Boolean", "Protection", "", "High", true, false, now),
            New("Overcurrent Operate", "LD0/PTOC1.Op.general", "ST", "Boolean", "Protection", "", "High", true, false, now),
            New("Differential Operate", "LD0/PDIF1.Op.general", "ST", "Boolean", "Protection", "", "High", true, false, now),
            New("Alarm 1", "LD0/GGIO1.Ind1.stVal", "ST", "Boolean", "Alarm", "", "Medium", true, false, now),
            New("Alarm 2", "LD0/GGIO1.Ind2.stVal", "ST", "Boolean", "Alarm", "", "Medium", true, false, now),
            New("IED Health", "LD0/LLN0.Health.stVal", "ST", "Enum", "Health", "", "High", true, 1, now),
            New("Local/Remote", "LD0/LLN0.Loc.stVal", "ST", "Boolean", "Control", "", "Medium", true, false, now),
            New("Temperature", "LD0/STMP1.Tmp.mag.f", "MX", "Float32", "Measurement", "°C", "Medium", false, 36.5, now),
        };

        foreach (var signal in _signals)
            _values[signal.ObjectReference] = ParseValue(signal.Value, signal.DataType);

        return _signals;
    }

    public Task<object?> ReadValueAsync(string objectReference, CancellationToken cancellationToken)
    {
        if (!_values.TryGetValue(objectReference, out var current)) return Task.FromResult<object?>(null);

        object next = current;
        if (current is double d)
        {
            next = Math.Round(d + (_random.NextDouble() - 0.5) * 0.8, 3);
        }
        else if (current is bool b)
        {
            // Keep most digitals stable, but simulate occasional event changes.
            next = _random.NextDouble() < 0.025 ? !b : b;
        }
        else if (current is int i)
        {
            if (objectReference.Contains("XCBR") && _random.NextDouble() < 0.015)
                next = i == 1 ? 2 : 1; // 1=open, 2=closed in this demo
            else
                next = i;
        }

        _values[objectReference] = next;
        return Task.FromResult<object?>(next);
    }


    public Task<object?> ReadValueAsync(string objectReference, string functionalConstraint, string dataType, CancellationToken cancellationToken)
    {
        return ReadValueAsync(objectReference, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    private static SignalDefinition New(string name, string reference, string fc, string type, string category, string unit, string confidence, bool reportCapable, object value, DateTime timestamp)
    {
        return new SignalDefinition
        {
            Name = name,
            ObjectReference = reference,
            FunctionalConstraint = fc,
            DataType = type,
            Category = category,
            Unit = unit,
            Confidence = confidence,
            IsReportCapable = reportCapable,
            Value = Format(value, type, unit),
            Quality = "Good",
            Timestamp = timestamp,
            IsSelected = SignalDefinition.IsCoreScadaSignal(reference, SignalDefinition.DetectLogicalNodeClass(ExtractLogicalNode(reference)), type, category)
        };
    }

    private static string ExtractLogicalNode(string reference)
    {
        var slash = reference.IndexOf('/');
        if (slash < 0) return reference;
        var afterSlash = reference[(slash + 1)..];
        var dot = afterSlash.IndexOf('.');
        return dot > 0 ? afterSlash[..dot] : afterSlash;
    }

    public static string Format(object? value, string dataType, string unit)
    {
        return value switch
        {
            null => "-",
            bool b => b ? "True" : "False",
            int i when IsDbposDataType(dataType) && i == 0 => "Intermediate",
            int i when IsDbposDataType(dataType) && i == 1 => "Open",
            int i when IsDbposDataType(dataType) && i == 2 => "Closed",
            int i when IsDbposDataType(dataType) && i == 3 => "Bad-state",
            int i when dataType == "Enum" && i == 1 => "Open",
            int i when dataType == "Enum" && i == 2 => "Closed",
            string text when IsDbposDataType(dataType) => FormatDbposText(text),
            int i => i.ToString(),
            double d => string.IsNullOrWhiteSpace(unit) ? d.ToString("0.###") : $"{d:0.###} {unit}",
            float f => string.IsNullOrWhiteSpace(unit) ? f.ToString("0.###") : $"{f:0.###} {unit}",
            _ => value.ToString() ?? "-"
        };
    }

    private static bool IsDbposDataType(string dataType) =>
        dataType.Equals("Dbpos", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("DPC", StringComparison.OrdinalIgnoreCase);

    private static string FormatDbposText(string text)
    {
        var compact = text.Trim().Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
        return compact switch
        {
            "0" or "00" => "Intermediate",
            "1" or "01" or "open" or "off" => "Open",
            "2" or "10" or "closed" or "close" or "on" => "Closed",
            "3" or "11" or "bad" => "Bad-state",
            _ => text
        };
    }

    private static object ParseValue(string display, string dataType)
    {
        if (dataType == "Boolean") return display.Equals("True", StringComparison.OrdinalIgnoreCase);
        if (dataType == "Enum") return display.Equals("Closed", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        var numeric = display.Split(' ')[0].Replace(',', '.');
        return double.TryParse(numeric, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0.0;
    }
}
