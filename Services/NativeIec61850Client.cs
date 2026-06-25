using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AR.Iec61850.Binding;
using AR.Iec61850.Discovery;
using Ari61850Bridge.Models;
using ArMms = AR.Iec61850.Mms;

namespace Ari61850Bridge.Services;

/// <summary>
/// Native IEC 61850 MMS client backed by the ARIEC61850 engine.
/// </summary>
public sealed class NativeIec61850Client : IIec61850Client
{
    private readonly ArMms.MmsClientSession _session = new();
    private ArMms.MmsDiscoveryResult? _lastDiscovery;
    private LiveIedModelDiscoveryDocument? _liveModel;
    private readonly Dictionary<string, ArMms.MmsPersistentReportMonitorSession> _reportMonitorSessions = new(StringComparer.OrdinalIgnoreCase);
    private string _host = string.Empty;
    private int _port = 102;

    public bool IsConnected => _session.IsMmsInitiated;
    public bool IsTransportReady => _session.IsTransportConnected;
    public bool IsMmsReady => _session.IsMmsInitiated;
    public bool IsMmsInitiateFailed => _session.State == ArMms.MmsAssociationState.MmsInitiateFailed;
    public string NativeState => _session.State.ToString();
    public string ConnectionMode => "ARIEC61850 native MMS";
    public string LastErrorMessage { get; private set; } = string.Empty;
    public string LastAssociationResponseHex => _session.LastAssociationResponseHex;
    public string LastAssociationAttemptSummary => _session.LastAssociationAttemptSummary;
    public string LastReadRequestHex => _session.LastReadRequestHex;
    public string LastReadResponseHex => _session.LastReadResponseHex;
    public string LastReadAttemptSummary => _session.LastReadAttemptSummary;
    public string LastDiscoveryRequestHex => _session.LastDiscoveryRequestHex;
    public string LastDiscoveryResponseHex => _session.LastDiscoveryResponseHex;
    public string LastDiscoverySummary { get; private set; } = string.Empty;
    public NativeReportInventory LastReportInventory { get; private set; } = new();

