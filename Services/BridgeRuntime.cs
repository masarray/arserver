using System.Globalization;
using System.Collections.Concurrent;
using Ari61850Bridge.Models;

namespace Ari61850Bridge.Services;

public sealed class BridgeRuntime : IAsyncDisposable
{
    public const int MinimumMmsPollingIntervalMs = 10;
    public const int DefaultMmsPollingIntervalMs = 1000;
    public const int MaximumMmsPollingIntervalMs = 600000;

    private IIec61850Client _iecClient;
    private readonly Func<IIec61850Client>? _iecClientFactory;
    private readonly Dictionary<string, RelayEndpointView> _relayIndex;
    private readonly Dictionary<string, IIec61850Client> _relayClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ownedRelayClientIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ModbusTcpServer _modbusServer = new();
    private readonly MqttGatewayPublisher _mqttPublisher = new();
    private readonly List<BindingItem> _bindings;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _enableModbusTcp;
    private readonly bool _fastAcquisitionEnabled;
    private readonly MqttGatewaySettings _mqttSettings;
    private Task? _loopTask;
    private DateTime _lastModbusCounterSnapshot = DateTime.Now;
    private DateTime _lastModbusActivityLog = DateTime.MinValue;
    private long _lastModbusReadCount;
    private bool _iecDisconnectedLogged;
    private readonly Dictionary<string, DateTime> _lastPerTagReadWarning = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _nextPollDueUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _roundRobinCursorByRelay = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxReadsPerIedPerCycle = 8;
    private const int MaxFastReadsPerIedPerCycle = 16;
    private const int MaxNormalReadsPerIedPerCycleWhenFastLaneActive = 4;
    private const int SlowSchedulerDelayMs = 120;
    private const int FastSchedulerDelayMs = 2;

    public event Action<DiagnosticEntry>? Diagnostic;
    public event Action<BindingItem>? BindingUpdated;
    public event Action? RuntimeTick;

    public bool IsRunning { get; private set; }
    public int ClientCount => _modbusServer.ClientCount;
    public long ModbusReadCount => _modbusServer.ReadRequestCount;
    public string LastClientEndpoint => _modbusServer.LastClientEndpoint;
    public bool MqttIsConnected => _mqttPublisher.IsConnected;
    public long MqttPublishedCount => _mqttPublisher.PublishedCount;
    public long MqttDroppedCount => _mqttPublisher.DroppedCount;
    public string MqttEndpointText => _mqttPublisher.EndpointText;
    public string EventMode { get; private set; } = "Mock Polling Simulation";

    public void ReplaceIecClient(IIec61850Client iecClient)
    {
        _iecClient = iecClient;
        _iecDisconnectedLogged = false;
        _relayClients.Clear();
        _ownedRelayClientIds.Clear();
        EventMode = _iecClient.ConnectionMode.Contains("Mock", StringComparison.OrdinalIgnoreCase)
            ? "Mock Polling Simulation"
            : "IEC61850 MMS Polling";
        Log("INFO", "Runtime", "IEC 61850 client session refreshed. Modbus TCP server remained running.");
    }

    public BridgeRuntime(IIec61850Client iecClient, IEnumerable<BindingItem> bindings)
        : this(iecClient, bindings, Enumerable.Empty<RelayEndpointView>(), null, true, new MqttGatewaySettings())
    {
    }

