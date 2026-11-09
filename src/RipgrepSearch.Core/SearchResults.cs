namespace RipgrepSearch;

// Positions are UTF-16 indices into Text, independent of fonts and colors.
internal readonly record struct MatchRange(int Start, int Length);

internal sealed record SearchResultLine(
    string FilePath,
    string? NavigationPath,
    long LineNumber,
    string Text,
    bool IsMatch,
    IReadOnlyList<MatchRange> Matches);