    public async Task ConnectAsync(string ipAddress, int port, CancellationToken cancellationToken)
    {
        LastErrorMessage = string.Empty;
        _lastDiscovery = null;
        _liveModel = null;
        _reportMonitorSessions.Clear();
        _host = ipAddress?.Trim() ?? string.Empty;
        _port = port <= 0 ? 102 : port;

        try
        {
            await _session.ConnectAsync(
                _host,
                _port,
                TimeSpan.FromSeconds(8),
                cancellationToken).ConfigureAwait(false);

            LastErrorMessage = string.IsNullOrWhiteSpace(_session.LastAssociationAttemptSummary)
                ? _session.LastHandshakeMessage
                : _session.LastAssociationAttemptSummary;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastErrorMessage = $"ARIEC61850 TCP/TPKT/COTP/ACSE preflight failed for {ipAddress}:{port}. {ex.GetType().Name}: {ex.Message}. {_session.LastAssociationAttemptSummary}";
            await _session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<SignalDefinition>> DiscoverSignalsAsync(CancellationToken cancellationToken)
    {
        LastDiscoverySummary = string.Empty;
        cancellationToken.ThrowIfCancellationRequested();

        if (!_session.IsMmsInitiated)
        {
            LastErrorMessage = $"ARIEC61850 online discovery requires ACSE/MMS association. Current state: {_session.State}. {_session.LastAssociationAttemptSummary}";
            return Array.Empty<SignalDefinition>();
        }

        try
        {
            var discovery = await _session.DiscoverAsync(
                probeReportAttributes: true,
                maxReportAttributeProbes: 64,
                cancellationToken).ConfigureAwait(false);

            _lastDiscovery = discovery;
            _liveModel = LiveIedModelDiscoveryBuilder.Build(discovery, new LiveIedModelDiscoveryBuildOptions
            {
                Host = _host,
                Port = _port,
                IncludeLowConfidenceTemplates = true
            });

            var snapshot = ToNativeSnapshot(discovery.Snapshot);
            LastReportInventory = ToNativeInventory(discovery.ReportInventory);

            var signals = BuildSignalsFromArIecModel(_liveModel, snapshot);
            NativeReportDiscoveryMapper.ApplyReportHints(signals, LastReportInventory);

            LastDiscoverySummary = $"{discovery.Summary} {_liveModel.Summary} SCADA candidates={signals.Count}. Engine=ARIEC61850 live-model/schema/read-plan.";
            LastErrorMessage = LastDiscoverySummary;
            return signals;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastErrorMessage = $"ARIEC61850 online discovery failed: {ex.GetType().Name}: {ex.Message}. Last discovery: {_session.LastDiscoveryAttemptSummary}. Last request: {_session.LastDiscoveryRequestHex}";
            return Array.Empty<SignalDefinition>();
        }
    }

    public async Task ProbeReportControlAsync(NativeReportControlCandidate rcb, CancellationToken cancellationToken)
    {
        if (rcb == null) throw new ArgumentNullException(nameof(rcb));
        cancellationToken.ThrowIfCancellationRequested();

        if (!_session.IsMmsInitiated)
        {
            rcb.Status = $"Probe blocked: ACSE/MMS not associated ({_session.State})";
            LastErrorMessage = rcb.Status;
            return;
        }

        rcb.Status = "ARIEC61850 read-only attribute probe running";
        await TryReadReportAttributeAsync(rcb, "DatSet", value =>
        {
            var text = NormalizeReportAttributeText(value);
            if (!string.IsNullOrWhiteSpace(text))
                rcb.DataSetReference = NormalizeReportedDataSetReference(rcb.Domain, text);
        }, cancellationToken).ConfigureAwait(false);

        await TryReadReportAttributeAsync(rcb, "RptID", value => rcb.ReportId = NormalizeReportAttributeText(value), cancellationToken).ConfigureAwait(false);
        await TryReadReportAttributeAsync(rcb, "ConfRev", value => rcb.ConfRev = NormalizeReportAttributeText(value), cancellationToken).ConfigureAwait(false);
        await TryReadReportAttributeAsync(rcb, "IntgPd", value => rcb.IntegrityPeriodMs = NormalizeReportAttributeText(value), cancellationToken).ConfigureAwait(false);
        await TryReadReportAttributeAsync(rcb, "RptEna", value => rcb.EnabledState = NormalizeReportAttributeText(value), cancellationToken).ConfigureAwait(false);
        await TryReadReportAttributeAsync(rcb, "BufTm", _ => { }, cancellationToken).ConfigureAwait(false);
        await TryReadReportAttributeAsync(rcb, "TrgOps", _ => { }, cancellationToken).ConfigureAwait(false);
        await TryReadReportAttributeAsync(rcb, "OptFlds", _ => { }, cancellationToken).ConfigureAwait(false);
        await TryReadReportAttributeAsync(rcb, rcb.Buffered ? "ResvTms" : "Resv", _ => { }, cancellationToken).ConfigureAwait(false);

        rcb.Status = string.IsNullOrWhiteSpace(rcb.DataSetReference)
            ? "Probed: DataSet not returned"
            : "Probed read-only";
        LastErrorMessage = rcb.Status;
    }

    public async Task<NativeReportMonitorStartResult> StartReportMonitorAsync(ReportControlPlan plan, CancellationToken cancellationToken)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        cancellationToken.ThrowIfCancellationRequested();

        if (!_session.IsMmsInitiated)
        {
            LastErrorMessage = $"ARIEC61850 report monitor requires ACSE/MMS association. Current state: {_session.State}.";
            return new NativeReportMonitorStartResult
            {
                IsSuccess = false,
                PlanId = plan.PlanId,
                Message = LastErrorMessage
            };
        }

        if (_reportMonitorSessions.ContainsKey(plan.PlanId))
        {
            return new NativeReportMonitorStartResult
            {
                IsSuccess = true,
                PlanId = plan.PlanId,
                Message = $"Report monitor already active for {plan.DisplayReference}."
            };
        }

        var discovery = await EnsureDiscoveryForReportingAsync(cancellationToken).ConfigureAwait(false);
        if (discovery == null)
        {
            return new NativeReportMonitorStartResult
            {
                IsSuccess = false,
                PlanId = plan.PlanId,
                Message = LastErrorMessage
            };
        }

        var inventory = BuildEngineReportInventory(discovery.ReportInventory, plan);
        var directory = discovery.IedDirectory;
        var dataSetDirectories = await ReadPlannedDataSetDirectoriesAsync(plan, directory, cancellationToken).ConfigureAwait(false);

        var subscription = ArMms.MmsReportSubscriptionPlanner.BuildStaticPlan(
            inventory,
            dataSetDirectories,
            preferredRcbReference: plan.ReportControlReference,
            preferredDataSetReference: plan.DataSetReference,
            strictRcb: !string.IsNullOrWhiteSpace(plan.ReportControlReference),
            allowUrCbFallback: true,
            allowPollingFallback: true);

        if (!subscription.IsReady)
        {
            var dynamicPlan = ArMms.MmsReportSubscriptionPlanner.BuildDynamicPlan(
                inventory,
                directory,
                plan.Bindings.Select(b => b.IecReference),
                preferredLogicalDevice: ResolvePreferredLogicalDevice(plan),
                preferredRcbReference: plan.ReportControlReference,
                dataSetName: BuildDynamicDataSetName(plan),
                strictRcb: false,
                allowUrCbFallback: true,
                allowPollingFallback: true);

            if (dynamicPlan.IsReady)
                subscription = dynamicPlan;
        }

        if (!subscription.IsReady)
        {
            var blockers = subscription.Blockers.Count == 0 ? "no detailed blocker returned" : string.Join("; ", subscription.Blockers.Take(4));
            LastErrorMessage = $"ARIEC61850 report subscription plan blocked for {plan.DisplayReference}: {blockers}";
            return new NativeReportMonitorStartResult
            {
                IsSuccess = false,
                PlanId = plan.PlanId,
                Message = LastErrorMessage,
                SubscriptionSummary = subscription.Summary,
                MemberCount = subscription.Members.Count,
                Warnings = subscription.Warnings.Concat(subscription.Blockers).ToArray()
            };
        }

        var start = await _session.StartPersistentReportMonitorAsync(
            subscription,
            triggerGeneralInterrogation: true,
            deleteDynamicDataSetOnStop: true,
            directory,
            cancellationToken).ConfigureAwait(false);

        if (!start.IsSuccess || start.Session == null)
        {
            LastErrorMessage = $"ARIEC61850 persistent report monitor failed for {plan.DisplayReference}: {start.Message}";
            return new NativeReportMonitorStartResult
            {
                IsSuccess = false,
                PlanId = plan.PlanId,
                Message = LastErrorMessage,
                SubscriptionSummary = subscription.Summary,
                MemberCount = subscription.Members.Count,
                WriteStepCount = start.WriteSteps.Count,
                Warnings = start.Warnings.Concat(subscription.Warnings).ToArray()
            };
        }

        _reportMonitorSessions[plan.PlanId] = start.Session;
        LastErrorMessage = $"ARIEC61850 persistent report monitor active: {subscription.Summary}. {start.Message}";
        return new NativeReportMonitorStartResult
        {
            IsSuccess = true,
            PlanId = plan.PlanId,
            Message = LastErrorMessage,
            SubscriptionSummary = subscription.Summary,
            MemberCount = subscription.Members.Count,
            WriteStepCount = start.WriteSteps.Count,
            Warnings = start.Warnings.Concat(subscription.Warnings).ToArray()
        };
    }

    public async Task<NativeReportMonitorSliceResult> ReceiveReportMonitorSliceAsync(string planId, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (!_reportMonitorSessions.TryGetValue(planId, out var session))
        {
            return new NativeReportMonitorSliceResult
            {
                PlanId = planId,
                Message = $"Report monitor session not found for plan {planId}."
            };
        }

        var discovery = await EnsureDiscoveryForReportingAsync(cancellationToken).ConfigureAwait(false);
        var slice = await _session.ReceivePersistentReportMonitorSliceAsync(
            session,
            duration,
            discovery?.IedDirectory,
            pollReferences: null,
            pollInterval: null,
            triggerGeneralInterrogation: false,
            cancellationToken).ConfigureAwait(false);

        var updates = new List<NativeReportValueUpdate>();
        var warnings = new List<string>();
        foreach (var report in slice.Reports)
        {
            var projection = ArMms.MmsReportValueProjector.Project(report);
            warnings.AddRange(projection.Warnings);
            updates.AddRange(projection.Updates.Select(update => new NativeReportValueUpdate
            {
                Reference = update.Reference,
                FunctionalConstraint = update.FunctionalConstraint,
                Value = update.Value,
                Quality = update.Quality,
                Timestamp = update.Timestamp,
                Reason = update.Reason,
                Source = update.Source,
                ProjectionStatus = update.ProjectionStatus,
                UpdatedAt = update.UpdatedAt
            }));
        }

        return new NativeReportMonitorSliceResult
        {
            PlanId = planId,
            ReportCount = slice.Reports.Count,
            PollReadCount = slice.PollReads.Count,
            Message = slice.Message,
            Updates = updates,
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    public async Task<IReadOnlyList<NativeReportMonitorStopResult>> StopReportMonitorsAsync()
    {
        var results = new List<NativeReportMonitorStopResult>();
        foreach (var item in _reportMonitorSessions.ToArray())
        {
            try
            {
                var stop = await _session.StopPersistentReportMonitorAsync(item.Value, CancellationToken.None).ConfigureAwait(false);
                results.Add(new NativeReportMonitorStopResult
                {
                    IsSuccess = stop.IsSuccess,
                    PlanId = item.Key,
                    Message = stop.Message
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new NativeReportMonitorStopResult
                {
                    IsSuccess = false,
                    PlanId = item.Key,
                    Message = $"Report monitor cleanup failed: {ex.GetType().Name}: {ex.Message}"
                });
            }
            finally
            {
                _reportMonitorSessions.Remove(item.Key);
            }
        }

        return results;
    }

    private async Task TryReadReportAttributeAsync(NativeReportControlCandidate rcb, string attribute, Action<object?> apply, CancellationToken cancellationToken)
    {
        try
        {
            var value = await ReadValueAsync($"{rcb.Reference}.{attribute}", rcb.FunctionalConstraint, GuessReportAttributeType(attribute), cancellationToken).ConfigureAwait(false);
            if (value != null) apply(value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rcb.Status = $"Attribute probe partial: {attribute} {ex.GetType().Name}";
        }
    }

    private static string GuessReportAttributeType(string attribute)
    {
        return attribute.ToLowerInvariant() switch
        {
            "rptid" or "datset" or "entryid" => "String",
            "rptena" or "resv" or "gi" or "purgebuf" => "Boolean",
            "confrev" or "intgpd" or "buftm" or "sqnum" or "resvtms" => "UInt32",
            "trgops" or "optflds" => "BitString",
            _ => "String"
        };
    }

    private static string NormalizeReportAttributeText(object? value)
    {
        var text = value?.ToString()?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? string.Empty : text;
    }

    private static string NormalizeReportedDataSetReference(string domain, string value)
    {
        var text = value.Trim().Replace('$', '.');
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (text.Contains('/')) return text;

        return text.Contains('.') ? $"{domain}/{text}" : $"{domain}/LLN0.{text}";
    }

    private async Task<ArMms.MmsDiscoveryResult?> EnsureDiscoveryForReportingAsync(CancellationToken cancellationToken)
    {
        if (_lastDiscovery != null)
            return _lastDiscovery;

        try
        {
            var discovery = await _session.DiscoverAsync(
                probeReportAttributes: true,
                maxReportAttributeProbes: 96,
                cancellationToken).ConfigureAwait(false);

            _lastDiscovery = discovery;
            _liveModel = LiveIedModelDiscoveryBuilder.Build(discovery, new LiveIedModelDiscoveryBuildOptions
            {
                Host = _host,
                Port = _port,
                IncludeLowConfidenceTemplates = true
            });
            LastReportInventory = ToNativeInventory(discovery.ReportInventory);
            LastDiscoverySummary = $"{discovery.Summary} {_liveModel.Summary} Engine=ARIEC61850 live-model/schema/reporting.";
            return discovery;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastErrorMessage = $"ARIEC61850 reporting discovery failed: {ex.GetType().Name}: {ex.Message}. Last discovery: {_session.LastDiscoveryAttemptSummary}";
            return null;
        }
    }

    private async Task<IReadOnlyList<ArMms.MmsDataSetDirectoryResult>> ReadPlannedDataSetDirectoriesAsync(
        ReportControlPlan plan,
        ArMms.MmsIedModelDirectory directory,
        CancellationToken cancellationToken)
    {
        var dataSets = new[]
            {
                plan.DataSetReference
            }
            .Concat(_lastDiscovery?.ReportInventory.ReportControls
                .Where(rcb => ReferencesEqual(rcb.Reference, plan.ReportControlReference))
                .Select(rcb => rcb.DataSetReference) ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (dataSets.Length == 0)
            return Array.Empty<ArMms.MmsDataSetDirectoryResult>();

        return await _session.GetDataSetDirectoriesAsync(dataSets, directory, cancellationToken).ConfigureAwait(false);
    }

    private static ArMms.MmsReportInventory BuildEngineReportInventory(ArMms.MmsReportInventory source, ReportControlPlan plan)
    {
        var inventory = new ArMms.MmsReportInventory();
        foreach (var dataSet in source.DataSets)
        {
            inventory.DataSets.Add(new ArMms.MmsDataSetCandidate
            {
                Domain = dataSet.Domain,
                LogicalNode = dataSet.LogicalNode,
                Name = dataSet.Name,
                Reference = dataSet.Reference,
                RawMmsName = dataSet.RawMmsName
            });
        }

        foreach (var rcb in source.ReportControls)
            inventory.ReportControls.Add(CloneReportControl(rcb));

        if (!string.IsNullOrWhiteSpace(plan.DataSetReference) &&
            !inventory.DataSets.Any(ds => ReferencesEqual(ds.Reference, plan.DataSetReference)))
        {
            var parsedDataSet = ParseDataSetReference(plan.DataSetReference);
            inventory.DataSets.Add(new ArMms.MmsDataSetCandidate
            {
                Domain = parsedDataSet.Domain,
                LogicalNode = parsedDataSet.LogicalNode,
                Name = parsedDataSet.Name,
                Reference = plan.DataSetReference,
                RawMmsName = string.IsNullOrWhiteSpace(parsedDataSet.LogicalNode)
                    ? parsedDataSet.Name
                    : $"{parsedDataSet.LogicalNode}${parsedDataSet.Name}"
            });
        }

        if (!string.IsNullOrWhiteSpace(plan.ReportControlReference))
        {
            var existing = inventory.ReportControls.FirstOrDefault(rcb => ReferencesEqual(rcb.Reference, plan.ReportControlReference));
            if (existing == null)
                inventory.ReportControls.Add(CreateReportControlFromPlan(plan));
            else
                ApplyPlanHints(existing, plan);
        }

        return inventory;
    }

    private static ArMms.MmsReportControlCandidate CloneReportControl(ArMms.MmsReportControlCandidate source)
        => new()
        {
            Domain = source.Domain,
            LogicalNode = source.LogicalNode,
            FunctionalConstraint = source.FunctionalConstraint,
            Name = source.Name,
            Reference = source.Reference,
            Buffered = source.Buffered,
            DataSetReference = source.DataSetReference,
            ReportId = source.ReportId,
            ConfRev = source.ConfRev,
            IntegrityPeriodMs = source.IntegrityPeriodMs,
            EnabledState = source.EnabledState,
            ReservationState = source.ReservationState,
            ReservationTimeSeconds = source.ReservationTimeSeconds,
            BufferTimeMs = source.BufferTimeMs,
            TriggerOptions = source.TriggerOptions,
            OptionalFields = source.OptionalFields,
            Status = source.Status,
            Attributes = source.Attributes.ToList()
        };

    private static ArMms.MmsReportControlCandidate CreateReportControlFromPlan(ReportControlPlan plan)
    {
        var parsed = ParseReportControlReference(plan.ReportControlReference, plan.Buffered);
        var rcb = new ArMms.MmsReportControlCandidate
        {
            Domain = parsed.Domain,
            LogicalNode = parsed.LogicalNode,
            FunctionalConstraint = parsed.FunctionalConstraint,
            Name = parsed.Name,
            Reference = plan.ReportControlReference,
            Buffered = plan.Buffered,
            DataSetReference = plan.DataSetReference,
            ReportId = plan.ReportId,
            IntegrityPeriodMs = plan.IntegrityPeriodMs > 0 ? plan.IntegrityPeriodMs.ToString(CultureInfo.InvariantCulture) : string.Empty,
            TriggerOptions = plan.TriggerOptions,
            OptionalFields = plan.OptionalFields,
            Status = "ARServer report plan"
        };
        rcb.Attributes.AddRange(parsed.Buffered
            ? ["RptID", "RptEna", "DatSet", "ConfRev", "OptFlds", "BufTm", "SqNum", "TrgOps", "IntgPd", "GI", "PurgeBuf", "EntryID", "TimeOfEntry", "ResvTms"]
            : ["RptID", "RptEna", "Resv", "DatSet", "ConfRev", "OptFlds", "BufTm", "SqNum", "TrgOps", "IntgPd", "GI"]);
        return rcb;
    }

    private static void ApplyPlanHints(ArMms.MmsReportControlCandidate target, ReportControlPlan plan)
    {
        if (string.IsNullOrWhiteSpace(target.DataSetReference) && !string.IsNullOrWhiteSpace(plan.DataSetReference))
            target.DataSetReference = plan.DataSetReference;
        if (string.IsNullOrWhiteSpace(target.ReportId) && !string.IsNullOrWhiteSpace(plan.ReportId))
            target.ReportId = plan.ReportId;
        if (string.IsNullOrWhiteSpace(target.IntegrityPeriodMs) && plan.IntegrityPeriodMs > 0)
            target.IntegrityPeriodMs = plan.IntegrityPeriodMs.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(target.TriggerOptions) && !string.IsNullOrWhiteSpace(plan.TriggerOptions))
            target.TriggerOptions = plan.TriggerOptions;
        if (string.IsNullOrWhiteSpace(target.OptionalFields) && !string.IsNullOrWhiteSpace(plan.OptionalFields))
            target.OptionalFields = plan.OptionalFields;
    }

    private static (string Domain, string LogicalNode, string Name) ParseDataSetReference(string reference)
    {
        var text = reference.Trim().Replace('$', '.');
        var slash = text.IndexOf('/');
        var domain = slash > 0 ? text[..slash] : string.Empty;
        var item = slash > 0 && slash < text.Length - 1 ? text[(slash + 1)..] : text;
        var dot = item.LastIndexOf('.');
        if (dot <= 0 || dot >= item.Length - 1)
            return (domain, string.Empty, item);
        return (domain, item[..dot], item[(dot + 1)..]);
    }

    private static (string Domain, string LogicalNode, string FunctionalConstraint, string Name, bool Buffered) ParseReportControlReference(string reference, bool buffered)
    {
        var text = reference.Trim().Replace('$', '.');
        var slash = text.IndexOf('/');
        var domain = slash > 0 ? text[..slash] : string.Empty;
        var item = slash > 0 && slash < text.Length - 1 ? text[(slash + 1)..] : text;
        var segments = item.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var logicalNode = segments.Length > 0 ? segments[0] : string.Empty;
        var functionalConstraint = segments.FirstOrDefault(s => s.Equals("BR", StringComparison.OrdinalIgnoreCase) || s.Equals("RP", StringComparison.OrdinalIgnoreCase))
            ?? (buffered ? "BR" : "RP");
        var name = segments.Length > 0 ? segments[^1] : (buffered ? "BRCB" : "URCB");
        return (domain, logicalNode, functionalConstraint, name, buffered || functionalConstraint.Equals("BR", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolvePreferredLogicalDevice(ReportControlPlan plan)
    {
        if (!string.IsNullOrWhiteSpace(plan.ReportControlReference))
        {
            var slash = plan.ReportControlReference.IndexOf('/');
            if (slash > 0)
                return plan.ReportControlReference[..slash];
        }

        var reference = plan.Bindings.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.IecReference))?.IecReference ?? string.Empty;
        var refSlash = reference.IndexOf('/');
        return refSlash > 0 ? reference[..refSlash] : string.Empty;
    }

    private static string BuildDynamicDataSetName(ReportControlPlan plan)
        => "ARSRV_" + (string.IsNullOrWhiteSpace(plan.PlanId) ? Guid.NewGuid().ToString("N")[..8] : plan.PlanId[..Math.Min(8, plan.PlanId.Length)]).ToUpperInvariant();

    public Task<object?> ReadValueAsync(string objectReference, CancellationToken cancellationToken)
    {
        return ReadValueAsync(objectReference, string.Empty, string.Empty, cancellationToken);
    }

    public async Task<object?> ReadValueAsync(string objectReference, string functionalConstraint, string dataType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_session.IsMmsInitiated)
        {
            LastErrorMessage = !_session.IsTransportConnected
                ? "ARIEC61850 transport is not connected. Start the session again after TCP/COTP/ACSE association succeeds."
                : _session.State == ArMms.MmsAssociationState.MmsInitiateFailed
                    ? $"ARIEC61850 TCP/COTP connected, but ACSE/MMS Initiate was rejected or not understood by the IED. Last response: {_session.LastAssociationResponseHex}"
                    : $"ARIEC61850 transport is ready, but ACSE/MMS Initiate is not complete yet. State: {_session.State}.";
            return null;
        }

        var attempts = new List<string>();
        foreach (var candidate in BuildReadReferenceCandidates(objectReference, functionalConstraint))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (candidate.UseSmartDirectory && _lastDiscovery?.IedDirectory != null)
            {
                try
                {
                    var smart = await _session.ReadSmartAsync(_lastDiscovery.IedDirectory, candidate.Reference, cancellationToken).ConfigureAwait(false);
                    attempts.Add($"{candidate.Label}/smart: {smart.ReadResult.Message}");
                    if (smart.ReadResult.IsSuccess)
                    {
                        var projected = ProjectReadValue(smart.ReadResult.Value, dataType, candidate.Reference, objectReference);
                        if (projected != null)
                        {
                            LastErrorMessage = $"ARIEC61850 read OK via {candidate.Label}/smart. {smart.ResolveResult.Message}";
                            return projected;
                        }

                        attempts.Add($"{candidate.Label}/smart projection blocked: {LastErrorMessage}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    attempts.Add($"{candidate.Label}/smart exception: {ex.GetType().Name}: {ex.Message}");
                }
            }

            try
            {
                var normalized = ArMms.MmsObjectReference.Parse(candidate.Reference, candidate.FunctionalConstraint);
                var result = await _session.ReadSingleVariableAsync(normalized, cancellationToken).ConfigureAwait(false);
                attempts.Add($"{candidate.Label}/direct {normalized}: {(result.IsSuccess ? "OK" : result.Message)}");
                if (result.IsSuccess)
                {
                    var projected = ProjectReadValue(result.Value, dataType, candidate.Reference, objectReference);
                    if (projected != null)
                    {
                        LastErrorMessage = $"ARIEC61850 read OK via {candidate.Label}/direct: {normalized}. {result.Message}";
                        return projected;
                    }

                    attempts.Add($"{candidate.Label}/direct projection blocked: {LastErrorMessage}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                attempts.Add($"{candidate.Label}/direct exception: {ex.GetType().Name}: {ex.Message}");
            }
        }

        LastErrorMessage = attempts.Count == 0
            ? $"ARIEC61850 read failed for {objectReference}: no read candidates."
            : $"ARIEC61850 read failed for {objectReference}: {string.Join(" | ", attempts.Take(8))}";
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopReportMonitorsAsync().ConfigureAwait(false);
        await _session.DisposeAsync().ConfigureAwait(false);
    }

    private static IReadOnlyList<SignalDefinition> BuildSignalsFromArIecModel(
        LiveIedModelDiscoveryDocument model,
        NativeMmsDiscoverySnapshot fallbackSnapshot)
    {
        var now = DateTime.Now;
        var signals = new List<SignalDefinition>();

        foreach (var logicalDevice in model.LogicalDevices)
        {
            foreach (var logicalNode in logicalDevice.LogicalNodes)
            {
                foreach (var dataObject in logicalNode.DataObjects)
                {
                    AddArIecSmartTargets(signals, logicalNode, dataObject, now);
                    AddArIecAvrSemanticTargets(signals, logicalNode, dataObject, now);
                }
            }
        }

        foreach (var fallback in NativeMmsDiscoveryMapper.BuildSignals(fallbackSnapshot))
        {
            if (!signals.Any(s => ReferencesEqual(s.ObjectReference, fallback.ObjectReference)))
                signals.Add(fallback);
        }

        return signals
            .Where(s => s.DataType != "Directory")
            .Where(IsGatewayReadableSignal)
            .GroupBy(s => NormalizeReference(s.ObjectReference), StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(x => x.Source.StartsWith("ARIEC61850", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.IsScadaCoreSignal)
                .ThenByDescending(x => ConfidenceScore(x.Confidence))
                .First())
            .OrderBy(s => s.SortPriority)
            .ThenByDescending(s => ConfidenceScore(s.Confidence))
            .ThenBy(s => s.LogicalNode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.ObjectReference, StringComparer.OrdinalIgnoreCase)
            .Take(12000)
            .ToList();
    }

    private static void AddArIecSmartTargets(
        ICollection<SignalDefinition> signals,
        LiveIedLogicalNodeModel logicalNode,
        LiveIedDataObjectModel dataObject,
        DateTime now)
    {
        var targets = Iec61850SmartReadPlanBuilder.BuildForDataObject(dataObject);
        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.Reference) || string.IsNullOrWhiteSpace(target.FunctionalConstraint))
                continue;

            var reference = NormalizeStructuredStatusTargetReference(target.Reference, dataObject.Name, dataObject.InferredCdc);
            var signal = CreateArIecSignal(
                reference,
                target.FunctionalConstraint,
                target.Purpose,
                logicalNode.LnClass,
                dataObject.Name,
                dataObject.InferredCdc,
                now,
                "ARIEC61850 live model read plan");

            signals.Add(signal);
        }
    }

    private static string NormalizeStructuredStatusTargetReference(string reference, string dataObjectName, string cdc)
    {
        var normalized = (reference ?? string.Empty).Trim().Replace('$', '.');
        if (string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(dataObjectName))
            return normalized;

        if (normalized.EndsWith(".stVal", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".general", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".q", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".t", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (IsStatusCdc(cdc) && EndsWithPathSegment(normalized, dataObjectName))
            return normalized + ".stVal";

        return normalized;
    }

    private static bool IsStatusCdc(string cdc)
        => cdc.Equals("DPC", StringComparison.OrdinalIgnoreCase) ||
           cdc.Equals("SPC", StringComparison.OrdinalIgnoreCase) ||
           cdc.Equals("SPS", StringComparison.OrdinalIgnoreCase) ||
           cdc.Equals("INS", StringComparison.OrdinalIgnoreCase) ||
           cdc.Equals("ENS", StringComparison.OrdinalIgnoreCase) ||
           cdc.Equals("BSC", StringComparison.OrdinalIgnoreCase);

    private static bool EndsWithPathSegment(string reference, string segment)
    {
        var text = reference.Replace('$', '.').TrimEnd('.');
        var dot = text.LastIndexOf('.');
        var slash = text.LastIndexOf('/');
        var start = Math.Max(dot, slash) + 1;
        return start >= 0 &&
               start < text.Length &&
               text[start..].Equals(segment, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGatewayReadableSignal(SignalDefinition signal)
    {
        if (signal.IsScadaCoreSignal)
            return true;

        var r = NormalizeReference(signal.ObjectReference);
        var dataType = signal.DataType ?? string.Empty;

        if (dataType.Equals("Quality", StringComparison.OrdinalIgnoreCase))
            return r.EndsWith(".q");
        if (dataType.Equals("Timestamp", StringComparison.OrdinalIgnoreCase))
            return r.EndsWith(".t") || r.EndsWith(".tm");
        if (dataType.Equals("Float32", StringComparison.OrdinalIgnoreCase) || dataType.Equals("Double", StringComparison.OrdinalIgnoreCase))
            return r.EndsWith(".f") || r.EndsWith(".mag.f") || r.EndsWith(".ang.f");
        if (dataType.Equals("Dbpos", StringComparison.OrdinalIgnoreCase))
            return r.EndsWith(".pos.stval") || r.EndsWith(".stval");
        if (dataType.Equals("Boolean", StringComparison.OrdinalIgnoreCase))
            return r.EndsWith(".stval") || r.EndsWith(".general") || r.EndsWith(".ctlval");
        if (dataType.Equals("Enum", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("Int32", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("UInt32", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("Integer", StringComparison.OrdinalIgnoreCase))
        {
            return r.EndsWith(".stval") ||
                   r.EndsWith(".posval") ||
                   r.EndsWith(".actval") ||
                   r.EndsWith(".setval") ||
                   r.EndsWith(".ctlmodel");
        }

        return false;
    }

    private static void AddArIecAvrSemanticTargets(
        ICollection<SignalDefinition> signals,
        LiveIedLogicalNodeModel logicalNode,
        LiveIedDataObjectModel dataObject,
        DateTime now)
    {
        var lnClass = logicalNode.LnClass.ToUpperInvariant();
        if (lnClass is not ("ATCC" or "AVC" or "AVCO"))
            return;

        if (dataObject.Name.Equals("TapChg", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add(CreateArIecSignal(
                $"{dataObject.Reference}.ValWTr.posVal",
                "ST",
                "IntegerStepPosition",
                lnClass,
                dataObject.Name,
                "BSC",
                now,
                "ARIEC61850 AVR semantic profile"));
        }
    }

    private static SignalDefinition CreateArIecSignal(
        string reference,
        string functionalConstraint,
        string semanticKind,
        string logicalNodeClass,
        string dataObjectName,
        string cdc,
        DateTime now,
        string source)
    {
        var dataType = InferArIecDataType(reference, functionalConstraint, semanticKind, dataObjectName, cdc);
        var category = InferArIecCategory(reference, functionalConstraint, dataType, logicalNodeClass, semanticKind);
        var unit = InferArIecUnit(reference);
        var ln = ExtractLogicalNode(reference);
        var isCore = SignalDefinition.IsCoreScadaSignal(reference, SignalDefinition.DetectLogicalNodeClass(ln), dataType, category);

        return new SignalDefinition
        {
            Name = MakeArIecFriendlyName(reference, dataObjectName, category, semanticKind),
            ObjectReference = reference.Trim().Replace('$', '.'),
            FunctionalConstraint = functionalConstraint.Trim().ToUpperInvariant(),
            DataType = dataType,
            Category = category,
            Unit = unit,
            Confidence = isCore || source.Contains("semantic", StringComparison.OrdinalIgnoreCase) ? "High" : "Medium",
            IsSelected = isCore,
            IsReportCapable = isCore && functionalConstraint.Trim().ToUpperInvariant() is "ST" or "MX",
            Source = source,
            Value = "Pending read",
            Quality = "Pending",
            Timestamp = now
        };
    }

    private static string InferArIecDataType(string reference, string functionalConstraint, string semanticKind, string dataObjectName, string cdc)
    {
        var r = NormalizeReference(reference);
        var semantic = semanticKind ?? string.Empty;

        if (r.EndsWith(".q")) return "Quality";
        if (r.EndsWith(".t") || r.EndsWith(".tm")) return "Timestamp";
        if (r.EndsWith(".ctlmodel")) return "Enum";
        if (semantic.Contains("DoublePoint", StringComparison.OrdinalIgnoreCase) || cdc.Equals("DPC", StringComparison.OrdinalIgnoreCase)) return "Dbpos";
        if (r.EndsWith(".posval") || r.EndsWith(".actval") || r.EndsWith(".pulsqty")) return "Int32";
        if (r.EndsWith(".mag.f") || r.EndsWith(".ang.f") || r.EndsWith(".f")) return "Float32";
        if (r.EndsWith(".general") || semantic.Contains("Boolean", StringComparison.OrdinalIgnoreCase)) return "Boolean";
        if (r.EndsWith(".stval") && (dataObjectName.Contains("Cnt", StringComparison.OrdinalIgnoreCase) || cdc is "INS" or "INC" or "BCR")) return "Int32";
        if (r.EndsWith(".stval")) return "Enum";
        if (functionalConstraint.Equals("MX", StringComparison.OrdinalIgnoreCase)) return "Float32";
        return "Enum";
    }

    private static string InferArIecCategory(string reference, string functionalConstraint, string dataType, string logicalNodeClass, string semanticKind)
    {
        var r = NormalizeReference(reference);
        var cls = logicalNodeClass.ToUpperInvariant();
        if (r.Contains(".pos.") || dataType == "Dbpos") return "Position";
        if (dataType == "Float32" || functionalConstraint.Equals("MX", StringComparison.OrdinalIgnoreCase)) return "Measurement";
        if (cls.StartsWith("P", StringComparison.OrdinalIgnoreCase) || r.EndsWith(".op.general") || r.EndsWith(".str.general") || r.EndsWith(".tr.general")) return "Protection";
        if (cls is "ATCC" or "AVC" or "AVCO" or "YPTR" or "GGIO") return "Status";
        return semanticKind.Contains("Quality", StringComparison.OrdinalIgnoreCase) ? "Quality" : "Status";
    }

    private static string InferArIecUnit(string reference)
    {
        var r = NormalizeReference(reference);
        if (r.Contains(".a.") || r.Contains("loda") || r.Contains("circa") || r.Contains("limloda")) return "A";
        if (r.Contains(".phv.") || r.Contains(".ppv.") || r.Contains("ctlv") || r.Contains("bndctr") || r.Contains("ctldv")) return "V";
        if (r.Contains("phang") || r.EndsWith(".ang.f")) return "deg";
        if (r.Contains("tms")) return "s";
        if (r.Contains(".hz")) return "Hz";
        return string.Empty;
    }

    private static string MakeArIecFriendlyName(string reference, string dataObjectName, string category, string semanticKind)
    {
        var ln = ExtractLogicalNode(reference);
        var path = reference.Contains('.') ? reference[(reference.IndexOf('.') + 1)..] : reference;
        path = path
            .Replace("ValWTr.posVal", "Position", StringComparison.OrdinalIgnoreCase)
            .Replace("cVal.mag.f", "Value", StringComparison.OrdinalIgnoreCase)
            .Replace("mag.f", "Value", StringComparison.OrdinalIgnoreCase)
            .Replace("stVal", "Status", StringComparison.OrdinalIgnoreCase)
            .Replace("general", "General", StringComparison.OrdinalIgnoreCase);

        return $"{ln} {path}".Replace('.', ' ').Replace("  ", " ").Trim();
    }

    private static int ConfidenceScore(string confidence) => confidence switch
    {
        "High" => 3,
        "Medium" => 2,
        _ => 1
    };

    private static bool ReferencesEqual(string left, string right)
        => NormalizeReference(left).Equals(NormalizeReference(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeReference(string reference)
        => (reference ?? string.Empty).Trim().Replace('$', '.').Replace("..", ".").ToLowerInvariant();

    private static string ExtractLogicalNode(string reference)
    {
        var slash = reference.IndexOf('/');
        if (slash < 0 || slash >= reference.Length - 1) return string.Empty;
        var after = reference[(slash + 1)..];
        var dot = after.IndexOf('.');
        return dot > 0 ? after[..dot] : after;
    }

    private static NativeMmsDiscoverySnapshot ToNativeSnapshot(ArMms.MmsDiscoverySnapshot snapshot)
        => new()
        {
            DomainVariables = snapshot.DomainVariables,
            DomainVariableLists = snapshot.DomainVariableLists
        };

    private static NativeReportInventory ToNativeInventory(ArMms.MmsReportInventory inventory)
        => new()
        {
            DataSets = inventory.DataSets.Select(x => new NativeDataSetCandidate
            {
                Domain = x.Domain,
                LogicalNode = x.LogicalNode,
                Name = x.Name,
                Reference = x.Reference,
                RawMmsName = x.RawMmsName
            }).ToList(),
            ReportControls = inventory.ReportControls.Select(x => new NativeReportControlCandidate
            {
                Domain = x.Domain,
                LogicalNode = x.LogicalNode,
                FunctionalConstraint = x.FunctionalConstraint,
                Name = x.Name,
                Reference = x.Reference,
                Buffered = x.Buffered,
                DataSetReference = x.DataSetReference,
                ReportId = x.ReportId,
                ConfRev = x.ConfRev,
                IntegrityPeriodMs = x.IntegrityPeriodMs,
                EnabledState = x.EnabledState,
                Status = x.Status,
                Attributes = x.Attributes.ToList()
            }).ToList()
        };

    private readonly record struct ReadReferenceCandidate(
        string Reference,
        string FunctionalConstraint,
        string Label,
        bool UseSmartDirectory);

    private static IReadOnlyList<ReadReferenceCandidate> BuildReadReferenceCandidates(string objectReference, string functionalConstraint)
    {
        var fc = NormalizeFunctionalConstraint(functionalConstraint, objectReference);
        var candidates = new List<ReadReferenceCandidate>();

        void Add(string reference, string label, bool useSmartDirectory = true)
        {
            if (string.IsNullOrWhiteSpace(reference))
                return;
            if (candidates.Any(x => x.Reference.Equals(reference, StringComparison.OrdinalIgnoreCase) &&
                                    x.FunctionalConstraint.Equals(fc, StringComparison.OrdinalIgnoreCase)))
                return;
            candidates.Add(new ReadReferenceCandidate(reference.Trim(), fc, label, useSmartDirectory));
        }

        var reference = objectReference.Trim().Replace('$', '.');
        Add(reference, "requested");

        if (TryGetDataObjectReference(reference, out var rootDataObjectReference) &&
            !ReferencesEqual(rootDataObjectReference, reference))
        {
            Add(rootDataObjectReference, "parent-data-object-schema", useSmartDirectory: false);
        }

        if (TryRemoveSuffix(reference, ".ValWTr.posVal", out var valWithTransParent))
        {
            Add(valWithTransParent, "parent-do-for-valwtr-posval", useSmartDirectory: false);
            Add($"{valWithTransParent}.ValWTr", "parent-valwtr-for-posval", useSmartDirectory: false);
        }
        if (TryRemoveSuffix(reference, ".posVal", out var posValParent))
            Add(posValParent, "parent-do-for-posval", useSmartDirectory: false);
        if (TryRemoveSuffix(reference, ".stVal", out var stValParent))
            Add(stValParent, "parent-do-for-stVal", useSmartDirectory: false);
        if (TryRemoveSuffix(reference, ".q", out var qParent))
            Add(qParent, "parent-do-for-q", useSmartDirectory: false);
        if (TryRemoveSuffix(reference, ".t", out var tParent))
            Add(tParent, "parent-do-for-t", useSmartDirectory: false);
        if (TryRemoveSuffix(reference, ".cVal.mag.f", out var cValParent))
        {
            Add(cValParent, "parent-do-for-cval", useSmartDirectory: false);
            Add($"{cValParent}.cVal", "parent-cval-for-f", useSmartDirectory: false);
            Add($"{cValParent}.cVal.mag", "parent-cval-mag-for-f", useSmartDirectory: false);
        }
        if (TryRemoveSuffix(reference, ".mag.f", out var magParent))
        {
            Add(magParent, "parent-do-for-mag-f", useSmartDirectory: false);
            Add($"{magParent}.mag", "parent-mag-for-f", useSmartDirectory: false);
        }
        if (TryRemoveSuffix(reference, ".ang.f", out var angParent))
        {
            Add(angParent, "parent-do-for-ang-f", useSmartDirectory: false);
            Add($"{angParent}.ang", "parent-ang-for-f", useSmartDirectory: false);
        }
        if (TryRemoveSuffix(reference, ".f", out var fParent))
            Add(fParent, "parent-for-f", useSmartDirectory: false);

        return candidates;
    }

    private static bool TryGetDataObjectReference(string reference, out string dataObjectReference)
    {
        dataObjectReference = string.Empty;
        var text = (reference ?? string.Empty).Trim().Replace('$', '.');
        var slash = text.IndexOf('/');
        if (slash < 0 || slash >= text.Length - 1)
            return false;

        var domain = text[..slash];
        var segments = text[(slash + 1)..]
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
            return false;

        dataObjectReference = $"{domain}/{segments[0]}.{segments[1]}";
        return true;
    }

    private static bool TryRemoveSuffix(string value, string suffix, out string result)
    {
        if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            result = value[..^suffix.Length];
            return !string.IsNullOrWhiteSpace(result);
        }

        result = string.Empty;
        return false;
    }

    private static string NormalizeFunctionalConstraint(string functionalConstraint, string reference)
    {
        var fc = (functionalConstraint ?? string.Empty).Trim().Trim('[', ']', '(', ')').ToUpperInvariant();
        if (fc.StartsWith("FC_", StringComparison.OrdinalIgnoreCase))
            fc = fc[3..];
        if (!string.IsNullOrWhiteSpace(fc) && fc != "-")
            return fc;

        var r = reference.Replace('$', '.');
        if (r.Contains(".mag.", StringComparison.OrdinalIgnoreCase) ||
            r.EndsWith(".f", StringComparison.OrdinalIgnoreCase) ||
            r.Contains(".cVal", StringComparison.OrdinalIgnoreCase))
            return "MX";
        if (r.Contains(".ctl", StringComparison.OrdinalIgnoreCase) ||
            r.Contains(".Oper", StringComparison.OrdinalIgnoreCase))
            return "CO";
        if (r.Contains(".set", StringComparison.OrdinalIgnoreCase))
            return "SP";
        if (r.Contains(".NamPlt", StringComparison.OrdinalIgnoreCase))
            return "DC";
        return "ST";
    }

    private Iec61850ReadValue? ProjectReadValue(ArMms.MmsDataValue? value, string dataType, string readReference, string requestedReference)
    {
        if (TryProjectStructuredLeafBySemantic(value, dataType, readReference, requestedReference, out var semanticProjection, out var semanticStatus))
            return semanticProjection;

        if (TryProjectWithArIecBinding(value, dataType, readReference, requestedReference, out var boundProjection, out var bindingStatus))
            return boundProjection;

        if (value == null)
        {
            LastErrorMessage = string.IsNullOrWhiteSpace(bindingStatus)
                ? $"ARIEC61850 read returned no MMS value for {requestedReference} via {readReference}."
                : bindingStatus;
            return null;
        }

        if (RequiresSchemaProjection(value, dataType, readReference, requestedReference))
        {
            LastErrorMessage = BuildSchemaProjectionBlockedMessage(value, readReference, requestedReference, FirstUsefulText(semanticStatus, bindingStatus));
            return null;
        }

        var projection = SelectProjectedValue(value, dataType, requestedReference);
        var rawValue = ConvertProjectedValue(projection.Value, dataType, requestedReference);
        var display = FormatProjectedDisplay(rawValue, dataType);

        return new Iec61850ReadValue
        {
            Value = rawValue,
            DisplayValue = display,
            Quality = projection.Quality,
            DeviceTimestamp = projection.Timestamp,
            SourceReference = requestedReference,
            ReadReference = readReference,
            Projection = projection.Description
        };
    }

    private bool TryProjectWithArIecBinding(
        ArMms.MmsDataValue? value,
        string dataType,
        string readReference,
        string requestedReference,
        out Iec61850ReadValue projected,
        out string bindingStatus)
    {
        projected = new Iec61850ReadValue();
        bindingStatus = string.Empty;
        if (value == null)
        {
            bindingStatus = "MMS value is null.";
            return false;
        }

        if (_liveModel == null)
        {
            bindingStatus = "live IEC 61850 model is not available; run discovery before projecting parent DA/FCD structures.";
            return false;
        }

        if (!TryFindLiveDataObject(requestedReference, out var dataObject))
        {
            bindingStatus = $"data object schema not found for {requestedReference}.";
            return false;
        }

        var rootSchema = Iec61850DataObjectSchemaBuilder.FromLiveDataObject(dataObject).ToRootNode();
        var readSchema = TryFindSchemaNode(rootSchema, readReference, out var schemaNode)
            ? schemaNode
            : ReferencesEqual(readReference, dataObject.Reference)
                ? rootSchema
                : null;
        if (readSchema == null)
        {
            bindingStatus = $"read reference {readReference} is outside discovered schema {rootSchema.Reference}.";
            return false;
        }

        var binding = Iec61850ValueBindingEngine.Bind(readSchema, value);
        var diagnostics = FormatBindingDiagnostics(binding.Diagnostics);
        if (binding.HasMismatch && RequiresSchemaProjection(value, dataType, readReference, requestedReference))
        {
            bindingStatus = $"schema/value mismatch from ARIEC61850 binding engine: {diagnostics}";
            return false;
        }

        if (!TryFindBoundRow(binding.Root, requestedReference, out var targetRow, out var ancestors))
        {
            if (ReferencesEqual(binding.Root.Reference, requestedReference))
            {
                targetRow = binding.Root;
                ancestors = Array.Empty<Iec61850BoundValueRow>();
            }
            else
            {
                bindingStatus = $"target leaf {requestedReference} was not found under bound schema {readSchema.Reference}. {diagnostics}";
                return false;
            }
        }

        if (IsStructuralDisplay(targetRow.Value))
        {
            bindingStatus = $"target {targetRow.Reference} resolved to structural value '{targetRow.Value}', not a scalar leaf. {diagnostics}";
            return false;
        }

        var rawValue = ConvertBoundDisplayValue(targetRow.Value, dataType, requestedReference);
        var display = rawValue is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : FormatProjectedDisplay(rawValue, dataType);

        projected = new Iec61850ReadValue
        {
            Value = rawValue,
            DisplayValue = display,
            Quality = FirstUseful(
                targetRow.Quality,
                ancestors.Reverse().Select(x => x.Quality).ToArray(),
                binding.Root.Quality),
            DeviceTimestamp = FirstUseful(
                targetRow.Timestamp,
                ancestors.Reverse().Select(x => x.Timestamp).ToArray(),
                binding.Root.Timestamp),
            SourceReference = requestedReference,
            ReadReference = readReference,
            Projection = $"ARIEC61850 schema bind: {readSchema.Reference} -> {targetRow.Reference}; confidence={targetRow.Confidence}; {diagnostics}"
        };
        bindingStatus = $"schema-bound {readSchema.Reference} -> {targetRow.Reference}; confidence={targetRow.Confidence}; {diagnostics}";
        return true;
    }

    private bool TryFindLiveDataObject(string reference, out LiveIedDataObjectModel dataObject)
    {
        dataObject = new LiveIedDataObjectModel();
        if (_liveModel == null || !TryGetDataObjectReference(reference, out var dataObjectReference))
            return false;

        foreach (var candidate in _liveModel.LogicalDevices
                     .SelectMany(ld => ld.LogicalNodes)
                     .SelectMany(ln => ln.DataObjects))
        {
            if (ReferencesEqual(candidate.Reference, dataObjectReference))
            {
                dataObject = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindSchemaNode(Iec61850ValueSchemaNode node, string reference, out Iec61850ValueSchemaNode result)
    {
        if (ReferencesEqual(node.Reference, reference))
        {
            result = node;
            return true;
        }

        foreach (var child in node.Children)
        {
            if (TryFindSchemaNode(child, reference, out result))
                return true;
        }

        result = new Iec61850ValueSchemaNode();
        return false;
    }

    private static bool TryFindBoundRow(
        Iec61850BoundValueRow root,
        string reference,
        out Iec61850BoundValueRow row,
        out IReadOnlyList<Iec61850BoundValueRow> ancestors)
    {
        var path = new List<Iec61850BoundValueRow>();
        return TryFindBoundRow(root, reference, path, out row, out ancestors);
    }

    private static bool TryFindBoundRow(
        Iec61850BoundValueRow current,
        string reference,
        List<Iec61850BoundValueRow> path,
        out Iec61850BoundValueRow row,
        out IReadOnlyList<Iec61850BoundValueRow> ancestors)
    {
        path.Add(current);
        if (ReferencesEqual(current.Reference, reference))
        {
            row = current;
            ancestors = path.Take(path.Count - 1).ToArray();
            path.RemoveAt(path.Count - 1);
            return true;
        }

        foreach (var child in current.Children)
        {
            if (TryFindBoundRow(child, reference, path, out row, out ancestors))
            {
                path.RemoveAt(path.Count - 1);
                return true;
            }
        }

        path.RemoveAt(path.Count - 1);
        row = new Iec61850BoundValueRow();
        ancestors = Array.Empty<Iec61850BoundValueRow>();
        return false;
    }

    private static object? ConvertBoundDisplayValue(string value, string dataType, string reference)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text) || text == "-")
            return null;

        if (bool.TryParse(text, out var boolean))
            return boolean;

        var hint = dataType ?? string.Empty;
        var r = NormalizeReference(reference);
        if (hint.Equals("Float32", StringComparison.OrdinalIgnoreCase) ||
            hint.Equals("Double", StringComparison.OrdinalIgnoreCase) ||
            r.EndsWith(".f") ||
            r.EndsWith(".mag.f") ||
            r.EndsWith(".ang.f"))
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return number;
        }

        if (hint.Equals("Int32", StringComparison.OrdinalIgnoreCase) ||
            hint.Equals("UInt32", StringComparison.OrdinalIgnoreCase) ||
            hint.Equals("Integer", StringComparison.OrdinalIgnoreCase) ||
            r.EndsWith(".posval") ||
            r.EndsWith(".actval") ||
            r.EndsWith(".pulsqty"))
        {
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                return integer;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
                return numeric;
        }

        return text;
    }

    private static bool IsStructuralDisplay(string value)
        => value.StartsWith("Struct(", StringComparison.OrdinalIgnoreCase) ||
           value.StartsWith("Array(", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresSchemaProjection(ArMms.MmsDataValue? value, string dataType, string readReference, string requestedReference)
    {
        if (value == null || value.Kind is not (ArMms.MmsDataKind.Structure or ArMms.MmsDataKind.Array))
            return false;

        if (!ReferencesEqual(readReference, requestedReference))
            return true;

        return IsScalarLeafReference(requestedReference) || IsScalarDataTypeHint(dataType);
    }

    private static bool IsScalarLeafReference(string reference)
    {
        var r = NormalizeReference(reference);
        return r.EndsWith(".stval") ||
               r.EndsWith(".general") ||
               r.EndsWith(".valwtr.posval") ||
               r.EndsWith(".posval") ||
               r.EndsWith(".ctlval") ||
               r.EndsWith(".actval") ||
               r.EndsWith(".setval") ||
               r.EndsWith(".ctlmodel") ||
               r.EndsWith(".q") ||
               r.EndsWith(".t") ||
               r.EndsWith(".f") ||
               r.EndsWith(".i");
    }

    private static bool IsScalarDataTypeHint(string dataType)
    {
        var hint = (dataType ?? string.Empty).Trim();
        return hint.Equals("Boolean", StringComparison.OrdinalIgnoreCase) ||
               hint.Equals("Bool", StringComparison.OrdinalIgnoreCase) ||
               hint.Equals("Dbpos", StringComparison.OrdinalIgnoreCase) ||
               hint.Equals("Enum", StringComparison.OrdinalIgnoreCase) ||
               hint.Equals("Int32", StringComparison.OrdinalIgnoreCase) ||
               hint.Equals("UInt32", StringComparison.OrdinalIgnoreCase) ||
               hint.Equals("Integer", StringComparison.OrdinalIgnoreCase) ||
               hint.Equals("Float32", StringComparison.OrdinalIgnoreCase) ||
               hint.Equals("Double", StringComparison.OrdinalIgnoreCase) ||
               hint.Equals("Quality", StringComparison.OrdinalIgnoreCase) ||
               hint.Equals("Timestamp", StringComparison.OrdinalIgnoreCase) ||
               hint.Equals("UtcTime", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBindingDiagnostics(IReadOnlyList<string> diagnostics)
    {
        if (diagnostics.Count == 0)
            return "no binding diagnostics";

        var text = string.Join("; ", diagnostics.Take(4));
        if (diagnostics.Count > 4)
            text += $"; +{diagnostics.Count - 4} more";
        return text;
    }

    private static string BuildSchemaProjectionBlockedMessage(
        ArMms.MmsDataValue? value,
        string readReference,
        string requestedReference,
        string bindingStatus)
    {
        var status = string.IsNullOrWhiteSpace(bindingStatus) ? "ARIEC61850 binding engine did not return a usable scalar projection." : bindingStatus;
        var shape = value == null ? "null" : $"{value.Kind} child-count={value.Children.Count.ToString(CultureInfo.InvariantCulture)}";
        var raw = value == null ? "-" : ArMms.MmsDataValueRenderer.ToCompactString(value, readReference);
        return $"ARIEC61850 schema binding required for structured MMS value {readReference} -> {requestedReference}, but binding was not usable: {status}. Raw shape={shape}, raw={Truncate(raw, 240)}. Value was not published to avoid wrong DA/leaf mapping.";
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        if (maxLength <= 3)
            return value[..maxLength];
        return value[..(maxLength - 3)] + "...";
    }

    private static string FirstUseful(string primary, IReadOnlyList<string> inherited, string fallback)
    {
        if (IsUsefulColumn(primary))
            return primary;
        foreach (var value in inherited)
        {
            if (IsUsefulColumn(value))
                return value;
        }
        return IsUsefulColumn(fallback) ? fallback : string.Empty;
    }

    private static bool IsUsefulColumn(string value)
        => !string.IsNullOrWhiteSpace(value) && value != "-";

    private static string FirstUsefulText(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && v != "-") ?? string.Empty;

    private static bool TryProjectStructuredLeafBySemantic(
        ArMms.MmsDataValue? value,
        string dataType,
        string readReference,
        string requestedReference,
        out Iec61850ReadValue projected,
        out string status)
    {
        projected = new Iec61850ReadValue();
        status = string.Empty;

        if (value == null || value.Kind is not (ArMms.MmsDataKind.Structure or ArMms.MmsDataKind.Array))
            return false;

        string[] segments;
        if (IsScalarLeafReference(requestedReference))
        {
            if (!TryGetRelativeLeafSegments(readReference, requestedReference, out segments))
                return false;
        }
        else if (IsStatusScalarDataTypeHint(dataType))
        {
            segments = new[] { "stVal" };
        }
        else
        {
            return false;
        }

        var leaf = segments[^1];
        if (!IsSemanticLeafProjectionCandidate(leaf, requestedReference))
            return false;

        var branch = value;
        if (segments.Length == 2 && IsKnownSingleStructBranch(segments[0]) && TryGetOnlyMeaningfulStructChild(value, out var childBranch))
            branch = childBranch;
        else if (segments.Length > 1)
            return false;

        if (!TrySelectSemanticLeaf(branch, leaf, dataType, requestedReference, out var selected, out var reason) || selected == null)
        {
            status = $"semantic structured projection did not find a trustworthy {leaf} child for {requestedReference}; raw structure was not published.";
            return false;
        }

        var rawValue = ConvertProjectedValue(selected, dataType, requestedReference);
        var display = FormatProjectedDisplay(rawValue, dataType);
        var quality = DecodeQuality(FindQualityChild(branch) ?? branch);
        var timestamp = DecodeTimestamp(FindTimestampChild(branch) ?? branch);

        if (IsQualityHint(dataType, requestedReference))
            quality = FirstUsefulText(DecodeQuality(selected), quality);
        if (IsTimestampHint(dataType, requestedReference))
            timestamp = FirstUsefulText(DecodeTimestamp(selected), timestamp);

        projected = new Iec61850ReadValue
        {
            Value = rawValue,
            DisplayValue = display,
            Quality = quality,
            DeviceTimestamp = timestamp,
            SourceReference = requestedReference,
            ReadReference = readReference,
            Projection = $"semantic MMS structure projection: {reason}; read={readReference}; source={requestedReference}"
        };
        status = projected.Projection;
        return true;
    }

    private static bool TryGetRelativeLeafSegments(string readReference, string requestedReference, out string[] segments)
    {
        segments = Array.Empty<string>();
        var read = NormalizeReference(readReference);
        var requested = NormalizeReference(requestedReference);
        if (string.IsNullOrWhiteSpace(requested))
            return false;

        if (ReferencesEqual(readReference, requestedReference))
        {
            var leaf = LastSegment(requestedReference);
            if (string.IsNullOrWhiteSpace(leaf))
                return false;
            segments = new[] { leaf };
            return true;
        }

        var prefix = read.EndsWith('.') ? read : read + ".";
        if (!requested.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = requested[prefix.Length..];
        segments = suffix.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length > 0;
    }

    private static bool IsSemanticLeafProjectionCandidate(string leaf, string requestedReference)
    {
        if (leaf.Equals("q", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("t", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("stVal", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("general", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("posVal", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("ctlVal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return requestedReference.EndsWith(".ValWTr.posVal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStatusScalarDataTypeHint(string dataType)
    {
        var hint = (dataType ?? string.Empty).Trim();
        return hint.Equals("Boolean", StringComparison.OrdinalIgnoreCase) ||
               hint.Equals("Bool", StringComparison.OrdinalIgnoreCase) ||
               hint.Equals("Dbpos", StringComparison.OrdinalIgnoreCase) ||
               hint.Equals("Enum", StringComparison.OrdinalIgnoreCase) ||
               hint.Equals("Int32", StringComparison.OrdinalIgnoreCase) ||
               hint.Equals("UInt32", StringComparison.OrdinalIgnoreCase) ||
               hint.Equals("Integer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownSingleStructBranch(string segment)
        => segment.Equals("ValWTr", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetOnlyMeaningfulStructChild(ArMms.MmsDataValue value, out ArMms.MmsDataValue child)
    {
        child = value;
        if (value.Kind is not (ArMms.MmsDataKind.Structure or ArMms.MmsDataKind.Array))
            return false;

        var candidates = value.Children
            .Where(x => x.Kind is ArMms.MmsDataKind.Structure or ArMms.MmsDataKind.Array)
            .Where(x => FindQualityChild(x) == null || x.Children.Any(c => c.Kind is not ArMms.MmsDataKind.BitString))
            .ToArray();
        if (candidates.Length != 1)
            return false;

        child = candidates[0];
        return true;
    }

    private static bool TrySelectSemanticLeaf(
        ArMms.MmsDataValue value,
        string leaf,
        string dataType,
        string requestedReference,
        out ArMms.MmsDataValue? selected,
        out string reason)
    {
        selected = null;
        reason = string.Empty;
        if (value.Kind is not (ArMms.MmsDataKind.Structure or ArMms.MmsDataKind.Array))
            return false;

        var children = value.Children.ToArray();
        if (children.Length == 0)
            return false;

        if (leaf.Equals("q", StringComparison.OrdinalIgnoreCase))
        {
            selected = FindQualityChild(value);
            reason = "quality child selected by IEC 61850 quality bit-string shape";
            return selected != null;
        }

        if (leaf.Equals("t", StringComparison.OrdinalIgnoreCase))
        {
            selected = FindTimestampChild(value);
            reason = "timestamp child selected by MMS UTC/BinaryTime kind";
            return selected != null;
        }

        var payloadChildren = children
            .Where(x => !IsTimestampValue(x))
            .Where(x => !LooksLikeQualityBitString(x))
            .ToArray();
        if (payloadChildren.Length == 0)
            return false;

        if (leaf.Equals("general", StringComparison.OrdinalIgnoreCase) || dataType.Equals("Boolean", StringComparison.OrdinalIgnoreCase))
        {
            selected = payloadChildren.FirstOrDefault(x => x.Kind == ArMms.MmsDataKind.Boolean)
                ?? payloadChildren.FirstOrDefault(x => x.Kind is ArMms.MmsDataKind.Integer or ArMms.MmsDataKind.Unsigned);
            reason = "Boolean/status child selected after excluding q/t siblings";
            return selected != null;
        }

        if (IsDbposHint(dataType, requestedReference))
        {
            selected = payloadChildren.FirstOrDefault(IsShortStatusBitString)
                ?? payloadChildren.FirstOrDefault(x => x.Kind is ArMms.MmsDataKind.Integer or ArMms.MmsDataKind.Unsigned)
                ?? payloadChildren.FirstOrDefault(x => x.Kind == ArMms.MmsDataKind.Boolean)
                ?? payloadChildren.FirstOrDefault(x => x.Kind == ArMms.MmsDataKind.BitString);
            reason = "Dbpos/status child selected after excluding quality/timestamp siblings";
            return selected != null;
        }

        if (leaf.Equals("posVal", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("ctlVal", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("Int32", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("UInt32", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("Integer", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("Enum", StringComparison.OrdinalIgnoreCase))
        {
            selected = payloadChildren.FirstOrDefault(x => x.Kind is ArMms.MmsDataKind.Integer or ArMms.MmsDataKind.Unsigned)
                ?? payloadChildren.FirstOrDefault(x => x.Kind == ArMms.MmsDataKind.Boolean)
                ?? payloadChildren.FirstOrDefault(IsShortStatusBitString)
                ?? payloadChildren.FirstOrDefault(x => x.Kind == ArMms.MmsDataKind.BitString);
            reason = "integer/enum status child selected after excluding q/t siblings";
            return selected != null;
        }

        selected = payloadChildren.FirstOrDefault(x => x.Kind is not (ArMms.MmsDataKind.Structure or ArMms.MmsDataKind.Array));
        reason = "first scalar payload child selected after excluding q/t siblings";
        return selected != null;
    }

    private static ArMms.MmsDataValue? FindQualityChild(ArMms.MmsDataValue value)
    {
        if (LooksLikeQualityBitString(value))
            return value;
        if (value.Kind is not (ArMms.MmsDataKind.Structure or ArMms.MmsDataKind.Array))
            return null;

        return value.Children.FirstOrDefault(LooksLikeQualityBitString)
            ?? value.Children.Select(FindQualityChild).FirstOrDefault(x => x != null);
    }

    private static ArMms.MmsDataValue? FindTimestampChild(ArMms.MmsDataValue value)
    {
        if (IsTimestampValue(value))
            return value;
        if (value.Kind is not (ArMms.MmsDataKind.Structure or ArMms.MmsDataKind.Array))
            return null;

        return value.Children.FirstOrDefault(IsTimestampValue)
            ?? value.Children.Select(FindTimestampChild).FirstOrDefault(x => x != null);
    }

    private static bool IsTimestampValue(ArMms.MmsDataValue value)
        => value.Kind is ArMms.MmsDataKind.UtcTime or ArMms.MmsDataKind.BinaryTime ||
           (value.Kind == ArMms.MmsDataKind.Unknown && value.UnknownTagNumber == 12);

    private static bool LooksLikeQualityBitString(ArMms.MmsDataValue value)
        => value.Kind == ArMms.MmsDataKind.BitString &&
           BitStringBitLength(value) >= 12 &&
           Iec61850QualityDecoder.Decode(value).IsDecoded;

    private static bool IsShortStatusBitString(ArMms.MmsDataValue value)
        => value.Kind == ArMms.MmsDataKind.BitString &&
           BitStringBitLength(value) > 0 &&
           BitStringBitLength(value) < 12;

    private static int BitStringBitLength(ArMms.MmsDataValue value)
    {
        if (value.Kind != ArMms.MmsDataKind.BitString || value.RawValue.Count == 0)
            return 0;
        var unusedBits = value.RawValue[0];
        var dataBytes = Math.Max(0, value.RawValue.Count - 1);
        return Math.Max(0, dataBytes * 8 - unusedBits);
    }

    private sealed record MmsValueProjection(
        ArMms.MmsDataValue? Value,
        string Quality,
        string Timestamp,
        string Description);

    private static MmsValueProjection SelectProjectedValue(ArMms.MmsDataValue? value, string dataType, string requestedReference)
    {
        if (value == null)
            return new MmsValueProjection(null, string.Empty, string.Empty, "null");

        var quality = DecodeQuality(value);
        var timestamp = DecodeTimestamp(value);
        if (value.Kind is not (ArMms.MmsDataKind.Structure or ArMms.MmsDataKind.Array))
            return new MmsValueProjection(value, quality, timestamp, "scalar");

        var r = NormalizeReference(requestedReference);
        var hint = dataType ?? string.Empty;

        if (IsQualityHint(hint, requestedReference))
        {
            var qValue = FindFirst(value, v => Iec61850QualityDecoder.Decode(v).IsDecoded);
            return new MmsValueProjection(qValue ?? value, quality, timestamp, "projected-quality");
        }

        if (IsTimestampHint(hint, requestedReference))
        {
            var tValue = FindFirst(value, v => Iec61850TimestampDecoder.Decode(v).IsDecoded);
            return new MmsValueProjection(tValue ?? value, quality, timestamp, "projected-timestamp");
        }

        if (r.Contains(".tapchg.") && (r.EndsWith(".valwtr.posval") || r.EndsWith(".posval") || r.EndsWith(".stval") || hint.Equals("Int32", StringComparison.OrdinalIgnoreCase)))
        {
            var branch = SelectValueBranch(value, requestedReference);
            var tap = FindFirstInteger(branch) ?? FindFirstFloating(branch) ?? FindFirstScalar(branch);
            return new MmsValueProjection(tap ?? value, quality, timestamp, "projected-avr-tapchg-posval");
        }

        if (r.EndsWith(".stval"))
        {
            var branch = SelectValueBranch(value, requestedReference);
            var selected = SelectStatusScalar(branch, hint);
            return new MmsValueProjection(selected ?? value, quality, timestamp, "projected-stval");
        }

        if (r.EndsWith(".f") || r.EndsWith(".mag.f") || hint.Equals("Float32", StringComparison.OrdinalIgnoreCase))
        {
            var branch = SelectValueBranch(value, requestedReference);
            var selected = FindFirstFloating(branch) ?? FindFirstInteger(branch);
            return new MmsValueProjection(selected ?? value, quality, timestamp, "projected-analogue");
        }

        if (IsDbposHint(hint, requestedReference))
        {
            var selected = FindFirst(value, v => v.Kind is ArMms.MmsDataKind.BitString or ArMms.MmsDataKind.Integer or ArMms.MmsDataKind.Unsigned);
            return new MmsValueProjection(selected ?? value, quality, timestamp, "projected-dbpos");
        }

        if (hint.Equals("Boolean", StringComparison.OrdinalIgnoreCase))
        {
            var branch = SelectValueBranch(value, requestedReference);
            var selected = FindFirst(branch, v => v.Kind == ArMms.MmsDataKind.Boolean);
            return new MmsValueProjection(selected ?? value, quality, timestamp, "projected-boolean");
        }

        if (hint.Equals("Int32", StringComparison.OrdinalIgnoreCase) || hint.Equals("UInt32", StringComparison.OrdinalIgnoreCase))
        {
            var branch = SelectValueBranch(value, requestedReference);
            var selected = FindFirstInteger(branch);
            return new MmsValueProjection(selected ?? value, quality, timestamp, "projected-integer");
        }

        return new MmsValueProjection(FindFirstScalar(value) ?? value, quality, timestamp, "projected-first-meaningful-scalar");
    }

    private static object? ConvertProjectedValue(ArMms.MmsDataValue? value, string dataType, string reference)
    {
        if (value == null)
            return null;

        var hint = dataType ?? string.Empty;

        if (IsQualityHint(hint, reference))
        {
            var quality = Iec61850QualityDecoder.Decode(value);
            return quality.IsDecoded ? quality.Validity : ArMms.MmsDataValueRenderer.ToCompactString(value, reference);
        }

        if (IsTimestampHint(hint, reference))
        {
            var timestamp = Iec61850TimestampDecoder.Decode(value);
            return timestamp.IsDecoded ? timestamp.DisplayTime : ArMms.MmsDataValueRenderer.ToCompactString(value, reference);
        }

        if (reference.EndsWith(".ctlModel", StringComparison.OrdinalIgnoreCase))
            return Iec61850EnumValueDecoder.DecodeControlModel(value);

        if (IsDbposHint(hint, reference))
            return DecodeDbposToGatewayValue(value);

        if (TryDecodeStandardEnum(value, reference, out var enumText))
            return enumText;

        switch (value.Kind)
        {
            case ArMms.MmsDataKind.Boolean:
                return value.Value is bool b && b;
            case ArMms.MmsDataKind.Integer:
                return Convert.ToInt64(value.Value, CultureInfo.InvariantCulture);
            case ArMms.MmsDataKind.Unsigned:
            {
                var unsigned = Convert.ToUInt64(value.Value, CultureInfo.InvariantCulture);
                return unsigned <= long.MaxValue ? (long)unsigned : unsigned.ToString(CultureInfo.InvariantCulture);
            }
            case ArMms.MmsDataKind.FloatingPoint:
                return Convert.ToDouble(value.Value, CultureInfo.InvariantCulture);
            case ArMms.MmsDataKind.VisibleString:
            case ArMms.MmsDataKind.MmsString:
                return Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            case ArMms.MmsDataKind.BitString:
                return ArMms.MmsDataValueRenderer.ToCompactString(value, reference);
            case ArMms.MmsDataKind.UtcTime:
            case ArMms.MmsDataKind.BinaryTime:
            case ArMms.MmsDataKind.OctetString:
                return ArMms.MmsDataCodec.ToDisplayString(value);
            default:
                return ArMms.MmsDataValueRenderer.ToCompactString(value, reference);
        }
    }

    private static string FormatProjectedDisplay(object? value, string dataType)
    {
        if (value == null)
            return "-";
        if (value is string text)
            return text;
        return MockIec61850Client.Format(value, dataType, string.Empty);
    }

    private static string DecodeQuality(ArMms.MmsDataValue value)
    {
        var quality = Iec61850QualityDecoder.Decode(value);
        return quality.IsDecoded ? quality.Validity : string.Empty;
    }

    private static string DecodeTimestamp(ArMms.MmsDataValue value)
    {
        var timestamp = Iec61850TimestampDecoder.Decode(value);
        return timestamp.IsDecoded ? timestamp.DisplayTime : string.Empty;
    }

    private static ArMms.MmsDataValue SelectValueBranch(ArMms.MmsDataValue value, string requestedReference)
    {
        if (value.Kind is not (ArMms.MmsDataKind.Structure or ArMms.MmsDataKind.Array) || value.Children.Count == 0)
            return value;

        var r = NormalizeReference(requestedReference);
        if (r.EndsWith(".valwtr.posval") || r.EndsWith(".posval"))
            return FirstChildOrSelf(FirstChildOrSelf(value));

        if (r.EndsWith(".cval.mag.f") || r.EndsWith(".instcval.mag.f"))
            return FirstChildOrSelf(FirstChildOrSelf(FirstChildOrSelf(value)));

        if (r.EndsWith(".mag.f") || r.EndsWith(".instmag.f") || r.EndsWith(".ang.f"))
            return FirstChildOrSelf(FirstChildOrSelf(value));

        if (r.EndsWith(".stval") || r.EndsWith(".general"))
            return FirstChildOrSelf(value);

        return value;
    }

    private static ArMms.MmsDataValue FirstChildOrSelf(ArMms.MmsDataValue value)
        => value.Kind is ArMms.MmsDataKind.Structure or ArMms.MmsDataKind.Array
            ? value.Children.FirstOrDefault() ?? value
            : value;

    private static ArMms.MmsDataValue? SelectStatusScalar(ArMms.MmsDataValue value, string dataType)
    {
        if (dataType.Equals("Boolean", StringComparison.OrdinalIgnoreCase))
            return FindFirst(value, v => v.Kind == ArMms.MmsDataKind.Boolean) ?? FindFirstInteger(value);

        return FindFirstInteger(value) ??
               FindFirst(value, v => v.Kind == ArMms.MmsDataKind.BitString) ??
               FindFirst(value, v => v.Kind == ArMms.MmsDataKind.Boolean) ??
               FindFirstScalar(value);
    }

    private static ArMms.MmsDataValue? FindFirstInteger(ArMms.MmsDataValue value)
        => FindFirst(value, v => v.Kind is ArMms.MmsDataKind.Integer or ArMms.MmsDataKind.Unsigned);

    private static ArMms.MmsDataValue? FindFirstFloating(ArMms.MmsDataValue value)
        => FindFirst(value, v => v.Kind == ArMms.MmsDataKind.FloatingPoint);

    private static ArMms.MmsDataValue? FindFirst(ArMms.MmsDataValue value, Func<ArMms.MmsDataValue, bool> predicate)
    {
        if (predicate(value))
            return value;

        if (value.Kind is not (ArMms.MmsDataKind.Structure or ArMms.MmsDataKind.Array))
            return null;

        foreach (var child in value.Children)
        {
            var match = FindFirst(child, predicate);
            if (match != null)
                return match;
        }

        return null;
    }

    private static bool TryDecodeStandardEnum(ArMms.MmsDataValue value, string reference, out string text)
    {
        text = string.Empty;
        if (value.Kind is not (ArMms.MmsDataKind.Integer or ArMms.MmsDataKind.Unsigned))
            return false;

        if (!TryParseReferenceParts(reference, out var logicalNodeClass, out var dataObjectName, out var attributeName))
            return false;

        if (!Iec61850StandardEnumRegistry.TryResolve(logicalNodeClass, dataObjectName, "INS", attributeName, out var enumDefinition) &&
            !Iec61850StandardEnumRegistry.TryResolve(logicalNodeClass, dataObjectName, "INC", attributeName, out enumDefinition) &&
            !Iec61850StandardEnumRegistry.TryResolve(logicalNodeClass, dataObjectName, "ENC", attributeName, out enumDefinition))
        {
            return false;
        }

        var numeric = value.Kind == ArMms.MmsDataKind.Integer
            ? Convert.ToInt64(value.Value, CultureInfo.InvariantCulture)
            : unchecked((long)Convert.ToUInt64(value.Value, CultureInfo.InvariantCulture));
        var match = enumDefinition.Values.FirstOrDefault(v => v.Ord == numeric);
        if (match == null)
            return false;

        text = match.Symbol;
        return true;
    }

    private static bool TryParseReferenceParts(string reference, out string logicalNodeClass, out string dataObjectName, out string attributeName)
    {
        logicalNodeClass = string.Empty;
        dataObjectName = string.Empty;
        attributeName = string.Empty;

        var text = (reference ?? string.Empty).Trim().Replace('$', '.');
        var slash = text.IndexOf('/');
        if (slash < 0 || slash >= text.Length - 1)
            return false;

        var path = text[(slash + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (path.Length < 2)
            return false;

        logicalNodeClass = SignalDefinition.DetectLogicalNodeClass(path[0]);
        dataObjectName = path[1];
        attributeName = path[^1];
        return !string.IsNullOrWhiteSpace(dataObjectName) && !string.IsNullOrWhiteSpace(attributeName);
    }

    private static object DecodeDbposToGatewayValue(ArMms.MmsDataValue value)
    {
        if (value.Kind == ArMms.MmsDataKind.BitString)
        {
            var raw = value.RawValue.ToArray();
            if (raw.Length >= 2)
            {
                var code = (raw[1] >> 6) & 0x03;
                return code;
            }
        }

        if (value.Kind == ArMms.MmsDataKind.Integer)
        {
            var numeric = Convert.ToInt64(value.Value, CultureInfo.InvariantCulture);
            if (numeric is >= 0 and <= 3)
                return (int)numeric;
        }

        if (value.Kind == ArMms.MmsDataKind.Unsigned)
        {
            var numeric = Convert.ToUInt64(value.Value, CultureInfo.InvariantCulture);
            if (numeric <= 3)
                return (int)numeric;
        }

        if (value.Kind == ArMms.MmsDataKind.Boolean)
            return value.Value is bool b && b ? 2 : 1;

        return Iec61850EnumValueDecoder.DecodeDbpos(value);
    }

    private static bool TrySelectStructuredChild(ArMms.MmsDataValue value, string reference, string dataType, out ArMms.MmsDataValue? child)
    {
        child = null;
        if (value.Children.Count == 0)
            return false;

        var leaf = LastSegment(reference);
        if (leaf.Equals("stVal", StringComparison.OrdinalIgnoreCase))
        {
            var qIndex = FindQualityIndex(value.Children);
            var index = qIndex > 0 ? qIndex - 1 : 0;
            child = value.Children[Math.Clamp(index, 0, value.Children.Count - 1)];
            return true;
        }

        if (leaf.Equals("q", StringComparison.OrdinalIgnoreCase))
        {
            var qIndex = FindQualityIndex(value.Children);
            if (qIndex >= 0)
            {
                child = value.Children[qIndex];
                return true;
            }
        }

        if (leaf.Equals("t", StringComparison.OrdinalIgnoreCase))
        {
            var tIndex = FindTimestampIndex(value.Children);
            if (tIndex >= 0)
            {
                child = value.Children[tIndex];
                return true;
            }
        }

        if (leaf.Equals("f", StringComparison.OrdinalIgnoreCase))
        {
            var scalar = FlattenScalars(value).FirstOrDefault(x => x.Kind == ArMms.MmsDataKind.FloatingPoint);
            if (scalar != null)
            {
                child = scalar;
                return true;
            }
        }

        if (IsDbposHint(dataType, reference))
        {
            var dbpos = value.Children.FirstOrDefault(x =>
                x.Kind is ArMms.MmsDataKind.BitString or ArMms.MmsDataKind.Integer or ArMms.MmsDataKind.Unsigned or ArMms.MmsDataKind.Boolean);
            if (dbpos != null)
            {
                child = dbpos;
                return true;
            }
        }

        return false;
    }

    private static ArMms.MmsDataValue? FindFirstScalar(ArMms.MmsDataValue value)
        => FlattenScalars(value).FirstOrDefault();

    private static IEnumerable<ArMms.MmsDataValue> FlattenScalars(ArMms.MmsDataValue value)
    {
        if (value.Kind is not (ArMms.MmsDataKind.Structure or ArMms.MmsDataKind.Array))
        {
            yield return value;
            yield break;
        }

        foreach (var child in value.Children)
        {
            foreach (var scalar in FlattenScalars(child))
                yield return scalar;
        }
    }

    private static int FindQualityIndex(IReadOnlyList<ArMms.MmsDataValue> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (Iec61850QualityDecoder.Decode(values[i]).IsDecoded)
                return i;
        }

        return -1;
    }

    private static int FindTimestampIndex(IReadOnlyList<ArMms.MmsDataValue> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (Iec61850TimestampDecoder.Decode(values[i]).IsDecoded)
                return i;
        }

        return -1;
    }

    private static bool IsQualityHint(string dataType, string reference)
        => dataType.Equals("Quality", StringComparison.OrdinalIgnoreCase) ||
           reference.EndsWith(".q", StringComparison.OrdinalIgnoreCase);

    private static bool IsTimestampHint(string dataType, string reference)
        => dataType.Equals("Timestamp", StringComparison.OrdinalIgnoreCase) ||
           reference.EndsWith(".t", StringComparison.OrdinalIgnoreCase);

    private static bool IsDbposHint(string dataType, string reference)
        => dataType.Equals("Dbpos", StringComparison.OrdinalIgnoreCase) ||
           reference.EndsWith(".Pos.stVal", StringComparison.OrdinalIgnoreCase) ||
           reference.Contains(".Pos.stVal", StringComparison.OrdinalIgnoreCase);

    private static string LastSegment(string reference)
    {
        var text = (reference ?? string.Empty).Replace('$', '.');
        var slash = text.LastIndexOf('/');
        var start = slash >= 0 ? slash + 1 : 0;
        var dot = text.LastIndexOf('.');
        return dot >= start && dot < text.Length - 1 ? text[(dot + 1)..] : text[start..];
    }
}
