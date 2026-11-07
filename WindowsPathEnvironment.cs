using System.Collections;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RipgrepSearch;

internal static class WindowsPathEnvironment
{
    private const int MaximumPathLength = 32_766;
    private static readonly Regex VariableReference = new("%([^%]+)%", RegexOptions.NonBacktracking);

    public static string RefreshProcessPath() => RefreshProcessPath(
        ReadSavedEnvironment(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
        ReadSavedEnvironment(Registry.CurrentUser, @"Environment"),
        Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process));

    private static IDictionary ReadSavedEnvironment(RegistryKey root, string subkey)
    {
        var values = new Hashtable(StringComparer.OrdinalIgnoreCase);
        using var key = root.OpenSubKey(subkey);
        if (key is null)
            return values;
        foreach (string name in key.GetValueNames())
        {
            // Environment.GetEnvironmentVariables expands REG_EXPAND_SZ using the
            // stale process environment. Read raw references and expand them below.
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
                values[name] = value;
        }
        return values;
    }

    internal static string RefreshProcessPath(IDictionary machine, IDictionary user, IDictionary process)
    {
        string path = BuildPath(machine, user, process);
        // Update this app and future child processes only; never write Windows settings.
        Environment.SetEnvironmentVariable("PATH", path, EnvironmentVariableTarget.Process);
        return path;
    }

    internal static string BuildPath(IDictionary machine, IDictionary user, IDictionary process)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Overlay(process);
        variables.Remove("PATH"); // Do not resurrect stale entries from the inherited PATH.
        Overlay(machine);
        string machinePath = variables.GetValueOrDefault("PATH", string.Empty);
        variables.Remove("PATH");
        machinePath = Expand(machinePath, variables, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Overlay(user);
        string userPath = variables.GetValueOrDefault("PATH", string.Empty);
        variables["PATH"] = machinePath;
        userPath = Expand(userPath, variables, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        string path = string.Join(Path.PathSeparator, new[] { machinePath, userPath }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        CheckLength(path.Length);
        return path;

        void Overlay(IDictionary values)
        {
            foreach (DictionaryEntry entry in values)
                if (entry.Key is string name && entry.Value is string value)
                    variables[name] = value;
        }
    }

    private static string Expand(string value, IReadOnlyDictionary<string, string> variables, HashSet<string> expanding)
    {
        CheckLength(value.Length);
        int length = value.Length;
        return VariableReference.Replace(value, match =>
        {
            string name = match.Groups[1].Value;
            if (expanding.Count >= 16 || !variables.TryGetValue(name, out string? replacement) || !expanding.Add(name))
                return match.Value;
            try
            {
                replacement = Expand(replacement, variables, expanding);
                length += replacement.Length - match.Length;
                CheckLength(length);
                return replacement;
            }
            finally { expanding.Remove(name); }
        });
    }

    private static void CheckLength(int length)
    {
        if (length > MaximumPathLength)
            throw new InvalidOperationException("The refreshed Windows PATH exceeds the supported environment-variable length.");
    }
}
