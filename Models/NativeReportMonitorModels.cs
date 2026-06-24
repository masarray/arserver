namespace Ari61850Bridge.Models;

public sealed class NativeReportMonitorStartResult
{
    public bool IsSuccess { get; init; }
    public string PlanId { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string SubscriptionSummary { get; init; } = string.Empty;
    public int MemberCount { get; init; }
    public int WriteStepCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class NativeReportMonitorSliceResult
{
    public string PlanId { get; init; } = string.Empty;
    public int ReportCount { get; init; }
    public int PollReadCount { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<NativeReportValueUpdate> Updates { get; init; } = Array.Empty<NativeReportValueUpdate>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class NativeReportMonitorStopResult
{
    public bool IsSuccess { get; init; }
    public string PlanId { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class NativeReportValueUpdate
{
    public string Reference { get; init; } = string.Empty;
    public string FunctionalConstraint { get; init; } = string.Empty;
    public string Value { get; init; } = "-";
    public string Quality { get; init; } = string.Empty;
    public string Timestamp { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string Source { get; init; } = "report";
    public string ProjectionStatus { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
}
