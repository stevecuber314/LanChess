using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using LanChess.Chess;
using LanChess.Net;
using LanChess.Storage;
using WinForms = System.Windows.Forms;

namespace LanChess;

/// <summary>
/// Shell and board rendering. Setup, networking, game flow and history live in the other partial files.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan ReconnectWindow = TimeSpan.FromMinutes(5);

    private static readonly Color LightSquareColor = Color.FromRgb(0xEE, 0xEE, 0xD2);
    private static readonly Color DarkSquareColor = Color.FromRgb(0x76, 0x96, 0x56);
    private static readonly Color LastLightColor = Color.FromRgb(0xCD, 0xD2, 0x6A);
    private static readonly Color LastDarkColor = Color.FromRgb(0xAA, 0xA2, 0x3A);
    private static readonly Color SelectedColor = Color.FromRgb(0xF6, 0xF6, 0x69);
    private static readonly Color CheckColor = Color.FromRgb(0xE5, 0x48, 0x4D);
    private static readonly Color ActiveCardColor = Color.FromRgb(0x2F, 0x4A, 0x2A);
    private static readonly Color CardColor = Color.FromRgb(0x22, 0x25, 0x2C);

    private static readonly FontFamily SymbolFont = new("Segoe UI Symbol");
    private static readonly SolidColorBrush DotBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)));
    private static readonly SolidColorBrush WhitePieceBrush = Frozen(new SolidColorBrush(Colors.White));
    private static readonly SolidColorBrush BlackPieceBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)));
    private static readonly SolidColorBrush LowTimeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));
    private static readonly SolidColorBrush NormalTimeBrush = Frozen(new SolidColorBrush(Colors.White));
    private static readonly SolidColorBrush WarnBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xC1, 0x4E)));
    private static readonly SolidColorBrush MutedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x9C)));
    private static readonly SolidColorBrush LightSquareBrush = Frozen(new SolidColorBrush(LightSquareColor));

    private static readonly DropShadowEffect LightPieceShadow = Frozen(new DropShadowEffect
    {
        Color = Colors.Black, BlurRadius = 3, ShadowDepth = 0, Opacity = 0.9,
    });

    private static readonly DropShadowEffect DarkPieceShadow = Frozen(new DropShadowEffect
    {
        Color = Colors.White, BlurRadius = 2, ShadowDepth = 0, Opacity = 0.35,
    });

    private sealed record CellView(Border Root, TextBlock Piece, Ellipse Dot, Ellipse Ring);

    private readonly CellView[] _cells = new CellView[64];
    private readonly List<Move> _targets = new();
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.2) };
    private readonly DispatcherTimer _deadlineTimer = new() { Interval = ReconnectWindow };
    private readonly DispatcherTimer _lobbyTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly WinForms.NotifyIcon _tray;

    private LobbyBrowser? _browser;
    private string _lobbyKey = "";

    // The current game
    private ChessGame? _game;
    private ChessClock? _clock;
    private PieceColor _me;
    private bool _flipped;
    private int? _selected;
    private Guid _gameId;
    private string _opponentName = "";
    private string _clockTag = "-";
    private string _timeControlName = "";
    private bool _savedThisGame;
    private bool _opponentOnline;
    private PieceColor? _drawOffer;
    private PeerConnection? _peer;
    private CancellationTokenSource? _reconnectCts;

    // Hosting and joining
    private bool _isHost;
    private HostListener? _listener;
    private LobbyAnnouncer? _announcer;
    private TimeControl _hostControl = TimeControl.Presets[5];
    private int _hostPort;
    private string _hostAddress = "";
    private int _joinPort;

    public MainWindow()
    {
        InitializeComponent();

        PlayerNameBox.Text = _settings.PlayerName;
        HostPortBox.Text = _settings.Port.ToString();

        foreach (var control in TimeControl.Presets)
        {
            TimeControlBox.Items.Add(control);
        }

        TimeControlBox.Items.Add(TimeControl.Custom);
        TimeControlBox.SelectedIndex = 5; // Rapid 10 + 0

        BuildBoard();

        _clockTimer.Tick += (_, _) => OnClockTick();
        _deadlineTimer.Tick += (_, _) => OnDeadline();
        _lobbyTimer.Tick += (_, _) => RefreshLobby();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.BeginAnimation(OpacityProperty, Fade(0));
        };

        _tray = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Information,
            Text = "LAN Chess",
        };
        _tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() =>
        {
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private string MyName => string.IsNullOrWhiteSpace(PlayerNameBox.Text) ? "Player" : PlayerNameBox.Text.Trim();

    private bool InProgress => _game is { IsOver: false };

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static DoubleAnimation Fade(double to) => new(to, TimeSpan.FromMilliseconds(250));

    private static long ToMs(TimeSpan time) => (long)time.TotalMilliseconds;

    private static TimeSpan FromMs(long milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static string Glyph(PieceType type) => type switch
    {
        PieceType.King => "♚︎",
        PieceType.Queen => "♛︎",
        PieceType.Rook => "♜︎",
        PieceType.Bishop => "♝︎",
        PieceType.Knight => "♞︎",
        PieceType.Pawn => "♟︎",
        _ => "",
    };

    private static SolidColorBrush PieceBrush(PieceColor color) =>
        color == PieceColor.White ? WhitePieceBrush : BlackPieceBrush;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _browser = new LobbyBrowser();
            _lobbyTimer.Start();
        }
        catch (SocketException)
        {
            JoinStatusText.Text = "Game discovery is unavailable because the lobby port is in use. Enter the host's address instead.";
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (InProgress)
        {
            FinishGame(GameStatus.Abandoned, null);
        }

        _reconnectCts?.Cancel();
        _peer?.Dispose();
        StopHosting();
        _browser?.Dispose();
        _clockTimer.Stop();
        _deadlineTimer.Stop();
        _lobbyTimer.Stop();
        _tray.Dispose();
    }

    // ---------- Notifications ----------

    private void NotifyOpponent(string text)
    {
        ShowToast(text);
        System.Media.SystemSounds.Asterisk.Play();

        if (!IsActive)
        {
            _tray.ShowBalloonTip(4000, "LAN Chess", text, WinForms.ToolTipIcon.Info);
        }
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.BeginAnimation(OpacityProperty, Fade(1));
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---------- Rendering ----------

    private void BuildBoard()
    {
        for (int i = 0; i < 64; i++)
        {
            var piece = new TextBlock
            {
                FontFamily = SymbolFont,
                FontSize = 52,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            var dot = new Ellipse
            {
                Width = 22,
                Height = 22,
                Fill = DotBrush,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            var ring = new Ellipse
            {
                Width = 66,
                Height = 66,
                Stroke = DotBrush,
                StrokeThickness = 6,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };

            var grid = new Grid();
            grid.Children.Add(ring);
            grid.Children.Add(dot);
            grid.Children.Add(piece);

            var root = new Border { Child = grid, Cursor = Cursors.Hand };
            int cell = i;
            root.MouseLeftButtonDown += (_, _) => OnSquareClicked(SquareAt(cell / 8, cell % 8));

            _cells[i] = new CellView(root, piece, dot, ring);
            BoardGrid.Children.Add(root);
        }
    }

    /// <summary>Maps a display row and column (0 = top left) to a board square.</summary>
    private int SquareAt(int row, int col) =>
        _flipped ? Position.Index(7 - col, row) : Position.Index(col, 7 - row);

    private void RefreshAll()
    {
        UpdateAnnouncement();

        if (_game is null || _clock is null)
        {
            return;
        }

        RenderBoard();
        RenderClocks();
        RenderMoves();
        RenderStatus();
        RenderControls();
    }

    private void UpdateAnnouncement()
    {
        _announcer?.Update(new LobbyAnnouncement(LobbyAnnouncer.AppTag, MyName, _hostPort, _hostControl.Name, InProgress));
    }

    private void RenderBoard()
    {
        var position = _game!.Current;
        var last = _game.LastMove;
        int checkSquare = position.InCheck(position.ToMove) ? position.KingSquare(position.ToMove) : -1;

        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                int square = SquareAt(row, col);
                var cell = _cells[row * 8 + col];
                var piece = position[square];
                bool light = (Position.FileOf(square) + Position.RankOf(square)) % 2 == 1;

                var background = light ? LightSquareColor : DarkSquareColor;
                if (last is { } lastMove && (square == lastMove.From || square == lastMove.To))
                {
                    background = light ? LastLightColor : LastDarkColor;
                }

                if (_selected == square)
                {
                    background = SelectedColor;
                }

                if (square == checkSquare)
                {
                    background = CheckColor;
                }

                cell.Root.Background = new SolidColorBrush(background);

                cell.Piece.Text = piece.IsNone ? "" : Glyph(piece.Type);
                cell.Piece.Foreground = PieceBrush(piece.Color);
                cell.Piece.Effect = piece.Color == PieceColor.White ? LightPieceShadow : DarkPieceShadow;

                bool target = _targets.Any(m => m.To == square);
                cell.Dot.Visibility = target && piece.IsNone ? Visibility.Visible : Visibility.Collapsed;
                cell.Ring.Visibility = target && !piece.IsNone ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void RenderClocks()
    {
        var game = _game!;
        var clock = _clock!;

        var myTime = clock.TimeOf(_me);
        var opponentTime = clock.TimeOf(_me.Opponent());

        MyClock.Text = clock.Enabled ? ChessClock.Format(myTime) : "∞";
        OpponentClock.Text = clock.Enabled ? ChessClock.Format(opponentTime) : "∞";
        MyClock.Foreground = clock.Enabled && myTime < TimeSpan.FromSeconds(10) ? LowTimeBrush : NormalTimeBrush;
        OpponentClock.Foreground = clock.Enabled && opponentTime < TimeSpan.FromSeconds(10) ? LowTimeBrush : NormalTimeBrush;

        bool running = !game.IsOver && !clock.Paused;
        MyCard.Background = new SolidColorBrush(running && game.Current.ToMove == _me ? ActiveCardColor : CardColor);
        OpponentCard.Background = new SolidColorBrush(running && game.Current.ToMove != _me ? ActiveCardColor : CardColor);
    }

    private void RenderMoves()
    {
        var history = _game!.History;
        var sb = new StringBuilder();
        for (int i = 0; i < history.Count; i += 2)
        {
            sb.Append($"{i / 2 + 1,3}. {history[i].San,-8}");
            if (i + 1 < history.Count)
            {
                sb.Append(history[i + 1].San);
            }

            sb.AppendLine();
        }

        MoveText.Text = sb.ToString().TrimEnd();
        MoveScroll.ScrollToEnd();
    }

    private void RenderStatus()
    {
        var game = _game!;
        var clock = _clock!;

        if (game.IsOver)
        {
            StatusText.Text = ResultText();
        }
        else if (clock.Paused)
        {
            StatusText.Text = _opponentOnline ? "Clock paused." : "Clock paused while your opponent reconnects.";
        }
        else if (_drawOffer == _me)
        {
            StatusText.Text = "Draw offered. Waiting for your opponent…";
        }
        else if (game.Current.ToMove == _me)
        {
            StatusText.Text = game.Current.InCheck(_me) ? "Your move. You're in check!" : "Your move.";
        }
        else
        {
            StatusText.Text = "Waiting for your opponent…";
        }
    }

    private void RenderControls()
    {
        bool live = InProgress;
        var clock = _clock!;

        PauseButton.Content = clock.Paused ? "Resume" : "Pause";
        PauseButton.IsEnabled = live && clock.Enabled && (!clock.Paused || _opponentOnline);

        DrawButton.Content = _drawOffer == _me ? "Withdraw draw" : "Offer draw";
        DrawButton.IsEnabled = live && _drawOffer != _me.Opponent();
        ResignButton.IsEnabled = live;

        DrawBanner.Visibility = live && _drawOffer == _me.Opponent() ? Visibility.Visible : Visibility.Collapsed;

        PausedOverlay.Visibility = live && clock.Paused ? Visibility.Visible : Visibility.Collapsed;
        PauseSubtitle.Text = _opponentOnline
            ? "Press Resume to continue."
            : "Waiting for your opponent to reconnect…";

        if (!live)
        {
            OpponentStatusText.Text = "Game over";
            OpponentStatusText.Foreground = MutedBrush;
        }
        else if (_opponentOnline)
        {
            OpponentStatusText.Text = "Connected";
            OpponentStatusText.Foreground = MutedBrush;
        }
        else
        {
            OpponentStatusText.Text = _isHost
                ? "Disconnected. Waiting for them to rejoin…"
                : "Connection lost. Reconnecting…";
            OpponentStatusText.Foreground = WarnBrush;
        }
    }

    private string ResultText()
    {
        var game = _game!;
        string winner = game.Winner == _me ? "You win" : "Your opponent wins";

        return game.Status switch
        {
            GameStatus.Checkmate => $"Checkmate. {winner}!",
            GameStatus.Resigned => $"{winner} by resignation.",
            GameStatus.TimedOut => $"{winner} on time.",
            GameStatus.Stalemate => "Draw by stalemate.",
            GameStatus.FiftyMoveRule => "Draw by the 50-move rule.",
            GameStatus.ThreefoldRepetition => "Draw by threefold repetition.",
            GameStatus.InsufficientMaterial => "Draw. Not enough material to mate.",
            GameStatus.DrawAgreed => "Draw agreed.",
            GameStatus.Abandoned => "Game ended without a result.",
            _ => "",
        };
    }
}
