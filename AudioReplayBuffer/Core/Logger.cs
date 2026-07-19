namespace AudioReplayBuffer.Core;

internal static class Logger
{
    private static readonly object Lock = new();
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "log.txt");

    public static void Log(string message)
    {
        try
        {
            lock (Lock)
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
