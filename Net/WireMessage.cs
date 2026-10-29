using System.Text.Json;

namespace LanChess.Net;

public static class WireTypes
{
    public const string Hello = "hello";
    public const string Start = "start";
    public const string Reject = "reject";
    public const string Sync = "sync";
    public const string Move = "move";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Resign = "resign";
    public const string Timeout = "timeout";
    public const string DrawOffer = "drawOffer";
    public const string DrawCancel = "drawCancel";
    public const string DrawDecline = "drawDecline";
    public const string DrawAccept = "drawAccept";
    public const string Ping = "ping";
}

/// <summary>One JSON line on the wire. Fields a message doesn't use stay at their defaults.</summary>
public sealed class WireMessage
{
    public string Type { get; set; } = "";

    /// <summary>Game id, sent by the host in "start" and echoed by a guest in "hello" to reconnect.</summary>
    public string? Token { get; set; }
    public string? Name { get; set; }
    public string? Reason { get; set; }

    // start
    public string? TimeControl { get; set; }
    public long BaseMs { get; set; }
    public long IncrementMs { get; set; }

    // move
    public string? Uci { get; set; }

    // move, pause, resume, sync: clocks as the sender sees them
    public long WhiteMs { get; set; }
    public long BlackMs { get; set; }

    // sync: full game state so a reconnecting player can catch up
    public bool Paused { get; set; }
    public List<string>? Moves { get; set; }
    public string? DrawFrom { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static WireMessage? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WireMessage>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
