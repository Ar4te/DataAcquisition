namespace DataAcquisition.Clients.CIP;

public partial class CipClient
{
    public class CipMetrics
    {
        public Counter32 RequestsPerSecond { get; } = new Counter32();
        public Counter32 ErrorCount { get; } = new Counter32();
        public Gauge32 ConnectionCount { get; } = new Gauge32();
    }
}