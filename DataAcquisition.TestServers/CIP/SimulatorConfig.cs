namespace DataAcquisition.TestServers.CIP;

/// <summary>
/// 设备配置
/// </summary>
public class SimulatorConfig
{
    public string DeviceName { get; set; } = "SimPLC";
    public int MaxConnections { get; set; } = 10;
    public int SessionTimeout { get; set; } = 300000; // 5分钟
}
