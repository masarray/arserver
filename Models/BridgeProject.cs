namespace Ari61850Bridge.Models;

public class BridgeProject
{
    public string ProjectName { get; set; } = "ArServer Project";
    public string RelayIpAddress { get; set; } = "192.168.1.10";
    public int MmsPort { get; set; } = 102;
    public bool UseRealIecEngine { get; set; }
    public string ModbusBindAddress { get; set; } = "0.0.0.0";
    public bool EnableModbusTcp { get; set; } = true;
    public int ModbusPort { get; set; } = 502;
    public int ModbusUnitId { get; set; } = 1;
    public MqttGatewaySettings Mqtt { get; set; } = new();
    public List<SignalDefinition> Signals { get; set; } = new();
    public List<BindingItem> Bindings { get; set; } = new();
    public List<RelayEndpointView> Relays { get; set; } = new();
}
