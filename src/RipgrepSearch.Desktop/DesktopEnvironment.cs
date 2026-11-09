namespace RipgrepSearch.Desktop;

internal enum DesktopPlatform { Windows, MacOS, Linux }

// Host policy only. No registry access, shell startup scripts or file creation.
internal sealed record DesktopEnvironment(DesktopPlatform Platform, string Home, string WorkingDirectory,
    string SettingsFile)
{
    public StringComparer FolderComparer => Platform == DesktopPlatform.Windows
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static DesktopEnvironment Current()
    {
        var platform = OperatingSystem.IsWindows() ? DesktopPlatform.Windows
            : OperatingSystem.IsMacOS() ? DesktopPlatform.MacOS : DesktopPlatform.Linux;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);
        string workingDirectory;
        try { workingDirectory = Environment.CurrentDirectory; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { workingDirectory = home; }
        return new(platform, home, workingDirectory, SettingsPath(platform, home,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")));
    }

    internal static string SettingsPath(DesktopPlatform platform, string home, string roaming, string? xdgConfigHome)
    {
        string parent = platform switch
        {
            DesktopPlatform.Windows => Path.IsPathFullyQualified(roaming) ? roaming : Path.Combine(home, "AppData", "Roaming"),
            DesktopPlatform.MacOS => Path.Combine(home, "Library", "Application Support"),
            _ => !string.IsNullOrEmpty(xdgConfigHome) && Path.IsPathFullyQualified(xdgConfigHome)
                ? xdgConfigHome : Path.Combine(home, ".config")
        };
        // Never fall back to writing settings in the executable or current directory.
        return Path.IsPathFullyQualified(parent) ? Path.Combine(parent, "RipgrepSearch.Desktop", "settings.json") : string.Empty;
    }

    public string FolderInput(string text) => Platform == DesktopPlatform.Windows ? text.Trim() : text;

    public string ResolveFolder(string text)
    {
        string value = FolderInput(text);
        if (Path.IsPathFullyQualified(value)) return Path.GetFullPath(value);
        string baseDirectory = Path.IsPathFullyQualified(WorkingDirectory) ? WorkingDirectory : Home;
        return Path.GetFullPath(value, baseDirectory);
    }

    // Runs on the startup worker with settings loading, not during window construction.
    public string InitialFolder(string? argument, IReadOnlyList<string> history)
    {
        if (!string.IsNullOrWhiteSpace(argument))
        {
            try
            {
                string candidate = ResolveFolder(argument.Trim('"'));
                if (Directory.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        // WinForms supports Explorer's inherited cwd. Unix GUI launches commonly
        // inherit '/' or a bundle directory, so use history/home there instead.
        if (Platform == DesktopPlatform.Windows && Directory.Exists(WorkingDirectory)) return WorkingDirectory;
        return history.FirstOrDefault() ?? Home;
    }
}
