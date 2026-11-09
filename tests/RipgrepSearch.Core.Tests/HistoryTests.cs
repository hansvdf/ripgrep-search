using RipgrepSearch;

internal static partial class Tests
{
    private static void HistoryTests()
    {
        var original = new AppSettings
        {
            FolderHistory = ["Folder", "folder", "other", "", null!],
            SearchHistory = ["Needle", "needle", "Needle", "  "],
            UseRipgrepAll = true, BeforeContext = 7, ShowCommand = true
        };
        var windows = original.WithRecentSearch("FOLDER", "needle", StringComparer.OrdinalIgnoreCase);
        Check(windows.FolderHistory.SequenceEqual(new[] { "FOLDER", "other" }), "Windows MRU comparer and promotion");
        Check(windows.SearchHistory.SequenceEqual(new[] { "needle", "Needle" }), "patterns remain ordinal case-sensitive");
        var unix = original.WithRecentSearch("folder", "needle ", StringComparer.Ordinal);
        Check(unix.FolderHistory.SequenceEqual(new[] { "folder", "Folder", "other" }), "Unix MRU does not merge case variants");
        Check(unix.SearchHistory[0] == "needle ", "pattern trailing whitespace preserved");
        Check(original.FolderHistory.Count == 5 && original.SearchHistory.Count == 4, "MRU snapshot does not mutate its source");
        Check(windows.UseRipgrepAll && windows.BeforeContext == 7 && windows.ShowCommand, "snapshot preserves other existing preferences");
        windows.FolderHistory.Add("only copy");
        Check(!original.FolderHistory.Contains("only copy"), "history lists are independent in snapshots");
        var many = new AppSettings();
        for (int i = 0; i < 25; i++) many = many.WithRecentSearch("folder " + i, "pattern " + i, StringComparer.Ordinal);
        Check(many.FolderHistory.Count == 10 && many.FolderHistory[0] == "folder 24" && many.FolderHistory[^1] == "folder 15", "folder MRU cap and ordering");
        Check(many.SearchHistory.Count == 20 && many.SearchHistory[0] == "pattern 24" && many.SearchHistory[^1] == "pattern 5", "pattern MRU cap and ordering");
        var blank = many.WithRecentSearch(" ", "", StringComparer.Ordinal);
        Check(blank.FolderHistory.SequenceEqual(many.FolderHistory) && blank.SearchHistory.SequenceEqual(many.SearchHistory), "blank values do not enter or reorder MRUs");
        var corrupt = new AppSettings { FolderHistory = null!, SearchHistory = [null!, "", "ok", "ok"] }
            .WithRecentSearch(null, null, StringComparer.Ordinal);
        Check(corrupt.FolderHistory.Count == 0 && corrupt.SearchHistory.SequenceEqual(new[] { "ok" }), "opt-in MRU normalization handles null JSON histories/items");
    }
}
