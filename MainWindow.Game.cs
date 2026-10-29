using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LanChess.Chess;
using LanChess.Net;
using LanChess.Storage;

namespace LanChess;

/// <summary>Starting and ending games, moves, the clock, pause, draws and resignation.</summary>
public partial class MainWindow
{
    private void BeginGame(PieceColor me, string timeControlName, TimeSpan baseTime, TimeSpan increment)
    {
        _me = me;
        _flipped = me == PieceColor.Black;
        _game = new ChessGame();
        _clock = new ChessClock(baseTime, increment);
        _clockTag = ClockTag(baseTime, increment);
        _timeControlName = timeControlName;
        _savedThisGame = false;
        _opponentOnline = true;
        _drawOffer = null;
        _selected = null;
        _targets.Clear();

        OpponentLabel.Text = $"{_opponentName} ({me.Opponent()})";
        MyLabel.Text = $"{MyName} ({me})";
        GameInfoText.Text = timeControlName;
        _tray.Visible = true;

        SetupPanel.Visibility = Visibility.Collapsed;
        HistoryPanel.Visibility = Visibility.Collapsed;
        GamePanel.Visibility = Visibility.Visible;

        _clockTimer.Start();
        RefreshAll();
    }

    /// <summary>PGN-style time control, such as "600+0", or "-" for no clock.</summary>
    private static string ClockTag(TimeSpan baseTime, TimeSpan increment) =>
        baseTime <= TimeSpan.Zero ? "-" : $"{(int)baseTime.TotalSeconds}+{(int)increment.TotalSeconds}";

    private void FinishGame(GameStatus status, PieceColor? winner)
    {
        if (_game is null)
        {
            return;
        }

        _game.End(status, winner);
        StopClock();
        _deadlineTimer.Stop();
        _reconnectCts?.Cancel();

        if (!_savedThisGame)
        {
            SaveGame();
        }

        RefreshAll();
    }

