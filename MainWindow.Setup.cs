using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using LanChess.Chess;
using LanChess.Net;
using LanChess.Storage;

namespace LanChess;

/// <summary>The setup screen: hosting, the games list, and joining.</summary>
public partial class MainWindow
{
    private void PlayerNameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _settings.PlayerName = MyName;
        _settings.Save();
        UpdateAnnouncement();
    }

    private void TimeControlBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CustomTimePanel.Visibility = ReferenceEquals(TimeControlBox.SelectedItem, TimeControl.Custom)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private bool TryReadTimeControl(out TimeControl control)
    {
        control = (TimeControl)TimeControlBox.SelectedItem;
        if (!ReferenceEquals(control, TimeControl.Custom))
        {
            return true;
        }

        if (!int.TryParse(CustomMinutesBox.Text, out int minutes) || minutes is < 1 or > 180
            || !int.TryParse(CustomIncrementBox.Text, out int increment) || increment is < 0 or > 60)
        {
            HostInfoText.Text = "Custom time: minutes must be 1–180 and increment 0–60.";
            return false;
        }

        control = TimeControl.CreateCustom(minutes, increment);
        return true;
    }

    private void HostButton_Click(object sender, RoutedEventArgs e)
    {
        if (_listener is null)
        {
            StartHosting();
        }
        else
        {
            StopHosting();
        }
    }

    private void StartHosting()
    {
        if (!TryReadTimeControl(out var control))
        {
            return;
        }

        if (!int.TryParse(HostPortBox.Text, out int port) || port is < 1024 or > 65535)
        {
            HostInfoText.Text = "Port must be a number between 1024 and 65535.";
            return;
        }

        var listener = new HostListener(port);
        listener.Accepted += peer => Dispatcher.InvokeAsync(() => AttachIncoming(peer));

        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            listener.Dispose();
            HostInfoText.Text = $"Could not use port {port}: {ex.Message}";
            return;
        }

        _listener = listener;
        _isHost = true;
        _hostControl = control;
        _hostPort = port;
        _settings.Port = port;
        _settings.Save();

        _announcer = new LobbyAnnouncer(new LobbyAnnouncement(LobbyAnnouncer.AppTag, MyName, port, control.Name, false));

        HostButton.Content = "Stop hosting";
        HostPortBox.IsEnabled = false;
        TimeControlBox.IsEnabled = false;
        HostInfoText.Text = $"Hosting as {MyName}. Friends on your network will see this game in their list.\n"
            + $"Or they can type: {AddressList(port)}\n"
            + "If Windows asks about firewall access, allow it on private networks.";
    }

    private void StopHosting()
    {
        _listener?.Dispose();
        _listener = null;
        _announcer?.Dispose();
        _announcer = null;
        _isHost = false;

        HostButton.Content = "Start hosting";
        HostPortBox.IsEnabled = true;
        TimeControlBox.IsEnabled = true;
        HostInfoText.Text = "";
    }

    private static string AddressList(int port)
    {
        var addresses = PeerConnection.LocalIPv4Addresses();
        return addresses.Count == 0
            ? $"your IP address, port {port}"
            : string.Join(" or ", addresses.Select(a => $"{a}:{port}"));
    }

    private void RefreshLobby()
    {
        if (_browser is null)
        {
            return;
        }

        var entries = _browser.Snapshot();
        string key = string.Join("|", entries.Select(e => $"{e.Endpoint}{e.Name}{e.TimeControl}{e.Busy}"));
        if (key == _lobbyKey)
        {
            return;
        }

        _lobbyKey = key;
        var selected = (LobbyList.SelectedItem as LobbyEntry)?.Endpoint;
        LobbyList.ItemsSource = entries;
        if (selected is not null)
        {
            LobbyList.SelectedItem = entries.FirstOrDefault(e => e.Endpoint == selected);
        }
    }

    private async void JoinButton_Click(object sender, RoutedEventArgs e)
    {
        string address;
        int port;

        if (LobbyList.SelectedItem is LobbyEntry entry)
        {
            address = entry.Address;
            port = entry.Port;
        }
        else if (!TryParseAddress(JoinAddressBox.Text.Trim(), out address, out port))
        {
            JoinStatusText.Text = "Pick a game from the list, or enter the host's address.";
            return;
        }

        await JoinAsync(address, port);
    }

    private void LobbyList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LobbyList.SelectedItem is LobbyEntry entry)
        {
            _ = JoinAsync(entry.Address, entry.Port);
        }
    }

    private async Task JoinAsync(string address, int port)
    {
        if (_listener is not null)
        {
            JoinStatusText.Text = "Stop hosting before joining another game.";
            return;
        }

        JoinButton.IsEnabled = false;
        JoinStatusText.Text = $"Connecting to {address}:{port}…";

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var peer = await PeerConnection.JoinAsync(address, port, timeout.Token);

            _peer?.Dispose();
            _peer = peer;
            _hostAddress = address;
            _joinPort = port;
            AttachIncoming(peer);
            peer.Send(new WireMessage { Type = WireTypes.Hello, Name = MyName });
            JoinStatusText.Text = "Connected. Waiting for the host to start…";
        }
        catch (OperationCanceledException)
        {
            JoinStatusText.Text = "Timed out. Check the address, and that the host is hosting.";
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            JoinStatusText.Text = $"Could not connect: {ex.Message}";
        }
        finally
        {
            JoinButton.IsEnabled = true;
        }
    }

    private static bool TryParseAddress(string text, out string host, out int port)
    {
        host = text;
        port = PeerConnection.DefaultPort;

        int colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out int parsed))
        {
            host = text[..colon];
            port = parsed;
        }

        return !string.IsNullOrWhiteSpace(host);
    }
}
