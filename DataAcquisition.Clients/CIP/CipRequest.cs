namespace DataAcquisition.Clients.CIP;

public partial class CipClient
{
    private record CipRequest(byte[] Payload, TaskCompletionSource<byte[]> CompletionSource);
}