namespace RipgrepSearch;

/// <summary>The shared settings schema. Hosts choose separate storage paths and folder comparison policies.</summary>
public sealed class AppSettings
{
    public List<string> FolderHistory { get; set; } = [];
    public List<string> SearchHistory { get; set; } = [];
    public bool ShowCommand { get; set; } = false;
    public bool ShowSearchOptions { get; set; } = true;
    public int BeforeContext { get; set; } = 0;
    public int AfterContext { get; set; } = 0;
    public bool UseRipgrepAll { get; set; } = false;
    public bool SortEnabled { get; set; } = false;
    public string SortBy { get; set; } = "path";
    public bool SortDescending { get; set; } = false;

    /// <summary>
    /// Returns a separate snapshot with the supplied nonblank entries first, unique
    /// folders/patterns capped at 10/20. Patterns always use ordinal comparison.
    /// Empty entries only normalize existing history, including null JSON lists/items.
    /// Does not modify this instance or perform I/O.
    /// </summary>
    public AppSettings WithRecentSearch(string? folder, string? pattern, StringComparer folderComparer)
    {
        ArgumentNullException.ThrowIfNull(folderComparer);
        var copy = (AppSettings)MemberwiseClone();
        copy.FolderHistory = Recent(FolderHistory, folder, 10, folderComparer);
        copy.SearchHistory = Recent(SearchHistory, pattern, 20, StringComparer.Ordinal);
        return copy;
    }

    private static List<string> Recent(IEnumerable<string>? history, string? value, int limit, StringComparer comparer)
    {
        var seen = new HashSet<string>(comparer);
        return (new[] { value }).Concat(history ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item) && seen.Add(item))
            .Take(limit).Select(item => item!).ToList();
    }
}

