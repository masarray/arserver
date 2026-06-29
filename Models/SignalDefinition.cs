using System.Text.RegularExpressions;

namespace Ari61850Bridge.Models;

public class SignalDefinition : ObservableObject
{
    private bool _isSelected;
    private bool _isReportCapable;
    private string _value = "-";
    private string _quality = "Unknown";
    private string _deviceTimestamp = "-";
    private string _probeStatus = "Not probed";
    private string _reportCoverage = "Polling fallback";
    private DateTime _timestamp = DateTime.MinValue;

    private static readonly string[] KnownLogicalNodeClasses =
    {
        "CSWI", "XCBR", "XSWI",
        "ATCC", "AVCO", "AVC", "YPTR",
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
    public string ReportCoverageReason { get; set; } = "Readable by MMS; report coverage not confirmed.";

    // Compatibility alias used by the UI search filter.
    // The canonical field is ReportCoverageReason; keeping this read-only alias prevents
    // compile breaks when older/newer UI code searches by report-plan reason text.
    public string ReportPlanReason => ReportCoverageReason;

    public string QualityReference { get; set; } = "";
    public string TimestampReference { get; set; } = "";
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

    // q/t, Health, Beh, Mod, RCB attributes, nameplate, and other engineering leaves are
    // companion/diagnostic attributes. They must not become user-selected SCADA points.
    public bool IsRawAttribute => IsRawEngineeringAttribute(ObjectReference, DataType);

    public bool IsValueSignal => IsRuntimeValueSignal(ObjectReference, FunctionalConstraint, DataType, Category);
    public bool CanPublishAsSignal => IsValueSignal && !IsRawAttribute;
    public bool IsKnownReadFailure => IsKnownReadFailureState(Value, Quality, ProbeStatus);
    public bool CanPublishToRuntime => CanPublishAsSignal && !IsKnownReadFailure;
    public bool IsScadaCoreSignal => IsCoreScadaSignal(ObjectReference, LogicalNodeClass, DataType, Category);
    public int SortPriority => CalculateSortPriority(LogicalNodeClass, ObjectReference, Category, Confidence, IsScadaCoreSignal);

    public string ReportCoverage
    {
        get => _reportCoverage;
        set
        {
            if (Set(ref _reportCoverage, string.IsNullOrWhiteSpace(value) ? "Polling fallback" : value))
                Raise(nameof(ReportPlan));
        }
    }

    public string ReportPlan => !IsSelected
        ? "Not selected"
        : !CanPublishAsSignal
            ? "Hidden attribute"
            : !string.IsNullOrWhiteSpace(ReportCoverage)
                ? ReportCoverage
                : IsReportCapable
                    ? "Report candidate + polling fallback"
                    : "MMS polling";

    public string SignalPropertiesSummary => BuildSignalPropertiesSummary();

    public string Value { get => _value; set => Set(ref _value, value); }
    public string Quality { get => _quality; set => Set(ref _quality, value); }
    public string DeviceTimestamp { get => _deviceTimestamp; set => Set(ref _deviceTimestamp, string.IsNullOrWhiteSpace(value) ? "-" : value); }
    public string ProbeStatus { get => _probeStatus; set => Set(ref _probeStatus, string.IsNullOrWhiteSpace(value) ? "Not probed" : value); }
    public DateTime Timestamp { get => _timestamp; set => Set(ref _timestamp, value); }

    private string BuildSignalPropertiesSummary()
    {
        var q = string.IsNullOrWhiteSpace(QualityReference) ? "auto sidecar / not confirmed" : QualityReference;
        var t = string.IsNullOrWhiteSpace(TimestampReference) ? "auto sidecar / not confirmed" : TimestampReference;
        var ds = string.IsNullOrWhiteSpace(DataSetReference) ? "not covered by confirmed DataSet" : DataSetReference;
        var rcb = string.IsNullOrWhiteSpace(ReportControlReference) ? "none / polling fallback" : ReportControlReference;
        var reason = string.IsNullOrWhiteSpace(ReportCoverageReason) ? ReportPlan : ReportCoverageReason;

        return $"IEC Signal: {ObjectReference}\n" +
               $"Functional Constraint: {FunctionalConstraint}\n" +
               $"Data Type: {DataType}\n" +
               $"Category: {Category}\n" +
               $"Logical Node: {LogicalNode} ({LogicalNodeClass})\n" +
               $"Quality Attribute: {q}\n" +
               $"Timestamp Attribute: {t}\n" +
               $"DataSet: {ds}\n" +
               $"Report Control Block: {rcb}\n" +
               $"Runtime Source: {ReportPlan}\n" +
               $"Reason: {reason}";
    }

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
        // Example from live IEDs: BI6GGIO1, OCRSR12PROT/PTRC1, CTRLCSWI1.
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
            "ATCC" => 180,
            "AVC" or "AVCO" => 185,
            "MMXU" => 220,
            "MMXN" => 225,
            "GGIO" => 260,
            "YPTR" => 270,
            _ when string.Equals(category, "Position", StringComparison.OrdinalIgnoreCase) => 20,
            _ when string.Equals(category, "Protection", StringComparison.OrdinalIgnoreCase) => 120,
            _ when string.Equals(category, "Measurement", StringComparison.OrdinalIgnoreCase) => 240,
            _ => 300
        };
    }

    private static int AttributeNoisePenalty(string reference)
    {
        var lower = NormalizeRef(reference);
        if (lower.EndsWith(".q")) return 40;
        if (lower.EndsWith(".t") || lower.EndsWith(".tm")) return 50;
        if (lower.Contains(".ctlval") || lower.Contains(".origin") || lower.Contains(".ctlmodel")) return 60;
        if (lower.Contains(".mod.") || lower.EndsWith(".mod.stval") || lower.Contains(".beh.") || lower.Contains(".health") || lower.Contains(".eehealth")) return 90;
        return 0;
    }

    public static bool IsCoreScadaSignal(string reference, string logicalNodeClass, string dataType, string category)
    {
        var r = NormalizeRef(reference);
        var cls = logicalNodeClass.ToUpperInvariant();

        if (IsRawEngineeringAttribute(reference, dataType))
            return false;
        if (!IsRuntimeValueLeaf(r, dataType))
            return false;
        if (IsExcludedStatisticLogicalNode(reference))
            return false;

        // Primary equipment status that operators expect in HMI/SCADA.
        if ((cls is "CSWI" or "XCBR" or "XSWI") && r.EndsWith(".pos.stval"))
            return true;

        // Normal measurement groups use cVal. Siemens OperationalValues groups expose
        // the directly readable instantaneous leaf as instCVal, while cVal can reject
        // direct MMS reads even though the parent DO is visible in an engineering tool.
        if (cls is "MMXU" or "MMXN")
            return IsDefaultScadaMeasurementMagnitude(r);

        // Protection HMI points: operate/trip/start general flags only.
        if (cls == "PTOC" && (r.EndsWith(".op.general") || r.EndsWith(".str.general"))) return true;
        if (cls == "PTRC" && r.EndsWith(".tr.general")) return true;
        if (cls == "RBRF" && (r.EndsWith(".opex.general") || r.EndsWith(".op.general"))) return true;
        if ((cls is "PDIF" or "PDIS" or "PIOC" or "PTOV" or "PTUV" or "PTEF" or "PDEF") && r.EndsWith(".op.general")) return true;

        if (cls is "ATCC" or "AVC" or "AVCO")
            return IsAvrOperationalSignal(r, dataType, category);

        if (cls == "GGIO")
            return IsGgioOperationalSignal(r, dataType, category);

        if (cls == "YPTR" && r.Contains(".tappos."))
            return dataType is "Int32" or "Integer" or "UInt32" or "Enum";

        return false;
    }

    private static bool IsGgioOperationalSignal(string normalizedReference, string dataType, string category)
    {
        var r = NormalizeRef(normalizedReference);

        // Gateway IEDs often expose DI points as GGIO.Ind15.stVal and analogs as GGIO.AnIn1.mag.f.
        // Keep those as real SCADA points, but never promote GGIO.Beh/Health/q/t as selectable signals.
        if (Regex.IsMatch(r, @"\.ind\d+\.stval$", RegexOptions.IgnoreCase))
            return dataType is "Boolean" or "Enum" or "Int32" or "Integer";

        if (Regex.IsMatch(r, @"\.anin\d+\.(?:mag\.)?f$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(r, @"\.anin\d+\.mag\.f$", RegexOptions.IgnoreCase))
            return dataType is "Float32" or "Float" or "Double";

        if (dataType is "Boolean" && r.EndsWith(".stval") && string.Equals(category, "Status", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsAvrOperationalSignal(string normalizedReference, string dataType, string category)
    {
        var r = NormalizeRef(normalizedReference);
        if (IsRawEngineeringAttribute(r, dataType) ||
            r.EndsWith(".ctlmodel") ||
            r.EndsWith(".persistent") ||
            r.EndsWith(".d") ||
            r.Contains(".oper.") ||
            r.EndsWith(".oper"))
        {
            return false;
        }

        if (string.Equals(category, "Measurement", StringComparison.OrdinalIgnoreCase) && dataType == "Float32")
            return IsKnownAvrMeasurement(r);

        if (dataType is "Boolean" or "Enum" or "Int32" or "Integer" or "UInt32")
        {
            return r.Contains(".loc.") ||
                   r.Contains(".tapchg.valwtr.posval") ||
                   r.EndsWith(".tapchg.stval") ||
                   r.Contains(".parop.") ||
                   r.Contains(".ltcblk") ||
                   r.Contains(".mastersel.") ||
                   r.Contains(".followsel.") ||
                   r.Contains(".circasel.") ||
                   r.Contains(".circapfsel.") ||
                   r.Contains(".funcmon.") ||
                   r.Contains(".auto.") ||
                   r.Contains(".ldc.") ||
                   r.Contains(".errpar.") ||
                   r.Contains(".opcntrs.");
        }

        return false;
    }

    private static bool IsKnownAvrMeasurement(string normalizedReference)
    {
        var r = NormalizeRef(normalizedReference);
        return Regex.IsMatch(
            r,
            @"\.(?:ctlv|loda|circa|phang|ctldv)\.(?:mag\.)?f$",
            RegexOptions.IgnoreCase);
    }

    private static bool IsRuntimeValueSignal(string reference, string functionalConstraint, string dataType, string category)
    {
        var fc = (functionalConstraint ?? string.Empty).Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(fc) && fc is not ("ST" or "MX"))
            return false;

        var r = NormalizeRef(reference);
        if (IsRawEngineeringAttribute(r, dataType))
            return false;

        return IsRuntimeValueLeaf(r, dataType) ||
               (string.Equals(category, "Position", StringComparison.OrdinalIgnoreCase) && r.EndsWith(".stval")) ||
               (string.Equals(category, "Protection", StringComparison.OrdinalIgnoreCase) && r.EndsWith(".general"));
    }

    private static bool IsRuntimeValueLeaf(string normalizedReference, string dataType)
    {
        var r = NormalizeRef(normalizedReference);
        if (string.Equals(dataType, "Quality", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dataType, "Timestamp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dataType, "Directory", StringComparison.OrdinalIgnoreCase))
            return false;

        return r.EndsWith(".stval") ||
               r.EndsWith(".general") ||
               r.EndsWith(".posval") ||
               r.EndsWith(".actval") ||
               r.EndsWith(".setval") ||
               r.EndsWith(".mag.f") ||
               r.EndsWith(".ang.f") ||
               r.EndsWith(".f") ||
               r.EndsWith(".i");
    }

    public static bool IsKnownReadFailureState(string value, string quality, string probeStatus)
    {
        var status = (probeStatus ?? string.Empty).Trim();
        if (status.Equals("Readable", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("Not probed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("Reading...", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (status.Equals("Not readable", StringComparison.OrdinalIgnoreCase) ||
            status.EndsWith("Exception", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("TimeoutException", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("OperationCanceledException", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var v = (value ?? string.Empty).Trim();
        var q = (quality ?? string.Empty).Trim();
        return q.Equals("Bad", StringComparison.OrdinalIgnoreCase) &&
               (string.IsNullOrWhiteSpace(v) || v == "-" || v.Equals("Read failed", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRawEngineeringAttribute(string reference, string dataType)
    {
        if (string.Equals(dataType, "Quality", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(dataType, "Timestamp", StringComparison.OrdinalIgnoreCase)) return true;

        var r = NormalizeRef(reference);
        return IsStatisticsOrHarmonicNoise(r) ||
               r.EndsWith(".q") ||
               r.EndsWith(".t") ||
               r.EndsWith(".tm") ||
               r.Contains(".rp.") ||
               r.Contains(".br.") ||
               r.Contains(".ctlmodel") ||
               r.Contains(".ctlval") ||
               r.Contains(".origin") ||
               r.Contains(".db") ||
               r.EndsWith(".d") ||
               r.Contains(".du") ||
               r.Contains(".configrev") ||
               r.Contains(".numpts") ||
               r.Contains(".olddata") ||
               r.Contains(".mod.") ||
               r.EndsWith(".mod.stval") ||
               r.Contains(".beh.") ||
               r.EndsWith(".beh.stval") ||
               r.Contains(".health.") ||
               r.EndsWith(".health.stval") ||
               r.Contains(".eehealth.") ||
               r.EndsWith(".eehealth.stval") ||
               r.Contains(".namplt.") ||
               r.Contains(".vendor") ||
               r.Contains(".swrev") ||
               r.Contains(".configrev");
    }


    public static bool IsDefaultScadaMeasurementMagnitude(string normalizedReference)
    {
        var r = NormalizeRef(normalizedReference);
        if (IsStatisticsOrHarmonicNoise(r)) return false;
        if (!r.EndsWith(".mag.f")) return false;

        var operationalValues = r.Contains("operationalvalues") || r.Contains("operational_values");
        if (operationalValues)
        {
            if (!r.Contains(".instcval.mag.f")) return false;
        }
        else
        {
            if (!r.Contains(".cval.mag.f") || r.Contains(".instcval.")) return false;
        }

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
