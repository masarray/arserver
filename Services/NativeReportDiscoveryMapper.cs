using System;
using System.Collections.Generic;
using System.Linq;
using Ari61850Bridge.Models;

namespace Ari61850Bridge.Services;

public static class NativeReportDiscoveryMapper
{
    private static readonly string[] ReportAttributeNames =
    {
        "RptID", "RptEna", "Resv", "ResvTms", "DatSet", "ConfRev", "OptFlds", "BufTm", "SqNum", "TrgOps", "IntgPd", "GI", "PurgeBuf", "EntryID", "TimeOfEntry"
    };

    public static NativeReportInventory BuildInventory(NativeMmsDiscoverySnapshot snapshot)
    {
        var inventory = new NativeReportInventory();
        inventory.DataSets.AddRange(BuildDataSets(snapshot.DomainVariableLists));
        inventory.ReportControls.AddRange(BuildReportControls(snapshot.DomainVariables, inventory.DataSets));
        return inventory;
    }

    public static void ApplyReportHints(IReadOnlyList<SignalDefinition> signals, NativeReportInventory inventory)
    {
        if (signals.Count == 0 || inventory.ReportControls.Count == 0)
            return;

        foreach (var signal in signals)
        {
            if (!IsReportCandidateSignal(signal))
                continue;

            var best = PickBestReportControl(signal, inventory);
            if (best == null)
                continue;

            signal.IsReportCapable = true;
            signal.ReportControlReference = best.Reference;
            signal.DataSetReference = best.DataSetReference;
            signal.Source = string.IsNullOrWhiteSpace(signal.Source)
                ? "Native MMS GetNameList + RCB inventory"
                : signal.Source.Contains("RCB", StringComparison.OrdinalIgnoreCase)
                    ? signal.Source
                    : signal.Source + " + RCB inventory";
        }
    }

