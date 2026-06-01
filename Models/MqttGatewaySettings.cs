namespace Ari61850Bridge.Models;

public class MqttGatewaySettings
{
    public bool IsEnabled { get; set; }
    public string BrokerHost { get; set; } = "127.0.0.1";
    public int BrokerPort { get; set; } = 1883;
    public string ClientId { get; set; } = "arserver-gateway";
    public string TopicRoot { get; set; } = "arserver";
    public int QualityOfService { get; set; } = 0;
    public bool RetainLastValue { get; set; } = true;
    public bool PublishJsonState { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
