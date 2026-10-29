using System.Net;
using System.Net.Sockets;

namespace LanChess.Net;

/// <summary>Accepts incoming connections for as long as it runs. Each one is handed out unauthenticated.</summary>
public sealed class HostListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();

    public HostListener(int port)
    {
        _listener = new TcpListener(IPAddress.Any, port);
    }

    /// <summary>Raised on a background thread for every new connection.</summary>
    public event Action<PeerConnection>? Accepted;

    /// <summary>Throws SocketException if the port is already in use.</summary>
    public void Start()
    {
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                Accepted?.Invoke(PeerConnection.FromClient(client));
            }
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
