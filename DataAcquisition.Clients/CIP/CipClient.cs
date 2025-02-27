using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace DataAcquisition.Clients.CIP;

public partial class CipClient
{
    #region 常量
    private const int HeaderSize = 24;
    private const ushort CommandRegisterSession = 0x0001;
    private const ushort CommandSendRRData = 0x006F;
    private const ushort ProtocolVersion = 0x01;
    #endregion

    #region 字段
    private TcpClient _tcpClient;
    private NetworkStream _networkStream;
    private uint _sessionHandle;
    private long _senderContext;
    private readonly object _syncLock = new();
    private readonly object _sessionHandleLock = new();
    private readonly ILogger _logger;
    private Timer _keepAliveTimer;
    private readonly Channel<CipRequest> _requestChannel = Channel.CreateUnbounded<CipRequest>();
    private Task _processingTask;
    private volatile bool _isProcessing;
    private readonly object _processingLock = new();
    private bool _disposed;
    private int _timeout = 5000;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private readonly SemaphoreSlim _throughputLimiter = new(100, 100);
    private int _reconnectAttempts;
    private string _clientId;
    #endregion

    #region 属性
    public string Host { get; }
    public int Port { get; }
    public int Timeout
    {
        get => _timeout;
        set => _timeout = value;
    }
    public uint SessionHandle => _sessionHandle;
    public string ClientId
    {
        get => _clientId;
        set
        {
            _clientId = value ?? Guid.NewGuid().ToString("N");
        }
    }
    public bool IsConnected => _tcpClient?.Connected ?? false;
    public DateTime LastActivity { get; private set; }
    public CipMetrics Metrics { get; } = new CipMetrics();
    #endregion

