# CLAUDE.md

Project context for Claude Code. Read this before making changes.

## What this is

**Cutback** — a cross-platform desktop video editor (Windows / macOS / Linux) that automatically removes
dead air and spoken filler words ("um", "ah", false starts) from talking-head screen recordings.

UX target: **Loom-simple**. One video, one timeline, one export button. This is not a NLE. There is no
multi-track, no transitions, no effects, no titles. Resist every urge to add them.

The timeline shows the whole source video. Kept regions are highlighted; removed regions are grayed out.
The user can toggle any region, drag its boundaries, and export the result. Editing is always
non-destructive.

License: **GPL-3.0** (forced by linking a GPL FFmpeg build with libx264 — see Licensing below).

---

## Non-negotiables

Violating any of these is a bug, not a style preference.

1. **The Anthropic API cannot transcribe audio.** There is no speech-to-text in Claude. Never write code
   that uploads audio or video to `api.anthropic.com`. Claude's only role is reasoning over an
   already-timestamped *text* transcript produced locally.
2. **Never modify the source video file.** All edits live in the project file. The source is opened
   read-only, always.
3. **Segments partition the timeline.** See Data Model. Any operation that leaves gaps, overlaps, or
   unsorted segments is a bug. `SegmentList` enforces this and is unit-tested.
4. **Every ffmpeg filter graph is written to a temp file and passed via `-filter_complex_script`.**
   Inline `-filter_complex` will blow past the Windows command-line length limit once a video has more
   than ~50 cuts. This is not a theoretical concern; it is the normal case.
5. **Every kept audio segment gets an 8ms `afade` in and out.** Without it, every single cut is an
   audible click. This is the difference between a toy and a usable tool.
6. **Cuts are padded by 60ms on each side by default** (configurable). Detection boundaries are
   approximate and tight cuts clip consonants.
7. **Speech recognition and Claude analysis are out of scope for the current phase.** Define the
   interfaces, stub the implementations, do not build them yet. See Roadmap.

---

## Stack

Target framework: **.NET 8** (LTS). Nullable enabled, implicit usings enabled, `TreatWarningsAsErrors`.

