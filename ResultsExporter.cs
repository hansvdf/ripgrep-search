using System.Drawing;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RipgrepSearch;

internal sealed record ResultSpan(string Text, Color Color, FontStyle Style, string? FilePath = null);

internal sealed record SearchReport(string Folder, string Pattern, string Command, DateTimeOffset StartedAt)
{
    public List<ResultSpan> Spans { get; } = [];
    public string Status { get; set; } = string.Empty;
}

internal static class ResultsExporter
{
    public static string ToHtml(SearchReport report)
    {
        var html = new StringBuilder("""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Ripgrep Search results</title>
            <style>
            body { background: #161616; color: #fff; font-family: 'Segoe UI', sans-serif; margin: 24px; }
            dt { font-weight: bold; margin-top: 12px; }
            dd { margin: 4px 0; white-space: pre-wrap; overflow-wrap: anywhere; }
            pre { background: #000; padding: 16px; overflow-x: auto; font: 10pt Consolas, 'Courier New', monospace; }
            a { color: inherit; }
            </style>
            </head>
            <body>
            <h1>Ripgrep Search results</h1>
            <dl>
            """);
        AppendDetail("Folder", report.Folder);
        AppendDetail("Pattern", report.Pattern);
        AppendDetail("Command", report.Command);
        AppendDetail("Search started", report.StartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        AppendDetail("Status", report.Status);
        html.Append("</dl><pre><code>");

        foreach (ResultSpan span in report.Spans)
        {
            html.Append("<span style=\"color: ").Append(ColorTranslator.ToHtml(span.Color)).Append(';');
            if (span.Style.HasFlag(FontStyle.Bold))
                html.Append(" font-weight: bold;");
            if (span.Style.HasFlag(FontStyle.Underline))
                html.Append(" text-decoration: underline;");
            html.Append("\">");

            string? uri = GetFileUri(span.FilePath);
            if (uri is not null)
                html.Append("<a href=\"").Append(WebUtility.HtmlEncode(uri)).Append("\">");
            html.Append(WebUtility.HtmlEncode(span.Text));
            if (uri is not null)
                html.Append("</a>");
            html.Append("</span>");
        }

        html.AppendLine("</code></pre></body></html>");
        return html.ToString();

        void AppendDetail(string label, string value)
        {
            html.Append("<dt>").Append(label).Append("</dt><dd>")
                .Append(WebUtility.HtmlEncode(value)).AppendLine("</dd>");
        }
    }

    public static string ToMarkdown(SearchReport report)
    {
        var markdown = new StringBuilder("# Ripgrep Search results\n\n");
        AppendDetail("Folder", report.Folder);
        AppendDetail("Pattern", report.Pattern);
        AppendDetail("Command", report.Command);
        AppendDetail("Search started", report.StartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        AppendDetail("Status", report.Status);
        markdown.AppendLine("## Results").AppendLine();

        var content = new StringBuilder();
        foreach (ResultSpan span in report.Spans)
        {
            if (span.FilePath is not null)
            {
                FlushContent();
                string filename = EscapeMarkdown(span.Text.TrimEnd('\r', '\n'));
                string? uri = GetFileUri(span.FilePath);
                markdown.Append("### ");
                if (uri is not null)
                    markdown.Append('[').Append(filename).Append("](<").Append(uri).Append(">)");
                else
                    markdown.Append(filename);
                markdown.AppendLine().AppendLine();
            }
            else
            {
                content.Append(span.Text);
            }
        }
        FlushContent();
        return markdown.ToString();

        void AppendDetail(string label, string value)
        {
            markdown.Append("**").Append(label).AppendLine("**").AppendLine();
            AppendCodeBlock(markdown, value);
        }

        void FlushContent()
        {
            if (!string.IsNullOrWhiteSpace(content.ToString()))
                AppendCodeBlock(markdown, content.ToString());
            content.Clear();
        }
    }

    private static void AppendCodeBlock(StringBuilder markdown, string text)
    {
        // A longer fence keeps backticks in searched files from ending the block.
        int longestRun = Regex.Matches(text, "`+").Select(match => match.Length).DefaultIfEmpty(0).Max();
        string fence = new('`', Math.Max(3, longestRun + 1));
        markdown.Append(fence).AppendLine("text").Append(text);
        if (!text.EndsWith('\n'))
            markdown.AppendLine();
        markdown.AppendLine(fence).AppendLine();
    }

    private static string EscapeMarkdown(string text)
    {
        var escaped = new StringBuilder();
        foreach (char character in text)
        {
            if ("\\`*_{}[]<>()#+-.!|&".Contains(character))
                escaped.Append('\\');
            escaped.Append(character);
        }
        return escaped.ToString();
    }

    private static string? GetFileUri(string? path)
    {
        if (path is null)
            return null;

        try
        {
            return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
