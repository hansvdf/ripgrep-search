namespace RipgrepSearch;

// Windows policy remains in the WinForms host; probes and resolution live in Core.
internal static class WindowsDependencies
{
    public static Task<DependencyCheckResult[]> CheckAllAsync(CancellationToken cancellationToken = default) =>
        DependencyChecker.CheckAllWithPathProviderAsync(WindowsPathEnvironment.RefreshProcessPath,
            TimeSpan.FromSeconds(5), cancellationToken);
}
