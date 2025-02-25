namespace DataAcquisition.TestServers.CIP;

/// <summary>
/// 标签存储
/// </summary>
public class TagValue
{
    public DataType Type { get; }
    public object Value { get; set; }

    public TagValue(DataType type, object value)
    {
        Type = type;
        Value = value;
    }
}

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

public static class DataTypeExtensions
{
    public static int GetTypeLength(this DataType type) => type switch
    {
        DataType.BOOL => 1,
        DataType.UINT16 => 2,
        DataType.UINT32 => 4,
        DataType.UINT64 => 4,
        DataType.SINGLE => 4,
        _ => 4
    };

    public static DataType GetDataType(byte dataType) => dataType switch
    {
        0xC1 => DataType.BOOL,
        0xC2 => DataType.UINT16,
        0xC3 => DataType.UINT32,
        0xC4 => DataType.UINT64,
        0xCA => DataType.SINGLE,
        0xCB => DataType.DOUBLE,
        0xDA => DataType.STRING,
        _ => throw new InvalidCastException("不支持的数据类型")
    };
}