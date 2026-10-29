namespace LanChess.Chess;

/// <summary>A clock setting such as "Rapid 10 + 0". BaseMinutes of 0 means no clock.</summary>
public sealed record TimeControl(string Name, int BaseMinutes, int IncrementSeconds)
{
    /// <summary>Sentinel shown in the picker; the setup screen reads the custom boxes instead.</summary>
    public static readonly TimeControl Custom = new("Custom…", -1, 0);

    public static readonly TimeControl[] Presets =
    {
        new("Bullet 1 + 0", 1, 0),
        new("Bullet 2 + 1", 2, 1),
        new("Blitz 3 + 0", 3, 0),
        new("Blitz 3 + 2", 3, 2),
        new("Blitz 5 + 3", 5, 3),
        new("Rapid 10 + 0", 10, 0),
        new("Rapid 15 + 10", 15, 10),
        new("Classical 30 + 0", 30, 0),
        new("No clock", 0, 0),
    };

    public TimeSpan Base => TimeSpan.FromMinutes(BaseMinutes);
    public TimeSpan Increment => TimeSpan.FromSeconds(IncrementSeconds);

    public static TimeControl CreateCustom(int minutes, int increment) =>
        new($"Custom {minutes} + {increment}", minutes, increment);
}
