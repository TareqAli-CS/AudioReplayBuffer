using System.Diagnostics;
using ReplayPad.Configuration;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace ReplayPad.Core;

/// <summary>
/// Turns a PCM snapshot into an MP3 file. Primary encoder is Windows
/// Media Foundation (built in, no dependencies); falls back to ffmpeg if
/// available, and to a plain WAV as a last resort so audio is never lost.
/// </summary>
public sealed class ReplaySaver(AppSettings settings)
{
    public string Save(byte[] pcm, string? tag = null)
    {
        if (pcm.Length == 0)
            throw new InvalidOperationException("The buffer is empty — nothing captured yet.");

        string folder = settings.ResolveOutputFolder();
        Directory.CreateDirectory(folder);
        string suffix = tag != null ? "-" + tag : "";
        string baseName = $"{settings.FileNamePrefix}{suffix}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        string mp3Path = UniquePath(folder, baseName, ".mp3");

        try
        {
            using var source = CreateWaveStream(pcm);
            MediaFoundationEncoder.EncodeToMp3(source, mp3Path, settings.Bitrate * 1000);
            return mp3Path;
        }
        catch (Exception ex)
        {
            Logger.Log("MediaFoundation MP3 encoding failed: " + ex.Message);
            TryDelete(mp3Path);
        }

        try
        {
            string? ffmpeg = FindFFmpeg();
            if (ffmpeg != null)
            {
                EncodeWithFFmpeg(ffmpeg, pcm, mp3Path);
                return mp3Path;
            }

            // Last resort: uncompressed WAV beats losing the recording.
            string wavPath = UniquePath(folder, baseName, ".wav");
            using var wavSource = CreateWaveStream(pcm);
            WaveFileWriter.CreateWaveFile(wavPath, wavSource);
            Logger.Log("No MP3 encoder available — saved WAV instead: " + wavPath);
            return wavPath;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(ex.Message + ControlledFolderHint(folder), ex);
        }
    }

    /// <summary>
    /// Every encoder failing on a folder Windows Ransomware Protection
    /// guards (Music, Documents, …) is almost always Controlled Folder
    /// Access blocking this exe — the errors it produces ("no such file")
    /// are misleading, so spell it out.
    /// </summary>
    private static string ControlledFolderHint(string folder)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        bool inProtected = new[]
        {
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.Desktop
        }.Any(sf =>
        {
            string p = Environment.GetFolderPath(sf);
            return p.Length > 0 && folder.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        }) || folder.StartsWith(profile, StringComparison.OrdinalIgnoreCase);

        return inProtected
            ? "\n\nIf this keeps happening, Windows Ransomware Protection is likely blocking ReplayPad: " +
              "Windows Security → Virus & threat protection → Manage ransomware protection → " +
              "Allow an app through Controlled folder access → add ReplayPad.exe."
            : "";
    }

    /// <summary>
    /// Encodes PCM to MP3 at an exact output path (used by the editor).
    /// Media Foundation first, ffmpeg fallback; throws if neither works.
    /// </summary>
    public void EncodeTo(byte[] pcm, string outputPath)
    {
        try
        {
            using var source = CreateWaveStream(pcm);
            MediaFoundationEncoder.EncodeToMp3(source, outputPath, settings.Bitrate * 1000);
            return;
        }
        catch (Exception ex)
        {
            Logger.Log("MediaFoundation MP3 encoding failed: " + ex.Message);
            TryDelete(outputPath);
        }

        string? ffmpeg = FindFFmpeg();
        if (ffmpeg == null)
            throw new InvalidOperationException(
                "No MP3 encoder available (Media Foundation failed and ffmpeg was not found).");
        EncodeWithFFmpeg(ffmpeg, pcm, outputPath);
    }

    private static RawSourceWaveStream CreateWaveStream(byte[] pcm)
        => new(new MemoryStream(pcm), AudioCaptureEngine.BufferFormat);

    private void EncodeWithFFmpeg(string ffmpegPath, byte[] pcm, string outputPath)
    {
        string tempWav = Path.Combine(Path.GetTempPath(), $"arb_{Guid.NewGuid():N}.wav");
        try
        {
            using (var source = CreateWaveStream(pcm))
                WaveFileWriter.CreateWaveFile(tempWav, source);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-hide_banner -nostdin -y -i \"{tempWav}\" -codec:a libmp3lame -b:a {settings.Bitrate}k \"{outputPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ffmpeg.");
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("ffmpeg timed out.");
            }
            if (process.ExitCode != 0 || !File.Exists(outputPath))
                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode}: {stderr[^Math.Min(stderr.Length, 500)..]}");
        }
        finally
        {
            TryDelete(tempWav);
        }
    }

    private string? FindFFmpeg()
    {
        if (!string.IsNullOrWhiteSpace(settings.FFmpegPath) && File.Exists(settings.FFmpegPath))
            return settings.FFmpegPath;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            try
            {
                string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }
        return null;
    }

    public static string UniquePath(string folder, string baseName, string extension)
    {
        string path = Path.Combine(folder, baseName + extension);
        for (int i = 1; File.Exists(path); i++)
            path = Path.Combine(folder, $"{baseName} ({i}){extension}");
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