    public CipClient(string host, int port, ILogger logger)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        Port = port;
        _logger = logger;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _disposed = true;
            _keepAliveTimer?.Dispose();
            _requestChannel.Writer.Complete();
            _processingTask?.Wait(TimeSpan.FromSeconds(5));
            _networkStream?.Close();
            _networkStream?.Dispose();
            _tcpClient?.Close();
            _tcpClient?.Dispose();
            _reconnectLock?.Dispose();
        }
        catch (AggregateException ae)
        {
            foreach (var e in ae.InnerExceptions)
            {
                _logger?.LogError(e, "资源释放时发生异常");
            }
        }

        _logger?.LogInformation("CIP客户端已释放");
    }

    #region 连接管理
    public async Task ConnectAsync()
    {
        await _reconnectLock.WaitAsync();

        try
        {
            if (IsConnected) return;

            for (int i = 0; i < 3; i++)
            {
                try
                {
                    _tcpClient = new TcpClient
                    {
                        ReceiveTimeout = Timeout,
                        SendTimeout = Timeout
                    };

                    using var cts = new CancellationTokenSource(Timeout);
                    await _tcpClient.ConnectAsync(Host, Port, cts.Token);
                    _networkStream = _tcpClient.GetStream();

                    var registerSession = BuildRegisterSessionPacket();
                    var response = await SendAndReceiveInternalAsync(registerSession);
                    ValidateResponseStatus(response);
                    lock (_sessionHandleLock)
                    {
                        _sessionHandle = BitConverter.ToUInt32(response.AsSpan()[4..8]);
                    }

                    StartAsyncProcessing();
                    StartKeepAlive();

                    _logger?.LogInformation("成功建立CIP会话 [SessionHandle: {SessionHandle}]", $"0x{_sessionHandle:X8}");

                    _reconnectAttempts = 0;
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "尝试连接 {count}/3 失败", i + 1);
                    if (i == 2)
                    {
                        throw new CipException($"连接超时 {Host}:{Port}");
                    }
                    await Task.Delay(1000 * (i + 1));
                }
            }
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    private byte[] BuildRegisterSessionPacket()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(CommandRegisterSession);
        writer.Write((ushort)0);     // Length placeholder
        writer.Write((uint)0);       // Session Handle
        writer.Write((uint)0);       // Status
        writer.Write(GetNextContext());   // Sender Context
        writer.Write((uint)0);       // Options

        // Command Specific Data
        writer.Write(ProtocolVersion);
        writer.Write((ushort)0);     // Options Flags

        var packet = ms.ToArray();
        BitConverter.GetBytes((ushort)(packet.Length - HeaderSize)).CopyTo(packet, 2);
        return packet;
    }
    #endregion

    #region 核心通讯方法
    private async Task EnsureConnectionAsync()
    {
        if (!IsConnected)
        {
            _logger?.LogInformation("连接丢失，尝试重新连接...");
            try
            {
                await ConnectAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "重新连接失败");
                throw;
            }
        }
    }

    public async Task<byte[]> SendAsync(byte[] payload)
    {
        await _throughputLimiter.WaitAsync();
        try
        {
            var tcs = new TaskCompletionSource<byte[]>();
            await _requestChannel.Writer.WriteAsync(new CipRequest(payload, tcs));
            Metrics.RequestsPerSecond.Increment();
            return await tcs.Task;
        }
        finally
        {
            _throughputLimiter.Release();
        }
    }

    private void StartAsyncProcessing()
    {
        lock (_processingLock)
        {
            if (_isProcessing) return;
            _isProcessing = true;

            _processingTask = Task.Run(async () =>
            {
                await foreach (var request in _requestChannel.Reader.ReadAllAsync())
                {
                    try
                    {
                        await EnsureConnectionAsync();
                        var response = await SendAndReceiveInternalAsync(request.Payload);
                        request.CompletionSource.SetResult(response);
                    }
                    catch (Exception ex)
                    {
                        request.CompletionSource.SetException(ex);
                        Metrics.ErrorCount.Increment();
                    }
                }

                _isProcessing = false;
            });
        }
    }

    private async Task<byte[]> SendAndReceiveInternalAsync(byte[] request)
    {
        CheckConnection();
        await EnsureConnectionAsync();
        try
        {
            lock (_syncLock)
            {
                ushort command = BitConverter.ToUInt16(request, 0);
                long context = BitConverter.ToInt64(request, 12);
                LogSendPacket(command, request.Length, context);
                _networkStream.Write(request, 0, request.Length);
                LastActivity = DateTime.UtcNow;
            }

            var headerBuffer = new byte[HeaderSize];
            await ReadFullAsync(headerBuffer, 0, HeaderSize);

            int dataLength = BitConverter.ToUInt16(headerBuffer, 2);
            var fullResponse = new byte[24 + dataLength];
            Array.Copy(headerBuffer, fullResponse, HeaderSize);

            if (dataLength > 0)
            {
                await ReadFullAsync(fullResponse, HeaderSize, dataLength);
            }

            ValidateChecksum(fullResponse);
            ValidateResponseStatus(fullResponse);

            return fullResponse;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "网络通信异常");
            Dispose();
            throw new CipException("连接已中断", ex);
        }
    }
    #endregion

    #region 数据读写
    public async Task<object> ReadTagAsync(string tagName)
    {
        var request = BuildReadTagRequest(tagName);
        var response = await SendAsync(request);
        return ParseReadResponse(response);
    }

    private byte[] BuildReadTagRequest(string tagName)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        var header = BuildHeaderPacket(CommandSendRRData, _sessionHandle);
        writer.Write(header);

        // CIP服务数据
        var cipPayload = BuildCipReadService(tagName);
        writer.Write((ushort)0);         // Interface Handle
        writer.Write((ushort)Timeout);        // Timeout
        writer.Write(cipPayload);

        var packet = ms.ToArray();
        BitConverter.GetBytes((ushort)(packet.Length - HeaderSize)).CopyTo(packet, 2);
        return packet;
    }

    private byte[] BuildCipReadService(string tagName)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)0x52);
        writer.Write((byte)0x02);

        var path = EncodeTagPath(tagName);
        writer.Write(path);

        return ms.ToArray();
    }

    private object ParseReadResponse(byte[] response)
    {
        var sessionHandle = BitConverter.ToUInt32(response.AsSpan()[4..8]);
        if (sessionHandle != _sessionHandle)
        {

        }
        const int dataOffset = HeaderSize + 6;
        const int dataTypeOffset = HeaderSize + 5;
        byte dataType = response[dataTypeOffset];

        return dataType switch
        {
            0xC1 => response[dataOffset] == 1,
            0xC2 => BitConverter.ToUInt16(response, dataOffset),
            0xC3 => BitConverter.ToUInt32(response, dataOffset),
            0xC4 => BitConverter.ToUInt64(response, dataOffset),
            0xCA => BitConverter.ToSingle(response, dataOffset),
            0xCB => BitConverter.ToDouble(response, dataOffset),
            0xDA => Encoding.UTF8.GetString(response, dataOffset, response.Length - dataOffset - 2),
            _ => throw new CipException($"不支持的数据类型: 0x{dataType:X2}")
        };
    }

    public async Task WriteTagAsync(string tagName, object value)
    {
        var request = BuildWriteTagRequest(tagName, value);
        var response = await SendAsync(request);
        //ValidateResponseStatus(response);

        ParseWriteResponse(response);
    }

    private byte[] BuildWriteTagRequest(string tagName, object value)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        var header = BuildHeaderPacket(CommandSendRRData, _sessionHandle);
        writer.Write(header);

        var cipPayload = BuildCipWriteService(tagName, value);
        writer.Write((ushort)0);         // Interface Handle
        writer.Write((ushort)Timeout);        // Timeout
        writer.Write(cipPayload);

        var packet = ms.ToArray();
        BitConverter.GetBytes((ushort)(packet.Length - HeaderSize)).CopyTo(packet, 2);

        return packet;
    }

    private byte[] BuildCipWriteService(string tagName, object value)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)0x53);
        writer.Write((byte)0x02);

        var path = EncodeTagPath(tagName);
        writer.Write(path);

        switch (value)
        {
            case bool boolValue:
                writer.Write((byte)0xC1);  // 1字节布尔类型
                writer.Write(boolValue ? (byte)1 : (byte)0);
                break;
            case ushort ushortValue:
                writer.Write((byte)0xC2);  // 4字节无符号整数类型
                writer.Write(ushortValue);
                break;
            case uint uintValue:
                writer.Write((byte)0xC3);  // 4字节无符号整数类型
                writer.Write((byte)4);
                writer.Write(uintValue);
                break;
            case ulong ulongValue:
                writer.Write((byte)0xC4);  // 4字节整数类型
                writer.Write(ulongValue);
                break;
            case string strValue:
                writer.Write((byte)0xDA);  // 字符串类型
                var strBytes = Encoding.UTF8.GetBytes(strValue);
                writer.Write((ushort)strBytes.Length);
                writer.Write(strBytes);
                break;
            case float floatValue:
                writer.Write((byte)0xCA);  // 浮点数类型
                writer.Write(floatValue);
                break;
            case double doubleValue:
                writer.Write((byte)0xCB);  // 浮点数类型
                writer.Write(doubleValue);
                break;
            default:
                throw new InvalidOperationException("不支持的数据类型");
        }

        return ms.ToArray();
    }

    private void ParseWriteResponse(byte[] response)
    {
        const int dataOffset = 28;
        byte dataType = response[dataOffset - 1];

        if (dataType != 0x00)
        {
            throw new CipException($"写入操作失败，返回的状态类型: 0x{dataType:X2}");
        }

        _logger?.LogInformation("写入操作成功");


        var sessionHandle = BitConverter.ToUInt32(response.AsSpan()[4..8]);
        if (sessionHandle != _sessionHandle)
        {

        }
    }
    #endregion

    #region 校验
    private void ValidateChecksum(byte[] fullResponse)
    {
        Console.WriteLine(BitConverter.ToString(fullResponse));
        ushort dataLength = BitConverter.ToUInt16(fullResponse, 2);
        if (fullResponse.Length == HeaderSize + dataLength)
        {
            _logger?.LogDebug("响应长度符合预期，无CRC校验");
        }
        // 实现CRC校验逻辑
        if (fullResponse.Length == HeaderSize + dataLength + 2)
        {
            ushort receivedCrc = BitConverter.ToUInt16(fullResponse, fullResponse.Length - 2);
            if (receivedCrc == 0)
            {
                _logger?.LogDebug("响应CRC字段为0，跳过CRC校验");
                return;
            }

            ushort calculatedCrc = CalculateCRC16(fullResponse, 0, fullResponse.Length - 2);
            if (receivedCrc != calculatedCrc)
            {
                throw new CipException($"校验失败 (计算值:0x{calculatedCrc:X4} 接收值:0x{receivedCrc:X4})");
            }
        }
        else
        {
            // 如果响应长度不符合预期，可以记录日志或抛出异常
            _logger?.LogWarning("响应长度异常，无法进行CRC校验");
        }
    }

    private static ushort CalculateCRC16(byte[] data, int offset, int length)
    {
        ushort crc = 0xFFFF;
        for (int i = offset; i < offset + length; i++)
        {
            crc ^= data[i];
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 0x0001) != 0)
                {
                    crc >>= 1;
                    crc ^= 0xA001;
                }
                else
                {
                    crc >>= 1;
                }
            }
        }

        return crc;
    }

    private void ValidateResponseStatus(byte[] fullResponse)
    {
        uint status = BitConverter.ToUInt32(fullResponse, 8);
        if (status != 0)
        {
            CipException.ThrowFromStatus(status);
        }
    }
    #endregion

    private async Task ReadFullAsync(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            var read = await _networkStream.ReadAsync(buffer, totalRead + offset, count - totalRead);
            if (read == 0)
            {
                //throw new CipException("CIP客户端连接已断开");
            }
            totalRead += read;
        }

        _logger?.LogDebug("已完整读取数据: {TotalRead} bytes", totalRead);
    }
    #region 辅助方法
    private static byte[] EncodeTagPath(string tagName)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        foreach (var segment in tagName.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(segment);
            writer.Write((byte)0x91);
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }

        return ms.ToArray();
    }

    private byte[] GetNextContext()
    {
        var context = Interlocked.Increment(ref _senderContext);
        return BitConverter.GetBytes(context);
    }

    private byte[] BuildHeaderPacket(ushort command, uint sessionHandle)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(command);
        writer.Write((ushort)0);     // Length placeholder
        writer.Write(sessionHandle);
        writer.Write((uint)0);       // Status
        writer.Write(GetNextContext());   // Sender Context
        writer.Write((uint)0);       // Options

        return ms.ToArray();
    }

    private void CheckConnection()
    {
        if (!IsConnected) throw new CipException("CIP客户端未连接");
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "发送CIP数据包: [CMD: 0x{Command:X4} (Size: {Size} bytes) Context: 0x{Context:X16}")]
    partial void LogSendPacket(ushort command, int size, long context);
    #endregion

    #region 保活机制

    private void StartKeepAlive(int interval = 3000)
    {
        _keepAliveTimer = new Timer(async _ =>
        {
            try
            {
                await EnsureConnectionAsync();
                if ((DateTime.UtcNow - LastActivity).TotalMilliseconds > interval)
                {
                    await SendNopAsync();
                    await Console.Out.WriteLineAsync($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} Send Nop");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "保活失败: {Message}", ex.Message);
            }
        }, null, interval, interval);
    }

    private async Task SendNopAsync()
    {
        var nopPacket = BuildNopPacket();
        await SendAsync(nopPacket);
    }

    private byte[] BuildNopPacket()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        var header = BuildHeaderPacket(0x00, _sessionHandle);
        return header;
    }

    #endregion
}