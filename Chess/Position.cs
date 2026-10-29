using System.Text;

namespace LanChess.Chess;

public enum PieceColor { White, Black }

public enum PieceType { None, Pawn, Knight, Bishop, Rook, Queen, King }

public static class PieceColorExtensions
{
    public static PieceColor Opponent(this PieceColor color) =>
        color == PieceColor.White ? PieceColor.Black : PieceColor.White;
}

public readonly record struct Piece(PieceType Type, PieceColor Color)
{
    public static readonly Piece None = new(PieceType.None, PieceColor.White);

    public bool IsNone => Type == PieceType.None;
}

[Flags]
public enum MoveFlags
{
    None = 0,
    Capture = 1,
    DoublePush = 2,
    EnPassant = 4,
    Castle = 8,
}

[Flags]
public enum CastlingRights
{
    None = 0,
    WhiteKingSide = 1,
    WhiteQueenSide = 2,
    BlackKingSide = 4,
    BlackQueenSide = 8,
    All = 15,
}

public readonly record struct Move(int From, int To, PieceType Promotion = PieceType.None, MoveFlags Flags = MoveFlags.None)
{
    public bool IsCapture => Flags.HasFlag(MoveFlags.Capture);

    public string ToUci()
    {
        string promotion = Promotion switch
        {
            PieceType.Queen => "q",
            PieceType.Rook => "r",
            PieceType.Bishop => "b",
            PieceType.Knight => "n",
            _ => "",
        };
        return Position.SquareName(From) + Position.SquareName(To) + promotion;
    }
}

/// <summary>Board state plus the rules needed to generate legal moves.</summary>
public sealed class Position
{
    private static readonly (int File, int Rank)[] KnightSteps =
    {
        (1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2),
    };

    private static readonly (int File, int Rank)[] KingSteps =
    {
        (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1),
    };

    private static readonly (int File, int Rank)[] RookDirections = { (1, 0), (-1, 0), (0, 1), (0, -1) };
    private static readonly (int File, int Rank)[] BishopDirections = { (1, 1), (1, -1), (-1, 1), (-1, -1) };
    private static readonly PieceType[] PromotionTypes = { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight };

    private Piece[] _squares = new Piece[64];

    private Position() { }

    public PieceColor ToMove { get; private set; } = PieceColor.White;
    public CastlingRights Castling { get; private set; } = CastlingRights.All;

    /// <summary>The square a pawn skipped over on the last double push, or -1.</summary>
    public int EnPassantSquare { get; private set; } = -1;
    public int HalfmoveClock { get; private set; }
    public int FullmoveNumber { get; private set; } = 1;

    public Piece this[int square] => _squares[square];

    public static Position Start()
    {
        string[] rows =
        {
            "rnbqkbnr",
            "pppppppp",
            "........",
            "........",
            "........",
            "........",
            "PPPPPPPP",
            "RNBQKBNR",
        };

        var position = new Position();
        for (int row = 0; row < 8; row++)
        {
            for (int file = 0; file < 8; file++)
            {
                char c = rows[row][file];
                if (c == '.')
                {
                    continue;
                }

                var color = char.IsUpper(c) ? PieceColor.White : PieceColor.Black;
                position._squares[Index(file, 7 - row)] = new Piece(TypeOf(c), color);
            }
        }

        return position;
    }

    public static int Index(int file, int rank) => rank * 8 + file;
    public static int FileOf(int square) => square & 7;
    public static int RankOf(int square) => square >> 3;
    public static char FileChar(int square) => (char)('a' + FileOf(square));
    public static string SquareName(int square) => $"{FileChar(square)}{RankOf(square) + 1}";
    public static int ParseSquare(string name) => Index(name[0] - 'a', name[1] - '1');

    private static bool OnBoard(int file, int rank) => file >= 0 && file < 8 && rank >= 0 && rank < 8;

