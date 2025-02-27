using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DataAcquisition.TestServers.CIP;

public partial class CipSimulator : IDisposable
{
    #region 常量定义
    private const int Port = 44818;
    private const ushort ProtocolVersion = 0x01;
    private const ushort CommandRegisterSession = 0x0001;
    private const ushort CommandSendRRData = 0x006F;
    private const int HeaderSize = 24;
    private const int InitialBufferSize = 4096;
    private static readonly TimeSpan SessionCleanupInterval = TimeSpan.FromMinutes(1);
    #endregion

    #region 状态存储
    private readonly ConcurrentDictionary<uint, DeviceSession> _sessions = new();
    private readonly ReaderWriterLockSlim _seesionRWLock = new();
    private readonly ConcurrentDictionary<string, TagValue> _tags = new();
    private readonly TcpListener _listener;
    private readonly SimulatorConfig _config;
    private readonly SemaphoreSlim _connectionLimiter;
    private readonly CancellationTokenSource _cts = new();
    private bool _isRunning;
    #endregion

    #region 启动/停止
    public CipSimulator(SimulatorConfig config)
    {
        _config = config?.Validate() ?? throw new ArgumentNullException(nameof(config));
        _listener = new TcpListener(IPAddress.Any, Port);
        _connectionLimiter = new SemaphoreSlim(config.MaxConnections);
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        _listener.Start();

        _ = Task.Run(ClientAcceptLoop).ConfigureAwait(false);
        _ = Task.Run(SessionCleanupLoop).ConfigureAwait(false);

        Console.WriteLine($"CIP模拟器已启动，监听端口：{Port}");
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts.Cancel();

        _listener?.Stop();
        _connectionLimiter.Dispose();

        Console.WriteLine($"[{_config.DeviceName}] 模拟器已停止");
    }
    #endregion

