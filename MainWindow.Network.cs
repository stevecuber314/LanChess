using System.Windows;
using LanChess.Chess;
using LanChess.Net;

namespace LanChess;

/// <summary>
/// Connection handling. The host admits players and sends the full game state when someone rejoins.
/// A guest that loses its connection retries until the host answers or the reconnect window closes.
/// </summary>
public partial class MainWindow
{
    private void AttachIncoming(PeerConnection peer)
    {
        peer.LineReceived += line => Dispatcher.InvokeAsync(() => HandleIncoming(peer, line));
        peer.Closed += () => Dispatcher.InvokeAsync(() => OnPeerClosed(peer));
        peer.Start();
    }

    private void SendToPeer(WireMessage message) => _peer?.Send(message);

    private WireMessage ClockMessage(string type) => new()
    {
        Type = type,
        WhiteMs = ToMs(_clock!.WhiteTime),
        BlackMs = ToMs(_clock.BlackTime),
    };

    private void HandleIncoming(PeerConnection peer, string line)
    {
        var msg = WireMessage.Parse(line);
        if (msg is null)
        {
            return;
        }

        if (peer == _peer)
        {
            HandleLine(msg);
        }
        else if (_isHost && msg.Type == WireTypes.Hello)
        {
            Admit(peer, msg);
        }
        else
        {
            peer.Dispose();
        }
    }

    /// <summary>The host decides whether a new connection is a rejoin, a new player, or a refusal.</summary>
    private void Admit(PeerConnection peer, WireMessage hello)
    {
        bool rejoining = InProgress && hello.Token == _gameId.ToString("N");
        if (rejoining)
        {
            _peer?.Dispose();
            _peer = peer;
            _opponentName = hello.Name ?? _opponentName;
            AutoPause();
            _opponentOnline = true;
            _deadlineTimer.Stop();
            peer.Send(SyncMessage());
            ShowToast($"{_opponentName} reconnected. Press Resume when you're ready.");
            RefreshAll();
            return;
        }

        if (InProgress)
        {
            peer.Send(new WireMessage { Type = WireTypes.Reject, Reason = "This computer is already in a game." });
            peer.Dispose();
            return;
        }

        _peer?.Dispose();
        _gameId = Guid.NewGuid();
        _peer = peer;
        _opponentName = hello.Name ?? "Opponent";

        peer.Send(new WireMessage
        {
            Type = WireTypes.Start,
            Token = _gameId.ToString("N"),
            Name = MyName,
            TimeControl = _hostControl.Name,
            BaseMs = ToMs(_hostControl.Base),
            IncrementMs = ToMs(_hostControl.Increment),
        });

        BeginGame(PieceColor.White, _hostControl.Name, _hostControl.Base, _hostControl.Increment);
    }

    private void HandleLine(WireMessage msg)
    {
        switch (msg.Type)
        {
            case WireTypes.Start when _game is null:
                _gameId = Guid.TryParse(msg.Token, out var id) ? id : Guid.NewGuid();
                _opponentName = msg.Name ?? "Opponent";
                BeginGame(PieceColor.Black, msg.TimeControl ?? "", FromMs(msg.BaseMs), FromMs(msg.IncrementMs));
                break;

            case WireTypes.Reject:
                JoinStatusText.Text = msg.Reason ?? "The host refused the connection.";
                _peer?.Dispose();
                _peer = null;
                break;

            case WireTypes.Sync when !_isHost:
                ApplySync(msg);
                break;

            case WireTypes.Move when InProgress:
                HandleOpponentMove(msg);
                break;

            case WireTypes.Pause when InProgress:
                _clock!.Sync(FromMs(msg.WhiteMs), FromMs(msg.BlackMs), _game!.Current.ToMove, paused: true);
                ShowToast($"{_opponentName} paused the clock.");
                RefreshAll();
                break;

            case WireTypes.Resume when InProgress:
                _clock!.Sync(FromMs(msg.WhiteMs), FromMs(msg.BlackMs), _game!.Current.ToMove, paused: false);
                ShowToast($"{_opponentName} resumed the clock.");
                RefreshAll();
                break;

            case WireTypes.Resign when InProgress:
                NotifyOpponent($"{_opponentName} resigned.");
                FinishGame(GameStatus.Resigned, _me);
                break;

            case WireTypes.Timeout when InProgress:
                NotifyOpponent($"{_opponentName} ran out of time.");
                FinishGame(GameStatus.TimedOut, _me);
                break;

            case WireTypes.DrawOffer when InProgress:
                _drawOffer = _me.Opponent();
                NotifyOpponent($"{_opponentName} offers a draw.");
                RefreshAll();
                break;

            case WireTypes.DrawCancel when InProgress:
                _drawOffer = null;
                ShowToast($"{_opponentName} withdrew the draw offer.");
                RefreshAll();
                break;

            case WireTypes.DrawDecline when InProgress:
                _drawOffer = null;
                ShowToast($"{_opponentName} declined the draw.");
                RefreshAll();
                break;

            case WireTypes.DrawAccept when InProgress:
                NotifyOpponent($"{_opponentName} accepted the draw.");
                FinishGame(GameStatus.DrawAgreed, null);
                break;
        }
    }