    public BridgeRuntime(
        IIec61850Client iecClient,
        IEnumerable<BindingItem> bindings,
        IEnumerable<RelayEndpointView> relays,
        Func<IIec61850Client>? iecClientFactory,
        bool enableModbusTcp,
        MqttGatewaySettings mqttSettings,
        bool fastAcquisitionEnabled = true)
    {
        _iecClient = iecClient;
        _iecClientFactory = iecClientFactory;
        _enableModbusTcp = enableModbusTcp;
        _fastAcquisitionEnabled = fastAcquisitionEnabled;
        _mqttSettings = mqttSettings;
        _bindings = bindings.Where(b => b.IsEnabled && (b.PublishToModbus || b.PublishToMqtt)).ToList();
        _relayIndex = relays
            .Where(r => !string.IsNullOrWhiteSpace(r.RelayId))
            .GroupBy(r => r.RelayId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _modbusServer.Log += (level, message) => Log(level, "Modbus", message);
        _mqttPublisher.Log += (level, message) => Log(level, "MQTT", message);
    }

    public async Task StartAsync(string modbusBindAddress, int modbusPort, int unitId)
    {
        if (IsRunning) return;

        PrepareBindingsForRuntimeStart();
        await EnsureIecClientsAsync(_cts.Token);

        var safeUnitId = unitId < 1 || unitId > 247 ? 1 : unitId;
        if (_enableModbusTcp)
            await _modbusServer.StartAsync(modbusBindAddress, modbusPort, (byte)safeUnitId, _cts.Token);
        else
            Log("INFO", "Modbus", "Modbus TCP output is disabled for this runtime session.");

        await _mqttPublisher.StartAsync(_mqttSettings, _cts.Token);
        IsRunning = true;
        EventMode = ResolveRuntimeMode();

        var fastestPollMs = _bindings.Count == 0 ? DefaultMmsPollingIntervalMs : _bindings.Min(GetPollIntervalMs);
        var fastCandidateCount = _fastAcquisitionEnabled ? _bindings.Count(IsFastAcquisitionCandidate) : 0;
        Log("INFO", "Runtime", $"Runtime started. Active bindings: {_bindings.Count}. Scheduler: target fastest MMS poll {fastestPollMs} ms, fast CB lane {(_fastAcquisitionEnabled ? "ON" : "OFF")} ({fastCandidateCount} candidate point(s)), UI grid uses buffered snapshots.");
        if (fastestPollMs <= 50)
            Log("WARN", "Runtime", "Fast MMS polling mode is enabled. Effective speed still depends on relay response time, network latency, number of active tags, and MMS server limits; use reports/RCB for critical event capture when available.");
        if (_fastAcquisitionEnabled)
            Log("INFO", "Runtime", "Fast CB acquisition lane is active: CB position/status/Boolean/protection flags are scheduled before measurements and quality/timestamp points.");
        Log("INFO", "Runtime", $"IEC source mode: {EventMode}.");
        if (_enableModbusTcp)
            Log("INFO", "Runtime", $"Modbus TCP slave/server ready on {modbusBindAddress}:{modbusPort}, Unit ID {safeUnitId}.");
        if (_mqttSettings.IsEnabled)
            Log("INFO", "Runtime", $"MQTT publisher enabled for broker {_mqttSettings.BrokerHost}:{_mqttSettings.BrokerPort}, topic root '{_mqttSettings.TopicRoot}'.");
        _loopTask = Task.Run(RuntimeLoopAsync);
    }

    private string ResolveRuntimeMode()
    {
        var relayCount = _bindings.Select(b => string.IsNullOrWhiteSpace(b.RelayId) ? "__single__" : b.RelayId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (_iecClient.ConnectionMode.Contains("Mock", StringComparison.OrdinalIgnoreCase))
            return relayCount > 1 ? "Mock Multi-IED Polling" : "Mock Polling Simulation";
        var mode = relayCount > 1 ? "IEC61850 MMS Polling / Multi-IED" : "IEC61850 MMS Polling";
        return _fastAcquisitionEnabled ? $"{mode} + Fast CB Lane" : mode;
    }

    private async Task EnsureIecClientsAsync(CancellationToken token)
    {
        var grouped = _bindings
            .Where(b => b.IsEnabled)
            .GroupBy(b => string.IsNullOrWhiteSpace(b.RelayId) ? "__single__" : b.RelayId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (grouped.Count <= 1 || _iecClientFactory == null)
        {
            var key = grouped.FirstOrDefault()?.Key ?? "__single__";
            _relayClients[key] = _iecClient;
            return;
        }

        Log("INFO", "IEC61850", $"Starting isolated MMS client sessions for {grouped.Count} IED(s). This prevents the last-added IED from replacing the first IED client.");

        foreach (var group in grouped)
        {
            token.ThrowIfCancellationRequested();
            var relayId = group.Key;
            if (_relayClients.TryGetValue(relayId, out var existing) && existing.IsConnected)
                continue;

            var sample = group.First();
            _relayIndex.TryGetValue(relayId, out var relay);
            var ip = !string.IsNullOrWhiteSpace(sample.RelayIpAddress) ? sample.RelayIpAddress : relay?.IpAddress ?? "";
            var port = relay?.MmsPort > 0 ? relay.MmsPort : 102;
            var display = !string.IsNullOrWhiteSpace(sample.IedName) ? sample.IedName : relay?.DisplayName ?? relayId;

            if (string.IsNullOrWhiteSpace(ip))
            {
                MarkGroupDisconnected(group, "IED IP empty");
                Log("ERROR", "IEC61850", $"{display}: runtime cannot create MMS client because relay IP is empty.");
                continue;
            }

            var client = _iecClientFactory();
            await client.ConnectAsync(ip, port, token);

            if (!client.IsConnected)
            {
                MarkGroupDisconnected(group, "IEC connect failed");
                if (client is RealLibIec61850Client real && !string.IsNullOrWhiteSpace(real.LastErrorMessage))
                    Log("ERROR", "IEC61850", $"{display} {ip}:{port}: {real.LastErrorMessage}");
                else
                    Log("ERROR", "IEC61850", $"{display} {ip}:{port}: MMS client connection failed.");

                await client.DisposeAsync();
                continue;
            }

            _relayClients[relayId] = client;
            _ownedRelayClientIds.Add(relayId);
            Log("INFO", "IEC61850", $"{display} {ip}:{port}: isolated MMS client connected.");
        }
    }

    private static void MarkGroupDisconnected(IEnumerable<BindingItem> group, string status)
    {
        foreach (var binding in group)
        {
            binding.Status = status;
            binding.Quality = "Bad";
            binding.CurrentValue = "-";
        }
    }

    public async Task StopAsync()
    {
        if (!IsRunning && _relayClients.Count == 0) return;
        _cts.Cancel();
        if (_loopTask != null)
        {
            try { await _loopTask; } catch { }
        }
        await _mqttPublisher.StopAsync();
        await _modbusServer.StopAsync();

        foreach (var relayId in _ownedRelayClientIds.ToList())
        {
            if (_relayClients.TryGetValue(relayId, out var client))
            {
                try { await client.DisposeAsync(); } catch { }
            }
        }
        _relayClients.Clear();
        _ownedRelayClientIds.Clear();

        IsRunning = false;
        Log("INFO", "Runtime", "Runtime stopped.");
    }

    private void PrepareBindingsForRuntimeStart()
    {
        _nextPollDueUtc.Clear();
        _roundRobinCursorByRelay.Clear();

        foreach (var binding in _bindings.Where(b => b.IsEnabled))
        {
            if (string.IsNullOrWhiteSpace(binding.Status) ||
                binding.Status.Equals("Mapped", StringComparison.OrdinalIgnoreCase) ||
                binding.Status.Equals("SCL imported", StringComparison.OrdinalIgnoreCase) ||
                binding.Status.Equals("Idle", StringComparison.OrdinalIgnoreCase))
            {
                binding.Status = "Queued";
                binding.Quality = "Pending";
                binding.CurrentValue = "Pending read";
            }

            binding.AgeMs = 0;
        }
    }

    private List<BindingItem> SelectDueBindings(DateTime nowUtc)
    {
        var selected = new List<BindingItem>();

        var groups = _bindings
            .Where(b => b.IsEnabled)
            .GroupBy(b => string.IsNullOrWhiteSpace(b.RelayId) ? "__single__" : b.RelayId, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var relayKey = group.Key;
            var ordered = group
                .OrderBy(b => b.ModbusArea, StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => b.ModbusAddress)
                .ThenBy(b => b.IecReference, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ordered.Count == 0) continue;

            if (!_fastAcquisitionEnabled)
            {
                selected.AddRange(SelectDueFromOrderedPool(relayKey, ordered, nowUtc, MaxReadsPerIedPerCycle));
                continue;
            }

            var fastOrdered = ordered.Where(IsFastAcquisitionCandidate).ToList();
            var normalOrdered = ordered.Where(b => !IsFastAcquisitionCandidate(b)).ToList();

            // Fast acquisition lane: CB position, switch status, trip/start and Boolean points
            // are selected first so a breaker status change does not wait behind analog/quality tags.
            selected.AddRange(SelectDueFromOrderedPool($"{relayKey}|fast", fastOrdered, nowUtc, MaxFastReadsPerIedPerCycle));

            // Keep a small normal lane alive to prevent analog/quality starvation, but do not let it
            // dominate the short scheduler cycles needed by fast status acquisition.
            var normalBudget = fastOrdered.Count > 0 ? MaxNormalReadsPerIedPerCycleWhenFastLaneActive : MaxReadsPerIedPerCycle;
            selected.AddRange(SelectDueFromOrderedPool($"{relayKey}|normal", normalOrdered, nowUtc, normalBudget));
        }

        return selected;
    }

    private List<BindingItem> SelectDueFromOrderedPool(string cursorKey, IReadOnlyList<BindingItem> ordered, DateTime nowUtc, int budget)
    {
        var selected = new List<BindingItem>();
        if (ordered.Count == 0 || budget <= 0)
            return selected;

        var start = _roundRobinCursorByRelay.TryGetValue(cursorKey, out var cursor)
            ? Math.Clamp(cursor, 0, ordered.Count - 1)
            : 0;

        var scanned = 0;
        var index = start;

        while (scanned < ordered.Count && selected.Count < budget)
        {
            var candidate = ordered[index];
            if (IsDueForPoll(candidate, nowUtc))
                selected.Add(candidate);

            index = (index + 1) % ordered.Count;
            scanned++;
        }

        // Important: advance the cursor even when many points are due.
        // Otherwise the first N signals of every IED are polled forever and
        // later SCL/IP points stay at Pending read / Mapped indefinitely.
        if (selected.Count > 0)
            _roundRobinCursorByRelay[cursorKey] = index;

        return selected;
    }

    private async Task RuntimeLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;
            var dueBindings = SelectDueBindings(nowUtc);

            foreach (var binding in dueBindings)
            {
                if (token.IsCancellationRequested) break;
                ScheduleNextPoll(binding, nowUtc);

                var client = GetClientForBinding(binding);
                if (client == null || !client.IsConnected)
                {
                    binding.CurrentValue = "-";
                    binding.Quality = "Bad";
                    binding.Status = "IEC Disconnected";
                    if (ShouldLogReadWarning(binding))
                        Log("WARN", "IEC61850", $"{binding.IedName} {binding.RelayIpAddress}: MMS client not connected for {binding.SignalName}.");
                    PublishBindingToMqtt(binding, null, binding.CurrentValue);
                    BindingUpdated?.Invoke(binding);
                    continue;
                }

                try
                {
                    var old = binding.CurrentValue;
                    var oldQuality = binding.Quality;
                    var oldStatus = binding.Status;
                    var value = await client.ReadValueAsync(binding.IecReference, binding.FunctionalConstraint, binding.IecDataType, token);

                    if (value == null)
                    {
                        binding.CurrentValue = "-";
                        binding.Quality = "Bad";
                        binding.LastUpdate = DateTime.Now;
                        binding.AgeMs = 0;
                        binding.Status = "Not readable";

                        if (ShouldLogReadWarning(binding))
                            Log("WARN", "IEC61850", $"{binding.IedName}: {binding.SignalName} not readable by MMS. IEC object: {binding.IecReference} [{binding.FunctionalConstraint}]");

                        PublishBindingToMqtt(binding, null, binding.CurrentValue);
                        BindingUpdated?.Invoke(binding);
                        continue;
                    }

                    var display = FormatBindingDisplay(value, binding);
                    var changed = old != display;

                    binding.CurrentValue = display;
                    binding.Quality = "Good";
                    binding.LastUpdate = DateTime.Now;
                    binding.AgeMs = 0;
                    binding.Status = "MMS Polling/Live";
                    if (changed) binding.Sequence++;

                    // Outputs are cache-based. A FUXA/SCADA read never triggers direct MMS reads.
                    WriteBindingToModbus(binding, value);
                    if (changed ||
                        !string.Equals(oldQuality, binding.Quality, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(oldStatus, binding.Status, StringComparison.OrdinalIgnoreCase))
                    {
                        PublishBindingToMqtt(binding, value, display);
                    }

                    if (changed && ShouldLogValueChange(binding))
                        Log("EVENT", "IEC61850", $"{binding.IedName}: {binding.SignalName}: {old} → {display} | {binding.ModbusArea} {binding.ModbusAddress}");

                    BindingUpdated?.Invoke(binding);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    binding.Status = "Error";
                    binding.Quality = "Bad";

                    if (IsDisconnectedClientError(ex))
                    {
                        if (!_iecDisconnectedLogged)
                        {
                            Log("WARN", "IEC61850", $"Runtime read paused for {binding.IedName}: IEC61850 client is disconnected.");
                            _iecDisconnectedLogged = true;
                        }
                        PublishBindingToMqtt(binding, null, binding.CurrentValue);
                        continue;
                    }

                    if (ShouldLogReadWarning(binding))
                        Log("ERROR", "IEC61850", $"{binding.IedName}: {binding.SignalName}: {ex.Message}");
                    PublishBindingToMqtt(binding, null, binding.CurrentValue);
                }
            }

            foreach (var binding in _bindings)
            {
                if (binding.LastUpdate == DateTime.MinValue) continue;
                binding.AgeMs = (int)Math.Max(0, (DateTime.Now - binding.LastUpdate).TotalMilliseconds);
                if (binding.AgeMs > binding.StaleTimeoutMs && binding.Status != "Stale")
                {
                    binding.Status = "Stale";
                    Log("WARN", "Runtime", $"{binding.IedName}: {binding.SignalName} stale > {binding.StaleTimeoutMs} ms.");
                    PublishBindingToMqtt(binding, null, binding.CurrentValue);
                }
            }

            var now = DateTime.Now;
            if ((now - _lastModbusCounterSnapshot).TotalSeconds >= 2)
            {
                var readDelta = _modbusServer.ReadRequestCount - _lastModbusReadCount;
                if (readDelta > 0 && (now - _lastModbusActivityLog).TotalMinutes >= 5)
                {
                    Log("INFO", "Modbus", "Modbus master polling is active. Repetitive reads are summarized in the status strip and activity indicator.");
                    _lastModbusActivityLog = now;
                }
                _lastModbusReadCount = _modbusServer.ReadRequestCount;
                _lastModbusCounterSnapshot = now;
            }

            RuntimeTick?.Invoke();
            await Task.Delay(GetSchedulerDelayMs(), token);
        }
    }


    public static bool IsFastAcquisitionCandidate(BindingItem binding)
    {
        var category = binding.Category ?? string.Empty;
        var dataType = binding.IecDataType ?? string.Empty;
        var modbusType = binding.ModbusDataType ?? string.Empty;
        var modbusArea = binding.ModbusArea ?? string.Empty;
        var name = binding.SignalName ?? string.Empty;
        var reference = (binding.IecReference ?? string.Empty).Replace('$', '.').ToLowerInvariant();
        var search = $"{name} {reference} {category} {dataType} {modbusType}".ToLowerInvariant();

        if (category.Equals("Position", StringComparison.OrdinalIgnoreCase)) return true;
        if (category.Equals("Protection", StringComparison.OrdinalIgnoreCase) &&
            (reference.EndsWith(".op.general") || reference.EndsWith(".str.general") || reference.EndsWith(".tr.general"))) return true;

        if (dataType.Equals("Boolean", StringComparison.OrdinalIgnoreCase) ||
            modbusType.Equals("Bool", StringComparison.OrdinalIgnoreCase) ||
            modbusArea.Equals("Coil", StringComparison.OrdinalIgnoreCase) ||
            modbusArea.Equals("DiscreteInput", StringComparison.OrdinalIgnoreCase)) return true;

        if (reference.Contains(".pos.stval") || reference.EndsWith(".pos")) return true;
        if (reference.Contains("xcbr") || reference.Contains("xswi") || reference.Contains("cswi")) return true;

        return search.Contains("breaker") ||
               search.Contains("circuit breaker") ||
               search.Contains(" cb ") ||
               search.Contains("52a") ||
               search.Contains("52b") ||
               search.Contains("open") ||
               search.Contains("close") ||
               search.Contains("closed") ||
               search.Contains("trip") ||
               search.Contains("start") ||
               search.Contains("status") ||
               search.Contains("position");
    }

    private bool IsDueForPoll(BindingItem binding, DateTime nowUtc)
    {
        var key = GetBindingPollKey(binding);
        return !_nextPollDueUtc.TryGetValue(key, out var due) || nowUtc >= due;
    }

    private void ScheduleNextPoll(BindingItem binding, DateTime nowUtc)
    {
        _nextPollDueUtc[GetBindingPollKey(binding)] = nowUtc.AddMilliseconds(GetPollIntervalMs(binding));
    }

    private static string GetBindingPollKey(BindingItem binding) => $"{binding.RelayId}|{binding.IecReference}|{binding.ModbusArea}|{binding.ModbusAddress}";

    public static int NormalizeMmsPollingIntervalMs(int requestedMs)
    {
        if (requestedMs <= 0) return DefaultMmsPollingIntervalMs;
        return Math.Clamp(requestedMs, MinimumMmsPollingIntervalMs, MaximumMmsPollingIntervalMs);
    }

    private static int GetPollIntervalMs(BindingItem binding)
    {
        if (binding.PollingIntervalMs > 0) return NormalizeMmsPollingIntervalMs(binding.PollingIntervalMs);

        var category = binding.Category ?? string.Empty;
        var dataType = binding.IecDataType ?? string.Empty;
        if (category.Equals("Position", StringComparison.OrdinalIgnoreCase)) return 500;
        if (category.Equals("Protection", StringComparison.OrdinalIgnoreCase)) return 700;
        if (category.Equals("Measurement", StringComparison.OrdinalIgnoreCase) || dataType.Equals("Float32", StringComparison.OrdinalIgnoreCase)) return 1500;
        if (category.Equals("Quality", StringComparison.OrdinalIgnoreCase) || category.Equals("Timestamp", StringComparison.OrdinalIgnoreCase)) return 4000;
        return 2500;
    }

    private int GetSchedulerDelayMs()
    {
        if (_nextPollDueUtc.Count == 0) return SlowSchedulerDelayMs;

        var nowUtc = DateTime.UtcNow;
        var nextDue = _nextPollDueUtc.Values.Min();
        var waitMs = (int)Math.Ceiling((nextDue - nowUtc).TotalMilliseconds);
        if (waitMs <= 0) return 1;

        var fastestActivePoll = _bindings.Count == 0 ? DefaultMmsPollingIntervalMs : _bindings.Min(GetPollIntervalMs);
        var ceiling = fastestActivePoll <= 50 ? 25 : fastestActivePoll <= 100 ? 50 : SlowSchedulerDelayMs;
        return Math.Clamp(waitMs, FastSchedulerDelayMs, ceiling);
    }

    private IIec61850Client? GetClientForBinding(BindingItem binding)
    {
        var key = string.IsNullOrWhiteSpace(binding.RelayId) ? "__single__" : binding.RelayId;
        if (_relayClients.TryGetValue(key, out var client)) return client;
        return _iecClient;
    }

    private static bool IsDisconnectedClientError(Exception ex)
    {
        var cursor = ex;
        while (cursor != null)
        {
            if (cursor.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase) ||
                cursor.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase))
                return true;
            cursor = cursor.InnerException;
        }
        return false;
    }