    private static PieceType TypeOf(char c) => char.ToLowerInvariant(c) switch
    {
        'p' => PieceType.Pawn,
        'n' => PieceType.Knight,
        'b' => PieceType.Bishop,
        'r' => PieceType.Rook,
        'q' => PieceType.Queen,
        _ => PieceType.King,
    };

    public Position Clone()
    {
        var copy = (Position)MemberwiseClone();
        copy._squares = (Piece[])_squares.Clone();
        return copy;
    }

    /// <summary>Identifies a position for threefold-repetition detection.</summary>
    public string Key()
    {
        var sb = new StringBuilder(80);
        foreach (var piece in _squares)
        {
            sb.Append(piece.IsNone ? '.' : PieceChar(piece));
        }

        sb.Append(ToMove == PieceColor.White ? 'w' : 'b')
          .Append((int)Castling)
          .Append(':')
          .Append(EnPassantSquare);
        return sb.ToString();
    }

    private static char PieceChar(Piece piece)
    {
        char c = piece.Type switch
        {
            PieceType.Pawn => 'p',
            PieceType.Knight => 'n',
            PieceType.Bishop => 'b',
            PieceType.Rook => 'r',
            PieceType.Queen => 'q',
            _ => 'k',
        };
        return piece.Color == PieceColor.White ? char.ToUpperInvariant(c) : c;
    }

    public int KingSquare(PieceColor color)
    {
        for (int square = 0; square < 64; square++)
        {
            if (_squares[square].Type == PieceType.King && _squares[square].Color == color)
            {
                return square;
            }
        }

        return -1;
    }

    public bool InCheck(PieceColor color)
    {
        int king = KingSquare(color);
        return king >= 0 && IsAttacked(king, color.Opponent());
    }

