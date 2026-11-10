namespace RipgrepSearch;

/// <summary>Human-readable command preview using v0.10.0 quoting. Never use this string for shell execution.</summary>
public static class SearchCommandFormatter
{
    public static string Format(string executableName, IEnumerable<string> arguments) =>
        executableName + " " + string.Join(" ", arguments.Select(QuoteForDisplay));

    private static string QuoteForDisplay(string argument)
    {
        bool needsQuotes = argument.Length == 0 || argument.Any(char.IsWhiteSpace) || argument.Contains('"');
        if (!needsQuotes)
            return argument;

        return '"' + argument.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"';
    }
}
