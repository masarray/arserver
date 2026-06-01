using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Ari61850Bridge.Models;
using MQTTnet;
using MQTTnet.Protocol;

namespace Ari61850Bridge.Services;

public sealed class MqttGatewayPublisher : IAsyncDisposable
{
    private readonly Channel<MqttPublishItem> _queue = Channel.CreateBounded<MqttPublishItem>(new BoundedChannelOptions(2048)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });

    private MqttGatewaySettings _settings = new();
    private IMqttClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private long _publishedCount;
    private long _droppedCount;
    private bool _started;

    public event Action<string, string>? Log;

    public bool IsEnabled => _settings.IsEnabled;
    public bool IsConnected => _client?.IsConnected == true;
    public long PublishedCount => Interlocked.Read(ref _publishedCount);
    public long DroppedCount => Interlocked.Read(ref _droppedCount);
    public string EndpointText => $"{_settings.BrokerHost}:{_settings.BrokerPort}";
    public string TopicRoot => NormalizeTopicSegment(_settings.TopicRoot, "arserver");

    public async Task StartAsync(MqttGatewaySettings settings, CancellationToken cancellationToken)
    {
        _settings = NormalizeSettings(settings);
        if (!_settings.IsEnabled)
            return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _client = new MqttClientFactory().CreateMqttClient();
        _worker = Task.Run(() => WorkerAsync(_cts.Token));
        _started = true;

        try
        {
            await EnsureConnectedAsync(_cts.Token);
            Log?.Invoke("INFO", $"MQTT publisher ready on broker {_settings.BrokerHost}:{_settings.BrokerPort}, root '{TopicRoot}'.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log?.Invoke("WARN", $"MQTT broker not reachable yet: {ex.Message}. Runtime will keep reading IEC values and retry MQTT publishing in the background.");
        }

        EnqueueRaw($"{TopicRoot}/status", "online", retain: true);
    }

    public void EnqueueBinding(BindingItem binding, object? rawValue, string displayValue)
    {
        if (!_settings.IsEnabled || !_started)
            return;

        var topicBase = BuildTopicBase(binding);
        var numericValue = FormatValuePayload(rawValue, displayValue);
        var state = new
        {
            value = numericValue,
            display = displayValue,
            quality = binding.Quality,
            status = binding.Status,
            unit = binding.Unit,
            ied = binding.IedName,
            tag = ResolveTagName(binding),
            iecReference = binding.IecReference,
            functionalConstraint = binding.FunctionalConstraint,
            dataType = binding.IecDataType,
            timestamp = binding.LastUpdate == DateTime.MinValue ? DateTime.Now : binding.LastUpdate,
            sequence = binding.Sequence,
            ageMs = binding.AgeMs
        };

        EnqueueRaw($"{topicBase}/value", numericValue, _settings.RetainLastValue);
        EnqueueRaw($"{topicBase}/quality", binding.Quality, _settings.RetainLastValue);
        EnqueueRaw($"{topicBase}/status", binding.Status, _settings.RetainLastValue);

        if (_settings.PublishJsonState)
        {
            var json = JsonSerializer.Serialize(state);
            EnqueueRaw($"{topicBase}/state", json, _settings.RetainLastValue, contentType: "application/json");
        }
    }

    public async Task StopAsync()
    {
        if (!_settings.IsEnabled)
            return;

        try
        {
            EnqueueRaw($"{TopicRoot}/status", "offline", retain: true);
            await Task.Delay(120);
        }
        catch
        {
            // Best effort shutdown status only.
        }

        try { _cts?.Cancel(); } catch { }
        if (_worker != null)
        {
            try { await _worker; } catch { }
        }

        if (_client?.IsConnected == true)
        {
            try { await _client.DisconnectAsync(); } catch { }
        }

        _client?.Dispose();
        _client = null;
        _started = false;
        Log?.Invoke("INFO", "MQTT publisher stopped.");
    }

    private void EnqueueRaw(string topic, string payload, bool retain, string contentType = "text/plain")
    {
        if (!_queue.Writer.TryWrite(new MqttPublishItem(topic, payload, retain, contentType)))
            Interlocked.Increment(ref _droppedCount);
    }

    private async Task WorkerAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var item = await _queue.Reader.ReadAsync(token);
                await EnsureConnectedAsync(token);
                if (_client?.IsConnected != true)
                    continue;

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(item.Topic)
                    .WithPayload(item.Payload)
                    .WithContentType(item.ContentType)
                    .WithQualityOfServiceLevel(ToMqttQos(_settings.QualityOfService))
                    .WithRetainFlag(item.Retain)
                    .Build();

                await _client.PublishAsync(message, token);
                Interlocked.Increment(ref _publishedCount);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke("WARN", $"MQTT publish delayed: {ex.Message}");
                try { await Task.Delay(1000, token); } catch { break; }
            }
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken token)
    {
        if (_client?.IsConnected == true)
            return;
        if (_client == null)
            _client = new MqttClientFactory().CreateMqttClient();

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithClientId(_settings.ClientId)
            .WithTcpServer(_settings.BrokerHost, _settings.BrokerPort)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithWillTopic($"{TopicRoot}/status")
            .WithWillPayload("offline")
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        if (!string.IsNullOrWhiteSpace(_settings.Username))
            optionsBuilder = optionsBuilder.WithCredentials(_settings.Username, _settings.Password);

        await _client.ConnectAsync(optionsBuilder.Build(), token);
    }

    private string BuildTopicBase(BindingItem binding)
    {
        var root = TopicRoot;
        var ied = NormalizeTopicSegment(binding.IedName, "ied");
        var tag = NormalizeTopicSegment(ResolveTagName(binding), $"tag-{binding.ModbusAddress}");
        return $"{root}/{ied}/{tag}";
    }

    private static string ResolveTagName(BindingItem binding)
    {
        if (!string.IsNullOrWhiteSpace(binding.FuxaTagName)) return binding.FuxaTagName;
        if (!string.IsNullOrWhiteSpace(binding.SignalName)) return binding.SignalName;
        return binding.IecReference;
    }

    private static string FormatValuePayload(object? rawValue, string displayValue)
    {
        return rawValue switch
        {
            null => displayValue,
            bool b => b ? "true" : "false",
            float f => f.ToString("0.########", CultureInfo.InvariantCulture),
            double d => d.ToString("0.########", CultureInfo.InvariantCulture),
            decimal m => m.ToString("0.########", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? displayValue,
            _ => displayValue
        };
    }

    private static MqttGatewaySettings NormalizeSettings(MqttGatewaySettings settings)
    {
        return new MqttGatewaySettings
        {
            IsEnabled = settings.IsEnabled,
            BrokerHost = string.IsNullOrWhiteSpace(settings.BrokerHost) ? "127.0.0.1" : settings.BrokerHost.Trim(),
            BrokerPort = settings.BrokerPort is > 0 and <= 65535 ? settings.BrokerPort : 1883,
            ClientId = string.IsNullOrWhiteSpace(settings.ClientId) ? $"arserver-{Environment.MachineName}".ToLowerInvariant() : settings.ClientId.Trim(),
            TopicRoot = NormalizeTopicSegment(settings.TopicRoot, "arserver"),
            QualityOfService = Math.Clamp(settings.QualityOfService, 0, 1),
            RetainLastValue = settings.RetainLastValue,
            PublishJsonState = settings.PublishJsonState,
            Username = settings.Username?.Trim() ?? "",
            Password = settings.Password ?? ""
        };
    }

    private static MqttQualityOfServiceLevel ToMqttQos(int qos)
    {
        return qos <= 0 ? MqttQualityOfServiceLevel.AtMostOnce : MqttQualityOfServiceLevel.AtLeastOnce;
    }

    private static string NormalizeTopicSegment(string? value, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
                builder.Append(char.ToLowerInvariant(ch));
            else if (ch is '/' or '\\')
                builder.Append('/');
            else
                builder.Append('_');
        }

        var normalized = builder.ToString().Trim('/');
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }

    private sealed record MqttPublishItem(string Topic, string Payload, bool Retain, string ContentType);
}