    public bool IsAttacked(int square, PieceColor by)
    {
        int file = FileOf(square);
        int rank = RankOf(square);

        // A white pawn attacks upward, so an attacking white pawn sits one rank below the target.
        int pawnRank = rank + (by == PieceColor.White ? -1 : 1);
        if (IsPieceAt(file - 1, pawnRank, by, PieceType.Pawn) || IsPieceAt(file + 1, pawnRank, by, PieceType.Pawn))
        {
            return true;
        }

        foreach (var (df, dr) in KnightSteps)
        {
            if (IsPieceAt(file + df, rank + dr, by, PieceType.Knight))
            {
                return true;
            }
        }

        foreach (var (df, dr) in KingSteps)
        {
            if (IsPieceAt(file + df, rank + dr, by, PieceType.King))
            {
                return true;
            }
        }

        foreach (var (df, dr) in RookDirections)
        {
            if (SlideFinds(file, rank, df, dr, by, PieceType.Rook))
            {
                return true;
            }
        }

        foreach (var (df, dr) in BishopDirections)
        {
            if (SlideFinds(file, rank, df, dr, by, PieceType.Bishop))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsPieceAt(int file, int rank, PieceColor color, PieceType type)
    {
        if (!OnBoard(file, rank))
        {
            return false;
        }

        var piece = _squares[Index(file, rank)];
        return piece.Type == type && piece.Color == color;
    }

    /// <summary>True if the first piece along the ray is an enemy rook/bishop or a queen.</summary>
    private bool SlideFinds(int file, int rank, int df, int dr, PieceColor by, PieceType slider)
    {
        for (int f = file + df, r = rank + dr; OnBoard(f, r); f += df, r += dr)
        {
            var piece = _squares[Index(f, r)];
            if (piece.IsNone)
            {
                continue;
            }

            return piece.Color == by && (piece.Type == slider || piece.Type == PieceType.Queen);
        }

        return false;
    }

    public List<Move> LegalMoves()
    {
        var mover = ToMove;
        var legal = new List<Move>(40);
        foreach (var move in PseudoLegalMoves())
        {
            var next = Clone();
            next.Make(move);
            if (!next.InCheck(mover))
            {
                legal.Add(move);
            }
        }

        return legal;
    }

    private List<Move> PseudoLegalMoves()
    {
        var moves = new List<Move>(48);
        var color = ToMove;

        for (int square = 0; square < 64; square++)
        {
            var piece = _squares[square];
            if (piece.IsNone || piece.Color != color)
            {
                continue;
            }

            switch (piece.Type)
            {
                case PieceType.Pawn:
                    AddPawnMoves(moves, square, color);
                    break;
                case PieceType.Knight:
                    AddStepMoves(moves, square, KnightSteps, color);
                    break;
                case PieceType.Bishop:
                    AddSlideMoves(moves, square, BishopDirections, color);
                    break;
                case PieceType.Rook:
                    AddSlideMoves(moves, square, RookDirections, color);
                    break;
                case PieceType.Queen:
                    AddSlideMoves(moves, square, RookDirections, color);
                    AddSlideMoves(moves, square, BishopDirections, color);
                    break;
                case PieceType.King:
                    AddStepMoves(moves, square, KingSteps, color);
                    AddCastling(moves, color);
                    break;
            }
        }

        return moves;
    }

    private void AddStepMoves(List<Move> moves, int from, (int File, int Rank)[] steps, PieceColor color)
    {
        int file = FileOf(from);
        int rank = RankOf(from);
        foreach (var (df, dr) in steps)
        {
            if (!OnBoard(file + df, rank + dr))
            {
                continue;
            }

            int to = Index(file + df, rank + dr);
            var target = _squares[to];
            if (target.IsNone)
            {
                moves.Add(new Move(from, to));
            }
            else if (target.Color != color)
            {
                moves.Add(new Move(from, to, PieceType.None, MoveFlags.Capture));
            }
        }
    }

    private void AddSlideMoves(List<Move> moves, int from, (int File, int Rank)[] directions, PieceColor color)
    {
        int file = FileOf(from);
        int rank = RankOf(from);
        foreach (var (df, dr) in directions)
        {
            for (int f = file + df, r = rank + dr; OnBoard(f, r); f += df, r += dr)
            {
                int to = Index(f, r);
                var target = _squares[to];
                if (target.IsNone)
                {
                    moves.Add(new Move(from, to));
                    continue;
                }

                if (target.Color != color)
                {
                    moves.Add(new Move(from, to, PieceType.None, MoveFlags.Capture));
                }

                break;
            }
        }
    }

    private void AddPawnMoves(List<Move> moves, int from, PieceColor color)
    {
        int file = FileOf(from);
        int rank = RankOf(from);
        int dir = color == PieceColor.White ? 1 : -1;
        int startRank = color == PieceColor.White ? 1 : 6;
        int promotionRank = color == PieceColor.White ? 7 : 0;

        int oneAhead = rank + dir;
        int single = Index(file, oneAhead);
        if (_squares[single].IsNone)
        {
            AddPawnMove(moves, from, single, oneAhead == promotionRank, MoveFlags.None);

            int twoAhead = Index(file, rank + 2 * dir);
            if (rank == startRank && _squares[twoAhead].IsNone)
            {
                moves.Add(new Move(from, twoAhead, PieceType.None, MoveFlags.DoublePush));
            }
        }

        foreach (int df in new[] { -1, 1 })
        {
            if (!OnBoard(file + df, oneAhead))
            {
                continue;
            }

            int to = Index(file + df, oneAhead);
            var target = _squares[to];
            if (!target.IsNone && target.Color != color)
            {
                AddPawnMove(moves, from, to, oneAhead == promotionRank, MoveFlags.Capture);
            }
            else if (target.IsNone && to == EnPassantSquare)
            {
                moves.Add(new Move(from, to, PieceType.None, MoveFlags.Capture | MoveFlags.EnPassant));
            }
        }
    }

    private static void AddPawnMove(List<Move> moves, int from, int to, bool promotes, MoveFlags flags)
    {
        if (!promotes)
        {
            moves.Add(new Move(from, to, PieceType.None, flags));
            return;
        }

        foreach (var type in PromotionTypes)
        {
            moves.Add(new Move(from, to, type, flags));
        }
    }

    private void AddCastling(List<Move> moves, PieceColor color)
    {
        bool white = color == PieceColor.White;
        int rank = white ? 0 : 7;
        int kingFrom = Index(4, rank);
        var king = _squares[kingFrom];
        if (king.Type != PieceType.King || king.Color != color)
        {
            return;
        }

        var enemy = color.Opponent();
        if (IsAttacked(kingFrom, enemy))
        {
            return;
        }

        var kingSide = white ? CastlingRights.WhiteKingSide : CastlingRights.BlackKingSide;
        var queenSide = white ? CastlingRights.WhiteQueenSide : CastlingRights.BlackQueenSide;

        if (Castling.HasFlag(kingSide)
            && IsEmpty(5, rank) && IsEmpty(6, rank)
            && IsPieceAt(7, rank, color, PieceType.Rook)
            && !IsAttacked(Index(5, rank), enemy) && !IsAttacked(Index(6, rank), enemy))
        {
            moves.Add(new Move(kingFrom, Index(6, rank), PieceType.None, MoveFlags.Castle));
        }

        if (Castling.HasFlag(queenSide)
            && IsEmpty(1, rank) && IsEmpty(2, rank) && IsEmpty(3, rank)
            && IsPieceAt(0, rank, color, PieceType.Rook)
            && !IsAttacked(Index(3, rank), enemy) && !IsAttacked(Index(2, rank), enemy))
        {
            moves.Add(new Move(kingFrom, Index(2, rank), PieceType.None, MoveFlags.Castle));
        }
    }

    private bool IsEmpty(int file, int rank) => _squares[Index(file, rank)].IsNone;

    /// <summary>Applies a move in place. The move is assumed to be legal.</summary>
    public void Make(Move move)
    {
        var piece = _squares[move.From];

        _squares[move.From] = Piece.None;
        _squares[move.To] = move.Promotion == PieceType.None
            ? piece
            : new Piece(move.Promotion, piece.Color);

        if (move.Flags.HasFlag(MoveFlags.EnPassant))
        {
            _squares[Index(FileOf(move.To), RankOf(move.From))] = Piece.None;
        }

        if (move.Flags.HasFlag(MoveFlags.Castle))
        {
            bool kingSide = FileOf(move.To) == 6;
            int rank = RankOf(move.From);
            int rookFrom = Index(kingSide ? 7 : 0, rank);
            int rookTo = Index(kingSide ? 5 : 3, rank);
            _squares[rookTo] = _squares[rookFrom];
            _squares[rookFrom] = Piece.None;
        }

        RevokeCastling(move.From);
        RevokeCastling(move.To);

        EnPassantSquare = move.Flags.HasFlag(MoveFlags.DoublePush) ? (move.From + move.To) / 2 : -1;
        HalfmoveClock = piece.Type == PieceType.Pawn || move.IsCapture ? 0 : HalfmoveClock + 1;
        if (ToMove == PieceColor.Black)
        {
            FullmoveNumber++;
        }

        ToMove = ToMove.Opponent();
    }

    private void RevokeCastling(int square)
    {
        Castling &= square switch
        {
            0 => ~CastlingRights.WhiteQueenSide,
            4 => ~(CastlingRights.WhiteKingSide | CastlingRights.WhiteQueenSide),
            7 => ~CastlingRights.WhiteKingSide,
            56 => ~CastlingRights.BlackQueenSide,
            60 => ~(CastlingRights.BlackKingSide | CastlingRights.BlackQueenSide),
            63 => ~CastlingRights.BlackKingSide,
            _ => CastlingRights.All,
        };
    }

    /// <summary>Only kings, plus at most one knight or bishop in total, can never mate.</summary>
    public bool IsInsufficientMaterial()
    {
        int minors = 0;
        foreach (var piece in _squares)
        {
            switch (piece.Type)
            {
                case PieceType.None:
                case PieceType.King:
                    break;
                case PieceType.Knight:
                case PieceType.Bishop:
                    minors++;
                    break;
                default:
                    return false;
            }
        }

        return minors <= 1;
    }
}
