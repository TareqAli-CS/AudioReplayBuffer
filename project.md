# Replay Audio Converter

## Overview

Replay Audio Converter is a lightweight Windows background service that automatically converts OBS Replay Buffer recordings from MKV to MP3.

The service monitors a configured directory for newly created replay files. When OBS saves a replay, the service waits until the file is completely written, extracts the audio using FFmpeg, saves it as an MP3, optionally deletes the original MKV, and then returns to an idle state.

The service is designed to consume virtually no CPU while idle by relying on Windows file system notifications instead of polling.

---

# Problem

OBS Replay Buffer is an excellent replay system but has one limitation:

- It saves recordings as MKV/MP4 video files.
- Even when only audio is being recorded.
- Audio-only users end up with unnecessary video files.
- Additional manual work is required to convert them.

Current workflow:

```
Press Hotkey

↓

OBS saves Replay_001.mkv

↓

Open FFmpeg

↓

Convert to MP3

↓

Delete MKV
```

Desired workflow:

```
Press Hotkey

↓

OBS saves Replay_001.mkv

↓

ReplayAudioConverter automatically detects it

↓

Converts to MP3

↓

Deletes MKV

↓

Done
```

---

# Goals

- Extremely lightweight
- Fully automatic
- No user interaction
- No polling loops
- Minimal memory usage
- Minimal CPU usage
- Runs automatically with Windows
- Highly configurable

---

# Non Goals

This application will NOT:

- Record audio
- Replace OBS
- Capture microphone
- Capture desktop audio
- Provide a GUI (initial version)
- Modify OBS settings

OBS remains responsible for recording.

This application only converts files.

---

# High Level Architecture

```
             OBS Replay Buffer
                    │
                    ▼
          Replay_001.mkv created
                    │
                    ▼
        FileSystemWatcher Event
                    │
                    ▼
      Wait until file is unlocked
                    │
                    ▼
            FFmpeg Conversion
                    │
                    ▼
           Replay_001.mp3
                    │
                    ▼
         Delete original MKV
                    │
                    ▼
               Wait for next file
```

---

# Why FileSystemWatcher?

Many applications constantly check folders like this:

```
while(true)
{
    CheckFolder();

    Sleep(1000);
}
```

This is inefficient.

Instead, Windows provides FileSystemWatcher.

The operating system notifies our service immediately when a new file is created.

Advantages:

- Near-zero CPU usage while idle
- No unnecessary disk access
- Instant reaction
- No polling

---

# Why a Worker Service?

Worker Services are designed specifically for long-running background processes.

Advantages:

- Starts automatically
- Can run as a Windows Service
- Uses Microsoft's Generic Host
- Built-in Dependency Injection
- Built-in Logging
- Built-in Configuration
- Graceful shutdown

Unlike a desktop application, there is no UI consuming resources.

---

# Resource Usage

Idle:

CPU

≈ 0%

Memory

≈ 15–30 MB

Disk

0 I/O

Network

0

Only when a replay is saved does the service wake up.

During conversion:

- Launch FFmpeg
- Wait for completion
- Delete MKV
- Return to idle

---

# Why FFmpeg?

Reasons:

- Industry standard
- Extremely fast
- Excellent MP3 encoder
- Stable
- Easy to invoke from .NET

We are not building an audio encoder.

We simply orchestrate FFmpeg.

---

# Conversion Pipeline

```
MKV

↓

Extract Audio

↓

Encode

↓

MP3

↓

Delete MKV
```

Example command:

```
ffmpeg -i input.mkv -vn -codec:a libmp3lame -b:a 192k output.mp3
```

---

# Project Structure

```
ReplayAudioConverter

│

├── Program.cs

├── Worker.cs

│

├── Configuration
│   ├── AppSettings.cs
│   └── Validation.cs

│

├── Services
│   ├── ReplayWatcher.cs
│   ├── FileReadyService.cs
│   ├── FFmpegConverter.cs
│   ├── ReplayProcessor.cs
│   └── CleanupService.cs

│

├── Models
│   └── ReplayJob.cs

│

├── Logging

│

└── appsettings.json
```

---

# Responsibilities

## Worker

Application lifetime.

Starts services.

Nothing else.

---

## ReplayWatcher

Owns FileSystemWatcher.

Raises replay events.

---

## FileReadyService

Waits until OBS finishes writing the file.

Avoids partially written files.

---

## ReplayProcessor

Coordinates the entire workflow.

```
Wait

↓

Convert

↓

Delete

↓

Log
```

---

## FFmpegConverter

Executes FFmpeg.

Returns success/failure.

---

## CleanupService

Responsible for deleting original MKV files.

Future:

- Delete old MP3s
- Retention policies

---

# Configuration

Example:

```json
{
  "WatchFolder": "D:\\ReplayBuffer",

  "DeleteOriginal": true,

  "Bitrate": 192,

  "OutputExtension": "mp3",

  "FFmpegPath": "C:\\ffmpeg\\bin\\ffmpeg.exe"
}
```

---

# Logging

Every action is logged.

Example:

```
Watching folder:

D:\ReplayBuffer

Replay detected:

Replay_001.mkv

Waiting for file...

Converting...

Conversion successful.

Deleting original...

Done.
```

---

# Future Features

Version 2

- FLAC support
- WAV support
- Multiple watch folders
- Parallel conversions
- Automatic retries
- Windows notifications

Version 3

- System tray application
- Settings UI
- Auto update
- Drag & Drop conversion
- OBS integration
- Statistics page

---

# Design Principles

- Single Responsibility Principle
- Dependency Injection
- Interface-based services
- Event-driven architecture
- Configuration over hardcoding
- Minimal resource consumption
- Fail gracefully
- Easy to extend

---

# Technology Stack

Language

- C# (.NET 10)

Framework

- .NET Worker Service

Libraries

- Microsoft.Extensions.Hosting
- Microsoft.Extensions.Logging
- Microsoft.Extensions.Options

External Tool

- FFmpeg

Windows APIs

- FileSystemWatcher

---

# Expected Workflow

1. Windows starts.
2. Worker Service starts automatically.
3. FileSystemWatcher begins monitoring the replay folder.
4. Service becomes idle.
5. User presses OBS Replay hotkey.
6. OBS saves Replay_001.mkv.
7. Windows raises a Created event.
8. Service waits until OBS finishes writing.
9. FFmpeg converts MKV → MP3.
10. Original MKV is deleted.
11. Service returns to idle.

No polling.

No timers.

No busy waiting.

Only Windows events.

---

# Why This Design?

The service remains asleep nearly all the time.

Windows wakes it only when a replay file is created.

This results in extremely low resource usage while providing instant automatic conversion.
