namespace AudioReplayBuffer.Core;

/// <summary>
/// User data lives in %AppData%\AudioReplayBuffer, not next to the exe:
/// installer updates replace the install folder, and settings must survive
/// them. Legacy files from older versions (stored beside the exe) are
/// migrated once.
/// </summary>
public static class AppPaths
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioReplayBuffer");

    public static string SettingsPath => Path.Combine(DataDir, "appsettings.json");
    public static string SoundboardPath => Path.Combine(DataDir, "soundboard.json");
    public static string LogPath => Path.Combine(DataDir, "log.txt");

    /// <summary>
    /// Creates the data folder and copies legacy config files from the exe
    /// directory if no AppData copy exists yet. Call once at startup,
    /// before anything reads settings or logs.
    /// </summary>
    public static void EnsureAndMigrate()
    {
        Directory.CreateDirectory(DataDir);
        MigrateLegacyFile("appsettings.json", SettingsPath);
        MigrateLegacyFile("soundboard.json", SoundboardPath);
    }

    private static void MigrateLegacyFile(string fileName, string newPath)
    {
        try
        {
            string legacyPath = Path.Combine(AppContext.BaseDirectory, fileName);
            if (!File.Exists(newPath) && File.Exists(legacyPath))
                File.Copy(legacyPath, newPath);
        }
        catch
        {
            // Migration is best-effort; the app falls back to defaults.
        }
    }
}
