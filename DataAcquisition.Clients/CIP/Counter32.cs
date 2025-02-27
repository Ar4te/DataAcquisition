namespace DataAcquisition.Clients.CIP;

public partial class CipClient
{
    public class Counter32
    {
        private int _value;
        public int Value => _value;
        public void Increment() => Interlocked.Increment(ref _value);
    }
}