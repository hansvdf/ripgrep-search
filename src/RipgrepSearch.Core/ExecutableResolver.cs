namespace RipgrepSearch;

/// <summary>Resolves rg/rga from process PATH using the host OS's executable naming rules.</summary>
public static class ExecutableResolver
{
    public static string? ResolveSearchTool(bool useRipgrepAll) =>
        ResolveSearchTool(useRipgrepAll, Environment.GetEnvironmentVariable("PATH"));

    internal static string? ResolveSearchTool(bool useRipgrepAll, string? searchPath)
    {
        string name = useRipgrepAll ? "rga" : "rg";
        // Keep Windows' original .exe-first precedence across the entire PATH.
        // Unix searches extensionless names; successful execution is checked by the caller.
        return OperatingSystem.IsWindows()
            ? FindExecutable(name + ".exe", searchPath) ?? FindExecutable(name, searchPath)
            : FindExecutable(name, searchPath);
    }

    private static string? FindExecutable(string executable, string? searchPath)
    {
        if (Path.IsPathRooted(executable) && File.Exists(executable))
            return Path.GetFullPath(executable);

        if (string.IsNullOrWhiteSpace(searchPath))
            return null;

        foreach (string directory in searchPath.Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim(), executable);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
                // Ignore malformed PATH entries, as the search lookup always has.
            }
        }
        return null;
    }
}
