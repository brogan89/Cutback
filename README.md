# Cutback

A cross-platform desktop video editor that automatically removes dead air and spoken filler words
from talking-head screen recordings.

One video, one timeline, one export button. Cutback is not a non-linear editor: there is no
multi-track, no transitions, no effects, no titles. The timeline shows the whole source recording
with kept regions highlighted and removed regions greyed out. You can toggle any region, drag its
boundaries, and export the result. Editing is non-destructive and the source file is never
modified.

**Status:** Phase 1 (MVP) in progress. See [Roadmap](#roadmap).

## Requirements

| Requirement | Why |
|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | Build and run |
| [FFmpeg](https://ffmpeg.org/) (`ffmpeg` and `ffprobe` on `PATH`) | Waveform, silence detection, export |
| libVLC | Video preview. Bundled on Windows via NuGet; [VLC.app](https://www.videolan.org/) on macOS; system package on Linux |

### Installing FFmpeg

Cutback looks for `ffmpeg` in this order: the path saved in app settings, then `PATH`, then the
usual per-platform install locations. A GPL build (with libx264) is required for precise export.

**Windows**

```powershell
winget install Gyan.FFmpeg
```

**macOS**

```bash
brew install ffmpeg
brew install --cask vlc
```

The NuGet libVLC package for macOS is Intel-only and incomplete, so Cutback loads libVLC from
`/Applications/VLC.app` instead. Any VLC 3.x works.

**Linux (Debian / Ubuntu)**

```bash
sudo apt install ffmpeg libvlc-dev vlc-plugin-base
```

**Linux (Fedora)**

```bash
sudo dnf install ffmpeg vlc-devel
```

On Linux there is no NuGet package for libVLC, so the `vlc` / `libvlc` system packages above are
mandatory for video preview.

## Building

```bash
dotnet restore
dotnet build
```

Run the app:

```bash
dotnet run --project src/Cutback.App
```

Run the tests:

```bash
dotnet test
```

Format:

```bash
dotnet format
```

The same commands work on Windows (PowerShell or cmd), macOS, and Linux.

### Publishing a self-contained build

```bash
# pick one RID: win-x64, osx-arm64, osx-x64, linux-x64
dotnet publish src/Cutback.App -c Release -r osx-arm64 --self-contained
```

Output lands in `src/Cutback.App/bin/Release/net10.0/<rid>/publish/`.

## Project layout

```
src/
  Cutback.Core/        Models, the segment partition invariant, project save/load. No UI, no ffmpeg.
  Cutback.Media/       Everything that shells out to ffmpeg: probe, waveform, silence detection, export.
  Cutback.Analysis/    Phase 2/3 seams (ITranscriber, ICutSuggester). Interfaces only for now.
  Cutback.App/         Avalonia desktop application.
tests/
  Cutback.Core.Tests/  xunit tests for Core.
  Cutback.Media.Tests/ xunit tests for the ffmpeg-independent parts of Media (path resolution, peak reduction).
```

See [CLAUDE.md](CLAUDE.md) for the architecture, data model, and design constraints in detail.

## Roadmap

- **Phase 1 (current):** open a video, waveform timeline with toggleable segments, automatic silence
  detection, save/load `.cutback` projects, export.
- **Phase 2:** local speech recognition for filler-word detection.
- **Phase 3:** optional Claude analysis of the *text transcript* for false starts and repeated
  phrases. Audio never leaves the machine.

## Licence

Cutback is licensed under the [GNU General Public License v3.0](LICENSE). Precise export links
libx264, which is GPL, so the application as a whole is GPL. Third-party licences are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
