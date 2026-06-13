using System;
using System.Collections.Generic;
using System.Linq;
using Ari61850Bridge.Models;

namespace Ari61850Bridge.Services;

public sealed class ReportRuntimePlanner
{
    private readonly IReadOnlyDictionary<string, RelayEndpointView> _relays;

    public ReportRuntimePlanner(IReadOnlyDictionary<string, RelayEndpointView> relays)
    {
        _relays = relays;
    }

    public IReadOnlyList<ReportControlPlan> BuildPlans(IEnumerable<BindingItem> bindings)
    {
        var candidates = bindings
            .Where(IsReportPreferredBinding)
            .Where(b => !string.IsNullOrWhiteSpace(b.ReportControlReference) || !string.IsNullOrWhiteSpace(b.DataSetReference))
            .ToList();

        var plans = candidates
            .GroupBy(BuildGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(CreatePlan)
            .OrderByDescending(p => p.Buffered)
            .ThenByDescending(p => p.FastStatusCount)
            .ThenBy(p => p.DisplayReference, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return plans;
    }

    public static bool IsReportPreferredBinding(BindingItem binding)
    {
        var mode = $"{binding.ReadMode} {binding.RcbMode}";
        if (mode.Contains("polling only", StringComparison.OrdinalIgnoreCase)) return false;
        if (mode.Contains("mms polling only", StringComparison.OrdinalIgnoreCase)) return false;

        return !string.IsNullOrWhiteSpace(binding.ReportControlReference) ||
               !string.IsNullOrWhiteSpace(binding.DataSetReference) ||
               mode.Contains("report", StringComparison.OrdinalIgnoreCase) ||
               mode.Contains("rcb", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildGroupKey(BindingItem binding)
    {
        var relayId = string.IsNullOrWhiteSpace(binding.RelayId) ? "__single__" : binding.RelayId.Trim();
        var rcb = string.IsNullOrWhiteSpace(binding.ReportControlReference) ? "__no_rcb__" : binding.ReportControlReference.Trim();
        var ds = string.IsNullOrWhiteSpace(binding.DataSetReference) ? "__no_dataset__" : binding.DataSetReference.Trim();
        return $"{relayId}|{rcb}|{ds}";
    }

    private ReportControlPlan CreatePlan(IGrouping<string, BindingItem> group)
    {
        var first = group.First();
        _relays.TryGetValue(string.IsNullOrWhiteSpace(first.RelayId) ? "__single__" : first.RelayId, out var relay);

        var bindings = group
            .OrderByDescending(BridgeRuntime.IsFastAcquisitionCandidate)
            .ThenBy(b => b.ModbusAddress)
            .ThenBy(b => b.IecReference, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mode = ResolvePlanMode(first, relay);
        var plan = new ReportControlPlan
        {
            RelayId = string.IsNullOrWhiteSpace(first.RelayId) ? "__single__" : first.RelayId,
            RelayName = relay?.DisplayName ?? string.Empty,
            RelayIpAddress = first.RelayIpAddress ?? relay?.IpAddress ?? string.Empty,
            IedName = string.IsNullOrWhiteSpace(first.IedName) ? relay?.IedName ?? string.Empty : first.IedName,
            ReportControlReference = first.ReportControlReference ?? string.Empty,
            DataSetReference = first.DataSetReference ?? string.Empty,
            Mode = mode,
            Buffered = LooksBuffered(first.ReportControlReference, relay?.RcbName),
            Status = mode.Contains("dynamic", StringComparison.OrdinalIgnoreCase) ? "Dynamic candidate" : "Planned",
            Bindings = bindings
        };

        return plan;
    }

    private static string ResolvePlanMode(BindingItem binding, RelayEndpointView? relay)
    {
        if (!string.IsNullOrWhiteSpace(binding.RcbMode) && !binding.RcbMode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return binding.RcbMode;
        if (!string.IsNullOrWhiteSpace(binding.ReadMode) && !binding.ReadMode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return binding.ReadMode;
        if (!string.IsNullOrWhiteSpace(relay?.ReportRuntimeMode))
            return relay.ReportRuntimeMode;
        return "Report preferred + polling fallback";
    }

    private static bool LooksBuffered(string? reportControlReference, string? rcbName)
    {
        var text = $"{reportControlReference} {rcbName}".ToLowerInvariant();
        return text.Contains("brcb") || text.Contains("br.") || text.Contains("buffer");
    }
}
