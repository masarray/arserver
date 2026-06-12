namespace Ari61850Bridge.Models;

public sealed class ReportControlPlan
{
    public string PlanId { get; set; } = Guid.NewGuid().ToString("N");
    public string RelayId { get; set; } = string.Empty;
    public string RelayName { get; set; } = string.Empty;
    public string RelayIpAddress { get; set; } = string.Empty;
    public string IedName { get; set; } = string.Empty;
    public string ReportControlReference { get; set; } = string.Empty;
    public string DataSetReference { get; set; } = string.Empty;
    public string Mode { get; set; } = "Report preferred + polling fallback";
    public bool Buffered { get; set; }
    public string ReportId { get; set; } = string.Empty;
    public int IntegrityPeriodMs { get; set; }
    public string TriggerOptions { get; set; } = string.Empty;
    public string OptionalFields { get; set; } = string.Empty;
    public string Status { get; set; } = "Planned";
    public List<BindingItem> Bindings { get; set; } = new();

    public int BindingCount => Bindings.Count;
    public int FastStatusCount => Bindings.Count(IsFastStatusCandidate);
    public string DisplayReference => string.IsNullOrWhiteSpace(ReportControlReference) ? DataSetReference : ReportControlReference;
    public string Summary => $"{IedNameOrRelay()} • {BindingCount} tag(s) • {(Buffered ? "BRCB" : "URCB/RCB")} • {DisplayReference}";

    private string IedNameOrRelay()
    {
        if (!string.IsNullOrWhiteSpace(IedName)) return IedName;
        if (!string.IsNullOrWhiteSpace(RelayName)) return RelayName;
        return string.IsNullOrWhiteSpace(RelayIpAddress) ? "IED" : RelayIpAddress;
    }

    private static bool IsFastStatusCandidate(BindingItem binding)
    {
        var category = binding.Category ?? string.Empty;
        var dataType = binding.IecDataType ?? string.Empty;
        var modbusType = binding.ModbusDataType ?? string.Empty;
        var modbusArea = binding.ModbusArea ?? string.Empty;
        var reference = (binding.IecReference ?? string.Empty).Replace('$', '.').ToLowerInvariant();

        return category.Equals("Position", StringComparison.OrdinalIgnoreCase) ||
               category.Equals("Protection", StringComparison.OrdinalIgnoreCase) ||
               dataType.Equals("Boolean", StringComparison.OrdinalIgnoreCase) ||
               modbusType.Equals("Bool", StringComparison.OrdinalIgnoreCase) ||
               modbusArea.Equals("Coil", StringComparison.OrdinalIgnoreCase) ||
               modbusArea.Equals("DiscreteInput", StringComparison.OrdinalIgnoreCase) ||
               reference.Contains(".pos.stval") ||
               reference.Contains("xcbr") ||
               reference.Contains("xswi") ||
               reference.Contains("cswi");
    }
}
