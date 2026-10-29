using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LanChess.Net;

/// <summary>
/// One TCP connection to the other player. Messages are one JSON object per line.
/// A heartbeat runs underneath: if nothing arrives for a while, the connection is treated as dead.
/// </summary>
public sealed class PeerConnection : IDisposable
{
    public const int DefaultPort = 5055;

    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(10);

    private readonly TcpClient _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly object _sendLock = new();
    private readonly CancellationTokenSource _stop = new();
    private long _lastReceivedTicks;

    private PeerConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;

        var stream = client.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>Raised on a background thread.</summary>
    public event Action<string>? LineReceived;

    /// <summary>Raised on a background thread once the connection is gone.</summary>
    public event Action? Closed;

    public static PeerConnection FromClient(TcpClient client) => new(client);

    public static async Task<PeerConnection> JoinAsync(string host, int port, CancellationToken token)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, token);
            return new PeerConnection(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Starts reading. Subscribe to events before calling this so no line is missed.</summary>
    public void Start()
    {
        Volatile.Write(ref _lastReceivedTicks, DateTime.UtcNow.Ticks);
        _ = ReadLoopAsync();
        _ = HeartbeatAsync();
    }

    public void Send(WireMessage message) => SendRaw(message.ToJson());

    private void SendRaw(string json)
    {
        try
        {
            lock (_sendLock)
            {
                _writer.WriteLine(json);
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _reader.ReadLineAsync()) != null)
            {
                Volatile.Write(ref _lastReceivedTicks, DateTime.UtcNow.Ticks);

                if (WireMessage.Parse(line)?.Type != WireTypes.Ping)
                {
                    LineReceived?.Invoke(line);
                }
            }
        }
        catch (Exception)
        {
        }

        _stop.Cancel();
        Closed?.Invoke();
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(PingInterval, _stop.Token);
                SendRaw(new WireMessage { Type = WireTypes.Ping }.ToJson());

                var silence = DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastReceivedTicks));
                if (silence > SilenceLimit)
                {
                    Dispose();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _client.Dispose();
    }

    public static IReadOnlyList<string> LocalIPv4Addresses() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                        && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .Where(ip => !ip.StartsWith("169.254.", StringComparison.Ordinal))
            .ToList();
}
