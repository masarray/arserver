using System.Collections.Concurrent;
using System.Globalization;
using Ari61850Bridge.Models;

namespace Ari61850Bridge.Services;

/// <summary>
/// Monitoring-first IEC 61850 runtime. Each IED owns an isolated native MMS session.
/// Reporting is preferred when an RCB/DataSet plan can be armed; cyclic MMS reads remain
/// active as a fallback and for periodic integrity refresh.
/// </summary>
public sealed class Iec61850MonitorRuntime : IAsyncDisposable
{
    private sealed class RuntimePointState
    {
        public string Value { get; set; } = "-";
        public string Quality { get; set; } = "Unknown";
        public string DeviceTimestamp { get; set; } = "-";
        public DateTime LastUpdateUtc { get; set; } = DateTime.MinValue;
        public long Sequence { get; set; }
        public DateTime NextPollUtc { get; set; } = DateTime.MinValue;
        public string SourceMode { get; set; } = "Waiting";
        public string Reason { get; set; } = "-";
        public bool HasValue { get; set; }
        public bool StalePublished { get; set; }
    }

    private sealed class DeviceSession
    {
        public required Iec61850MonitorDevice Device { get; init; }
        public required NativeIec61850Client Client { get; init; }
        public CancellationTokenSource MonitorCancellation { get; set; } = new();
        public Task? MonitorTask { get; set; }
        public Dictionary<string, Iec61850MonitorPoint> Points { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RuntimePointState> States { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ReportControlPlan> ActiveReportPlans { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> PointPlanIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly ConcurrentDictionary<string, DeviceSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private long _eventSequence;

    public event Action<DiagnosticEntry>? Diagnostic;
    public event Action<Iec61850PointSnapshot>? PointUpdated;
    public event Action<Iec61850EventEntry>? EventRaised;

    public async Task<IReadOnlyList<SignalDefinition>> ConnectAndDiscoverAsync(
        Iec61850MonitorDevice device,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrWhiteSpace(device.IpAddress))
            throw new ArgumentException("IED IP address is required.", nameof(device));
        if (device.Port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(device), "MMS port must be between 1 and 65535.");

        await StopDeviceAsync(device.DeviceId).ConfigureAwait(false);

        var client = new NativeIec61850Client();
        var session = new DeviceSession { Device = device, Client = client };
        _sessions[device.DeviceId] = session;

        device.IsBusy = true;
        device.Status = "Connecting";
        device.Detail = $"Opening IEC 61850 MMS association to {device.IpAddress}:{device.Port}.";
        Log("INFO", device.Name, $"Connecting to {device.IpAddress}:{device.Port} over TCP/TPKT/COTP/ACSE/MMS.");

        try
        {
            await client.ConnectAsync(device.IpAddress, device.Port, cancellationToken).ConfigureAwait(false);
            if (!client.IsConnected)
            {
                device.Status = "Connection failed";
                device.Detail = string.IsNullOrWhiteSpace(client.LastErrorMessage)
                    ? "The IED did not complete ACSE/MMS association."
                    : client.LastErrorMessage;
                throw new InvalidOperationException(device.Detail);
            }

            device.Status = "MMS associated";
            device.Detail = "Association ready. Scanning live MMS schema and report capabilities.";
            Log("INFO", device.Name, $"MMS associated. Native state={client.NativeState}. Starting schema discovery.");

            var discovered = await client.DiscoverSignalsAsync(cancellationToken).ConfigureAwait(false);
            var values = discovered
                .Where(signal => signal.CanPublishAsSignal)
                .GroupBy(signal => NormalizeReference(signal.ObjectReference), StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(signal => signal.IsScadaCoreSignal)
                    .ThenByDescending(signal => signal.Confidence.Equals("High", StringComparison.OrdinalIgnoreCase))
                    .First())
                .OrderBy(signal => signal.SortPriority)
                .ThenBy(signal => signal.LogicalNode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(signal => signal.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var signal in values)
            {
                signal.IsSelected = signal.IsScadaCoreSignal;
                signal.ProbeStatus = "Discovered / not monitored";
            }

            device.Status = values.Count > 0 ? "Scan complete" : "No readable signal";
            device.Detail = values.Count > 0
                ? $"Found {values.Count} readable value leaf/leaves. Select points, then start monitoring."
                : "MMS association succeeded, but no ST/MX value leaf passed the monitor filter.";
            Log(values.Count > 0 ? "INFO" : "WARN", device.Name,
                $"Discovery completed: {values.Count} readable value leaf/leaves. {client.LastDiscoverySummary}");
            return values;
        }
        catch
        {
            if (!client.IsConnected)
                await RemoveSessionAsync(device.DeviceId, session).ConfigureAwait(false);
            throw;
        }
        finally
        {
            device.IsBusy = false;
            device.RefreshComputed();
        }
    }

    public async Task<IReadOnlyList<Iec61850MonitorPoint>> StartMonitoringAsync(
        Iec61850MonitorDevice device,
        IEnumerable<SignalDefinition> selectedSignals,
        int pollingIntervalMs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(selectedSignals);

        if (!_sessions.TryGetValue(device.DeviceId, out var session) || !session.Client.IsConnected)
            throw new InvalidOperationException($"{device.Name} is not connected. Run Connect & Scan first.");

        var selected = selectedSignals
            .Where(signal => signal.IsSelected && signal.CanPublishAsSignal)
            .GroupBy(signal => NormalizeReference(signal.ObjectReference), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (selected.Count == 0)
            throw new InvalidOperationException("No readable IEC 61850 signal is selected.");

        await StopMonitoringSessionAsync(session).ConfigureAwait(false);
        session.MonitorCancellation.Dispose();
        session.MonitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        session.Points.Clear();
        session.States.Clear();
        session.ActiveReportPlans.Clear();
        session.PointPlanIds.Clear();

        var safePollMs = Math.Clamp(pollingIntervalMs <= 0 ? 1000 : pollingIntervalMs, 50, 600000);
        var bindings = new List<BindingItem>();

        foreach (var signal in selected)
        {
            var point = CreatePoint(device, signal, safePollMs);
            session.Points[point.PointKey] = point;
            session.States[point.PointKey] = new RuntimePointState
            {
                NextPollUtc = DateTime.UtcNow,
                SourceMode = signal.IsReportCapable ? "Report pending / polling fallback" : "MMS polling",
                Reason = signal.IsReportCapable ? "report plan pending" : "cyclic"
            };
            bindings.Add(CreatePlanningBinding(device, signal, safePollMs));
        }

        var relay = BuildPlanningRelay(device, selected);
        var relayIndex = new Dictionary<string, RelayEndpointView>(StringComparer.OrdinalIgnoreCase)
        {
            [device.DeviceId] = relay
        };
        var plans = new ReportRuntimePlanner(relayIndex).BuildPlans(bindings);
        await StartReportPlansAsync(session, plans, session.MonitorCancellation.Token).ConfigureAwait(false);

        device.IsMonitoring = true;
        device.Status = "Monitoring";
        device.Detail = session.ActiveReportPlans.Count > 0
            ? $"{session.Points.Count} point(s): report subscription active with cyclic polling fallback."
            : $"{session.Points.Count} point(s): cyclic MMS polling active.";
        device.RefreshComputed();

        Log("INFO", device.Name,
            $"Monitoring started for {session.Points.Count} point(s). Reports active={session.ActiveReportPlans.Count}; polling={safePollMs} ms; q/t sidecar enabled.");

        session.MonitorTask = Task.Run(
            () => MonitorLoopAsync(session, session.MonitorCancellation.Token),
            CancellationToken.None);

        return session.Points.Values
            .OrderBy(point => point.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(point => point.SignalName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task StopMonitoringAsync(string deviceId)
    {
        if (!_sessions.TryGetValue(deviceId, out var session))
            return;

        await StopMonitoringSessionAsync(session).ConfigureAwait(false);
        session.Device.IsMonitoring = false;
        session.Device.Status = session.Client.IsConnected ? "MMS associated" : "Disconnected";
        session.Device.Detail = session.Client.IsConnected
            ? "Monitoring stopped. The IEC 61850 association remains available for a new selection."
            : "Session stopped.";
        session.Device.RefreshComputed();
        Log("INFO", session.Device.Name, "Live monitoring stopped.");
    }

    public async Task StopDeviceAsync(string deviceId)
    {
        if (!_sessions.TryRemove(deviceId, out var session))
            return;

        await StopMonitoringSessionAsync(session).ConfigureAwait(false);
        await session.Client.DisposeAsync().ConfigureAwait(false);
        session.Device.IsMonitoring = false;
        session.Device.Status = "Disconnected";
        session.Device.Detail = "IEC 61850 session closed.";
        session.Device.RefreshComputed();
        Log("INFO", session.Device.Name, "IEC 61850 session disconnected.");
    }

    private async Task StartReportPlansAsync(
        DeviceSession session,
        IReadOnlyList<ReportControlPlan> plans,
        CancellationToken cancellationToken)
    {
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await session.Client.StartReportMonitorAsync(plan, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    Log("WARN", session.Device.Name,
                        $"Report plan blocked for {plan.DisplayReference}. Polling fallback remains active. {result.Message}");
                    foreach (var warning in result.Warnings.Take(3))
                        Log("WARN", session.Device.Name, warning);
                    continue;
                }

                session.ActiveReportPlans[plan.PlanId] = plan;
                foreach (var binding in plan.Bindings)
                {
                    var key = BuildPointKey(session.Device.DeviceId, binding.IecReference);
                    if (session.Points.ContainsKey(key))
                        session.PointPlanIds[key] = plan.PlanId;
                }

                Log("INFO", session.Device.Name,
                    $"Report monitor active: {plan.DisplayReference}; members={result.MemberCount}; writes={result.WriteStepCount}. {result.SubscriptionSummary}");
                foreach (var warning in result.Warnings.Take(3))
                    Log("WARN", session.Device.Name, warning);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log("WARN", session.Device.Name,
                    $"Report plan failed for {plan.DisplayReference}; polling fallback remains active. {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task MonitorLoopAsync(DeviceSession session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReceiveReportSlicesAsync(session, cancellationToken).ConfigureAwait(false);
                await PollDuePointsAsync(session, cancellationToken).ConfigureAwait(false);
                PublishAgeSnapshots(session);

                var delay = session.ActiveReportPlans.Count > 0 ? 20 : 50;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("WARN", session.Device.Name, $"Monitor loop recovered from {ex.GetType().Name}: {ex.Message}");
                try
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ReceiveReportSlicesAsync(DeviceSession session, CancellationToken cancellationToken)
    {
        foreach (var plan in session.ActiveReportPlans.Values.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slice = await session.Client.ReceiveReportMonitorSliceAsync(
                plan.PlanId,
                TimeSpan.FromMilliseconds(8),
                cancellationToken).ConfigureAwait(false);

            foreach (var update in slice.Updates)
            {
                var point = FindPointForReportReference(session, update.Reference);
                if (point == null)
                    continue;

                var display = MockIec61850Client.Format(update.Value, point.DataType, point.Unit);
                if (LooksLikeReferenceEcho(display, update.Reference, point.ObjectReference))
                    continue;

                ApplyValueUpdate(
                    session,
                    point,
                    display,
                    string.IsNullOrWhiteSpace(update.Quality) ? "Good" : update.Quality,
                    string.IsNullOrWhiteSpace(update.Timestamp) ? "-" : update.Timestamp,
                    "RCB Report",
                    string.IsNullOrWhiteSpace(update.Reason) ? "data-change" : update.Reason,
                    update.UpdatedAt == default ? DateTime.UtcNow : update.UpdatedAt.UtcDateTime,
                    "Live / report");
            }

            foreach (var warning in slice.Warnings.Take(2))
                Log("WARN", session.Device.Name, warning);
        }
    }

    private async Task PollDuePointsAsync(DeviceSession session, CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var due = session.Points.Values
            .Where(point => session.States[point.PointKey].NextPollUtc <= nowUtc)
            .OrderByDescending(IsFastPoint)
            .ThenBy(point => session.States[point.PointKey].NextPollUtc)
            .Take(12)
            .ToList();

        foreach (var point in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = session.States[point.PointKey];
            var reportCovered = session.PointPlanIds.ContainsKey(point.PointKey);
            var interval = reportCovered
                ? Math.Max(point.PollingIntervalMs * 10, 5000)
                : point.PollingIntervalMs;
            state.NextPollUtc = DateTime.UtcNow.AddMilliseconds(interval);

            try
            {
                var signal = new SignalDefinition
                {
                    Name = point.SignalName,
                    ObjectReference = point.ObjectReference,
                    FunctionalConstraint = point.FunctionalConstraint,
                    DataType = point.DataType,
                    Category = point.Category,
                    Unit = point.Unit
                };

                var resolved = await IecSignalReadResolver.ReadAsync(session.Client, signal, cancellationToken).ConfigureAwait(false);
                if (resolved == null)
                {
                    EmitStatusSnapshot(session, point, state, reportCovered
                        ? "Awaiting report / polling read pending"
                        : "MMS read returned no value");
                    continue;
                }

                if (resolved.UsedAlternateReference(point.ObjectReference))
                    Log("INFO", session.Device.Name, $"Smart leaf resolver read {point.SignalName} through {resolved.EffectiveReference} while preserving the configured IEC reference.");

                var rich = resolved.Value as Iec61850ReadValue;
                var raw = Iec61850ReadValue.Unwrap(resolved.Value);
                var display = MockIec61850Client.Format(raw, point.DataType, point.Unit);
                var quality = rich?.HasQuality == true ? rich.Quality : state.Quality;
                var deviceTimestamp = rich?.HasDeviceTimestamp == true ? rich.DeviceTimestamp : state.DeviceTimestamp;

                if (rich?.HasQuality != true || rich?.HasDeviceTimestamp != true)
                {
                    var companions = await ReadCompanionAttributesAsync(
                        session.Client,
                        point,
                        quality,
                        deviceTimestamp,
                        cancellationToken).ConfigureAwait(false);
                    quality = companions.Quality;
                    deviceTimestamp = companions.DeviceTimestamp;
                }

                ApplyValueUpdate(
                    session,
                    point,
                    display,
                    NormalizeQuality(quality),
                    string.IsNullOrWhiteSpace(deviceTimestamp) ? "-" : deviceTimestamp,
                    reportCovered ? "MMS integrity poll" : "MMS Polling",
                    reportCovered ? "integrity / report fallback" : "cyclic",
                    DateTime.UtcNow,
                    reportCovered ? "Live / report fallback" : "Live / polling");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EmitStatusSnapshot(session, point, state, $"Read error: {ex.Message}", "Bad");
            }
        }
    }

    private void ApplyValueUpdate(
        DeviceSession session,
        Iec61850MonitorPoint point,
        string display,
        string quality,
        string deviceTimestamp,
        string sourceMode,
        string reason,
        DateTime timestampUtc,
        string status)
    {
        var state = session.States[point.PointKey];
        var oldValue = state.Value;
        var changed = state.HasValue && HasMeaningfulEdge(point, oldValue, display);

        state.HasValue = true;
        state.StalePublished = false;
        state.Value = display;
        state.Quality = quality;
        state.DeviceTimestamp = deviceTimestamp;
        state.LastUpdateUtc = timestampUtc;
        state.SourceMode = sourceMode;
        state.Reason = reason;
        if (changed)
            state.Sequence++;

        PointUpdated?.Invoke(new Iec61850PointSnapshot
        {
            Point = point,
            Value = display,
            Quality = quality,
            DeviceTimestamp = deviceTimestamp,
            LocalTimestamp = timestampUtc.ToLocalTime(),
            SourceMode = sourceMode,
            Reason = reason,
            Status = status,
            Sequence = state.Sequence,
            AgeMs = 0
        });

        if (!changed)
            return;

        var entry = new Iec61850EventEntry
        {
            Sequence = Interlocked.Increment(ref _eventSequence),
            LocalTimestamp = timestampUtc.ToLocalTime(),
            DeviceTimestamp = deviceTimestamp,
            DeviceName = point.DeviceName,
            IpAddress = point.IpAddress,
            SignalName = point.SignalName,
            ObjectReference = point.ObjectReference,
            OldValue = oldValue,
            NewValue = display,
            Quality = quality,
            SourceMode = sourceMode,
            Reason = reason
        };
        EventRaised?.Invoke(entry);
        Log("EVENT", point.DeviceName,
            $"{point.SignalName}: {oldValue} → {display}; q={quality}; source={sourceMode}; reason={reason}; ref={point.ObjectReference}");
    }

    private void EmitStatusSnapshot(
        DeviceSession session,
        Iec61850MonitorPoint point,
        RuntimePointState state,
        string status,
        string? quality = null)
    {
        PointUpdated?.Invoke(new Iec61850PointSnapshot
        {
            Point = point,
            Value = state.Value,
            Quality = quality ?? (state.HasValue ? state.Quality : "Pending"),
            DeviceTimestamp = state.DeviceTimestamp,
            LocalTimestamp = state.LastUpdateUtc == DateTime.MinValue ? DateTime.Now : state.LastUpdateUtc.ToLocalTime(),
            SourceMode = state.SourceMode,
            Reason = state.Reason,
            Status = status,
            Sequence = state.Sequence,
            AgeMs = state.LastUpdateUtc == DateTime.MinValue
                ? 0
                : (int)Math.Clamp((DateTime.UtcNow - state.LastUpdateUtc).TotalMilliseconds, 0, int.MaxValue)
        });
    }

    private void PublishAgeSnapshots(DeviceSession session)
    {
        var nowUtc = DateTime.UtcNow;
        foreach (var point in session.Points.Values)
        {
            var state = session.States[point.PointKey];
            if (!state.HasValue || state.LastUpdateUtc == DateTime.MinValue)
                continue;

            var ageMs = (int)Math.Clamp((nowUtc - state.LastUpdateUtc).TotalMilliseconds, 0, int.MaxValue);
            var staleLimit = Math.Max(point.PollingIntervalMs * 15, 10000);
            if (ageMs < staleLimit || state.StalePublished)
                continue;

            state.StalePublished = true;
            PointUpdated?.Invoke(new Iec61850PointSnapshot
            {
                Point = point,
                Value = state.Value,
                Quality = "Questionable / stale",
                DeviceTimestamp = state.DeviceTimestamp,
                LocalTimestamp = state.LastUpdateUtc.ToLocalTime(),
                SourceMode = state.SourceMode,
                Reason = state.Reason,
                Status = $"Stale ({ageMs} ms)",
                Sequence = state.Sequence,
                AgeMs = ageMs
            });
        }
    }

    private static async Task<(string Quality, string DeviceTimestamp)> ReadCompanionAttributesAsync(
        IIec61850Client client,
        Iec61850MonitorPoint point,
        string currentQuality,
        string currentTimestamp,
        CancellationToken cancellationToken)
    {
        var quality = currentQuality;
        var timestamp = currentTimestamp;
        var qRef = BuildCompanionReference(point.ObjectReference, "q");
        var tRef = BuildCompanionReference(point.ObjectReference, "t");

        if (!string.IsNullOrWhiteSpace(qRef))
        {
            try
            {
                var value = await client.ReadValueAsync(qRef, point.FunctionalConstraint, "Quality", cancellationToken).ConfigureAwait(false);
                if (value != null)
                    quality = value.ToString() ?? currentQuality;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // q is optional for some object shapes; value monitoring must continue.
            }
        }

        if (!string.IsNullOrWhiteSpace(tRef))
        {
            try
            {
                var value = await client.ReadValueAsync(tRef, point.FunctionalConstraint, "Timestamp", cancellationToken).ConfigureAwait(false);
                if (value != null)
                    timestamp = value.ToString() ?? currentTimestamp;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // t is optional for some object shapes; local receive time remains available.
            }
        }

        return (quality, timestamp);
    }

    private async Task StopMonitoringSessionAsync(DeviceSession session)
    {
        try
        {
            session.MonitorCancellation.Cancel();
        }
        catch
        {
            // Ignore repeated cancellation.
        }

        if (session.MonitorTask != null)
        {
            try
            {
                await session.MonitorTask.ConfigureAwait(false);
            }
            catch
            {
                // Loop errors are already surfaced through diagnostics.
            }
        }

        try
        {
            var results = await session.Client.StopReportMonitorsAsync().ConfigureAwait(false);
            foreach (var result in results)
                Log(result.IsSuccess ? "INFO" : "WARN", session.Device.Name, result.Message);
        }
        catch (Exception ex)
        {
            Log("WARN", session.Device.Name, $"Report monitor cleanup: {ex.Message}");
        }

        session.MonitorTask = null;
        session.ActiveReportPlans.Clear();
        session.PointPlanIds.Clear();
        session.Points.Clear();
        session.States.Clear();
        session.MonitorCancellation.Dispose();
        session.MonitorCancellation = new CancellationTokenSource();
    }

    private static Iec61850MonitorPoint CreatePoint(
        Iec61850MonitorDevice device,
        SignalDefinition signal,
        int pollingIntervalMs)
    {
        return new Iec61850MonitorPoint
        {
            DeviceId = device.DeviceId,
            DeviceName = device.Name,
            IpAddress = device.IpAddress,
            SignalName = string.IsNullOrWhiteSpace(signal.Name) ? signal.ObjectReference : signal.Name,
            ObjectReference = signal.ObjectReference,
            FunctionalConstraint = signal.FunctionalConstraint,
            DataType = signal.DataType,
            Category = signal.Category,
            Unit = signal.Unit,
            DataSetReference = signal.DataSetReference,
            ReportControlReference = signal.ReportControlReference,
            PollingIntervalMs = pollingIntervalMs,
            SourceMode = signal.IsReportCapable ? "Report pending / polling fallback" : "MMS polling"
        };
    }

    private static BindingItem CreatePlanningBinding(
        Iec61850MonitorDevice device,
        SignalDefinition signal,
        int pollingIntervalMs)
    {
        return new BindingItem
        {
            IsEnabled = true,
            PublishToModbus = false,
            PublishToMqtt = false,
            RelayId = device.DeviceId,
            IedName = device.Name,
            RelayIpAddress = device.IpAddress,
            SignalName = signal.Name,
            IecReference = signal.ObjectReference,
            FunctionalConstraint = signal.FunctionalConstraint,
            IecDataType = signal.DataType,
            Category = signal.Category,
            Unit = signal.Unit,
            ReadMode = signal.IsReportCapable
                ? "Static report preferred + dynamic report allowed + polling fallback"
                : "MMS polling only",
            RcbMode = signal.IsReportCapable ? "Dynamic report allowed" : "Polling only",
            DataSetReference = signal.DataSetReference,
            ReportControlReference = signal.ReportControlReference,
            PollingIntervalMs = pollingIntervalMs,
            StaleTimeoutMs = Math.Max(pollingIntervalMs * 15, 10000)
        };
    }

    private static RelayEndpointView BuildPlanningRelay(
        Iec61850MonitorDevice device,
        IReadOnlyCollection<SignalDefinition> signals)
    {
        var relay = new RelayEndpointView
        {
            RelayId = device.DeviceId,
            IedName = device.Name,
            IpAddress = device.IpAddress,
            MmsPort = device.Port,
            ReportRuntimeMode = "Static report preferred + dynamic report allowed + polling fallback",
            Status = "MMS associated"
        };

        foreach (var signal in signals)
            relay.Signals.Add(signal);
        return relay;
    }

    private static bool IsFastPoint(Iec61850MonitorPoint point)
    {
        var category = point.Category ?? string.Empty;
        var type = point.DataType ?? string.Empty;
        var reference = NormalizeReference(point.ObjectReference);
        return category.Equals("Position", StringComparison.OrdinalIgnoreCase) ||
               category.Equals("Protection", StringComparison.OrdinalIgnoreCase) ||
               category.Equals("Status", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("Boolean", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("Dbpos", StringComparison.OrdinalIgnoreCase) ||
               reference.Contains(".pos.stval") ||
               reference.EndsWith(".general");
    }

    private static bool HasMeaningfulEdge(Iec61850MonitorPoint point, string oldValue, string newValue)
    {
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
            return false;

        if (!IsAnalogPoint(point))
            return true;

        if (!TryExtractNumber(oldValue, out var oldNumber) || !TryExtractNumber(newValue, out var newNumber))
            return true;

        var absolute = Math.Abs(newNumber - oldNumber);
        var reference = Math.Max(Math.Abs(oldNumber), Math.Abs(newNumber));
        var deadband = Math.Max(reference * 0.001, 0.000001);
        return absolute >= deadband;
    }

    private static bool IsAnalogPoint(Iec61850MonitorPoint point)
        => point.Category.Equals("Measurement", StringComparison.OrdinalIgnoreCase) ||
           point.DataType.Contains("Float", StringComparison.OrdinalIgnoreCase) ||
           point.DataType.Contains("Double", StringComparison.OrdinalIgnoreCase);

    private static bool TryExtractNumber(string text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var token = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string NormalizeQuality(string? quality)
    {
        if (string.IsNullOrWhiteSpace(quality) || quality.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return "Good / q unavailable";
        return quality;
    }

    private static Iec61850MonitorPoint? FindPointForReportReference(DeviceSession session, string reference)
    {
        var normalized = NormalizeReference(reference);
        return session.Points.Values.FirstOrDefault(point =>
        {
            var candidate = NormalizeReference(point.ObjectReference);
            return candidate.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                   candidate.StartsWith(normalized + ".", StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith(candidate + ".", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool LooksLikeReferenceEcho(string value, string updateReference, string pointReference)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (bool.TryParse(value, out _) || TryExtractNumber(value, out _))
            return false;

        var normalizedValue = NormalizeReference(value);
        return normalizedValue.Equals(NormalizeReference(updateReference), StringComparison.OrdinalIgnoreCase) ||
               normalizedValue.Equals(NormalizeReference(pointReference), StringComparison.OrdinalIgnoreCase) ||
               (value.Contains('/') && value.Contains('$'));
    }

    private static string BuildCompanionReference(string reference, string companion)
    {
        var normalized = reference.Replace('$', '.').Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        foreach (var suffix in new[]
                 {
                     ".valWTr.posVal", ".instCVal.mag.f", ".cVal.mag.f", ".stVal", ".general", ".mag.f"
                 })
        {
            if (!normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;
            return normalized[..^suffix.Length] + "." + companion;
        }

        var dot = normalized.LastIndexOf('.');
        return dot > normalized.IndexOf('/') ? normalized[..dot] + "." + companion : string.Empty;
    }

    private static string BuildPointKey(string deviceId, string reference)
        => $"{deviceId}|{reference}";

    private static string NormalizeReference(string? reference)
        => (reference ?? string.Empty).Trim().Replace('$', '.').Replace("..", ".").ToLowerInvariant();

    private async Task RemoveSessionAsync(string deviceId, DeviceSession expected)
    {
        if (_sessions.TryRemove(new KeyValuePair<string, DeviceSession>(deviceId, expected)))
            await expected.Client.DisposeAsync().ConfigureAwait(false);
    }

    private void Log(string level, string source, string message)
        => Diagnostic?.Invoke(new DiagnosticEntry
        {
            Time = DateTime.Now,
            Level = level,
            Source = source,
            Message = message
        });

    public async ValueTask DisposeAsync()
    {
        foreach (var deviceId in _sessions.Keys.ToList())
            await StopDeviceAsync(deviceId).ConfigureAwait(false);
    }
}
