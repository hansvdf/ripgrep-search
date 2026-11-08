namespace RipgrepSearch;

internal static class ExecutableResolver
{
    public static string? ResolveSearchTool(bool useRipgrepAll) =>
        ResolveSearchTool(useRipgrepAll, Environment.GetEnvironmentVariable("PATH"));

    internal static string? ResolveSearchTool(bool useRipgrepAll, string? searchPath)
    {
        string name = useRipgrepAll ? "rga" : "rg";
        return FindExecutable(name + ".exe", searchPath) ?? FindExecutable(name, searchPath);
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
