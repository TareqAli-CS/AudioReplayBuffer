using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AudioReplayBuffer.Core;

public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

public sealed record TranscriptResult(string Text, IReadOnlyList<TranscriptSegment> Segments, string? Language);

/// <summary>
/// Speech-to-text via Groq's hosted Whisper (OpenAI-compatible API).
/// The audio file is uploaded to Groq for processing; the API key is
/// passed per call and never logged.
/// </summary>
public static class Transcriber
{
    private const string Endpoint = "https://api.groq.com/openai/v1/audio/transcriptions";
    private const string Model = "whisper-large-v3";
    public const long MaxFileBytes = 25 * 1024 * 1024;

    public static async Task<TranscriptResult> TranscribeAsync(string filePath, string apiKey,
                                                               CancellationToken ct = default)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists)
            throw new FileNotFoundException("Audio file not found.", filePath);
        if (info.Length > MaxFileBytes)
            throw new InvalidOperationException(
                "The file is larger than the API's 25 MB limit — trim it in the editor first.");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var form = new MultipartFormDataContent();
        await using var stream = File.OpenRead(filePath);
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            info.Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ? "audio/wav" : "audio/mpeg");
        form.Add(fileContent, "file", info.Name);
        form.Add(new StringContent(Model), "model");
        form.Add(new StringContent("verbose_json"), "response_format");

        using var response = await http.PostAsync(Endpoint, form, ct);
        string json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ExtractApiError(json) ?? $"HTTP {(int)response.StatusCode}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string text = root.TryGetProperty("text", out var t) ? t.GetString()?.Trim() ?? "" : "";
        string? language = root.TryGetProperty("language", out var lang) ? lang.GetString() : null;

        var segments = new List<TranscriptSegment>();
        if (root.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segs.EnumerateArray())
            {
                double start = seg.TryGetProperty("start", out var s) ? s.GetDouble() : 0;
                double end = seg.TryGetProperty("end", out var e) ? e.GetDouble() : start;
                string segText = seg.TryGetProperty("text", out var st) ? st.GetString()?.Trim() ?? "" : "";
                if (segText.Length > 0)
                    segments.Add(new TranscriptSegment(
                        TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), segText));
            }
        }

        return new TranscriptResult(text, segments, language);
    }

    private static string? ExtractApiError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
                return message.GetString();
        }
        catch { }
        return null;
    }
}
