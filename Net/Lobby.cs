using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LanChess.Net;

/// <summary>What a hosting computer broadcasts about itself.</summary>
public sealed record LobbyAnnouncement(string App, string Name, int Port, string TimeControl, bool Busy);

/// <summary>A hosted game seen on the local network. The address comes from the packet's sender.</summary>
public sealed record LobbyEntry(string Address, string Name, int Port, string TimeControl, bool Busy)
{
    public string Endpoint => $"{Address}:{Port}";
    public string Status => Busy ? "In a game" : "Open";
}

/// <summary>Host side: broadcasts this game on the LAN every two seconds.</summary>
public sealed class LobbyAnnouncer : IDisposable
{
    public const int LobbyPort = 5056;
    public const string AppTag = "LanChess";

    private readonly CancellationTokenSource _stop = new();
    private volatile LobbyAnnouncement _current;

    public LobbyAnnouncer(LobbyAnnouncement initial)
    {
        _current = initial;
        _ = RunAsync();
    }

    public void Update(LobbyAnnouncement announcement) => _current = announcement;

    private async Task RunAsync()
    {
        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            var target = new IPEndPoint(IPAddress.Broadcast, LobbyPort);

            while (true)
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_current));
                await udp.SendAsync(bytes, bytes.Length, target);
                await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token);
            }
        }
        catch (Exception)
        {
        }
    }

    public void Dispose() => _stop.Cancel();
}

/// <summary>Guest side: listens for announcements and keeps the games seen recently.</summary>
public sealed class LobbyBrowser : IDisposable
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(6);

    private readonly UdpClient _udp = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, (LobbyEntry Entry, DateTime SeenUtc)> _seen = new();
    private readonly object _lock = new();

    /// <summary>Throws SocketException if the lobby port is already in use.</summary>
    public LobbyBrowser()
    {
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, LobbyAnnouncer.LobbyPort));
        _ = ReceiveAsync();
    }

    public IReadOnlyList<LobbyEntry> Snapshot()
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - StaleAfter;
            foreach (var key in _seen.Where(p => p.Value.SeenUtc < cutoff).Select(p => p.Key).ToList())
            {
                _seen.Remove(key);
            }

            return _seen.Values.Select(v => v.Entry).OrderBy(e => e.Name).ToList();
        }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (true)
            {
                var packet = await _udp.ReceiveAsync(_stop.Token);
                var announcement = TryParse(packet.Buffer);
                if (announcement is null)
                {
                    continue;
                }

                var entry = new LobbyEntry(
                    packet.RemoteEndPoint.Address.ToString(),
                    announcement.Name,
                    announcement.Port,
                    announcement.TimeControl,
                    announcement.Busy);

                lock (_lock)
                {
                    _seen[entry.Endpoint] = (entry, DateTime.UtcNow);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private static LobbyAnnouncement? TryParse(byte[] bytes)
    {
        try
        {
            var announcement = JsonSerializer.Deserialize<LobbyAnnouncement>(bytes);
            return announcement?.App == LobbyAnnouncer.AppTag ? announcement : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
    }
}
