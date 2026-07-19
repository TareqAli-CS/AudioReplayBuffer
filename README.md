> [!WARNING]
> **This project was fully written by AI** (Claude, by Anthropic). It may contain bugs or unexpected behavior. Use it at your own risk — **we are not responsible** for any problems, data loss, or damage resulting from its use.

# 🔴 Audio Replay Buffer

**An audio-only replay buffer for Windows.** It silently keeps the last few minutes of your PC's sound in RAM — press a hotkey and that moment is saved as an MP3. Like OBS Replay Buffer, but for audio only: no video encoding, no disk writes while idle, ~0% CPU.

Someone said something hilarious on Discord? Clutch game moment? Press **Ctrl+Alt+D** and the last 30 seconds are yours forever. Then trim it, give it a name, bind it to a hotkey, and replay it into your next call.

![Audio Replay Buffer main window](docs/screenshot.png)

## Features

- 🎧 **Rolling replay buffer** — keeps the last 1–30 minutes (configurable) of audio in RAM. Nothing is written to disk until you save.
- ⚡ **Two save hotkeys** — `Ctrl+Alt+S` saves the whole buffer, `Ctrl+Alt+D` saves just the last 30 seconds (both configurable).
- 🎯 **Per-app capture** — record only one app's audio (just the game, just Discord…), or everything *except* one app, using the Windows process-loopback API. Or capture the whole desktop, or the microphone, or both mixed.
- ✂️ **Built-in editor** — trim with two range handles, cut sections out, fade in/out, adjust volume, normalize, undo — then save as a copy or overwrite.
- 🎙️ **Play to mic** — play any replay into your call via a virtual audio cable (Voicemod / VB-CABLE). Live volume control, optional self-monitor.
- 🎛️ **Full soundboard** — a pad grid with its own sound library: drag & drop any MP3/WAV in, or send replays to it. Click a pad to fire it into the call; search filters instantly; per-sound volume; overlap or interrupt mode.
- 🚀 **Quick launcher** — press `Ctrl+Alt+Q` in any game, type two letters, hit Enter — the sound plays into your call. No window, no mouse.
- ⌨️ **Soundboard hotkeys** — bind sounds to `Ctrl+Alt+1`–`9` and fire them from anywhere. `Ctrl+Alt+0` stops.
- 🏷️ **Labels** — give replays friendly names ("bruh sound #2") without touching the file name, or rename the file too.
- 📊 **Live UI** — level meter, buffer waveform, recent replays with durations. Closes to the system tray; capture keeps running.
- 🚀 **Lightweight** — ~0% CPU and ~70 MB RAM while idle (5-minute buffer). Starts with Windows (optional), hidden in the tray.

## Requirements

