namespace DataAcquisition.TestServers.CIP;

public class CipStatusCode
{
    public const uint Success = 0x00000000;
    public const uint UnsupportedCommand = 0x00000001;
    public const uint InvalidRequest = 0x00000002;
    public const uint InvalidSession = 0x00000005;
    public const uint TagNotFound = 0x00000014;
    public const uint DataTypeMismatch = 0x00000015;
    public const uint UnsupportedService = 0x00000016;
    public const uint InvalidData = 0x00000017;
}
