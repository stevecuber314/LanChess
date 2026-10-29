namespace LanChess.Chess;

/// <summary>
/// Clock for both sides. Each machine runs its own copy and keeps it in step with the
/// times the sender includes in each move, pause and resume message.
/// </summary>
public sealed class ChessClock
{
    private DateTime _lastTick = DateTime.UtcNow;
    private bool _stopped;

    public ChessClock(TimeSpan baseTime, TimeSpan increment)
    {
        WhiteTime = baseTime;
        BlackTime = baseTime;
        Increment = increment;
        Enabled = baseTime > TimeSpan.Zero;
    }

    public bool Enabled { get; }
    public TimeSpan Increment { get; }
    public TimeSpan WhiteTime { get; private set; }
    public TimeSpan BlackTime { get; private set; }
    public PieceColor ToMove { get; private set; } = PieceColor.White;
    public bool Paused { get; private set; }

    public TimeSpan TimeOf(PieceColor color) => color == PieceColor.White ? WhiteTime : BlackTime;

    /// <summary>Banks elapsed time for the side to move. Returns that side if its time has run out.</summary>
    public PieceColor? Tick()
    {
        if (!Enabled || Paused || _stopped)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var remaining = TimeOf(ToMove) - (now - _lastTick);
        _lastTick = now;

        if (remaining > TimeSpan.Zero)
        {
            SetTime(ToMove, remaining);
            return null;
        }

        SetTime(ToMove, TimeSpan.Zero);
        return ToMove;
    }

    public void Pause()
    {
        if (!Enabled || Paused)
        {
            return;
        }

        Tick();
        Paused = true;
    }

    public void Resume()
    {
        if (!Paused)
        {
            return;
        }

        Paused = false;
        _lastTick = DateTime.UtcNow;
    }

    /// <summary>Called by the side that just moved: adds its increment and hands the clock over.</summary>
    public void CompleteMove(PieceColor mover)
    {
        Tick();
        SetTime(mover, TimeOf(mover) + Increment);
        ToMove = mover.Opponent();
        _lastTick = DateTime.UtcNow;
    }

    /// <summary>Applies the state reported by the other side.</summary>
    public void Sync(TimeSpan white, TimeSpan black, PieceColor toMove, bool paused)
    {
        WhiteTime = white;
        BlackTime = black;
        ToMove = toMove;
        Paused = paused;
        _lastTick = DateTime.UtcNow;
    }

    public void Stop() => _stopped = true;

    private void SetTime(PieceColor color, TimeSpan time)
    {
        if (color == PieceColor.White)
        {
            WhiteTime = time;
        }
        else
        {
            BlackTime = time;
        }
    }

    public static string Format(TimeSpan time)
    {
        if (time <= TimeSpan.Zero)
        {
            return "0.0";
        }

        if (time.TotalSeconds < 10)
        {
            int tenths = (int)(time.TotalSeconds * 10);
            return $"{tenths / 10}.{tenths % 10}";
        }

        if (time.TotalHours >= 1)
        {
            return $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";
        }

        return $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }
}
