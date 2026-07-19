using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace AudioReplayBuffer.Core;

/// <summary>Compares the running version against the latest GitHub release.</summary>
public static class UpdateChecker
{
    private const string ApiUrl = "https://api.github.com/repos/TareqAli-CS/AudioReplayBuffer/releases/latest";
    public const string ReleasesPage = "https://github.com/TareqAli-CS/AudioReplayBuffer/releases";

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    /// <summary>Null when the check failed (offline, rate limited, …).</summary>
    public static async Task<(bool IsNewer, string LatestTag)?> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AudioReplayBuffer");
            string json = await http.GetStringAsync(ApiUrl);
            using var doc = JsonDocument.Parse(json);
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";

            var parsed = Version.Parse(tag.TrimStart('v', 'V'));
            var latest = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
            return (latest > CurrentVersion, tag);
        }
        catch (Exception ex)
        {
            Logger.Log("Update check failed: " + ex.Message);
            return null;
        }
    }
}
