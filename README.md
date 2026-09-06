```text
 ██████╗██╗   ██╗████████╗██████╗  █████╗  ██████╗██╗  ██╗
██╔════╝██║   ██║╚══██╔══╝██╔══██╗██╔══██╗██╔════╝██║ ██╔╝
██║     ██║   ██║   ██║   ██████╔╝███████║██║     █████╔╝ 
██║     ██║   ██║   ██║   ██╔══██╗██╔══██║██║     ██╔═██╗ 
╚██████╗╚██████╔╝   ██║   ██████╔╝██║  ██║╚██████╗██║  ██╗
 ╚═════╝ ╚═════╝    ╚═╝   ╚═════╝ ╚═╝  ╚═╝ ╚═════╝╚═╝  ╚═╝

  ▁▂▃▅▆▇█▇▆▅▃▂▁▂▃▅▆▇▆▅▃▂▁▁▁▁▁▁▁▁▁▁▁▁▁▂▃▅▆▇█▇▆▅▃▂▁▂▃▅▆▇▆▅▃▂
  ├─────── kept ───────┤├ removed ─┤├─────── kept ───────┤
```

[![CI](https://github.com/brogan89/Cutback/actions/workflows/ci.yml/badge.svg)](https://github.com/brogan89/Cutback/actions/workflows/ci.yml)

**Cutback** is a cross-platform desktop video editor that automatically removes dead air and spoken filler words
from talking-head screen recordings.

One video, one timeline, one export button. Cutback is not a non-linear editor: there is no
multi-track, no transitions, no effects, no titles. The timeline shows the whole source recording
with kept regions highlighted and removed regions greyed out. You can toggle any region, drag its
boundaries, and export the result. Editing is non-destructive and the source file is never
modified.

**Status:** Phase 1 (MVP) in progress. See [Roadmap](#roadmap).

## Installing a release

Prebuilt, self-contained binaries are on the
[Releases page](https://github.com/brogan89/Cutback/releases). They bundle the .NET runtime but
**not** FFmpeg, and libVLC is bundled only on Windows; see [Requirements](#requirements) for what to
install alongside. The binaries are not code-signed, so each OS shows a one-time warning.

**Windows (x64)** — unzip `Cutback-<version>-win-x64.zip` and run `Cutback.exe`. If SmartScreen
says "Windows protected your PC", choose *More info* → *Run anyway*.

**macOS (Apple Silicon)** — unzip `Cutback-<version>-osx-arm64.zip` and drag `Cutback.app` to
Applications. Gatekeeper blocks unsigned apps, so either clear the quarantine flag:

```bash
xattr -dr com.apple.quarantine /Applications/Cutback.app
```

or open it once, then go to *System Settings* → *Privacy & Security* and click *Open Anyway*.
Requires [VLC.app](https://www.videolan.org/) in `/Applications` and `ffmpeg` on `PATH`.

**Linux (x64)** — extract and run:

```bash
tar xzf Cutback-<version>-linux-x64.tar.gz
./Cutback-<version>-linux-x64/Cutback
```

Requires `ffmpeg`, the system libVLC packages listed under [Requirements](#requirements), and the
usual desktop libraries (`libx11-6 libice6 libsm6 libfontconfig1` on Debian/Ubuntu).

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

This is what the release workflow runs for each platform:

```bash
# pick one RID: win-x64, osx-arm64, linux-x64
dotnet publish src/Cutback.App -c Release -r osx-arm64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=false -p:DebugType=embedded -p:PublishDocumentationFiles=false \
  -p:Version=0.0.0-local -o artifacts/publish/osx-arm64
```

Windows must be published *on* Windows: the native libVLC package is only referenced when the
build host is Windows. Trimming is off because LibVLCSharp is not trim-annotated.

On macOS, wrap the output in an app bundle (publish with `IncludeNativeLibrariesForSelfExtract=false`
so the dylibs stay loose for signing):

```bash
scripts/package-macos.sh artifacts/publish/osx-arm64 0.0.0-local artifacts/dist
```

### Releasing

CI (`.github/workflows/ci.yml`) runs formatting, build and tests on Linux, Windows and macOS for
every push and pull request. `main` only accepts pull requests.

Releases are manual. Bump `<Version>` in `Directory.Build.props` and the `assemblyIdentity`
version in `src/Cutback.App/app.manifest`, merge, then open *Actions* → *Release* → *Run
workflow* on `main`, enter the version (for example `0.2.0`, or `0.2.0-beta.1` for a prerelease)
and tick **publish**. The workflow publishes all three platforms, creates the `v<version>` tag,
and opens a GitHub Release with the archives and a `SHA256SUMS.txt`. It refuses to overwrite an
existing release or tag.

Every merge to `main` also runs the Release workflow as a dry run: the archives are built and
uploaded as workflow artifacts but nothing is tagged or published, so packaging breakage shows
up before the next release. Leave **publish** unticked on a manual run to get the same dry run
for any branch.

## Using it

1. **Open a video** (button, drag-and-drop, or pass the path on the command line).
2. **Detect silence.** Tune threshold, minimum silence, padding and minimum keep in *Detection
   settings* and run again; your own edits are never overwritten.
3. **Adjust on the timeline.** Click a segment to keep or remove it, drag a boundary to move it
   (it snaps to the quietest nearby point), drag in the ruler to scrub, scroll to zoom, drag to pan.
   Space plays and pauses; playback skips removed regions.
4. **Save** the project as a `.cutback` file (JSON; the source video is never modified).
5. **Export.** *Precise* re-encodes with frame-accurate cuts. *Fast* stream-copies in seconds but each
   kept section starts at the previous keyframe, so it is approximate. The extension picks the
   container: `.mp4`, `.mov`, `.mkv`, `.webm`.

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
