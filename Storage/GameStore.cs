using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanChess.Chess;

namespace LanChess.Storage;

public enum GameOutcome { Win, Loss, Draw, Unfinished }

/// <summary>A finished (or abandoned) game as stored in games.json.</summary>
public sealed class SavedGame
{
    public string Id { get; set; } = "";
    public DateTime PlayedUtc { get; set; }
    public string MyName { get; set; } = "";
    public string OpponentName { get; set; } = "";
    public string MyColor { get; set; } = "";
    public string TimeControl { get; set; } = "";
    public string Result { get; set; } = "*";
    public string Termination { get; set; } = "";
    public int MoveCount { get; set; }
    public string Pgn { get; set; } = "";

    [JsonIgnore]
    public GameOutcome Outcome => Result switch
    {
        "1/2-1/2" => GameOutcome.Draw,
        "*" => GameOutcome.Unfinished,
        _ when (Result == "1-0") == (MyColor == "White") => GameOutcome.Win,
        _ => GameOutcome.Loss,
    };

    [JsonIgnore]
    public string Summary => $"{PlayedUtc.ToLocalTime():MMM d, HH:mm}  ·  vs {OpponentName}  ·  {TimeControl}  ·  {Outcome}";
}

public sealed record GameStats(int Wins, int Losses, int Draws, int Unfinished)
{
    public int Played => Wins + Losses + Draws;
    public double WinRate => Played == 0 ? 0 : (double)Wins / Played;

    public static GameStats From(IEnumerable<SavedGame> games)
    {
        var list = games.ToList();
        return new GameStats(
            list.Count(g => g.Outcome == GameOutcome.Win),
            list.Count(g => g.Outcome == GameOutcome.Loss),
            list.Count(g => g.Outcome == GameOutcome.Draw),
            list.Count(g => g.Outcome == GameOutcome.Unfinished));
    }
}

public static class GameStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string GamesFolder => Path.Combine(AppSettings.Folder, "Games");

    private static string IndexPath => Path.Combine(AppSettings.Folder, "games.json");

    public static List<SavedGame> LoadAll()
    {
        if (!File.Exists(IndexPath))
        {
            return new List<SavedGame>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<SavedGame>>(File.ReadAllText(IndexPath)) ?? new List<SavedGame>();
        }
        catch (JsonException)
        {
            return new List<SavedGame>();
        }
    }

    /// <summary>Saves the game to games.json and writes a .pgn copy to the Games folder.</summary>
    public static SavedGame Save(ChessGame game, string myName, string opponentName, PieceColor myColor,
        string clockTag, string timeControl, Guid id)
    {
        string white = myColor == PieceColor.White ? myName : opponentName;
        string black = myColor == PieceColor.Black ? myName : opponentName;
        string result = ResultCode(game);
        string termination = TerminationText(game.Status);
        var now = DateTime.Now;

        var saved = new SavedGame
        {
            Id = id.ToString("N"),
            PlayedUtc = DateTime.UtcNow,
            MyName = myName,
            OpponentName = opponentName,
            MyColor = myColor.ToString(),
            TimeControl = timeControl,
            Result = result,
            Termination = termination,
            MoveCount = game.History.Count,
            Pgn = BuildPgn(game, white, black, clockTag, now, result, termination),
        };

        Directory.CreateDirectory(GamesFolder);
        string fileName = Sanitize($"{now:yyyy-MM-dd HH.mm} {white} vs {black}") + ".pgn";
        File.WriteAllText(Path.Combine(GamesFolder, fileName), saved.Pgn);

        var all = LoadAll();
        all.Add(saved);
        Directory.CreateDirectory(AppSettings.Folder);
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(all, WriteOptions));

        return saved;
    }

    public static string BuildPgn(ChessGame game, string white, string black, string clockTag,
        DateTime localDate, string result, string termination)
    {
        var sb = new StringBuilder();
        void Header(string name, string value) => sb.AppendLine($"[{name} \"{value}\"]");

        Header("Event", "LAN Chess game");
        Header("Site", "LAN");
        Header("Date", localDate.ToString("yyyy.MM.dd"));
        Header("Round", "-");
        Header("White", white);
        Header("Black", black);
        Header("Result", result);
        Header("TimeControl", clockTag);
        Header("Termination", termination);
        sb.AppendLine();

        var tokens = new List<string>();
        for (int i = 0; i < game.History.Count; i++)
        {
            if (i % 2 == 0)
            {
                tokens.Add($"{i / 2 + 1}.");
            }

            tokens.Add(game.History[i].San);
        }

        tokens.Add(result);

        var line = new StringBuilder();
        foreach (var token in tokens)
        {
            if (line.Length > 0 && line.Length + 1 + token.Length > 80)
            {
                sb.AppendLine(line.ToString());
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(token);
        }

        if (line.Length > 0)
        {
            sb.AppendLine(line.ToString());
        }

        return sb.ToString();
    }

    private static string ResultCode(ChessGame game) => game.Status switch
    {
        GameStatus.Checkmate or GameStatus.Resigned or GameStatus.TimedOut => game.Winner switch
        {
            PieceColor.White => "1-0",
            PieceColor.Black => "0-1",
            _ => "*",
        },
        GameStatus.Stalemate or GameStatus.FiftyMoveRule or GameStatus.ThreefoldRepetition
            or GameStatus.InsufficientMaterial or GameStatus.DrawAgreed => "1/2-1/2",
        _ => "*",
    };

    private static string TerminationText(GameStatus status) => status switch
    {
        GameStatus.Checkmate => "checkmate",
        GameStatus.Stalemate => "stalemate",
        GameStatus.FiftyMoveRule => "50-move rule",
        GameStatus.ThreefoldRepetition => "threefold repetition",
        GameStatus.InsufficientMaterial => "insufficient material",
        GameStatus.DrawAgreed => "draw agreed",
        GameStatus.Resigned => "resignation",
        GameStatus.TimedOut => "time forfeit",
        GameStatus.Abandoned => "abandoned",
        _ => "unterminated",
    };

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