    private bool ShouldLogReadWarning(BindingItem binding)
    {
        var key = $"{binding.RelayId}|{binding.IecReference}|{binding.Status}";
        var now = DateTime.Now;
        if (_lastPerTagReadWarning.TryGetValue(key, out var last) && (now - last).TotalMinutes < 5)
            return false;
        _lastPerTagReadWarning[key] = now;
        return true;
    }

    private static bool ShouldLogValueChange(BindingItem binding)
    {
        if (string.Equals(binding.Category, "Measurement", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(binding.IecDataType, "Float32", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private void WriteBindingToModbus(BindingItem binding, object? value)
    {
        if (!_enableModbusTcp || !binding.PublishToModbus)
            return;

        var numeric = ToDouble(value, binding.IecDataType);
        var scaled = numeric * binding.Scale + binding.Offset;

        switch (binding.ModbusArea)
        {
            case "HoldingRegister" when binding.ModbusDataType == "Float32":
                _modbusServer.WriteFloat32ToHolding(binding.ModbusAddress, (float)scaled, binding.WordOrder);
                break;
            case "HoldingRegister":
                _modbusServer.WriteHoldingRegister(binding.ModbusAddress, ClampUShort(scaled));
                break;
            case "InputRegister":
                _modbusServer.WriteInputRegister(binding.ModbusAddress, ClampUShort(scaled));
                break;
            case "Coil":
                _modbusServer.WriteCoil(binding.ModbusAddress, ToBool(value));
                break;
            case "DiscreteInput":
                _modbusServer.WriteDiscreteInput(binding.ModbusAddress, ToBool(value));
                break;
        }
    }

    private void PublishBindingToMqtt(BindingItem binding, object? value, string displayValue)
    {
        if (!binding.PublishToMqtt)
            return;

        _mqttPublisher.EnqueueBinding(binding, value, displayValue);
    }


    private static string FormatBindingDisplay(object? value, BindingItem binding)
    {
        if (IsPositionStatus(binding))
        {
            var dbpos = NormalizeDbposForDisplay(value);
            if (dbpos.HasValue)
            {
                return dbpos.Value switch
                {
                    0 => "Intermediate",
                    1 => "Open",
                    2 => "Closed",
                    3 => "Bad-state",
                    _ => dbpos.Value.ToString(CultureInfo.InvariantCulture)
                };
            }
        }

        return MockIec61850Client.Format(value, binding.IecDataType, binding.Unit);
    }

    private static bool IsPositionStatus(BindingItem binding)
    {
        var r = binding.IecReference?.Replace('$', '.').ToLowerInvariant() ?? string.Empty;
        return r.EndsWith(".pos.stval") ||
               r.Contains(".pos.stval") ||
               binding.IecDataType.Equals("Dbpos", StringComparison.OrdinalIgnoreCase);
    }

    private static int? NormalizeDbposForDisplay(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case int i when i is >= 0 and <= 3:
                return i;
            case uint ui when ui <= 3:
                return (int)ui;
            case long l when l is >= 0 and <= 3:
                return (int)l;
            case bool b:
                return b ? 2 : 1;
            case string s:
                if (TryParseDbposString(s, out var parsed)) return parsed;
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt) && parsedInt is >= 0 and <= 3) return parsedInt;
                break;
        }
        return null;
    }

    private static bool TryParseDbposString(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var compact = text.Trim().Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
        switch (compact)
        {
            case "00":
            case "0":
            case "intermediate":
            case "intermediatestate":
                value = 0; return true;
            case "01":
            case "1":
            case "open":
            case "off":
                value = 1; return true;
            case "10":
            case "2":
            case "closed":
            case "close":
            case "on":
                value = 2; return true;
            case "11":
            case "3":
            case "bad":
            case "badstate":
                value = 3; return true;
            default:
                return false;
        }
    }

    private static double ToDouble(object? value, string dataType)
    {
        return value switch
        {
            null => 0,
            bool b => b ? 1 : 0,
            int i => i,
            uint ui => ui,
            long l => l,
            double d => d,
            float f => f,
            string s when TryParseDbposString(s, out var dbpos) => dbpos,
            string s when s.Equals("open", StringComparison.OrdinalIgnoreCase) || s.Equals("off", StringComparison.OrdinalIgnoreCase) => 1,
            string s when s.Equals("closed", StringComparison.OrdinalIgnoreCase) || s.Equals("close", StringComparison.OrdinalIgnoreCase) || s.Equals("on", StringComparison.OrdinalIgnoreCase) => 2,
            string s when double.TryParse(s.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => dataType == "Boolean" ? 0 : 0
        };
    }

    private static bool ToBool(object? value)
    {
        return value switch
        {
            bool b => b,
            int i => i != 0,
            uint ui => ui != 0,
            long l => l != 0,
            double d => Math.Abs(d) > double.Epsilon,
            float f => Math.Abs(f) > double.Epsilon,
            string s => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("closed", StringComparison.OrdinalIgnoreCase) || s.Equals("close", StringComparison.OrdinalIgnoreCase) || s.Equals("on", StringComparison.OrdinalIgnoreCase) || s == "1" || s == "2" || s == "10",
            _ => false
        };
    }

    private static ushort ClampUShort(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
        if (value < 0) return 0;
        if (value > ushort.MaxValue) return ushort.MaxValue;
        return (ushort)Math.Round(value);
    }

    private void Log(string level, string source, string message)
    {
        Diagnostic?.Invoke(new DiagnosticEntry { Time = DateTime.Now, Level = level, Source = source, Message = message });
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _mqttPublisher.DisposeAsync();
        await _modbusServer.DisposeAsync();
        foreach (var relayId in _ownedRelayClientIds.ToList())
        {
            if (_relayClients.TryGetValue(relayId, out var client))
            {
                try { await client.DisposeAsync(); } catch { }
            }
        }
        _cts.Dispose();
    }
}