| Concern | Package | Notes |
|---|---|---|
| UI | `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` | Only mature .NET UI that covers Linux. MAUI does not. |
| MVVM | `CommunityToolkit.Mvvm` | Use the source generators (`[ObservableProperty]`, `[RelayCommand]`). |
| Video playback | `LibVLCSharp`, `LibVLCSharp.Avalonia` | See LibVLCSharp gotchas below. |
| VLC native | `VideoLAN.LibVLC.Windows`, `VideoLAN.LibVLC.Mac` | **No NuGet package for Linux** — requires system `libvlc`. |
| FFmpeg | `FFMpegCore` | Wraps the ffmpeg CLI. Do not switch to `FFmpeg.AutoGen`; the raw P/Invoke bindings are not worth the pain here. |
| Waveform drawing | `SkiaSharp` (via Avalonia's custom `Control.Render`) | |
| JSON | `System.Text.Json` | Source-generated context, no reflection. |
| Tests | `xunit`, `FluentAssertions` | |

Deferred to a later phase, do not add yet: `Whisper.net`, `Whisper.net.Runtime`, `Vosk`,
`Microsoft.ML.OnnxRuntime`.

---

## Project layout

```
Cutback/
├── CLAUDE.md
├── Cutback.sln
├── Directory.Build.props          # shared TFM, nullable, warnings-as-errors
├── src/
│   ├── Cutback.Core/              # NO UI, NO ffmpeg dependencies. Pure logic.
│   │   ├── Models/                # Segment, Word, CutbackProject
│   │   ├── SegmentList.cs         # the partition invariant lives here
│   │   └── Projects/              # save / load / migrate
│   ├── Cutback.Media/             # everything that shells out to ffmpeg
│   │   ├── FfmpegLocator.cs
│   │   ├── MediaProbe.cs
│   │   ├── WaveformExtractor.cs
│   │   ├── SilenceDetector.cs
│   │   └── Export/
│   │       ├── FilterGraphBuilder.cs
│   │       └── Exporter.cs
│   ├── Cutback.Analysis/          # PHASE 2 — interfaces only for now
│   │   ├── ITranscriber.cs
│   │   └── ICutSuggester.cs
│   └── Cutback.App/               # Avalonia
│       ├── Views/
│       ├── ViewModels/
│       └── Controls/TimelineControl.cs
└── tests/
    └── Cutback.Core.Tests/
```

`Cutback.Core` must stay dependency-free apart from `System.Text.Json`. If you find yourself wanting to
reference FFMpegCore or Avalonia from Core, the logic is in the wrong project.

---

## Data model

A project is a JSON file (`.cutback`). Schema:

```jsonc
{
  "version": 1,
  "source": {
    "path": "/abs/path/to/recording.mp4",
    "sha256": "…",              // first 8MB + file size, not the whole file
    "durationSeconds": 612.44,
    "width": 1920, "height": 1080, "frameRate": 30.0
  },
  "segments": [
    { "id": "…", "start": 0.0,  "end": 3.21,  "enabled": true,  "origin": "auto",   "reason": null },
    { "id": "…", "start": 3.21, "end": 4.86,  "enabled": false, "origin": "auto",   "reason": "silence 1.65s" },
    { "id": "…", "start": 4.86, "end": 12.04, "enabled": true,  "origin": "manual", "reason": null }
  ],
  "transcript": [],             // PHASE 2: [{ "text", "start", "end", "confidence" }]
  "settings": {
    "paddingMs": 60,
    "minSilenceMs": 400,
    "silenceThresholdDb": -34.0,
    "minKeepMs": 120
  }
}
```

### The partition invariant

`segments` is a **complete, sorted, non-overlapping partition of `[0, duration]`**. Adjacent segments
share a boundary exactly: `segments[i].end == segments[i+1].start`. There are no gaps and no overlaps,
and the first starts at `0.0` while the last ends at `duration`.

This is deliberate. It means:
- Rendering the timeline is a single left-to-right pass with no gap handling.
- Toggling a region is a boolean flip, never an insert or delete.
- Export is `segments.Where(enabled)` — no reconciliation step.

Operations on `SegmentList` (`Split`, `MoveBoundary`, `Toggle`, `MergeAdjacentSameState`) must preserve
this. Assert it at the end of every mutating operation in Debug. Unit tests cover it.

`origin` is `auto | manual | claude` and exists so the UI can show why a cut was made and so a
re-analysis can replace `auto` cuts without touching the user's `manual` ones. **Re-running detection
must never discard manual edits.**

`sha256` is used to warn the user when the source file has changed or moved. It does not block opening.

---

## FFmpeg

### Locating the binary

`FfmpegLocator` resolves in this order: user setting → `PATH` → common install locations per OS. If
nothing is found, surface a clear, actionable message naming the platform's install command. Do not
crash, and do not silently no-op. Cache the resolved path in app settings.

### Waveform extraction

Decode to raw mono PCM at 8 kHz s16le and pipe to stdout:

```
ffmpeg -i <src> -vn -ac 1 -ar 8000 -f s16le -acodec pcm_s16le -
```

Compute min/max peak pairs bucketed to a fixed resolution (target ~2000 buckets per screen width, cached
at a few zoom levels). Never hold the full PCM stream in memory for a long recording — stream and reduce
as you read.

### Silence detection

Phase 1 uses ffmpeg's own `silencedetect` filter, parsed from stderr:

```
ffmpeg -i <src> -af silencedetect=noise=<threshold>dB:d=<minSilence> -f null -
```

Parse `silence_start` / `silence_end` lines. Convert to disabled segments, apply padding, then drop any
resulting *kept* segment shorter than `minKeepMs` (merge it into the neighbouring cut) so the output
doesn't machine-gun.

### Export

Build a filter graph over the enabled segments:

```
[0:v]trim=start=A:end=B,setpts=PTS-STARTPTS[v0];
[0:a]atrim=start=A:end=B,asetpts=PTS-STARTPTS,afade=t=in:st=0:d=0.008,afade=t=out:st=<len-0.008>:d=0.008[a0];
…
[v0][a0][v1][a1]…concat=n=N:v=1:a=1[outv][outa]
```

Write that to a temp file, invoke with `-filter_complex_script <file> -map "[outv]" -map "[outa]"`.

Two export modes:
- **Precise** (default): re-encode, `libx264 -crf 20 -preset medium`, `aac -b:a 192k`. Frame-accurate.
- **Fast**: snap cut points to the nearest keyframe and stream-copy. Much quicker, boundaries land where
  the keyframes are. Must be clearly labelled as approximate in the UI.

Report progress by parsing ffmpeg's `-progress pipe:1` output, not by guessing. Export runs off the UI
thread and is cancellable.

Container is chosen by the output extension: `.mp4`, `.mov`, `.mkv`, `.webm`.

---

## LibVLCSharp gotchas

These will cost you hours if you don't know them:

1. **`Core.Initialize()` must be called once at startup**, before any `LibVLC` instance is constructed.
2. **Airspace.** The Avalonia `VideoView` is a detached native window rendered over your control, so you
   cannot draw normal Avalonia content on top of it. Overlay content must be set as the **`Content` of
   the `VideoView` itself**, not placed as a sibling in a `Grid`:

   ```xml
   <!-- WRONG — the button will be invisible -->
   <Grid><vlc:VideoView /><Button /></Grid>

   <!-- RIGHT -->
   <Grid><vlc:VideoView><Button /></vlc:VideoView></Grid>
   ```

   The `VideoView`'s `DataContext` propagates to its content, so binding still works normally.
3. **Preview of the edited result is not gapless.** VLC has no EDL playlist concept, so skipping a cut
   means a seek, and a seek visibly hitches. For the MVP this is acceptable: on the position-changed
   event, if the playhead has entered a disabled segment, seek to that segment's end.

   Keep playback behind an `IVideoPlayer` interface. If the stutter proves unbearable, the fix is to
   swap the implementation for **libmpv**, which has a native `edl://` protocol that plays a list of
   `file,start,length` entries as one continuous stream — exactly this use case. That swap is a
   post-MVP decision; do not start it unprompted.

---

## Conventions

- MVVM throughout. Views bind to ViewModels; ViewModels never reference Avalonia types.
- All I/O and ffmpeg work is `async` with `CancellationToken`, off the UI thread.
- Long operations report progress through `IProgress<T>`.
- Prefer `record` for immutable models, `readonly struct` for value types on hot paths (peaks, samples).
- No `async void` except event handlers.
- Temp files go in a per-session directory under the OS temp path and are cleaned up on exit.
- Errors surface to the user as readable messages. No swallowed exceptions, no bare `catch {}`.

## Testing

Unit tests cover `Cutback.Core` only — the partition invariant, project round-trip serialisation,
version migration, and `FilterGraphBuilder` output (assert against expected filter strings; do not shell
out to ffmpeg in tests). Do not attempt to unit-test Avalonia views.

## Commands

```bash
dotnet restore
dotnet build
dotnet run --project src/Cutback.App
dotnet test
dotnet format
```

---

## Licensing

Precise export mode links **libx264, which is GPL**. That makes the distributed ffmpeg build GPL, which
makes this application GPL. The project is therefore **GPL-3.0** and every dependency must be
compatible: Avalonia (MIT), FFMpegCore (MIT), CommunityToolkit.Mvvm (MIT), SkiaSharp (MIT), LibVLCSharp
(LGPL-2.1+) all are. Before adding any new dependency, check the licence.

Ship a `THIRD-PARTY-NOTICES.md` and keep it current.

---

## Roadmap

**Phase 1 — MVP (current).** Open a video, waveform, timeline with toggleable segments, silence
detection, save/load, export. No speech recognition, no Claude.

**Phase 2 — Filler words.** `Whisper.net` for word-level timestamps behind `ITranscriber`. Critical
known issue: **Whisper is trained to strip disfluencies**, so a naive transcript will contain no "um"s
at all. Mitigation is threefold — prime the initial prompt with disfluent text, use Silero VAD to find
speech regions Whisper produced no word for, and offer Vosk as an alternative engine that retains
fillers more reliably. Budget real time for this; it is the hardest part of the product.

**Phase 3 — Claude analysis.** Behind `ICutSuggester`. Sends the timestamped transcript (text and
indices only — never audio) to `/v1/messages` and receives JSON cut spans for false starts, repeated
phrases, and tangents. Requires the user's own Anthropic API key, stored in the OS credential store,
never in the project file. Every suggestion is reviewable with its reason and confidence before it
applies.
