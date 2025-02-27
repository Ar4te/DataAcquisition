namespace DataAcquisition.Servers.CIP;

/// <summary>
/// 设备配置
/// </summary>
public class SimulatorConfig
{
    public string DeviceName { get; set; } = "SimPLC";
    public int MaxConnections { get; set; } = 100000;
    public int SessionTimeout { get; set; } = 300000; // 5分钟

    public SimulatorConfig Validate()
    {
        if (MaxConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxConnections));
        if (SessionTimeout < 5000)
            throw new ArgumentOutOfRangeException(nameof(SessionTimeout));

        return this;
    }
}