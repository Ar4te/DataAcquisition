using System.Buffers;
using System.Text;

namespace DataAcquisition.Servers.CIP;

public static class CipExtensions
{
    public static Type GetDotNetType(this DataType type) => type switch
    {
        DataType.BOOL => typeof(bool),
        DataType.UINT16 => typeof(ushort),
        DataType.UINT32 => typeof(uint),
        DataType.UINT64 => typeof(ulong),
        DataType.SINGLE => typeof(float),
        DataType.DOUBLE => typeof(double),
        DataType.STRING => typeof(string),
        _ => throw new NotSupportedException("未知数据类型")
    };

    public static ushort ToUInt16BigEndian(this Span<byte> span, int offset)
    {
        return BitConverter.ToUInt16(span.Slice(offset, 2).ToArray().Reverse().ToArray());
    }

    public static uint ToUInt32BigEndian(this Span<byte> span, int offset)
    {
        return BitConverter.ToUInt32(span.Slice(offset, 4).ToArray().Reverse().ToArray());
    }

    public static int GetTypeLength(this DataType type) => type switch
    {
        DataType.BOOL => 1,
        DataType.UINT16 => 2,
        DataType.UINT32 => 4,
        DataType.UINT64 => 8,
        DataType.SINGLE => 4,
        DataType.DOUBLE => 8,
        DataType.STRING => -1,
        _ => -1
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

    public static byte[] GetTagValueBytes(DataType dataType, object value)
    {
        return dataType switch
        {
            DataType.BOOL => [(bool)value ? (byte)1 : (byte)0],
            DataType.UINT16 => BitConverter.GetBytes(Convert.ToUInt16(value)),
            DataType.UINT32 => BitConverter.GetBytes(Convert.ToUInt32(value)),
            DataType.UINT64 => BitConverter.GetBytes(Convert.ToUInt64(value)),
            DataType.SINGLE => BitConverter.GetBytes(Convert.ToSingle(value)),
            DataType.DOUBLE => BitConverter.GetBytes(Convert.ToDouble(value)),
            DataType.STRING => Encoding.ASCII.GetBytes((string)value),
            _ => throw new InvalidOperationException("不支持的数据类型")
        };
    }
}
