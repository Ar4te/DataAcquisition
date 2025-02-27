using System.Collections.Concurrent;

namespace DataAcquisition.Servers.CIP;

public class TagStore
{
    private readonly ConcurrentDictionary<string, TagValue> _tags = new();
    private readonly ReaderWriterLockSlim _rwLock = new();

    public bool TryAddTag(string name, DataType dataType, object value)
    {
        _rwLock.EnterWriteLock();
        try
        {
            return _tags.TryAdd(name, new TagValue(dataType, value));
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public TagResult TryGetTag(string name, out TagValue? tag)
    {
        _rwLock.EnterReadLock();
        try
        {
            return _tags.TryGetValue(name, out tag) && tag is not null ? TagResult.Success : TagResult.NotFound;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public enum TagResult
    {
        Success, NotFound, ReadOnly
    }
}
