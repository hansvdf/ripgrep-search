using System.Text;
using System.Text.RegularExpressions;

namespace RipgrepSearch;

internal static class DependencyDiagnostics
{
    // Metadata and redaction policy are supplied by the host; no system data is collected here.
    public static string CreateReport(IReadOnlyList<DependencyCheckResult> results, string applicationVersion,
        string operatingSystem, string? userProfile, string operatingSystemLabel = "OS version",
        string profilePlaceholder = "%USERPROFILE%", bool ignoreProfileCase = true)
    {
        var report = new StringBuilder();
        report.AppendLine("Ripgrep Search — Dependency Diagnostics");
        report.AppendLine($"Application version: {applicationVersion}");
        report.AppendLine($"{operatingSystemLabel}: {operatingSystem}");
        foreach (DependencyCheckResult result in results)
        {
            report.AppendLine();
            report.AppendLine(result.Dependency.DisplayName);
            string status = result.Status == DependencyStatus.NotFound ? "Not found" : result.Status.ToString();
            report.AppendLine($"Status: {status}");
            report.AppendLine($"Version: {result.Version ?? "Not available"}");
            report.AppendLine($"Executable path: {result.ExecutablePath ?? "Not available"}");
            if (result.Status != DependencyStatus.Installed && !string.IsNullOrWhiteSpace(result.Details))
            {
                report.AppendLine("Diagnostics:");
                report.AppendLine(result.Details);
            }
        }
        return RedactUserProfile(report.ToString(), userProfile, profilePlaceholder, ignoreProfileCase);
    }

    internal static string RedactUserProfile(string text, string? userProfile,
        string placeholder = "%USERPROFILE%", bool ignoreCase = true)
    {
        if (string.IsNullOrWhiteSpace(userProfile))
            return text;
        string profile = userProfile.TrimEnd('\\', '/');
        if (profile.Length == 0)
            return text;
        string pathPattern = Regex.Escape(profile.Replace('\\', '/')).Replace("/", @"[\\/]+");
        // Match whole directory names, allowing either separator and escaped paths.
        // Keep the following delimiter so quoted paths and messages retain their format.
        return Regex.Replace(text, pathPattern + @"([\\/\s""'<>)\]},;:]|$)",
            match => placeholder + match.Groups[1].Value,
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking |
            (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None));
    }
}
