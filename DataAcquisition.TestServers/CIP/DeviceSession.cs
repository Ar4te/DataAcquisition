using System.Net;

namespace DataAcquisition.TestServers.CIP;

/// <summary>
/// 会话管理
/// </summary>
public sealed class DeviceSession : IDisposable
{
    public uint SessionHandle { get; }
    public DateTime LastActive { get => _lastActive; set => _lastActive = value; }
    public IPEndPoint RemoteEP { get; }


    private readonly Timer _inactivityTimer;
    private DateTime _lastActive;
    private readonly object _timeLock = new();

    public DeviceSession(uint handle, IPEndPoint ep, int timeoutMs)
    {
        SessionHandle = handle;
        RemoteEP = ep;
        _inactivityTimer = new Timer(_ => OnTimeout(), null, timeoutMs, Timeout.Infinite);
        UpdateActiveTime(timeoutMs);
    }

    public void UpdateActiveTime(int sessionTimeout)
    {
        lock (_timeLock)
        {
            _inactivityTimer.Change(sessionTimeout, Timeout.Infinite);
            _lastActive = DateTime.UtcNow;
        }
    }

    public bool IsExpired(int timeoutMs)
    {
        lock (_timeLock)
        {
            return (DateTime.UtcNow - _lastActive).TotalMilliseconds > timeoutMs;
        }
    }

    public event EventHandler? SessionExpired;
    private void OnTimeout() => SessionExpired?.Invoke(this, EventArgs.Empty);

    public void Dispose() => _inactivityTimer.Dispose();
}
