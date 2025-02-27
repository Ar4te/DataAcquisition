namespace DataAcquisition.Clients.CIP;

public class CipException : Exception
{
    public uint StatusCode { get; }

    public CipException(string message) : base(message)
    {

    }

    public CipException(string message, Exception exception) : base(message, exception)
    {

    }

    public CipException(string message, uint statusCode) : base($"{message} [Status: 0x{statusCode:X8}]")
    {
        StatusCode = statusCode;
    }

    public static void ThrowFromStatus(uint status)
    {
        var message = status switch
        {
            0x00000000 => "Success",
            0x00000001 => "Invalid Command",
            0x00000002 => "Insufficient Memory",
            0x00000005 => "Invalid Session Handle",
            _ => "Unknown Error"
        };
        throw new CipException(message, status);
    }
}