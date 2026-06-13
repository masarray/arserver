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
    private static readonly string[] FunctionalConstraints =
    {
        "ST", "MX", "CO", "CF", "DC", "SP", "SG", "SE", "EX", "OR", "BL", "RP", "BR", "LG", "GO", "MS", "US", "SV"
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
        if (parts.Length < 2) return;

        var logicalNode = parts[0];
        var fcIndex = Array.FindIndex(parts, p => IsFunctionalConstraint(p));
        if (fcIndex < 1) return;

        var fc = parts[fcIndex].ToUpperInvariant();
        var pathParts = parts.Skip(fcIndex + 1).ToArray();
        if (pathParts.Length == 0) return;

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
    {
        var ln = ExtractLogicalNode(reference);
        var category = InferCategory(reference, ln);
        var dataType = InferDataType(reference, fc);
        var unit = InferUnit(reference);
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
            Source = "Native MMS GetNameList",
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

        return (signal.FunctionalConstraint is "ST" or "MX") && (signal.DataType is "Boolean" or "Enum" or "Float32" or "Int32" or "UInt16" or "Dbpos");
    }

    private static bool LooksLikeReadableLeaf(string[] parts)
    {
        var last = parts[^1];
        if (EqualsAny(last, "stVal", "q", "t", "general", "f", "i", "ctlVal", "mag", "ang", "setVal", "actVal")) return true;
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
        if (IsProtectionClass(cls) || r.EndsWith(".op.general") || r.EndsWith(".str.general") || r.EndsWith(".tr.general")) return "Protection";
        if (r.EndsWith(".q")) return "Quality";
        if (r.EndsWith(".t")) return "Timestamp";
        return "Status";
    }

    private static string InferDataType(string reference, string fc)
    {
        var r = Normalize(reference);
        if (r.EndsWith(".pos.stval")) return "Dbpos";
        if (r.EndsWith(".q")) return "Quality";
        if (r.EndsWith(".t")) return "Timestamp";
        if (r.EndsWith(".mag.f") || r.EndsWith(".ang.f")) return "Float32";
        if (r.EndsWith(".general")) return "Boolean";
        if (r.EndsWith(".stval")) return fc.Equals("ST", StringComparison.OrdinalIgnoreCase) ? "Enum" : "Int32";
        return fc.Equals("MX", StringComparison.OrdinalIgnoreCase) ? "Float32" : "Enum";
    }

    private static string InferUnit(string reference)
    {
        var r = Normalize(reference);
        if (r.Contains(".a.")) return "A";
        if (r.Contains(".phv.") || r.Contains(".ppv.")) return "V";
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