    private static IEnumerable<NativeDataSetCandidate> BuildDataSets(IReadOnlyDictionary<string, IReadOnlyList<string>> domainVariableLists)
    {
        foreach (var domainPair in domainVariableLists.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var domain = domainPair.Key?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(domain)) continue;

            foreach (var raw in domainPair.Value.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var (ln, name, reference) = NormalizeDataSetReference(domain, raw);
                yield return new NativeDataSetCandidate
                {
                    Domain = domain,
                    LogicalNode = ln,
                    Name = name,
                    RawMmsName = raw,
                    Reference = reference
                };
            }
        }
    }

    private static IEnumerable<NativeReportControlCandidate> BuildReportControls(
        IReadOnlyDictionary<string, IReadOnlyList<string>> domainVariables,
        IReadOnlyList<NativeDataSetCandidate> dataSets)
    {
        var map = new Dictionary<string, NativeReportControlCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var domainPair in domainVariables.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var domain = domainPair.Key?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(domain)) continue;

            foreach (var raw in domainPair.Value.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!TryParseReportVariable(domain, raw, out var parsed))
                    continue;

                if (!map.TryGetValue(parsed.Reference, out var candidate))
                {
                    candidate = parsed;
                    candidate.DataSetReference = InferLikelyDataSet(candidate, dataSets);
                    map[candidate.Reference] = candidate;
                }

                foreach (var attr in parsed.Attributes)
                {
                    if (!candidate.Attributes.Contains(attr, StringComparer.OrdinalIgnoreCase))
                        candidate.Attributes.Add(attr);
                }
            }
        }

        return map.Values
            .OrderByDescending(x => x.Buffered)
            .ThenBy(x => x.Domain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.LogicalNode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryParseReportVariable(string domain, string raw, out NativeReportControlCandidate candidate)
    {
        candidate = new NativeReportControlCandidate();
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var parts = raw.Split('$', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3) return false;

        var fcIndex = Array.FindIndex(parts, p => p.Equals("RP", StringComparison.OrdinalIgnoreCase) || p.Equals("BR", StringComparison.OrdinalIgnoreCase));
        if (fcIndex < 1 || fcIndex + 1 >= parts.Length) return false;

        var ln = parts[0];
        var fc = parts[fcIndex].ToUpperInvariant();
        var name = parts[fcIndex + 1];
        if (string.IsNullOrWhiteSpace(ln) || string.IsNullOrWhiteSpace(name)) return false;

        var attrs = parts.Skip(fcIndex + 2)
            .Where(p => IsKnownReportAttribute(p) || !string.IsNullOrWhiteSpace(p))
            .ToList();

        candidate = new NativeReportControlCandidate
        {
            Domain = domain,
            LogicalNode = ln,
            FunctionalConstraint = fc,
            Name = name,
            Buffered = fc.Equals("BR", StringComparison.OrdinalIgnoreCase),
            Reference = $"{domain}/{ln}.{fc}.{name}",
            Attributes = attrs
        };
        return true;
    }

    private static bool IsKnownReportAttribute(string text)
        => ReportAttributeNames.Contains(text, StringComparer.OrdinalIgnoreCase);

    private static string InferLikelyDataSet(NativeReportControlCandidate rcb, IReadOnlyList<NativeDataSetCandidate> dataSets)
    {
        var sameDomain = dataSets
            .Where(ds => ds.Domain.Equals(rcb.Domain, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sameDomain.Count == 0) return string.Empty;

        var sameLn = sameDomain
            .Where(ds => ds.LogicalNode.Equals(rcb.LogicalNode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // IEC 61850 reports are commonly hosted in LLN0. If only one DataSet is visible in the
        // same domain/LN, it is a safe planner hint. If there are many, keep the RCB reference
        // without pretending that we know DatSet until the RCB attribute probe confirms it.
        if (sameLn.Count == 1) return sameLn[0].Reference;
        if (sameDomain.Count == 1) return sameDomain[0].Reference;

        var byName = sameDomain.FirstOrDefault(ds =>
            !string.IsNullOrWhiteSpace(ds.Name) &&
            (rcb.Name.Contains(ds.Name, StringComparison.OrdinalIgnoreCase) ||
             ds.Name.Contains(rcb.Name, StringComparison.OrdinalIgnoreCase)));
        return byName?.Reference ?? string.Empty;
    }

    private static NativeReportControlCandidate? PickBestReportControl(SignalDefinition signal, NativeReportInventory inventory)
    {
        var domain = ExtractDomain(signal.ObjectReference);
        if (string.IsNullOrWhiteSpace(domain)) return null;

        var candidates = inventory.ReportControls
            .Where(rcb => rcb.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0) return null;

        var fc = signal.FunctionalConstraint ?? string.Empty;
        var category = signal.Category ?? string.Empty;
        var reference = signal.ObjectReference.Replace('$', '.').ToLowerInvariant();

        return candidates
            .OrderByDescending(rcb => ShouldPreferBuffered(signal, category, reference) ? rcb.Buffered : !rcb.Buffered)
            .ThenByDescending(rcb => !string.IsNullOrWhiteSpace(rcb.DataSetReference))
            .ThenByDescending(rcb => rcb.LogicalNode.Equals("LLN0", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(rcb => HasUsefulRuntimeAttributes(rcb))
            .ThenBy(rcb => rcb.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool ShouldPreferBuffered(SignalDefinition signal, string category, string reference)
    {
        if (category.Equals("Position", StringComparison.OrdinalIgnoreCase)) return true;
        if (category.Equals("Protection", StringComparison.OrdinalIgnoreCase)) return true;
        if (reference.Contains("xcbr") || reference.Contains("xswi") || reference.Contains("cswi")) return true;
        if (reference.EndsWith(".pos.stval") || reference.EndsWith(".op.general") || reference.EndsWith(".str.general") || reference.EndsWith(".tr.general")) return true;
        return false;
    }

    private static bool HasUsefulRuntimeAttributes(NativeReportControlCandidate rcb)
        => rcb.Attributes.Contains("RptEna", StringComparer.OrdinalIgnoreCase) ||
           rcb.Attributes.Contains("DatSet", StringComparer.OrdinalIgnoreCase) ||
           rcb.Attributes.Contains("ConfRev", StringComparer.OrdinalIgnoreCase);

    private static bool IsReportCandidateSignal(SignalDefinition signal)
    {
        if (!signal.IsScadaCoreSignal) return false;
        var fc = signal.FunctionalConstraint ?? string.Empty;
        return fc.Equals("ST", StringComparison.OrdinalIgnoreCase) || fc.Equals("MX", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractDomain(string reference)
    {
        var slash = reference.IndexOf('/');
        return slash > 0 ? reference[..slash] : string.Empty;
    }

    private static (string LogicalNode, string Name, string Reference) NormalizeDataSetReference(string domain, string raw)
    {
        var cleaned = raw.Trim().Replace('$', '.');
        if (string.IsNullOrWhiteSpace(cleaned))
            return ("LLN0", raw, $"{domain}/LLN0.{raw}");

        var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
            return (parts[0], parts[^1], $"{domain}/{cleaned}");

        // Many IEDs expose domain-specific NamedVariableList as just the DataSet name.
        // In IEC 61850, operational DataSets used by reports are commonly hosted under LLN0.
        return ("LLN0", cleaned, $"{domain}/LLN0.{cleaned}");
    }
}