    private void OnPeerClosed(PeerConnection peer)
    {
        if (peer != _peer)
        {
            return;
        }

        _peer = null;
        _opponentOnline = false;

        if (!InProgress)
        {
            if (_game is null && !_isHost)
            {
                JoinStatusText.Text = "The connection was closed.";
            }

            return;
        }

        AutoPause();
        _deadlineTimer.Stop();
        _deadlineTimer.Start();

        if (_isHost)
        {
            ShowToast($"{_opponentName} disconnected. Waiting up to {ReconnectWindow.TotalMinutes:0} minutes for them to rejoin…");
        }
        else
        {
            ShowToast("Connection lost. Reconnecting…");
            StartReconnect();
        }

        RefreshAll();
    }

    private void OnDeadline()
    {
        _deadlineTimer.Stop();
        if (!InProgress || _opponentOnline)
        {
            return;
        }

        ShowToast("Your opponent didn't return in time. The game ended without a result.");
        FinishGame(GameStatus.Abandoned, null);
    }

    private void StartReconnect()
    {
        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();
        _ = ReconnectAsync(_reconnectCts.Token);
    }

    /// <summary>Retries the host every couple of seconds until connected or cancelled.</summary>
    private async Task ReconnectAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(TimeSpan.FromSeconds(3));

                var peer = await PeerConnection.JoinAsync(_hostAddress, _joinPort, attempt.Token);
                if (token.IsCancellationRequested)
                {
                    peer.Dispose();
                    return;
                }

                _peer = peer;
                AttachIncoming(peer);
                peer.Send(new WireMessage { Type = WireTypes.Hello, Name = MyName, Token = _gameId.ToString("N") });
                return;
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // The host isn't reachable yet. Try again below.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private WireMessage SyncMessage() => new()
    {
        Type = WireTypes.Sync,
        Moves = _game!.History.Select(h => h.Move.ToUci()).ToList(),
        WhiteMs = ToMs(_clock!.WhiteTime),
        BlackMs = ToMs(_clock.BlackTime),
        Paused = _clock.Paused,
        DrawFrom = _drawOffer?.ToString(),
    };

    /// <summary>The guest adopts the host's game state after a reconnect.</summary>
    private void ApplySync(WireMessage msg)
    {
        var game = ChessGame.Replay(msg.Moves ?? new List<string>());
        if (game is null || _clock is null)
        {
            ShowToast("Could not restore the game from your opponent's copy. Please leave and start again.");
            return;
        }

        _game = game;
        _selected = null;
        _targets.Clear();
        _drawOffer = ParseSide(msg.DrawFrom);
        _clock.Sync(FromMs(msg.WhiteMs), FromMs(msg.BlackMs), game.Current.ToMove, msg.Paused);

        _opponentOnline = true;
        _deadlineTimer.Stop();
        ShowToast("Reconnected. Press Resume when you're ready.");
        RefreshAll();
    }

    private static PieceColor? ParseSide(string? side) =>
        side is not null && Enum.TryParse(side, out PieceColor color) ? color : null;
}
