using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ari61850Bridge.Models;

namespace Ari61850Bridge.Services;

public sealed class NativeMmsDiscoverySnapshot
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DomainVariables { get; init; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DomainVariableLists { get; init; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

public static class NativeMmsDiscoveryMapper
{
    private sealed record FallbackPoint(string DataObject, string FunctionalConstraint, string LeafPath, string DataType, string Category, string Unit = "");

    private static readonly string[] FunctionalConstraints =
    {
        "ST", "MX", "CO", "CF", "DC", "SP", "SG", "SE", "EX", "OR", "BL", "RP", "BR", "LG", "GO", "MS", "US", "SV"
    };

    private static readonly FallbackPoint[] CommonLogicalNodeFallbacks =
    {
        new("Mod", "ST", "stVal", "Enum", "Status"),
        new("Beh", "ST", "stVal", "Enum", "Status"),
        new("Health", "ST", "stVal", "Enum", "Status")
    };

    private static readonly FallbackPoint[] AtccAvrFallbacks =
    {
        new("Loc", "ST", "stVal", "Boolean", "Status"),
        new("TapChg", "ST", "ValWTr.posVal", "Int32", "Status"),
        new("ParOp", "ST", "stVal", "Boolean", "Status"),
        new("LTCBlk", "ST", "stVal", "Boolean", "Status"),
        new("MasterSel", "ST", "stVal", "Boolean", "Status"),
        new("FollowSel", "ST", "stVal", "Boolean", "Status"),
        new("CircASel", "ST", "stVal", "Boolean", "Status"),
        new("CircAPFSel", "ST", "stVal", "Boolean", "Status"),
        new("FuncMon", "ST", "stVal", "Boolean", "Status"),
        new("Auto", "ST", "stVal", "Boolean", "Status"),
        new("LTCBlkVLo", "ST", "stVal", "Boolean", "Status"),
        new("LTCBlkVHi", "ST", "stVal", "Boolean", "Status"),
        new("LTCBlkAHi", "ST", "stVal", "Boolean", "Status"),
        new("LDC", "ST", "stVal", "Boolean", "Status"),
        new("ErrPar", "ST", "stVal", "Boolean", "Status"),
        new("OpCntRs", "ST", "stVal", "Int32", "Status"),
        new("CtlV", "MX", "mag.f", "Float32", "Measurement", "V"),
        new("LodA", "MX", "mag.f", "Float32", "Measurement", "A"),
        new("CircA", "MX", "mag.f", "Float32", "Measurement", "A"),
        new("RefPF", "MX", "mag.f", "Float32", "Measurement"),
        new("PhAng", "MX", "mag.f", "Float32", "Measurement", "deg"),
        new("BndCtr", "MX", "mag.f", "Float32", "Measurement", "V"),
        new("BndCtrV", "MX", "mag.f", "Float32", "Measurement", "V"),
        new("BndWid", "MX", "mag.f", "Float32", "Measurement"),
        new("CtlDITms", "MX", "mag.f", "Float32", "Measurement", "s"),
        new("LDCR", "MX", "mag.f", "Float32", "Measurement"),
        new("LDCX", "MX", "mag.f", "Float32", "Measurement"),
        new("BlkLV", "MX", "mag.f", "Float32", "Measurement"),
        new("LimLodA", "MX", "mag.f", "Float32", "Measurement", "A"),
        new("CtlDv", "MX", "mag.f", "Float32", "Measurement", "V"),
        new("LDCZ", "MX", "mag.f", "Float32", "Measurement")
    };

    private static readonly FallbackPoint[] GgioFallbacks =
    {
        new("Ind1", "ST", "stVal", "Boolean", "Status"),
        new("Ind2", "ST", "stVal", "Boolean", "Status"),
        new("Ind3", "ST", "stVal", "Boolean", "Status"),
        new("Ind4", "ST", "stVal", "Boolean", "Status"),
        new("AnIn1", "MX", "mag.f", "Float32", "Measurement"),
        new("AnIn2", "MX", "mag.f", "Float32", "Measurement"),
        new("AnIn3", "MX", "mag.f", "Float32", "Measurement"),
        new("AnIn4", "MX", "mag.f", "Float32", "Measurement")
    };

    public static IReadOnlyList<SignalDefinition> BuildSignals(NativeMmsDiscoverySnapshot snapshot)
    {
        var now = DateTime.Now;
        var signals = new List<SignalDefinition>();

        foreach (var domainPair in snapshot.DomainVariables.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var domain = domainPair.Key?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(domain)) continue;

            foreach (var item in domainPair.Value.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                AddCandidates(signals, domain, item, now);
        }

        if (signals.Count == 0)
        {
            foreach (var domain in snapshot.DomainVariables.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                AddDomainFallbackSignals(signals, domain, now);
        }

        var result = signals
            .Where(ShouldKeepCandidate)
            .GroupBy(s => s.ObjectReference, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.IsScadaCoreSignal).ThenByDescending(x => ConfidenceScore(x.Confidence)).First())
            .OrderBy(s => s.SortPriority)
            .ThenByDescending(s => ConfidenceScore(s.Confidence))
            .ThenBy(s => s.LogicalNode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.ObjectReference, StringComparer.OrdinalIgnoreCase)
            .Take(12000)
            .ToList();

        if (result.Count == 0)
        {
            foreach (var domain in snapshot.DomainVariables.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(new SignalDefinition
                {
                    Name = $"Logical Device {domain}",
                    ObjectReference = domain,
                    FunctionalConstraint = "-",
                    DataType = "Directory",
                    Category = "IED",
                    Confidence = "Low",
                    IsSelected = false,
                    IsReportCapable = false,
                    Source = "Native MMS GetNameList",
                    Value = "Online directory",
                    Quality = "Unknown",
                    Timestamp = now
                });
            }
        }

        return result;
    }

    private static void AddCandidates(List<SignalDefinition> signals, string domain, string rawItem, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(rawItem)) return;
        var item = rawItem.Trim();
        var parts = item.Split('$', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            AddLogicalNodeFallbackSignals(signals, domain, parts[0], now, "Native MMS shallow LN fallback");
            return;
        }

        var logicalNode = parts[0];
        var fcIndex = Array.FindIndex(parts, p => IsFunctionalConstraint(p));
        if (fcIndex < 1)
        {
            AddShallowDataObjectCandidates(signals, domain, logicalNode, parts.Skip(1).ToArray(), now);
            return;
        }

        var fc = parts[fcIndex].ToUpperInvariant();
        var pathParts = parts.Skip(fcIndex + 1).ToArray();
        if (pathParts.Length == 0)
        {
            AddLogicalNodeFallbackSignals(signals, domain, logicalNode, now, $"Native MMS {fc} shallow fallback");
            return;
        }

        foreach (var path in ExpandLikelyLeafPaths(logicalNode, fc, pathParts))
        {
            if (path.Length == 0) continue;
            var reference = $"{domain}/{logicalNode}.{string.Join('.', path)}";
            signals.Add(CreateSignal(reference, fc, now));
        }
    }

    private static IEnumerable<string[]> ExpandLikelyLeafPaths(string logicalNode, string fc, string[] pathParts)
    {
        var current = pathParts.Select(p => p.Trim()).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (current.Length == 0) yield break;

        if (LooksLikeReadableLeaf(current))
        {
            yield return current;
            yield break;
        }

        var lnClass = SignalDefinition.DetectLogicalNodeClass(logicalNode).ToUpperInvariant();
        var first = current[0];
        var last = current[^1];

        if (string.Equals(fc, "ST", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(first, "Pos", StringComparison.OrdinalIgnoreCase))
            {
                yield return Append(current, "stVal");
                yield break;
            }

            if (IsProtectionClass(lnClass) && (EqualsAny(first, "Op", "Str", "Tr") || EqualsAny(last, "Op", "Str", "Tr")))
            {
                yield return Append(current, "general");
                yield break;
            }

            if (!EqualsAny(first, "q", "t"))
            {
                yield return Append(current, "stVal");
                yield break;
            }
        }

        if (string.Equals(fc, "MX", StringComparison.OrdinalIgnoreCase))
        {
            if (EqualsAny(first, "A", "PhV", "PPV"))
            {
                foreach (var expanded in ExpandMeasurementMagnitude(current, first))
                    yield return expanded;
                yield break;
            }

            if (current.Any(p => string.Equals(p, "mag", StringComparison.OrdinalIgnoreCase)))
            {
                yield return Append(current, "f");
                yield break;
            }
        }

        // Keep conservative raw leaf for searchable diagnostics. The later read path will mark it Bad if it is structural only.
        yield return current;
    }

    private static void AddShallowDataObjectCandidates(List<SignalDefinition> signals, string domain, string logicalNode, string[] pathParts, DateTime now)
    {
        if (pathParts.Length == 0)
        {
            AddLogicalNodeFallbackSignals(signals, domain, logicalNode, now, "Native MMS shallow DO fallback");
            return;
        }

        var first = pathParts[0];
        foreach (var point in InferFallbackPoints(logicalNode, first))
        {
            var path = string.IsNullOrWhiteSpace(point.LeafPath)
                ? first
                : $"{first}.{point.LeafPath}";
            signals.Add(CreateSignal($"{domain}/{logicalNode}.{path}", point.FunctionalConstraint, now, "Native MMS shallow object fallback", point.DataType, point.Category, point.Unit));
        }
    }

    private static void AddDomainFallbackSignals(List<SignalDefinition> signals, string domain, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return;

        if (!LooksLikeAvrDomain(domain))
        {
            signals.Add(new SignalDefinition
            {
                Name = $"Logical Device {domain}",
                ObjectReference = domain,
                FunctionalConstraint = "-",
                DataType = "Directory",
                Category = "IED",
                Confidence = "Low",
                IsSelected = false,
                IsReportCapable = false,
                Source = "Native MMS GetNameList",
                Value = "Online directory",
                Quality = "Unknown",
                Timestamp = now
            });
            return;
        }

        // Some AVR IEDs expose only the logical-device shell through GetNameList(NamedVariable)
        // while commercial browsers still show the LN/DO tree by reading common AVR objects.
        // Keep this as low-risk discovery scaffolding: every point is still probed/polled normally.
        foreach (var ln in new[] { "LLN0", "LPHD1", "ATCC1", "AVC01", "GGIO1", "GGIO2", "MMXU1", "YPTR1" })
            AddLogicalNodeFallbackSignals(signals, domain, ln, now, "Native MMS AVR profile fallback");
    }

    private static void AddLogicalNodeFallbackSignals(List<SignalDefinition> signals, string domain, string logicalNode, DateTime now, string source)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(logicalNode))
            return;

        foreach (var point in InferLogicalNodeFallbackPoints(logicalNode))
        {
            var reference = $"{domain}/{logicalNode}.{point.DataObject}.{point.LeafPath}";
            signals.Add(CreateSignal(reference, point.FunctionalConstraint, now, source, point.DataType, point.Category, point.Unit));
        }
    }

    private static IEnumerable<FallbackPoint> InferLogicalNodeFallbackPoints(string logicalNode)
    {
        var lnClass = SignalDefinition.DetectLogicalNodeClass(logicalNode).ToUpperInvariant();

        foreach (var point in CommonLogicalNodeFallbacks)
            yield return point;

        if (lnClass is "ATCC" or "AVC" or "AVCO")
        {
            foreach (var point in AtccAvrFallbacks)
                yield return point;
            yield break;
        }

        if (lnClass == "GGIO")
        {
            foreach (var point in GgioFallbacks)
                yield return point;
            yield break;
        }

        if (lnClass == "MMXU")
        {
            foreach (var point in new[]
            {
                new FallbackPoint("PhV", "MX", "phsA.cVal.mag.f", "Float32", "Measurement", "V"),
                new FallbackPoint("PhV", "MX", "phsB.cVal.mag.f", "Float32", "Measurement", "V"),
                new FallbackPoint("PhV", "MX", "phsC.cVal.mag.f", "Float32", "Measurement", "V"),
                new FallbackPoint("A", "MX", "phsA.cVal.mag.f", "Float32", "Measurement", "A"),
                new FallbackPoint("A", "MX", "phsB.cVal.mag.f", "Float32", "Measurement", "A"),
                new FallbackPoint("A", "MX", "phsC.cVal.mag.f", "Float32", "Measurement", "A"),
                new FallbackPoint("PPV", "MX", "phsAB.cVal.mag.f", "Float32", "Measurement", "V"),
                new FallbackPoint("PPV", "MX", "phsBC.cVal.mag.f", "Float32", "Measurement", "V"),
                new FallbackPoint("PPV", "MX", "phsCA.cVal.mag.f", "Float32", "Measurement", "V")
            })
                yield return point;
            yield break;
        }

        if (lnClass == "YPTR")
        {
            yield return new FallbackPoint("TapPos", "ST", "stVal", "Int32", "Status");
        }
    }

    private static IEnumerable<FallbackPoint> InferFallbackPoints(string logicalNode, string dataObjectName)
    {
        var name = dataObjectName.Trim();
        var lnClass = SignalDefinition.DetectLogicalNodeClass(logicalNode).ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(name))
            yield break;

        var knownAtcc = AtccAvrFallbacks
            .Concat(CommonLogicalNodeFallbacks)
            .FirstOrDefault(p => p.DataObject.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (knownAtcc != null)
        {
            yield return knownAtcc with { DataObject = name };
            yield break;
        }

        if (lnClass == "MMXU" && EqualsAny(name, "A", "PhV", "PPV"))
        {
            var unit = name.Equals("A", StringComparison.OrdinalIgnoreCase) ? "A" : "V";
            foreach (var path in ExpandMeasurementMagnitude(new[] { name }, name))
                yield return new FallbackPoint(name, "MX", string.Join('.', path.Skip(1)), "Float32", "Measurement", unit);
            yield break;
        }

        if (LooksLikeAnalogDataObject(name))
        {
            yield return new FallbackPoint(name, "MX", "mag.f", "Float32", "Measurement", InferUnitFromDataObject(name));
            yield break;
        }

        if (LooksLikeIntegerStatusDataObject(name))
        {
            yield return new FallbackPoint(name, "ST", "stVal", "Int32", "Status");
            yield break;
        }

        var type = LooksLikeModeStatusDataObject(name) ? "Enum" : "Boolean";
        yield return new FallbackPoint(name, "ST", "stVal", type, "Status");
    }

    private static bool LooksLikeAvrDomain(string domain)
        => domain.Contains("AVR", StringComparison.OrdinalIgnoreCase) ||
           domain.Contains("ATCC", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeAnalogDataObject(string name)
    {
        return name.Contains("V", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Amp", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("LodA", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("CircA", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("PhAng", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("PF", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Bnd", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Lim", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("LDC", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("CtlD", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeIntegerStatusDataObject(string name)
        => name.Contains("Cnt", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("TapChg", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("TapPos", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeModeStatusDataObject(string name)
        => EqualsAny(name, "Mod", "Beh", "Health");

    private static string InferUnitFromDataObject(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains('v')) return "V";
        if (lower.Contains("loda") || lower.Contains("circa") || lower.Contains("limloda")) return "A";
        if (lower.Contains("phang")) return "deg";
        if (lower.Contains("tms")) return "s";
        return string.Empty;
    }

    private static IEnumerable<string[]> ExpandMeasurementMagnitude(string[] current, string first)
    {
        if (current.Length >= 4 && current.Any(p => string.Equals(p, "cVal", StringComparison.OrdinalIgnoreCase)) && current.Any(p => string.Equals(p, "mag", StringComparison.OrdinalIgnoreCase)))
        {
            yield return current.Last().Equals("f", StringComparison.OrdinalIgnoreCase) ? current : Append(current, "f");
            yield break;
        }

        if (string.Equals(first, "A", StringComparison.OrdinalIgnoreCase) || string.Equals(first, "PhV", StringComparison.OrdinalIgnoreCase))
        {
            yield return new[] { first, "phsA", "cVal", "mag", "f" };
            yield return new[] { first, "phsB", "cVal", "mag", "f" };
            yield return new[] { first, "phsC", "cVal", "mag", "f" };
            yield break;
        }

        if (string.Equals(first, "PPV", StringComparison.OrdinalIgnoreCase))
        {
            yield return new[] { first, "phsAB", "cVal", "mag", "f" };
            yield return new[] { first, "phsBC", "cVal", "mag", "f" };
            yield return new[] { first, "phsCA", "cVal", "mag", "f" };
            yield break;
        }

        yield return Append(current, "cVal", "mag", "f");
    }

    private static SignalDefinition CreateSignal(string reference, string fc, DateTime now)
        => CreateSignal(reference, fc, now, "Native MMS GetNameList", null, null, null);

    private static SignalDefinition CreateSignal(string reference, string fc, DateTime now, string source, string? dataTypeOverride, string? categoryOverride, string? unitOverride)
    {
        var ln = ExtractLogicalNode(reference);
        var category = string.IsNullOrWhiteSpace(categoryOverride) ? InferCategory(reference, ln) : categoryOverride;
        var dataType = string.IsNullOrWhiteSpace(dataTypeOverride) ? InferDataType(reference, fc) : dataTypeOverride;
        var unit = string.IsNullOrWhiteSpace(unitOverride) ? InferUnit(reference) : unitOverride;
        var isCore = SignalDefinition.IsCoreScadaSignal(reference, SignalDefinition.DetectLogicalNodeClass(ln), dataType, category);
        var confidence = InferConfidence(reference, dataType, category, isCore);

        return new SignalDefinition
        {
            Name = MakeFriendlyName(reference, category),
            ObjectReference = reference,
            FunctionalConstraint = fc,
            DataType = dataType,
            Category = category,
            Unit = unit,
            Confidence = confidence,
            IsSelected = isCore,
            IsReportCapable = isCore && (fc is "ST" or "MX"),
            Source = source,
            Value = "Pending read",
            Quality = "Pending",
            Timestamp = now
        };
    }

    private static bool ShouldKeepCandidate(SignalDefinition signal)
    {
        if (signal.DataType == "Directory") return true;
        if (signal.IsScadaCoreSignal) return true;

        var normalized = Normalize(signal.ObjectReference);
        if (SignalDefinition.IsStatisticsOrHarmonicNoise(normalized)) return false;
        if (normalized.EndsWith(".q") || normalized.EndsWith(".t")) return false;
        if (normalized.Contains(".origin") || normalized.Contains(".ctlmodel") || normalized.Contains(".ctlval")) return false;
        if (normalized.Contains(".numpts") || normalized.Contains(".olddata") || normalized.Contains(".configrev")) return false;
        if (normalized.Contains(".mod.") || normalized.Contains(".beh.")) return false;

        return (signal.FunctionalConstraint is "ST" or "MX") &&
               (signal.DataType is "Boolean" or "Enum" or "Float32" or "Int32" or "UInt16" or "Dbpos") &&
               IsKnownScalarSignalReference(normalized, signal.DataType);
    }

    private static bool IsKnownScalarSignalReference(string normalizedReference, string dataType)
    {
        if (normalizedReference.EndsWith(".stval") ||
            normalizedReference.EndsWith(".general") ||
            normalizedReference.EndsWith(".posval") ||
            normalizedReference.EndsWith(".actval") ||
            normalizedReference.EndsWith(".setval") ||
            normalizedReference.EndsWith(".ctlval") ||
            normalizedReference.EndsWith(".ctlmodel") ||
            normalizedReference.EndsWith(".f") ||
            normalizedReference.EndsWith(".i"))
        {
            return true;
        }

        if (string.Equals(dataType, "Float32", StringComparison.OrdinalIgnoreCase))
            return normalizedReference.Contains(".mag.") || normalizedReference.Contains(".ang.");

        return false;
    }

    private static bool LooksLikeReadableLeaf(string[] parts)
    {
        var last = parts[^1];
        if (EqualsAny(last, "stVal", "posVal", "q", "t", "general", "f", "i", "ctlVal", "mag", "ang", "setVal", "actVal")) return true;
        return parts.Length >= 4 && parts.Any(p => string.Equals(p, "mag", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFunctionalConstraint(string text) => FunctionalConstraints.Contains(text, StringComparer.OrdinalIgnoreCase);

    private static string[] Append(string[] input, params string[] suffix)
    {
        var output = new string[input.Length + suffix.Length];
        Array.Copy(input, output, input.Length);
        Array.Copy(suffix, 0, output, input.Length, suffix.Length);
        return output;
    }

    private static bool EqualsAny(string text, params string[] candidates)
        => candidates.Any(c => string.Equals(text, c, StringComparison.OrdinalIgnoreCase));

    private static bool IsProtectionClass(string lnClass)
        => lnClass is "PTOC" or "PTRC" or "PDIF" or "PDIS" or "PIOC" or "PTOV" or "PTUV" or "PTEF" or "PDEF" or "RREC" or "RBRF";

    private static string ExtractLogicalNode(string reference)
    {
        var slash = reference.IndexOf('/');
        if (slash < 0 || slash >= reference.Length - 1) return string.Empty;
        var after = reference[(slash + 1)..];
        var dot = after.IndexOf('.');
        return dot > 0 ? after[..dot] : after;
    }

    private static string InferCategory(string reference, string ln)
    {
        var r = Normalize(reference);
        var cls = SignalDefinition.DetectLogicalNodeClass(ln).ToUpperInvariant();
        if (r.EndsWith(".pos.stval")) return "Position";
        if (r.EndsWith(".mag.f") || r.Contains(".cval.mag.f")) return "Measurement";
        if (cls is "ATCC" or "AVC" or "AVCO" or "GGIO" or "YPTR") return "Status";
        if (IsProtectionClass(cls) || r.EndsWith(".op.general") || r.EndsWith(".str.general") || r.EndsWith(".tr.general")) return "Protection";
        if (r.EndsWith(".q")) return "Quality";
        if (r.EndsWith(".t")) return "Timestamp";
        return "Status";
    }

    private static string InferDataType(string reference, string fc)
    {
        var r = Normalize(reference);
        if (r.EndsWith(".pos.stval")) return "Dbpos";
        if (r.EndsWith(".posval")) return "Int32";
        if (r.EndsWith(".q")) return "Quality";
        if (r.EndsWith(".t")) return "Timestamp";
        if (r.EndsWith(".mag.f") || r.EndsWith(".ang.f")) return "Float32";
        if (r.EndsWith(".general")) return "Boolean";
        if (r.Contains("cnt") || r.Contains("tapchg") || r.Contains("tappos")) return "Int32";
        if (r.EndsWith(".stval")) return fc.Equals("ST", StringComparison.OrdinalIgnoreCase) ? "Enum" : "Int32";
        return fc.Equals("MX", StringComparison.OrdinalIgnoreCase) ? "Float32" : "Enum";
    }

    private static string InferUnit(string reference)
    {
        var r = Normalize(reference);
        if (r.Contains(".a.")) return "A";
        if (r.Contains("loda") || r.Contains("circa") || r.Contains("limloda")) return "A";
        if (r.Contains(".phv.") || r.Contains(".ppv.")) return "V";
        if (r.Contains("ctlv") || r.Contains("bndctrv") || r.Contains("ctldv")) return "V";
        if (r.Contains("phang")) return "deg";
        if (r.Contains("tms")) return "s";
        if (r.Contains(".hz")) return "Hz";
        return string.Empty;
    }

    private static string InferConfidence(string reference, string dataType, string category, bool isCore)
    {
        if (isCore) return "High";
        if ((category is "Status" or "Protection") && (dataType is "Boolean" or "Enum")) return "Medium";
        if (category == "Measurement" && dataType == "Float32") return "Medium";
        return "Low";
    }

    private static string MakeFriendlyName(string reference, string category)
    {
        var ln = ExtractLogicalNode(reference);
        var afterSlash = reference.Contains('/') ? reference[(reference.IndexOf('/') + 1)..] : reference;
        var dot = afterSlash.IndexOf('.');
        var path = dot >= 0 ? afterSlash[(dot + 1)..] : afterSlash;
        path = Regex.Replace(path, @"\.", " ");
        return string.IsNullOrWhiteSpace(ln) ? $"{category} {path}" : $"{ln} {path}";
    }

    private static int ConfidenceScore(string confidence) => confidence switch
    {
        "High" => 3,
        "Medium" => 2,
        "Low" => 1,
        _ => 0
    };

    private static string Normalize(string reference)
        => (reference ?? string.Empty).Replace('$', '.').Replace("..", ".").ToLowerInvariant();
}