    private void SaveGame()
    {
        _savedThisGame = true;
        try
        {
            GameStore.Save(_game!, MyName, _opponentName, _me, _clockTag, _timeControlName, _gameId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast($"Could not save the game: {ex.Message}");
        }
    }

    private void StopClock()
    {
        _clockTimer.Stop();
        _clock?.Stop();
    }

    /// <summary>Pauses the clock locally when the connection drops. Resuming is always a player action.</summary>
    private void AutoPause()
    {
        if (InProgress && _clock is { Enabled: true, Paused: false })
        {
            _clock.Pause();
        }
    }

    private void OnClockTick()
    {
        if (!InProgress)
        {
            return;
        }

        if (_clock!.Tick() is PieceColor flagged)
        {
            if (flagged == _me)
            {
                SendToPeer(new WireMessage { Type = WireTypes.Timeout });
            }

            FinishGame(GameStatus.TimedOut, flagged.Opponent());
            return;
        }

        RenderClocks();
    }

    private void CloseSession()
    {
        _reconnectCts?.Cancel();
        _reconnectCts = null;

        var peer = _peer;
        _peer = null;
        peer?.Dispose();

        StopClock();
        _game = null;
        _clock = null;
        _selected = null;
        _targets.Clear();
        _tray.Visible = false;
        StopHosting();

        GamePanel.Visibility = Visibility.Collapsed;
        HistoryPanel.Visibility = Visibility.Collapsed;
        SetupPanel.Visibility = Visibility.Visible;
        JoinStatusText.Text = "";
    }

    private void HandleOpponentMove(WireMessage msg)
    {
        var move = _game!.FindMove(msg.Uci ?? "");
        if (move is null)
        {
            ShowToast("Out of sync with your opponent. The game ended without a result.");
            FinishGame(GameStatus.Abandoned, null);
            return;
        }

        var played = _game.Play(move.Value);
        _drawOffer = null;

        // The sender's clocks already include its increment, so take them as they are.
        _clock!.Sync(FromMs(msg.WhiteMs), FromMs(msg.BlackMs), _me, paused: false);

        NotifyOpponent($"{_opponentName} played {played.San}");

        if (_game.IsOver)
        {
            FinishGame(_game.Status, _game.Winner);
        }
        else
        {
            RefreshAll();
        }
    }

    private void OnSquareClicked(int square)
    {
        if (!InProgress || _clock!.Paused || _game!.Current.ToMove != _me)
        {
            return;
        }

        if (_selected is int from && _targets.Any(m => m.To == square))
        {
            PlayLocal(from, square);
            return;
        }

        var piece = _game.Current[square];
        if (!piece.IsNone && piece.Color == _me)
        {
            _selected = square;
            _targets.Clear();
            _targets.AddRange(_game.Legal.Where(m => m.From == square));
        }
        else
        {
            _selected = null;
            _targets.Clear();
        }

        RenderBoard();
    }

    private void PlayLocal(int from, int to)
    {
        var candidates = _game!.Legal.Where(m => m.From == from && m.To == to).ToList();
        var move = candidates[0];

        if (candidates.Count > 1)
        {
            var choice = AskPromotion();
            if (choice is null)
            {
                return;
            }

            move = candidates.First(m => m.Promotion == choice);
        }

        _selected = null;
        _targets.Clear();
        _drawOffer = null;

        _game.Play(move);
        _clock!.CompleteMove(_me);

        var message = ClockMessage(WireTypes.Move);
        message.Uci = move.ToUci();
        SendToPeer(message);

        if (_game.IsOver)
        {
            FinishGame(_game.Status, _game.Winner);
        }
        else
        {
            RefreshAll();
        }
    }

    private PieceType? AskPromotion()
    {
        PieceType? choice = null;

        var dialog = new Window
        {
            Title = "Promote pawn",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x25, 0x2C)),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12) };

        foreach (var type in new[] { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight })
        {
            var button = new Button
            {
                Width = 76,
                Height = 76,
                Margin = new Thickness(6),
                Background = LightSquareBrush,
                Cursor = Cursors.Hand,
                Content = new TextBlock
                {
                    Text = Glyph(type),
                    FontFamily = new FontFamily("Segoe UI Symbol"),
                    FontSize = 46,
                    Foreground = PieceBrush(_me),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            button.Click += (_, _) =>
            {
                choice = type;
                dialog.Close();
            };
            row.Children.Add(button);
        }

        dialog.Content = row;
        dialog.ShowDialog();
        return choice;
    }

    // ---------- Buttons ----------

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!InProgress || !_clock!.Enabled)
        {
            return;
        }

        if (_clock.Paused)
        {
            if (!_opponentOnline)
            {
                ShowToast("Wait for your opponent to reconnect first.");
                return;
            }

            _clock.Resume();
            SendToPeer(ClockMessage(WireTypes.Resume));
        }
        else
        {
            _clock.Pause();
            SendToPeer(ClockMessage(WireTypes.Pause));
        }

        RefreshAll();
    }

    private void DrawButton_Click(object sender, RoutedEventArgs e)
    {
        if (!InProgress || _drawOffer == _me.Opponent())
        {
            return;
        }

        if (_drawOffer == _me)
        {
            _drawOffer = null;
            SendToPeer(new WireMessage { Type = WireTypes.DrawCancel });
        }
        else
        {
            _drawOffer = _me;
            SendToPeer(new WireMessage { Type = WireTypes.DrawOffer });
            ShowToast("Draw offered. Waiting for your opponent…");
        }

        RefreshAll();
    }

    private void AcceptDrawButton_Click(object sender, RoutedEventArgs e)
    {
        if (!InProgress || _drawOffer != _me.Opponent())
        {
            return;
        }

        SendToPeer(new WireMessage { Type = WireTypes.DrawAccept });
        FinishGame(GameStatus.DrawAgreed, null);
    }

    private void DeclineDrawButton_Click(object sender, RoutedEventArgs e)
    {
        if (!InProgress || _drawOffer != _me.Opponent())
        {
            return;
        }

        _drawOffer = null;
        SendToPeer(new WireMessage { Type = WireTypes.DrawDecline });
        RefreshAll();
    }

    private void ResignButton_Click(object sender, RoutedEventArgs e)
    {
        if (!InProgress)
        {
            return;
        }

        if (MessageBox.Show(this, "Resign this game?", "LAN Chess", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            return;
        }

        SendToPeer(new WireMessage { Type = WireTypes.Resign });
        FinishGame(GameStatus.Resigned, _me.Opponent());
    }

    private void LeaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (InProgress)
        {
            if (MessageBox.Show(this, "Leave now? This forfeits the game.", "LAN Chess",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            SendToPeer(new WireMessage { Type = WireTypes.Resign });
            FinishGame(GameStatus.Resigned, _me.Opponent());
        }

        CloseSession();
    }
}
