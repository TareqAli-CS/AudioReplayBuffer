namespace AudioReplayBuffer.Core;

internal static class Logger
{
    private static readonly object Lock = new();

    public static void Log(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.AppendAllText(AppPaths.LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
