using System.Text.Json;

namespace RipgrepSearch;

/// <summary>Best-effort JSON storage at a host-selected path. Loading never creates, migrates, or rewrites a file.</summary>
public static class AppSettingsStore
{
    public static AppSettings Load(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath)) ?? new AppSettings();
        }
        catch
        {
            // Preserve the application's best-effort settings behavior.
        }
        return new AppSettings();
    }

    public static void Save(string filePath, AppSettings settings)
    {
        try
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
        catch
        {
            // Settings persistence must not prevent searches from working.
        }
    }
}
