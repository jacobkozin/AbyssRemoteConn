using System.Net.WebSockets;
using System.Text.Json;

namespace AbyssRemote.Host;

public enum SocketKind { Video, Control, Audio }

/// <summary>WebSocket close codes the browser client understands (4000-4999 are app-defined).</summary>
public static class CloseCodes
{
    public const int Revoked = 4001;      // device signed out, revoked, or expired
    public const int Replaced = 4008;     // a newer connection from the same device took over
    public const int Busy = 4029;         // host-wide connection limit reached
    public const int Unsupported = 4415;  // e.g. no hardware video encoder on this PC
}

/// <summary>A WebSocket whose sends are serialized, so other code (revocation, replacement)
/// can close it with a meaningful code while its owner is busy streaming.</summary>
public sealed class LiveSocket
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public LiveSocket(WebSocket socket) => Socket = socket;

    public WebSocket Socket { get; }

    /// <summary>Sends one complete message. Returns false once the socket is closing or closed.</summary>
    public async Task<bool> SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            if (Socket.State != WebSocketState.Open) return false;
            await Socket.SendAsync(data, type, endOfMessage: true, ct);
            return true;
        }
        finally { _sendLock.Release(); }
    }

    public Task<bool> SendJsonAsync(object message, CancellationToken ct) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, ct);

    /// <summary>Starts the close handshake. A peer that never answers (Wi-Fi gone, phone asleep)
    /// is aborted a few seconds later so it cannot hold a connection slot.</summary>
    public async Task CloseAsync(int code, string reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await _sendLock.WaitAsync(timeout.Token);
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await Socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, timeout.Token);
            }
            finally { _sendLock.Release(); }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
        {
            try { if (Socket.State != WebSocketState.Closed) Socket.Abort(); }
            catch (ObjectDisposedException) { }
        }, TaskScheduler.Default);
    }
}

/// <summary>Tracks every live WebSocket by device. Revoking a device closes its sockets at
/// once, and a reconnect replaces an older socket instead of being refused while the old
/// one (often a dead mobile connection) is still timing out.</summary>
public sealed class SessionRegistry
{
    public const int MaxPerKind = 6;

    private readonly object _sync = new();
    private readonly List<Entry> _entries = new();

    private sealed record Entry(string DeviceId, SocketKind Kind, LiveSocket Socket);

    // Two video sockets per device lets one browser show two monitors in two tabs.
    public static int PerDeviceLimit(SocketKind kind) => kind == SocketKind.Video ? 2 : 1;

    /// <summary>Returns null when the host-wide limit for this kind is reached.</summary>
    public IDisposable? Register(string deviceId, SocketKind kind, LiveSocket socket)
    {
        var replaced = new List<Entry>();
        var entry = new Entry(deviceId, kind, socket);
        lock (_sync)
        {
            var sameDevice = _entries.Where(e => e.DeviceId == deviceId && e.Kind == kind).ToList();
            int excess = Math.Max(0, sameDevice.Count + 1 - PerDeviceLimit(kind));
            if (_entries.Count(e => e.Kind == kind) - excess >= MaxPerKind) return null;
            foreach (var old in sameDevice.Take(excess))
            {
                _entries.Remove(old);
                replaced.Add(old);
            }
            _entries.Add(entry);
        }
        foreach (var old in replaced) _ = old.Socket.CloseAsync(CloseCodes.Replaced, "Replaced by a newer connection");
        return new Registration(this, entry);
    }

    public int Count(SocketKind kind) { lock (_sync) return _entries.Count(e => e.Kind == kind); }

    public void CloseDevice(string deviceId) => CloseWhere(e => e.DeviceId == deviceId);

    public void CloseAll() => CloseWhere(_ => true);

    private void CloseWhere(Func<Entry, bool> match)
    {
        List<Entry> closing;
        lock (_sync)
        {
            closing = _entries.Where(match).ToList();
            foreach (var e in closing) _entries.Remove(e);
        }
        foreach (var e in closing) _ = e.Socket.CloseAsync(CloseCodes.Revoked, "This device is no longer paired");
    }

    private sealed class Registration(SessionRegistry owner, Entry entry) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._sync) owner._entries.Remove(entry);
        }
    }
}
