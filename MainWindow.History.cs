using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using LanChess.Storage;

namespace LanChess;

/// <summary>The history screen: saved games, their PGN, and stats.</summary>
public partial class MainWindow
{
    private void ShowHistory_Click(object sender, RoutedEventArgs e)
    {
        ReloadHistory();
        SetupPanel.Visibility = Visibility.Collapsed;
        HistoryPanel.Visibility = Visibility.Visible;
    }

    private void BackToSetup_Click(object sender, RoutedEventArgs e)
    {
        HistoryPanel.Visibility = Visibility.Collapsed;
        SetupPanel.Visibility = Visibility.Visible;
    }

    private void ReloadHistory()
    {
        var games = GameStore.LoadAll().OrderByDescending(g => g.PlayedUtc).ToList();

        GamesList.ItemsSource = games;
        StatsText.Text = StatsSummary(games);
        PgnBox.Text = games.Count == 0 ? "No games yet. Finished games are saved here automatically." : "";

        if (games.Count > 0)
        {
            GamesList.SelectedIndex = 0;
        }
    }

    private void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GamesList.SelectedItem is SavedGame game)
        {
            PgnBox.Text = game.Pgn;
        }
    }

    private static string StatsSummary(List<SavedGame> games)
    {
        var stats = GameStats.From(games);
        var sb = new StringBuilder();

        sb.AppendLine($"Played {stats.Played}    Wins {stats.Wins}    Losses {stats.Losses}    Draws {stats.Draws}");
        sb.AppendLine($"Win rate {stats.WinRate:P0}" + (stats.Unfinished > 0 ? $"    Unfinished {stats.Unfinished}" : ""));

        if (games.Count == 0)
        {
            return sb.ToString();
        }

        sb.AppendLine();
        sb.AppendLine("By time control");
        foreach (var group in games.GroupBy(g => g.TimeControl).OrderByDescending(g => g.Count()))
        {
            AppendRow(sb, group.Key, group);
        }

        sb.AppendLine();
        sb.AppendLine("Against");
        foreach (var group in games.GroupBy(g => g.OpponentName).OrderByDescending(g => g.Count()).Take(5))
        {
            AppendRow(sb, group.Key, group);
        }

        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, string label, IEnumerable<SavedGame> games)
    {
        var s = GameStats.From(games);
        sb.AppendLine($"  {label,-18} {s.Played,3} played   {s.Wins}W {s.Losses}L {s.Draws}D");
    }

    private void OpenGamesFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(GameStore.GamesFolder);
        Process.Start(new ProcessStartInfo { FileName = GameStore.GamesFolder, UseShellExecute = true });
    }
}
