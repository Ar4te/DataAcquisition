using DataAcquisition.TestServers.CIP;

namespace DataAcquisition.TestServers;

public class Program
{
    static void Main(string[] args)
    {
        var simulatorConfig = new SimulatorConfig();
        var simulator = new CipSimulator(simulatorConfig);
        AddTestTags(simulator);
        simulator.Start();
        Console.WriteLine("CIP 服务端已启动...");
        Console.ReadLine();
    }

    public static void AddTestTags(CipSimulator simulator)
    {
        simulator.AddTag("MyDevice.Tag1", DataType.UINT32, (uint)123);
        simulator.AddTag("MyDevice.Tag2", DataType.SINGLE, 45.67f);
        simulator.AddTag("MyDevice.Tag3", DataType.BOOL, true);
    }
}
