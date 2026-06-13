namespace Ari61850Bridge.Models;

public sealed class ReportDataSetMemberView
{
    public string DataSetReference { get; set; } = string.Empty;
    public string ObjectReference { get; set; } = string.Empty;
    public string FunctionalConstraint { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string Coverage { get; set; } = "Unknown";
    public string Source { get; set; } = string.Empty;

    public string DisplayObject => string.IsNullOrWhiteSpace(FunctionalConstraint)
        ? ObjectReference
        : $"{ObjectReference} [{FunctionalConstraint}]";
}
