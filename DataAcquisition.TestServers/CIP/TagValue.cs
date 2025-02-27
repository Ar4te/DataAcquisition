using System.Text;

namespace DataAcquisition.TestServers.CIP;

/// <summary>
/// 标签存储
/// </summary>
public class TagValue
{
    public DataType Type { get; }
    public object Value { get => GetValue(); set => _value = value; }

    private object _value;
    private readonly object _valueLock = new();

    public TagValue(DataType type, object value)
    {
        Type = type;
        _value = ConvertValue(value) ?? throw new ArgumentNullException(nameof(value));
    }

    public byte[] GetValueBytes() => Type switch
    {
        DataType.BOOL => [(bool)_value ? (byte)1 : (byte)0],
        DataType.UINT16 => BitConverter.GetBytes((ushort)_value),
        DataType.UINT32 => BitConverter.GetBytes((uint)_value),
        DataType.UINT64 => BitConverter.GetBytes((ulong)_value),
        DataType.SINGLE => BitConverter.GetBytes((float)_value),
        DataType.DOUBLE => BitConverter.GetBytes((double)_value),
        DataType.STRING => Encoding.ASCII.GetBytes((string)_value),
        _ => throw new InvalidOperationException("未知数据类型")
    };

    public object GetValue()
    {
        lock (_valueLock)
        {
            return Type switch
            {
                DataType.BOOL => (bool)_value,
                DataType.UINT16 => (ushort)_value,
                DataType.UINT32 => (uint)_value,
                DataType.UINT64 => (ulong)_value,
                DataType.SINGLE => (float)_value,
                DataType.DOUBLE => (double)_value,
                DataType.STRING => (string)_value,
                _ => throw new InvalidOperationException("未知数据类型")
            };
        }
    }

    public bool TrySetValue(object value)
    {
        lock (_valueLock)
        {
            try
            {
                _value = Convert.ChangeType(value, Type.GetDotNetType());
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private object ConvertValue(object value)
    {
        return Type switch
        {
            DataType.BOOL => Convert.ToBoolean(value),
            DataType.UINT16 => Convert.ToUInt16(value),
            DataType.UINT32 => Convert.ToUInt32(value),
            DataType.UINT64 => Convert.ToUInt64(value),
            DataType.SINGLE => Convert.ToSingle(value),
            DataType.DOUBLE => Convert.ToDouble(value),
            DataType.STRING => Convert.ToString(value) ?? string.Empty,
            _ => throw new InvalidOperationException("未知数据类型")
        };
    }
}
