using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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
    #endregion

    #region 状态存储
    private readonly ConcurrentDictionary<uint, DeviceSession> _sessions = new();
    private readonly ConcurrentDictionary<string, TagValue> _tags = new();
    private readonly TcpListener _listener;
    private bool _isRunning;
    private SimulatorConfig _config;

    #endregion

    #region 启动/停止
    public CipSimulator(SimulatorConfig config)
    {
        _listener = new TcpListener(IPAddress.Any, Port);
        _config = config;
    }

    public void Start()
    {
        _isRunning = true;
        _listener.Start();

        Task.Run(ClientAcceptLoop);
        Task.Run(SessionCleanupLoop);

        Console.WriteLine($"CIP模拟器已启动，监听端口：{Port}");
    }

    public void Stop()
    {
        _isRunning = false;
        _listener?.Stop();
        Console.WriteLine("CIP模拟器已停止");
    }
    #endregion

    #region 客户端处理
    private async Task ClientAcceptLoop()
    {
        while (_isRunning)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClient(client));
            }
            catch (ObjectDisposedException)
            {
                // 正常停止
            }
        }
    }

    private async Task HandleClient(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var remoteEndPoint = (IPEndPoint)client.Client.RemoteEndPoint!;
            var buffer = new byte[4096];
            while (client.Connected)
            {
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;
                var request = buffer.AsSpan(0, bytesRead).ToArray();
                Console.WriteLine($"监听{remoteEndPoint.Address}:{remoteEndPoint.Port}：{BitConverter.ToString(request)}\n");
                var response = ProcessRequest(request, remoteEndPoint);
                await stream.WriteAsync(response, 0, response.Length);
                Console.WriteLine($"响应{remoteEndPoint.Address}:{remoteEndPoint.Port}：{BitConverter.ToString(response)}\n");
            }
            Console.WriteLine($"{remoteEndPoint.Address}:{remoteEndPoint.Port}已断开\n");
        }
    }
    #endregion

    #region 协议处理核心
    private byte[] ProcessRequest(byte[] request, IPEndPoint remoteEp)
    {
        try
        {
            var command = BitConverter.ToUInt16(request);
            return command switch
            {
                // 注册会话
                CommandRegisterSession => HandleRegisterSession(request, remoteEp),
                // 发送RR数据
                CommandSendRRData => HandleSendRRData(request),
                _ => BuildErrorResponse(0x01, "不支持的指令")
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"协议处理异常: {ex.Message}\n");
            return BuildErrorResponse(0x02, "协议错误");
        }
    }

    private byte[] HandleRegisterSession(byte[] request, IPEndPoint remoteEp)
    {
        var sessionHandle = (uint)new Random().Next(1, int.MaxValue);  // 随机生成会话句柄
        var session = new DeviceSession(sessionHandle, remoteEp);

        _sessions.TryAdd(sessionHandle, session);

        var response = new byte[HeaderSize + 4];
        BitConverter.GetBytes(CommandRegisterSession).CopyTo(response.AsSpan(0, response.Length));
        BitConverter.GetBytes((ushort)4).CopyTo(response, 2); // 数据长度
        BitConverter.GetBytes(sessionHandle).CopyTo(response, 4);
        BitConverter.GetBytes((uint)0).CopyTo(response, 8); // 状态码
        Console.WriteLine($"{session.RemoteEP.Address}:{session.RemoteEP.Port}注册成功：0x{sessionHandle:X8}\n");
        return response;
    }

    private byte[] HandleSendRRData(Span<byte> request)
    {
        var sessionHandle = BitConverter.ToUInt32(request[4..8]);
        if (!_sessions.TryGetValue(sessionHandle, out var session))
            return BuildErrorResponse(0x05, "无效会话");

        session.LastActive = DateTime.UtcNow;

        // 解析服务请求
        var serviceOffset = HeaderSize + 4; // 跳过接口句柄和超时
        var serviceCode = request[serviceOffset];

        return serviceCode switch
        {
            // 读取标签
            0x52 => HandleReadTag(request, serviceOffset + 1, sessionHandle),
            // 写入标签
            0x53 => HandleWriteTag(request, serviceOffset + 1, sessionHandle),
            _ => BuildErrorResponse(0x01, "不支持的服务")
        };
    }

    private byte[] HandleWriteTag(Span<byte> request, int offset, uint sessionHandle)
    {
        // 解析标签路径
        var pathSize = request[offset++];
        var tagName = ParseTagPath(request[offset..], ref offset);

        // 检查标签是否存在
        if (!_tags.TryGetValue(tagName, out var tag))
        {
            return BuildErrorResponse(0x14, "标签不存在");
        }

        // 解析写入的数据类型和值
        var dataType = DataTypeExtensions.GetDataType(request[offset++]); // 获取数据类型
        var valueSize = request[offset++]; // 获取数据长度
        var valueBytes = request.Slice(offset, valueSize); // 获取数据值

        // 将接收到的值写入标签
        bool writeSuccess = WriteTagValue(tag, dataType, valueBytes);
        if (!writeSuccess)
        {
            return BuildErrorResponse(0x15, "写入数据失败");
        }

        // 构建并返回响应
        var response = new byte[HeaderSize + 4];
        BuildResponseHeader(response, CommandSendRRData, sessionHandle);

        // 状态码设为0，表示成功
        BitConverter.GetBytes((uint)0).CopyTo(response, HeaderSize);

        return response;
    }

    #endregion

    #region 标签操作
    private byte[] HandleReadTag(Span<byte> request, int offset, uint sessionHandle)
    {
        // 解析标签路径
        var pathSize = request[offset++];
        var tagName = ParseTagPath(request[offset..], ref offset);

        // 检查标签是否存在
        if (!_tags.TryGetValue(tagName, out var tag))
        {
            return BuildErrorResponse(0x14, "标签不存在");
        }

        // 构建CIP响应
        var response = new byte[HeaderSize + 6 + tag.Type.GetTypeLength()];
        BuildResponseHeader(response, CommandSendRRData, sessionHandle);

        // 保留字段
        response[HeaderSize + 4] = 0x00;

        // 标签数据类型
        response[HeaderSize + 5] = (byte)tag.Type;

        // 将标签值写入响应
        WriteTagValueToResponse(response.AsSpan()[(HeaderSize + 6)..], tag);

        return response;
    }

    private static void WriteTagValueToResponse(Span<byte> target, TagValue tag)
    {
        switch (tag.Type)
        {
            case DataType.BOOL:
                target[0] = (bool)tag.Value ? (byte)1 : (byte)0;
                break;
            case DataType.UINT16:
                BitConverter.GetBytes((ushort)tag.Value).CopyTo(target);
                break;
            case DataType.UINT32:
                BitConverter.GetBytes((uint)tag.Value).CopyTo(target);
                break;
            case DataType.UINT64:
                BitConverter.GetBytes((ulong)tag.Value).CopyTo(target);
                break;
            case DataType.SINGLE:
                BitConverter.GetBytes((float)tag.Value).CopyTo(target);
                break;
            case DataType.STRING:
                var stringValue = (string)tag.Value;
                var stringBytes = Encoding.ASCII.GetBytes(stringValue);
                stringBytes.CopyTo(target);
                break;
            default:
                throw new InvalidOperationException("不支持的标签类型");
        }
    }

    private static string ParseTagPath(Span<byte> pathData, ref int offset)
    {
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
        offset += position - 1;
        return sb.ToString();
    }

    private static bool WriteTagValue(TagValue tag, DataType dataType, Span<byte> valueBytes)
    {
        try
        {
            switch (dataType)
            {
                case DataType.BOOL:
                    tag.Value = valueBytes[0] == 1;
                    break;
                case DataType.UINT16:
                    tag.Value = BitConverter.ToUInt16(valueBytes);
                    break;
                case DataType.UINT32:
                    tag.Value = BitConverter.ToUInt32(valueBytes);
                    break;
                case DataType.UINT64:
                    tag.Value = BitConverter.ToUInt64(valueBytes);
                    break;
                case DataType.SINGLE:
                    tag.Value = BitConverter.ToSingle(valueBytes);
                    break;
                case DataType.STRING:
                    tag.Value = Encoding.ASCII.GetString(valueBytes.ToArray());
                    break;
                default:
                    return false; // 不支持的类型
            }

            return true;
        }
        catch
        {
            return false; // 如果转换失败，返回false
        }
    }
    #endregion

    #region 工具方法
    private static byte[] BuildErrorResponse(ushort errorCode, string message)
    {
        var response = new byte[HeaderSize + 4];
        BitConverter.GetBytes(0x8000 | errorCode).CopyTo(response, 0);
        BitConverter.GetBytes((ushort)4).CopyTo(response, 2);
        BitConverter.GetBytes((uint)0x00000000).CopyTo(response, 8);
        Console.WriteLine($"返回错误: {message}\n");
        return response;
    }

    private static void BuildResponseHeader(byte[] response, ushort command, uint sessionHandle)
    {
        BitConverter.GetBytes(command).CopyTo(response.AsSpan(0, response.Length));
        BitConverter.GetBytes((ushort)(response.Length - HeaderSize)).CopyTo(response, 2);
        BitConverter.GetBytes(sessionHandle).CopyTo(response, 4);
        BitConverter.GetBytes((uint)0).CopyTo(response, 8); // 状态码
    }
    #endregion

    #region 会话维护
    private async Task SessionCleanupLoop()
    {
        while (_isRunning)
        {
            await Task.Delay(60000); // 每分钟清理一次
            var cutoff = DateTime.UtcNow.AddMilliseconds(-_config.SessionTimeout);

            foreach (var session in _sessions.Values.Where(s => s.LastActive < cutoff))
            {
                _sessions.TryRemove(session.SessionHandle, out _);
                Console.WriteLine($"清理过期会话: {session.SessionHandle:X8}");
            }
        }
    }
    #endregion

    #region 设备管理接口
    public void AddTag(string name, DataType type, object initialValue)
    {
        _tags.TryAdd(name, new TagValue(type, initialValue));
    }

    public void UpdateTag(string name, object value)
    {
        if (_tags.TryGetValue(name, out var tag))
            tag.Value = value;
    }
    #endregion

    public void Dispose() => Stop();
}
