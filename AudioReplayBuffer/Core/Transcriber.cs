using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AudioReplayBuffer.Core;

public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text, string? Speaker = null);

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

    // ---------- AssemblyAI (speaker diarization) ----------

    private const string AssemblyAiBase = "https://api.assemblyai.com/v2";

    /// <summary>
    /// Transcription with speaker labels via AssemblyAI: upload → create
    /// transcript job with speaker_labels → poll until done. Speakers are
    /// mapped to "Speaker 1", "Speaker 2", … in order of first appearance.
    /// </summary>
    public static async Task<TranscriptResult> TranscribeWithSpeakersAsync(string filePath, string apiKey,
                                                                           CancellationToken ct = default)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists)
            throw new FileNotFoundException("Audio file not found.", filePath);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Add("authorization", apiKey.Trim());

        // 1. Upload the audio.
        string uploadUrl;
        await using (var stream = File.OpenRead(filePath))
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var uploadResponse = await http.PostAsync($"{AssemblyAiBase}/upload", content, ct);
            string uploadJson = await uploadResponse.Content.ReadAsStringAsync(ct);
            if (!uploadResponse.IsSuccessStatusCode)
                throw new InvalidOperationException(ExtractAssemblyAiError(uploadJson)
                    ?? $"Upload failed: HTTP {(int)uploadResponse.StatusCode}");
            using var uploadDoc = JsonDocument.Parse(uploadJson);
            uploadUrl = uploadDoc.RootElement.GetProperty("upload_url").GetString()
                ?? throw new InvalidOperationException("Upload returned no URL.");
        }

        // 2. Create the transcription job.
        string requestJson = JsonSerializer.Serialize(new
        {
            audio_url = uploadUrl,
            speaker_labels = true,
            language_detection = true
        });
        string transcriptId;
        using (var createResponse = await http.PostAsync($"{AssemblyAiBase}/transcript",
                   new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json"), ct))
        {
            string createJson = await createResponse.Content.ReadAsStringAsync(ct);
            if (!createResponse.IsSuccessStatusCode)
                throw new InvalidOperationException(ExtractAssemblyAiError(createJson)
                    ?? $"HTTP {(int)createResponse.StatusCode}");
            using var createDoc = JsonDocument.Parse(createJson);
            transcriptId = createDoc.RootElement.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Transcript job returned no id.");
        }

        // 3. Poll for completion.
        while (true)
        {
            await Task.Delay(1500, ct);
            using var pollResponse = await http.GetAsync($"{AssemblyAiBase}/transcript/{transcriptId}", ct);
            string pollJson = await pollResponse.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(pollJson);
            var root = doc.RootElement;
            string status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";

            if (status == "error")
                throw new InvalidOperationException(
                    root.TryGetProperty("error", out var err) ? err.GetString() ?? "Transcription error." : "Transcription error.");
            if (status != "completed")
                continue;

            string text = root.TryGetProperty("text", out var t) ? t.GetString()?.Trim() ?? "" : "";
            string? language = root.TryGetProperty("language_code", out var lang) ? lang.GetString() : null;

            var segments = new List<TranscriptSegment>();
            var speakerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("utterances", out var utterances) &&
                utterances.ValueKind == JsonValueKind.Array)
            {
                foreach (var utterance in utterances.EnumerateArray())
                {
                    string speakerKey = utterance.TryGetProperty("speaker", out var sp)
                        ? sp.GetString() ?? "?" : "?";
                    if (!speakerNames.TryGetValue(speakerKey, out string? name))
                    {
                        name = $"Speaker {speakerNames.Count + 1}";
                        speakerNames[speakerKey] = name;
                    }
                    double startMs = utterance.TryGetProperty("start", out var s) ? s.GetDouble() : 0;
                    double endMs = utterance.TryGetProperty("end", out var e) ? e.GetDouble() : startMs;
                    string segText = utterance.TryGetProperty("text", out var stx)
                        ? stx.GetString()?.Trim() ?? "" : "";
                    if (segText.Length > 0)
                        segments.Add(new TranscriptSegment(
                            TimeSpan.FromMilliseconds(startMs), TimeSpan.FromMilliseconds(endMs),
                            segText, name));
                }
            }

            return new TranscriptResult(text, segments, language);
        }
    }

    private static string? ExtractAssemblyAiError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var error))
                return error.ValueKind == JsonValueKind.String ? error.GetString() : error.ToString();
        }
        catch { }
        return null;
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