    #region 客户端处理
    private async Task ClientAcceptLoop()
    {
        while (_isRunning)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                if (!_connectionLimiter.Wait(0))
                {
                    client.Dispose();
                    Console.WriteLine($"连接数超过限制：{_config.MaxConnections}");
                    continue;
                }

                _ = Task.Run(() => HandleClientAsync(client))
                    .ContinueWith(t =>
                    {
                        _connectionLimiter.Release();
                        t.Exception?.Handle(e => true);
                    })
                    .ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (!_isRunning)
            {
                // 正常停止
            }
            catch (Exception ex)
            {
                Console.WriteLine($"接受客户端连接异常: {ex.Message}\n");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            var remoteEp = (IPEndPoint)client.Client.RemoteEndPoint!;
            Console.WriteLine($"[{remoteEp}] 客户端已连接\n");

            try
            {
                using var stream = client.GetStream();
                using var bufferOwner = MemoryPool<byte>.Shared.Rent(InitialBufferSize);
                var buffer = bufferOwner.Memory;
                try
                {
                    while (client.Connected)
                    {
                        var bytesRead = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                        if (bytesRead == 0) break;

                        var request = buffer.Span[..bytesRead].ToArray();
                        Console.WriteLine($"监听{remoteEp}：{BitConverter.ToString(request)}\n");

                        var response = ProcessRequest(buffer.Span[..bytesRead], remoteEp);
                        await stream.WriteAsync(response, _cts.Token).ConfigureAwait(false);
                        Console.WriteLine($"响应{remoteEp}：{BitConverter.ToString(response)}\n");
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException)
                {
                    Console.WriteLine($"客户端{remoteEp}断开连接:{ex.Message}");
                }
                finally
                {
                    bufferOwner?.Dispose();
                }
            }
            finally
            {
                _connectionLimiter.Release();
                Console.WriteLine($"[{remoteEp}] 客户端已断开");
            }
        }
    }
    #endregion

    #region 协议处理核心
    private byte[] ProcessRequest(ReadOnlySpan<byte> request, IPEndPoint remoteEp)
    {
        try
        {
            if (request.Length < HeaderSize)
                return BuildErrorResponse(0x02, 0, "报文长度不足");

            var command = MemoryMarshal.Read<ushort>(request);
            return command switch
            {
                // 注册会话
                CommandRegisterSession => HandleRegisterSession(remoteEp),
                // 发送RR数据
                CommandSendRRData => HandleSendRRData(request),
                _ => BuildErrorResponse(0x01, 0, "不支持的指令")
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"协议处理异常: {ex.Message}\n");
            return BuildErrorResponse(0x02, 0, "协议错误");
        }
    }

    private byte[] HandleRegisterSession(IPEndPoint remoteEp)
    {
        // 随机生成会话句柄
        var sessionHandle = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        var session = new DeviceSession(sessionHandle, remoteEp, _config.SessionTimeout);
        _sessions.TryAdd(sessionHandle, session);

        var response = new byte[HeaderSize + 4];

        BuildResponseHeader(response, CommandRegisterSession, sessionHandle, CipStatusCode.Success);
        MemoryMarshal.Write(response.AsSpan(HeaderSize), CipStatusCode.Success);

        Console.WriteLine($"{remoteEp} 会话注册成功：0x{sessionHandle:X8}\n");
        return response;
    }

    private byte[] HandleSendRRData(ReadOnlySpan<byte> request)
    {
        var sessionHandle = MemoryMarshal.Read<uint>(request[4..8]);
        if (!_sessions.TryGetValue(sessionHandle, out var session) || session.IsExpired(_config.SessionTimeout))
            return BuildErrorResponse(CipStatusCode.InvalidSession, sessionHandle, "会话无效或已过期");

        session.UpdateActiveTime(_config.SessionTimeout);

        // 解析服务请求
        var serviceOffset = HeaderSize + 4; // 跳过接口句柄和超时
        if (serviceOffset >= request.Length)
            return BuildErrorResponse(CipStatusCode.InvalidRequest, sessionHandle, "无效请求");

        var serviceCode = request[serviceOffset++];
        return serviceCode switch
        {
            // 读取标签
            0x52 => HandleReadTag(request, serviceOffset, sessionHandle),
            // 写入标签
            0x53 => HandleWriteTag(request, serviceOffset, sessionHandle),
            // 批量读取
            0x5A => HandleBatchOperation(request, serviceOffset, sessionHandle, isRead: true),
            // 批量写入
            0x5B => HandleBatchOperation(request, serviceOffset, sessionHandle, isRead: false),
            _ => BuildErrorResponse(CipStatusCode.UnsupportedCommand, sessionHandle, "不支持的服务")
        };
    }
    #endregion

    #region 标签操作
    private byte[] HandleReadTag(ReadOnlySpan<byte> request, int offset, uint sessionHandle)
    {
        if (offset >= request.Length)
            return BuildErrorResponse(CipStatusCode.InvalidRequest, sessionHandle, "偏移量超出范围");
        // 解析标签路径
        var pathSize = request[offset++];
        var tagName = ParseTagPath(request[offset..], out int bytesConsumed);
        offset += bytesConsumed;

        // 检查标签是否存在
        if (!_tags.TryGetValue(tagName, out var tag))
            return BuildErrorResponse(CipStatusCode.TagNotFound, sessionHandle, $"标签{tagName}不存在");

        return BuildTagResponse(sessionHandle, tag);
    }

    private byte[] HandleWriteTag(ReadOnlySpan<byte> request, int offset, uint sessionHandle)
    {
        if (offset >= request.Length)
            return BuildErrorResponse(CipStatusCode.InvalidRequest, sessionHandle, "偏移量超出范围");
        // 解析标签路径
        var pathSize = request[offset++];
        var tagName = ParseTagPath(request[offset..], out int bytesConsumed);
        offset += bytesConsumed;

        // 检查标签是否存在
        if (!_tags.TryGetValue(tagName, out var tag))
            return BuildErrorResponse(CipStatusCode.TagNotFound, sessionHandle, $"标签{tagName}不存在");

        if (offset + 2 >= request.Length)
            return BuildErrorResponse(CipStatusCode.InvalidData, sessionHandle, "数据长度不足");

        // 解析写入的数据类型和值
        var dataType = CipExtensions.GetDataType(request[offset++]); // 获取数据类型
        var valueSize = request[offset++]; // 获取数据长度

        if (offset + valueSize > request.Length)
            return BuildErrorResponse(CipStatusCode.InvalidData, sessionHandle, "数据长度不足");

        var valueBytes = request.Slice(offset, valueSize); // 获取数据值
        // 将接收到的值写入标签
        if (!TryParseTagValue(dataType, valueBytes, out object parsedValue) || !tag.TrySetValue(parsedValue))
            return BuildErrorResponse(CipStatusCode.DataTypeMismatch, sessionHandle, $"写入数据到{tagName}失败");

        return BuildSuccessResponse(sessionHandle);
    }

    private byte[] HandleBatchOperation(ReadOnlySpan<byte> request, int offset, uint sessionHandle, bool isRead = true)
    {
        if (offset >= request.Length)
            return BuildErrorResponse(CipStatusCode.InvalidRequest, sessionHandle, "偏移量超出范围");
        var tagCount = request[offset++];
        var responseBuffer = new ArrayBufferWriter<byte>(tagCount * 32);

        for (int i = 0; i < tagCount; i++)
        {
            var result = isRead
                ? ProcessBatchRead(request, ref offset, responseBuffer)
                : ProcessBatchWrite(request, ref offset, responseBuffer);

            if (result != CipStatusCode.Success)
                return BuildErrorResponse(result, sessionHandle, "批量操作失败");
        }
        return BuildBatchResponse(sessionHandle, responseBuffer.WrittenSpan);
    }

    #region 批量操作标签
    private uint ProcessBatchRead(ReadOnlySpan<byte> request, ref int offset, ArrayBufferWriter<byte> responseBuffer)
    {
        var tagName = ParseTagPath(request[offset..], out int bytesConsumed);
        offset += bytesConsumed;

        if (!_tags.TryGetValue(tagName, out var tag))
            return CipStatusCode.TagNotFound;

        var tagResponse = BuildSingleTagResponse(tag);
        responseBuffer.Write(tagResponse);
        return CipStatusCode.Success;
    }

    private uint ProcessBatchWrite(ReadOnlySpan<byte> request, ref int offset, ArrayBufferWriter<byte> responseBuffer)
    {
        var tagName = ParseTagPath(request[offset..], out int bytesConsumed);
        offset += bytesConsumed;

        if (offset + 2 >= request.Length)
            return CipStatusCode.InvalidData;

        var dataType = CipExtensions.GetDataType(request[offset++]);
        var valueSize = request[offset++];
        if (offset + valueSize > request.Length)
            return CipStatusCode.InvalidData;
        if (!_tags.TryGetValue(tagName, out var tag))
            return CipStatusCode.TagNotFound;
        if (!TryParseTagValue(dataType, request.Slice(offset, valueSize), out object parsedValue) || !tag.TrySetValue(parsedValue))
            return CipStatusCode.DataTypeMismatch;

        offset += valueSize;
        responseBuffer.Write(BuildSingleTagResponse(tag));
        return CipStatusCode.Success;
    }
    #endregion

    #region 构建响应
    private static byte[] BuildSuccessResponse(uint sessionHandle)
    {
        var response = new byte[HeaderSize + 4];
        BuildResponseHeader(response, CommandSendRRData, sessionHandle, CipStatusCode.Success);
        return response;
    }

    private static byte[] BuildTagResponse(uint sessionHandle, TagValue tag)
    {
        byte[] valueBytes = tag.GetValueBytes();
        var response = new byte[HeaderSize + 6 + valueBytes.Length];
        BuildResponseHeader(response, CommandSendRRData, sessionHandle, CipStatusCode.Success);

        response[HeaderSize + 4] = 0x00;
        response[HeaderSize + 5] = (byte)tag.Type;
        valueBytes.CopyTo(response.AsSpan(HeaderSize + 6));
        return response;
    }

    private static byte[] BuildBatchResponse(uint sessionHandle, ReadOnlySpan<byte> payload)
    {
        var response = new byte[HeaderSize + 4 + payload.Length];
        BuildResponseHeader(response, CommandSendRRData, sessionHandle, CipStatusCode.Success);
        payload.CopyTo(response.AsSpan(HeaderSize + 4));
        return response;
    }

    private static void BuildResponseHeader(Span<byte> response, ushort command, uint sessionHandle, uint cipStatusCode)
    {
        MemoryMarshal.Write(response, in command);
        MemoryMarshal.Write(response[2..], (ushort)(response.Length - HeaderSize));
        MemoryMarshal.Write(response[4..], in sessionHandle);
        MemoryMarshal.Write(response[8..], in cipStatusCode);
    }

    private static byte[] BuildErrorResponse(uint cipStatusCode, uint sessionHandle = 0, string message = "")
    {
        Console.WriteLine($"出现错误响应：[{cipStatusCode:X8}] : {message}");
        var response = new byte[HeaderSize + 4];
        BuildResponseHeader(response, (ushort)(cipStatusCode | 0x8000), sessionHandle, cipStatusCode);
        return response;
    }

    private static string ParseTagPath(ReadOnlySpan<byte> pathData, out int bytesConsumed)
    {
        bytesConsumed = 0;
        var sb = new StringBuilder();
        int position = 0;

        while (position < pathData.Length)
        {
            var segmentType = pathData[position++];
            if (segmentType != 0x91)
                break;
            var length = pathData[position++];
            var segment = Encoding.ASCII.GetString(
                pathData.Slice(position, length).ToArray());

            sb.Append(sb.Length > 0 ? "." : "").Append(segment);
            position += length;
        }
        bytesConsumed += position - 1;
        return sb.ToString();
    }

    private static byte[] BuildSingleTagResponse(TagValue tag)
    {
        byte[] valueBytes = CipExtensions.GetTagValueBytes(tag.Type, tag.Value);
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(0x00);  // 保留字段
        writer.Write((byte)tag.Type);  // 标签类型
        writer.Write(valueBytes);
        return ms.ToArray();
    }
    #endregion
    #endregion

    #region 工具方法

    private static bool TryParseTagValue(DataType type, ReadOnlySpan<byte> valueBytes, out object value)
    {
        try
        {
            value = type switch
            {
                DataType.BOOL => valueBytes[0] == 1,
                DataType.UINT16 => MemoryMarshal.Read<ushort>(valueBytes),
                DataType.UINT32 => MemoryMarshal.Read<uint>(valueBytes),
                DataType.UINT64 => MemoryMarshal.Read<ulong>(valueBytes),
                DataType.SINGLE => MemoryMarshal.Read<float>(valueBytes),
                DataType.DOUBLE => MemoryMarshal.Read<double>(valueBytes),
                DataType.STRING => Encoding.ASCII.GetString(valueBytes),
                _ => throw new InvalidOperationException("不支持的数据类型")
            };
            return true;
        }
        catch
        {
            value = null!;
            return false;
        }
    }
    #endregion

    #region 会话维护
    private async Task SessionCleanupLoop()
    {
        while (_isRunning)
        {
            await Task.Delay(SessionCleanupInterval, _cts.Token); // 每分钟清理一次

            foreach (var (handle, session) in _sessions)
            {
                if (session.IsExpired(_config.SessionTimeout))
                {
                    _sessions.TryRemove(handle, out _);
                    Console.WriteLine($"清理过期会话: 0x{handle:X8}");
                }
            }
        }
    }
    #endregion 

    #region 设备管理接口
    public void AddTag(string name, DataType type, object initialValue)
    {
        if (!_tags.TryAdd(name, new TagValue(type, initialValue)))
            throw new InvalidOperationException($"标签已存在：{name}");
    }

    public bool TryUpdateTag(string name, object value)
    {
        return _tags.TryGetValue(name, out var tag) && tag.TrySetValue(value);
    }

    public bool TryGetSession(uint handle, out DeviceSession session)
    {
        _seesionRWLock.EnterReadLock();
        try
        {
            return _sessions.TryGetValue(handle, out session!);
        }
        finally
        {
            _seesionRWLock.ExitReadLock();
        }
    }
    #endregion

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}