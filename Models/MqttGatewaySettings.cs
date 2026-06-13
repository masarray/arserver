namespace Ari61850Bridge.Models;

public class MqttGatewaySettings : ObservableObject
{
    private bool _isEnabled;
    private string _brokerHost = "127.0.0.1";
    private int _brokerPort = 1883;
    private string _clientId = "arserver-gateway";
    private string _topicRoot = "arserver";
    private int _qualityOfService;
    private bool _retainLastValue = true;
    private bool _publishJsonState = true;
    private string _username = "";
    private string _password = "";

    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }
    public string BrokerHost { get => _brokerHost; set => Set(ref _brokerHost, value); }
    public int BrokerPort { get => _brokerPort; set => Set(ref _brokerPort, value); }
    public string ClientId { get => _clientId; set => Set(ref _clientId, value); }
    public string TopicRoot { get => _topicRoot; set => Set(ref _topicRoot, value); }
    public int QualityOfService { get => _qualityOfService; set => Set(ref _qualityOfService, value); }
    public bool RetainLastValue { get => _retainLastValue; set => Set(ref _retainLastValue, value); }
    public bool PublishJsonState { get => _publishJsonState; set => Set(ref _publishJsonState, value); }
    public string Username { get => _username; set => Set(ref _username, value); }
    public string Password { get => _password; set => Set(ref _password, value); }
}