- **Windows 10 (version 2004+) or Windows 11** — per-app capture needs 2004+; everything else works on any Windows 10.
- **[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)** (or build self-contained, see below).
- **For "Play to mic":** a virtual audio cable — [VB-CABLE](https://vb-audio.com/Cable/) (free) or [Voicemod](https://www.voicemod.net/). Not needed for recording.
- MP3 encoding uses Windows' built-in Media Foundation — no extra codecs needed. (If present, [ffmpeg](https://ffmpeg.org/) is used as an automatic fallback.)

## Installation

**Easiest — the installer:** download `AudioReplayBuffer-Setup-x.x.x.exe` from the **[Releases page](https://github.com/TareqAli-CS/AudioReplayBuffer/releases)** and run it. No .NET installation needed, no admin rights needed — it installs per-user with a Start Menu shortcut and a clean uninstaller. A **portable zip** (unzip & run) is also available there.

> **Note:** Windows SmartScreen may warn about an unsigned exe the first time — click *More info → Run anyway*.

**Or build from source:**

```powershell
git clone https://github.com/TareqAli-CS/AudioReplayBuffer.git
cd AudioReplayBuffer

# Framework-dependent build (needs the .NET 10 Desktop Runtime installed):
dotnet publish AudioReplayBuffer -c Release -o publish

# OR self-contained (bigger, but runs on machines without .NET):
dotnet publish AudioReplayBuffer -c Release -r win-x64 --self-contained true -o publish
```

Then run `publish\AudioReplayBuffer.exe`. The installer itself is built from [installer/AudioReplayBuffer.iss](installer/AudioReplayBuffer.iss) with [Inno Setup](https://jrsoftware.org/isinfo.php).

## Quick Start

1. **Run the app.** It immediately starts buffering desktop audio (the red *Recording* dot and level meter confirm it). The **■ Stop / ▶ Start** button halts and resumes buffering; every launch always starts in the recording state.
2. **Press `Ctrl+Alt+S`** anytime — the last 5 minutes land in `Music\Replays` as an MP3. **`Ctrl+Alt+D`** saves just the last 30 seconds.
3. **Double-click a replay** in the list to play it into your call, or right-click for more (edit, rename, show in Explorer, delete).
4. Open **⚙ Settings** to change what's captured, buffer length, hotkeys, MP3 quality and output folder. Changes apply instantly.
5. Enable **Start with Windows** in Settings and forget it's there.

## Setting Up "Play to Mic" (Soundboard)

Windows doesn't let apps inject audio into a physical microphone — every soundboard works through a **virtual audio device**:

1. Install [VB-CABLE](https://vb-audio.com/Cable/) (or use Voicemod's virtual device if you have it).
2. In **⚙ Settings → Play to mic**, pick the cable's *playback* device (e.g. `CABLE Input (VB-Audio Virtual Cable)` or `Line (Voicemod Virtual Audio Device)`).
3. In Discord (or any call app), set the **input device** to the cable's *microphone* side (e.g. `CABLE Output`). Voicemod users: keep Discord on the Voicemod mic — Voicemod mixes your voice and the replay together automatically.
4. Select a replay and hit **Play to mic** (or just double-click it). Adjust the volume with the slider — it works live.

**The Soundboard is the main view** — the app opens on a pad grid with its own library (kept in a `Soundboard` subfolder of your output folder); the **🎞 Replays** button switches to the recording history. Add sounds by dragging MP3/WAV files onto it, with the **＋ Add sounds** button, or right-click a replay → *Send to soundboard*. Click a pad to fire it into the call (click again to stop it); right-click a pad to edit the sound in the built-in editor, rename it, set per-sound volume, assign a hotkey, or delete it. The search box filters as you type. In Settings you can choose whether sounds **overlap** or cut each other off.

**Quick launcher:** press `Ctrl+Alt+Q` (configurable) anywhere — even mid-game — type a few letters of a sound's name, press Enter, and it plays into your call.

**Soundboard hotkeys:** in any sound's **Rename** dialog, pick a slot. Now `Ctrl+Alt+<slot>` plays it from anywhere. `Ctrl+Alt+0` stops playback.

**Hearing an echo?** Untick *"Hear it too"* next to the volume slider — echo comes from the sound playing on your speakers and being picked up again by your real mic (or doubled by Voicemod's own monitoring).

## Editing a Replay

Select a replay → **Edit** (or run `AudioReplayBuffer.exe --edit "file.mp3"`):

- Drag the **two range handles** under the waveform around the part you want — **saving exports exactly that range**, so trimming is just: drag handles → *Save as copy*.
- *Play selection* previews the range before saving.
- Extra tools: delete a section, fade in/out, volume, normalize — all with undo.
- *Overwrite original* encodes to a temp file first, so a failed save can never destroy your recording.

## Default Hotkeys

| Hotkey | Action |
|---|---|
| `Ctrl+Alt+S` | Save the whole buffer |
| `Ctrl+Alt+D` | Save the last 30 seconds |
| `Ctrl+Alt+1`–`9` | Play soundboard slot into the call |
| `Ctrl+Alt+0` | Stop soundboard playback |
| `Ctrl+Alt+Q` | Open the quick launcher (search & play any sound) |

The save hotkeys and clip length are configurable in Settings.

## Configuration

Everything lives in `appsettings.json` in `%AppData%\AudioReplayBuffer` (editable from the Settings window — restart not required). Settings survive app updates and reinstalls:

| Setting | Default | Meaning |
|---|---|---|
| `CaptureMode` | `Desktop` | `Desktop`, `Microphone`, or `Both` |
| `TargetApp` / `TargetAppExclude` | `""` / `false` | Capture only this app — or everything except it |
| `BufferMinutes` | `5` | Buffer length (≈11 MB RAM per minute) |
| `Bitrate` | `192` | MP3 quality in kbps |
| `Hotkey` / `ClipHotkey` / `ClipSeconds` | `Ctrl+Alt+S` / `Ctrl+Alt+D` / `30` | Save hotkeys |
| `OutputFolder` | `Music\Replays` | Where MP3s go |
| `VoiceDevice` / `VoiceVolume` / `VoiceAlsoSpeakers` | — | Play-to-mic device, volume, self-monitor |
| `SoundboardOverlap` / `LauncherHotkey` | `false` / `Ctrl+Alt+Q` | Overlap vs interrupt; quick-launcher hotkey |
| `DesktopGain` / `MicrophoneGain` | `1.0` | Per-source capture volume |

Labels and soundboard slots are stored in `soundboard.json` in the same folder.

## Troubleshooting

- **"Hotkey is already in use"** — another app owns that combo; pick a different one in Settings.
- **Per-app capture says the app is not running** — start the target app first, or switch back to *All apps*. Requires Windows 10 2004+.
- **No sound in your call from Play to mic** — double-check step 3 above: the call app must use the *cable's microphone* as its input.
- **Nothing captured while the PC was silent** — that's by design; silence is recorded as silence, and the buffer timeline stays accurate.
- Errors are logged to `log.txt` in `%AppData%\AudioReplayBuffer`. Unexpected errors won't kill the app — it logs them and keeps recording.

## Tech Notes

C# / .NET 10, WPF (dark UI, custom-drawn waveforms), [NAudio](https://github.com/naudio/NAudio) for WASAPI capture/playback and Media Foundation encoding. Per-app capture is a hand-written COM interop of the Windows process-loopback API (NAudio has no wrapper). Architecture details in [Architecture.md](Architecture.md); [project.md](project.md) is the original concept document.

## License

Free for **personal, non-commercial** use — see [LICENSE](LICENSE). Commercial use requires permission.
