using System.Text;

namespace LanChess.Chess;

public enum GameStatus
{
    Ongoing,
    Checkmate,
    Stalemate,
    FiftyMoveRule,
    ThreefoldRepetition,
    InsufficientMaterial,
    DrawAgreed,
    Resigned,
    TimedOut,
    Abandoned,
}

public readonly record struct PlayedMove(Move Move, PieceColor Color, string San);

/// <summary>A game in progress: current position, move history, and the result once it ends.</summary>
public sealed class ChessGame
{
    private readonly List<PlayedMove> _history = new();
    private readonly List<string> _positionKeys = new();

    public ChessGame()
    {
        Current = Position.Start();
        Legal = Current.LegalMoves();
        _positionKeys.Add(Current.Key());
    }

    public Position Current { get; private set; }
    public List<Move> Legal { get; private set; }
    public IReadOnlyList<PlayedMove> History => _history;
    public Move? LastMove { get; private set; }
    public GameStatus Status { get; private set; } = GameStatus.Ongoing;
    public PieceColor? Winner { get; private set; }
    public bool IsOver => Status != GameStatus.Ongoing;

    /// <summary>Finds the legal move matching a UCI string such as "e2e4" or "e7e8q".</summary>
    public Move? FindMove(string uci)
    {
        if (uci.Length < 4)
        {
            return null;
        }

        int from = Position.ParseSquare(uci.Substring(0, 2));
        int to = Position.ParseSquare(uci.Substring(2, 2));
        var promotion = uci.Length > 4 ? uci[4] switch
        {
            'q' => PieceType.Queen,
            'r' => PieceType.Rook,
            'b' => PieceType.Bishop,
            'n' => PieceType.Knight,
            _ => PieceType.None,
        } : PieceType.None;

        foreach (var move in Legal)
        {
            if (move.From == from && move.To == to && move.Promotion == promotion)
            {
                return move;
            }
        }

        return null;
    }

    public PlayedMove Play(Move move)
    {
        if (IsOver)
        {
            throw new InvalidOperationException("The game is over.");
        }

        if (!Legal.Contains(move))
        {
            throw new ArgumentException($"Illegal move {move.ToUci()}.", nameof(move));
        }

        var mover = Current.ToMove;
        string san = BuildSan(Current, Legal, move);

        var next = Current.Clone();
        next.Make(move);
        var nextLegal = next.LegalMoves();
        bool check = next.InCheck(next.ToMove);
        if (check)
        {
            san += nextLegal.Count == 0 ? "#" : "+";
        }

        Current = next;
        Legal = nextLegal;
        LastMove = move;

        var played = new PlayedMove(move, mover, san);
        _history.Add(played);

        string key = next.Key();
        _positionKeys.Add(key);

        if (nextLegal.Count == 0)
        {
            Status = check ? GameStatus.Checkmate : GameStatus.Stalemate;
            Winner = check ? mover : null;
        }
        else if (next.HalfmoveClock >= 100)
        {
            Status = GameStatus.FiftyMoveRule;
        }
        else if (next.IsInsufficientMaterial())
        {
            Status = GameStatus.InsufficientMaterial;
        }
        else if (_positionKeys.Count(k => k == key) >= 3)
        {
            Status = GameStatus.ThreefoldRepetition;
        }

        return played;
    }

    /// <summary>Rebuilds a game from its UCI move list. Returns null if any move is illegal.</summary>
    public static ChessGame? Replay(IEnumerable<string> uciMoves)
    {
        var game = new ChessGame();
        foreach (var uci in uciMoves)
        {
            var move = game.FindMove(uci);
            if (move is null || game.IsOver)
            {
                return null;
            }

            game.Play(move.Value);
        }

        return game;
    }

    /// <summary>Ends the game from outside the board (resignation, time, disconnect). No-op if already over.</summary>
    public void End(GameStatus status, PieceColor? winner)
    {
        if (IsOver)
        {
            return;
        }

        Status = status;
        Winner = winner;
    }

    private static string BuildSan(Position before, IReadOnlyList<Move> legal, Move move)
    {
        if (move.Flags.HasFlag(MoveFlags.Castle))
        {
            return Position.FileOf(move.To) == 6 ? "O-O" : "O-O-O";
        }

        var piece = before[move.From];
        var sb = new StringBuilder();

        if (piece.Type == PieceType.Pawn)
        {
            if (move.IsCapture)
            {
                sb.Append(Position.FileChar(move.From));
            }
        }
        else
        {
            sb.Append(Letter(piece.Type));

            // Disambiguate when another piece of the same type could also reach the square.
            var rivals = legal
                .Where(o => o.To == move.To && o.From != move.From && before[o.From].Type == piece.Type)
                .ToList();

            if (rivals.Count > 0)
            {
                bool fileIsUnique = rivals.All(o => Position.FileOf(o.From) != Position.FileOf(move.From));
                bool rankIsUnique = rivals.All(o => Position.RankOf(o.From) != Position.RankOf(move.From));

                if (fileIsUnique)
                {
                    sb.Append(Position.FileChar(move.From));
                }
                else if (rankIsUnique)
                {
                    sb.Append(Position.RankOf(move.From) + 1);
                }
                else
                {
                    sb.Append(Position.SquareName(move.From));
                }
            }
        }

        if (move.IsCapture)
        {
            sb.Append('x');
        }

        sb.Append(Position.SquareName(move.To));

        if (move.Promotion != PieceType.None)
        {
            sb.Append('=').Append(Letter(move.Promotion));
        }

        return sb.ToString();
    }

    private static char Letter(PieceType type) => type switch
    {
        PieceType.Knight => 'N',
        PieceType.Bishop => 'B',
        PieceType.Rook => 'R',
        PieceType.Queen => 'Q',
        PieceType.King => 'K',
        _ => '\0',
    };
}
