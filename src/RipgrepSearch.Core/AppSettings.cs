namespace RipgrepSearch;

internal sealed class AppSettings
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
}

