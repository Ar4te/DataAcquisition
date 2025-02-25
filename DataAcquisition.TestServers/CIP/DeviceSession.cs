using System.Net;

namespace DataAcquisition.TestServers.CIP;

/// <summary>
/// 会话管理
/// </summary>
public class DeviceSession
{
    public uint SessionHandle { get; }
    public DateTime LastActive { get; set; }
    public IPEndPoint RemoteEP { get; }

    public DeviceSession(uint handle, IPEndPoint ep)
    {
        SessionHandle = handle;
        RemoteEP = ep;
        LastActive = DateTime.UtcNow;
    }
}