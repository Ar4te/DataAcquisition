namespace DataAcquisition.Clients.CIP;

public partial class CipClient
{
    public class Gauge32
    {
        private int _value;
        public int Value => _value;
        public void Set(int value) => Interlocked.Exchange(ref _value, value);
    }
}