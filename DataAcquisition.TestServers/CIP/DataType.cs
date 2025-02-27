namespace DataAcquisition.TestServers.CIP;

public enum DataType : byte
{
    BOOL = 0xC1,
    UINT16 = 0xC2,
    UINT32 = 0xC3,
    UINT64 = 0xC4,
    SINGLE = 0xCA,
    DOUBLE = 0xCB,
    STRING = 0xDA
}
