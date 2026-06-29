using Ari61850Bridge.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ari61850Bridge.Services;

/// <summary>
/// Mock IEC 61850 client for testing without a relay or CID file.
/// This adapter provides deterministic sample values for UI, mapping, Modbus, and MQTT workflows.
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
        if (IsDbposDataType(dataType) && TryNormalizeDbpos(value, out var dbpos))
            return FormatDbpos(dbpos);

        return value switch
        {
            null => "-",
            bool b => b ? "True" : "False",
            int i when dataType == "Enum" && i == 1 => "Open",
            int i when dataType == "Enum" && i == 2 => "Closed",
            int i => i.ToString(),
            double d => string.IsNullOrWhiteSpace(unit) ? d.ToString("0.###") : $"{d:0.###} {unit}",
            float f => string.IsNullOrWhiteSpace(unit) ? f.ToString("0.###") : $"{f:0.###} {unit}",
            _ => value.ToString() ?? "-"
        };
    }

    private static bool IsDbposDataType(string dataType) =>
        dataType.Equals("Dbpos", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("DPC", StringComparison.OrdinalIgnoreCase);

    private static string FormatDbpos(int code) => code switch
    {
        0 => "Intermediate [00]",
        1 => "Open [01]",
        2 => "Close [10]",
        3 => "Invalid [11]",
        _ => code.ToString(CultureInfo.InvariantCulture)
    };

    internal static bool TryNormalizeDbpos(object? value, out int code)
    {
        code = 0;
        switch (value)
        {
            case byte b when b <= 3: code = b; return true;
            case sbyte b when b is >= 0 and <= 3: code = b; return true;
            case short s when s is >= 0 and <= 3: code = s; return true;
            case ushort s when s <= 3: code = s; return true;
            case int i when i is >= 0 and <= 3: code = i; return true;
            case uint i when i <= 3: code = (int)i; return true;
            case long l when l is >= 0 and <= 3: code = (int)l; return true;
            case ulong l when l <= 3: code = (int)l; return true;
            case bool b: code = b ? 2 : 1; return true;
            case string text: return TryParseDbposText(text, out code);
            default: return false;
        }
    }

    private static bool TryParseDbposText(string text, out int code)
    {
        code = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var bracketCode = Regex.Match(text, @"\[(00|01|10|11)\]", RegexOptions.CultureInvariant);
        if (bracketCode.Success)
            return TryParseDbposBits(bracketCode.Groups[1].Value, out code);

        // MmsDataValueRenderer uses this shape for BIT STRING values. For Dbpos,
        // exactly two bits are significant and occupy the high bits of the first byte.
        var renderedBits = Regex.Match(
            text,
            @"bits\(\s*(?:0x)?([0-9a-f]{2})\s*,\s*unused\s*=\s*(\d+)\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (renderedBits.Success &&
            byte.TryParse(renderedBits.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw) &&
            int.TryParse(renderedBits.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unused) &&
            unused == 6)
        {
            code = (raw >> unused) & 0x03;
            return true;
        }

        var compact = text.Trim().Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
        switch (compact)
        {
            case "0":
            case "00":
            case "intermediate":
            case "intermediatestate":
                code = 0; return true;
            case "1":
            case "01":
            case "open":
            case "off":
                code = 1; return true;
            case "2":
            case "10":
            case "closed":
            case "close":
            case "on":
                code = 2; return true;
            case "3":
            case "11":
            case "bad":
            case "badstate":
            case "invalid":
                code = 3; return true;
            default:
                return false;
        }
    }

    private static bool TryParseDbposBits(string bits, out int code)
    {
        code = bits switch
        {
            "00" => 0,
            "01" => 1,
            "10" => 2,
            "11" => 3,
            _ => -1
        };
        return code >= 0;
    }

    private static object ParseValue(string display, string dataType)
    {
        if (dataType == "Boolean") return display.Equals("True", StringComparison.OrdinalIgnoreCase);
        if (dataType == "Enum") return display.Equals("Closed", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        var numeric = display.Split(' ')[0].Replace(',', '.');
        return double.TryParse(numeric, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0.0;
    }
}
