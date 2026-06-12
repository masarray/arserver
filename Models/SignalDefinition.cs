using System.Text.RegularExpressions;

namespace Ari61850Bridge.Models;

public class SignalDefinition : ObservableObject
{
    private bool _isSelected;
    private bool _isReportCapable;
    private string _value = "-";
    private string _quality = "Unknown";
    private string _deviceTimestamp = "-";
    private DateTime _timestamp = DateTime.MinValue;

    private static readonly string[] KnownLogicalNodeClasses =
    {
        "CSWI", "XCBR", "XSWI",
        "MMXU", "MMXN", "MSQI",
        "PTOC", "PTRC", "PDIF", "PDIS", "PIOC", "PTOV", "PTUV", "PTEF", "PDEF", "PSCH", "RREC", "RBRF",
        "GGIO", "GAPC", "LLN0", "LPHD", "CILO", "CPOW"
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value))
                Raise(nameof(ReportPlan));
        }
    }

    public string Name { get; set; } = "";
    public string ObjectReference { get; set; } = "";
    public string FunctionalConstraint { get; set; } = "";
    public string DataType { get; set; } = "";
    public string Category { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Confidence { get; set; } = "Medium";
    public string DataSetReference { get; set; } = "";
    public string ReportControlReference { get; set; } = "";
    public string Source { get; set; } = "Online";

    public bool IsReportCapable
    {
        get => _isReportCapable;
        set
        {
            if (Set(ref _isReportCapable, value))
                Raise(nameof(ReportPlan));
        }
    }

    public string LogicalNode => ExtractLogicalNode(ObjectReference);
    public string LogicalNodeClass => DetectLogicalNodeClass(LogicalNode);
    public bool IsRawAttribute => IsRawEngineeringAttribute(ObjectReference, DataType);
    public bool IsScadaCoreSignal => IsCoreScadaSignal(ObjectReference, LogicalNodeClass, DataType, Category);
    public int SortPriority => CalculateSortPriority(LogicalNodeClass, ObjectReference, Category, Confidence, IsScadaCoreSignal);

    public string ReportPlan => !IsSelected
        ? "Not selected"
        : IsReportCapable
            ? "RCB candidate + polling fallback"
            : "MMS polling";

    public string Value { get => _value; set => Set(ref _value, value); }
    public string Quality { get => _quality; set => Set(ref _quality, value); }
    public string DeviceTimestamp { get => _deviceTimestamp; set => Set(ref _deviceTimestamp, value); }
    public DateTime Timestamp { get => _timestamp; set => Set(ref _timestamp, value); }

    private static string ExtractLogicalNode(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return "";
        var slash = reference.IndexOf('/');
        if (slash < 0 || slash == reference.Length - 1) return "";
        var afterSlash = reference[(slash + 1)..];
        var dot = afterSlash.IndexOf('.');
        return dot > 0 ? afterSlash[..dot] : afterSlash;
    }

    public static string DetectLogicalNodeClass(string logicalNodeName)
    {
        if (string.IsNullOrWhiteSpace(logicalNodeName)) return "";

        // IEC 61850 logical node names commonly allow vendor/project prefix/suffix.
        // Example from real IEDs: BI6GGIO1, OCRSR12PROT/PTRC1, CTRLCSWI1.
        // We therefore detect the standard LN class inside the full LN name, not only at the start.
        foreach (var cls in KnownLogicalNodeClasses)
        {
            if (logicalNodeName.Contains(cls, StringComparison.OrdinalIgnoreCase))
                return cls;
        }

        return logicalNodeName;
    }

    private static int CalculateSortPriority(string lnClass, string reference, string category, string confidence, bool isCoreScadaSignal)
    {
        if (!isCoreScadaSignal) return 800 + AttributeNoisePenalty(reference);

        // SCADA/FUXA operator workflow order: switchgear position first, protection second, measurements last.
        // In HMI operation the user usually needs CB/DS position visibility before analysing protection and analog trends.
        return lnClass.ToUpperInvariant() switch
        {
            "CSWI" => 10,
            "XCBR" => 12,
            "XSWI" => 14,
            "PTOC" => 100,
            "PTRC" => 102,
            "PDIF" => 104,
            "PDIS" => 106,
            "PIOC" => 108,
            "PTOV" => 110,
            "PTUV" => 112,
            "PTEF" => 114,
            "PDEF" => 116,
            "MMXU" => 220,
            "MMXN" => 225,
            _ when string.Equals(category, "Position", StringComparison.OrdinalIgnoreCase) => 20,
            _ when string.Equals(category, "Protection", StringComparison.OrdinalIgnoreCase) => 120,
            _ when string.Equals(category, "Measurement", StringComparison.OrdinalIgnoreCase) => 240,
            _ => 300
        };
    }

    private static int AttributeNoisePenalty(string reference)
    {
        var lower = reference.ToLowerInvariant();
        if (lower.EndsWith(".q")) return 40;
        if (lower.EndsWith(".t")) return 50;
        if (lower.Contains(".ctl") || lower.Contains(".origin") || lower.Contains(".ctlmodel")) return 60;
        if (lower.Contains(".mod.") || lower.EndsWith(".mod.stval") || lower.Contains(".beh.")) return 70;
        return 0;
    }

    public static bool IsCoreScadaSignal(string reference, string logicalNodeClass, string dataType, string category)
    {
        var r = NormalizeRef(reference);
        var cls = logicalNodeClass.ToUpperInvariant();

        if (IsExcludedStatisticLogicalNode(reference))
            return false;

        // Primary equipment status that operators expect in HMI/SCADA.
        if ((cls is "CSWI" or "XCBR" or "XSWI") && r.EndsWith(".pos.stval"))
            return true;

        // Measurements: expose cVal magnitude by default for HMI/SCADA.
        // instCVal is intentionally kept out of the default shortlist to avoid duplicate
        // Phase A/B/C current-voltage tags when both cVal and instCVal exist in a relay model.
        if (cls is "MMXU" or "MMXN")
            return IsDefaultScadaMeasurementMagnitude(r);

        // Protection HMI points: operate/trip/start general flags only.
        if (cls == "PTOC" && (r.EndsWith(".op.general") || r.EndsWith(".str.general"))) return true;
        if (cls == "PTRC" && r.EndsWith(".tr.general")) return true;
        if ((cls is "PDIF" or "PDIS" or "PIOC" or "PTOV" or "PTUV" or "PTEF" or "PDEF") && r.EndsWith(".op.general")) return true;

        return false;
    }

    private static bool IsRawEngineeringAttribute(string reference, string dataType)
    {
        if (string.Equals(dataType, "Quality", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(dataType, "Timestamp", StringComparison.OrdinalIgnoreCase)) return true;

        var r = NormalizeRef(reference);
        return IsStatisticsOrHarmonicNoise(r) ||
               r.EndsWith(".q") ||
               r.EndsWith(".t") ||
               r.Contains(".ctl") ||
               r.Contains(".origin") ||
               r.Contains(".ctlmodel") ||
               r.Contains(".db") ||
               r.Contains(".d") ||
               r.Contains(".du") ||
               r.Contains(".configrev") ||
               r.Contains(".numpts") ||
               r.Contains(".olddata") ||
               r.Contains(".mod.") ||
               r.Contains(".beh.");
    }


    public static bool IsDefaultScadaMeasurementMagnitude(string normalizedReference)
    {
        var r = NormalizeRef(normalizedReference);
        if (IsStatisticsOrHarmonicNoise(r)) return false;
        if (!r.EndsWith(".mag.f")) return false;
        if (!r.Contains(".cval.mag.f")) return false;
        if (r.Contains(".instcval.")) return false;

        return r.Contains(".a.phsa.") ||
               r.Contains(".a.phsb.") ||
               r.Contains(".a.phsc.") ||
               r.Contains(".a.neut.") ||
               r.Contains(".a.net.") ||
               r.Contains(".phv.phsa.") ||
               r.Contains(".phv.phsb.") ||
               r.Contains(".phv.phsc.") ||
               r.Contains(".ppv.phsab.") ||
               r.Contains(".ppv.phsbc.") ||
               r.Contains(".ppv.phsca.");
    }

    public static bool IsInstantCurrentOrVoltageMagnitude(string normalizedReference)
    {
        // Kept for compatibility with earlier code. Advanced raw browse can still find
        // instCVal, but default HMI recommendations use cVal only.
        return IsDefaultScadaMeasurementMagnitude(normalizedReference);
    }

    public static bool IsStatisticsOrHarmonicNoise(string normalizedReference)
    {
        var r = NormalizeRef(normalizedReference);
        return IsExcludedStatisticLogicalNode(r) ||
               Regex.IsMatch(r, @"(^|[./$])(?:har|harm|mean|min|max|avg|average|dmd|demand)\d*(?:mmxu|mmxn)", RegexOptions.IgnoreCase) ||
               r.Contains(".mean") || r.Contains("mean.") ||
               r.Contains(".min") || r.Contains("min.") ||
               r.Contains(".max") || r.Contains("max.") ||
               r.Contains(".avg") || r.Contains("avg.") ||
               r.Contains(".average") ||
               r.Contains(".dmd") || r.Contains("demand") ||
               r.Contains(".har") || r.Contains("harm") ||
               r.Contains(".thd") || r.Contains(".tdd") ||
               r.Contains(".hz") ||
               r.Contains(".w.") || r.Contains("totw") ||
               r.Contains(".var") || r.Contains("totvar") ||
               r.Contains(".va") || r.Contains("totva") ||
               r.Contains(".pf") ||
               r.Contains(".ang.") || r.EndsWith(".ang.f");
    }

    public static bool IsExcludedStatisticLogicalNode(string reference)
    {
        var text = (reference ?? string.Empty).Replace('$', '.').Replace('\\', '/');
        // Vendor IEDs often insert digits between the statistics prefix and MMXU, e.g. Har2MMXU.
        // These LNs are useful for power-quality/statistics pages, but are bad default HMI tags.
        return Regex.IsMatch(text, @"(^|[./])(?:HAR|HARM|MIN|MAX|MEAN|AVG|AVERAGE|DMD|DMMD)\d*(?:MMXU|MMXN)", RegexOptions.IgnoreCase);
    }

    private static string NormalizeRef(string reference)
    {
        return (reference ?? string.Empty)
            .Replace('$', '.')
            .Replace("..", ".")
            .ToLowerInvariant();
    }
}
