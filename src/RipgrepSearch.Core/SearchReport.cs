namespace RipgrepSearch;

/// <summary>The fixed v0.10.0 palette; enum values contain 24-bit RGB data.</summary>
public enum ResultColor
{
    White = 0xFFFFFF,
    DeepSkyBlue = 0x00BFFF,
    LimeGreen = 0x32CD32,
    DarkGray = 0xA9A9A9,
    OrangeRed = 0xFF4500,
    Gray = 0x808080
}

/// <summary>Portable text decorations for result spans.</summary>
[Flags]
public enum ResultTextStyle { Regular = 0, Bold = 1, Underline = 4 }

internal static class ResultPalette
{
    public static (byte R, byte G, byte B) GetRgb(ResultColor color) =>
        ((byte)((int)color >> 16), (byte)((int)color >> 8), (byte)color);

    // Preserve ColorTranslator's named-color output in existing HTML reports.
    public static string ToHtml(ResultColor color) => color.ToString();
}

/// <summary>Formatted text with optional file-heading navigation metadata; Text uses UTF-16 and preserves CRLF.</summary>
public sealed record ResultSpan(string Text, ResultColor Color, ResultTextStyle Style,
    string? FilePath = null, bool IsFileHeading = false);

internal sealed record SearchReport(string Folder, string Pattern, string Command, DateTimeOffset StartedAt)
{
    public List<ResultSpan> Spans { get; } = [];
    public List<SearchResultLine> Results { get; } = [];
    public string Status { get; set; } = string.Empty;
}
