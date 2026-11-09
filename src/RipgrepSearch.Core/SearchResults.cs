namespace RipgrepSearch;

/// <summary>A UTF-16 range in SearchResultLine.Text, independent of fonts and colors.</summary>
public readonly record struct MatchRange(int Start, int Length);

/// <summary>A parsed match or context line. NavigationPath is null when the path cannot safely be opened.</summary>
public sealed record SearchResultLine(
    string FilePath,
    string? NavigationPath,
    long LineNumber,
    string Text,
    bool IsMatch,
    IReadOnlyList<MatchRange> Matches);
