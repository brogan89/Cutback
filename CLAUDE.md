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
4. **Every ffmpeg filter graph is written to a temp file and passed by file, never inline.**
   Inline `-filter_complex` will blow past the Windows command-line length limit once a video has more
   than ~50 cuts. This is not a theoretical concern; it is the normal case. The option to use depends
   on the ffmpeg version: **ffmpeg 7+ takes `-/filter_complex <file>`** (the generic "read option value
   from file" syntax) and **ffmpeg 8 removed `-filter_complex_script`** entirely; ffmpeg ≤ 6 only
   understands `-filter_complex_script <file>`. `FfmpegCapabilities` parses `ffmpeg -version` and
   picks the right one. Git snapshot builds with no version number are treated as modern.
5. **Every kept audio segment gets an 8ms `afade` in and out.** Without it, every single cut is an
   audible click. This is the difference between a toy and a usable tool.
6. **Cuts are padded by 60ms on each side by default** (configurable). Detection boundaries are
   approximate and tight cuts clip consonants.
7. **Claude analysis is out of scope for the current phase.** `ICutSuggester` stays a stub. Speech
   recognition (Phase 2) is implemented in `Cutback.Analysis` with Whisper.net and runs entirely on
   the user's machine; the only network access in the app is the one-time model download.

---

## Stack

Target framework: **.NET 10** (LTS). Nullable enabled, implicit usings enabled, `TreatWarningsAsErrors`,
analyzers at `AnalysisLevel=latest` with code style enforced in build. All package versions live in
`Directory.Packages.props` (central package management) so the dependency set is auditable in one place.

| Concern | Package | Notes |
|---|---|---|
| UI | `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` | Only mature .NET UI that covers Linux. MAUI does not. **Pinned to 11.3.x**: `LibVLCSharp.Avalonia` is built against 11.3 and Avalonia 12 is a breaking major. Do not bump without checking LibVLCSharp. |
| MVVM | `CommunityToolkit.Mvvm` | Use the source generators (`[ObservableProperty]`, `[RelayCommand]`). |
| Video playback | `LibVLCSharp`, `LibVLCSharp.Avalonia` | See LibVLCSharp gotchas below. |
| VLC native | `VideoLAN.LibVLC.Windows` only | **No NuGet package for Linux** — requires system `libvlc`. **`VideoLAN.LibVLC.Mac` is unusable**: x86_64-only and ships no libvlccore or plugins. On macOS `LibVlcLocator` loads `/Applications/VLC.app` and must `setenv("VLC_PLUGIN_PATH")` via P/Invoke, because .NET's `Environment.SetEnvironmentVariable` does not reach native `getenv` on Unix. |
| FFmpeg | `FFMpegCore` | Used for **ffprobe analysis only** (`MediaProbe`). Everything that streams stdout or parses stderr/progress (waveform, silencedetect, export, keyframe listing) goes through the small `FfmpegProcess` wrapper over `System.Diagnostics.Process`, which is simpler and identical on every platform. Do not switch to `FFmpeg.AutoGen`; the raw P/Invoke bindings are not worth the pain here. |
| Speech recognition | `Whisper.net`, `Whisper.net.Runtime` | CPU runtime (the package also pulls in `Whisper.net.Runtime.Metal` for macOS), **pinned to 1.9.x**. Models are downloaded on first use into `<ApplicationData>/Cutback/models` by `ModelStore`, never bundled. Token `Start`/`End`/`DtwTimestamp` are `long` centiseconds (`DtwTimestamp` is -1 when unavailable). DTW alignment is on so words get an `Anchor`; see "Locating filler words". The initial prompt is primed with disfluent text (and carried into every window) because Whisper otherwise drops "um"s. |
| Waveform drawing | SkiaSharp **transitively via `Avalonia.Skia`** (2.88.x) | **Do not add a direct `SkiaSharp` PackageReference.** Custom drawing obtains an `SKCanvas` through `ISkiaSharpApiLeaseFeature` and must use the same SkiaSharp assembly Avalonia does. A direct reference to current SkiaSharp (4.x) unifies to an incompatible version and breaks Avalonia's renderer. |
| JSON | `System.Text.Json` | Source-generated context, no reflection. |
| Tests | `xunit`, `FluentAssertions` | FluentAssertions **pinned to 7.x** (Apache-2.0). 8.x moved to a commercial licence. |

Deferred to a later phase, do not add yet: `Vosk`, `Microsoft.ML.OnnxRuntime`, Silero VAD. Reach for
them only if disfluent prompting proves to miss too many fillers on real recordings.

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
│   │   ├── Detection/             # SilenceCutPlanner, FillerDetector, FillerCutPlanner
│   │   ├── Transcript/            # TranscriptView, OutputTimeline, TranscriptExporter
│   │   └── Projects/              # save / load / migrate
│   ├── Cutback.Media/             # everything that shells out to ffmpeg
│   │   ├── FfmpegLocator.cs
│   │   ├── MediaProbe.cs
│   │   ├── WaveformExtractor.cs
│   │   ├── PcmExtractor.cs        # 16 kHz float PCM for speech recognition
│   │   ├── SilenceDetector.cs
│   │   └── Export/
│   │       ├── FilterGraphBuilder.cs
│   │       └── Exporter.cs
│   ├── Cutback.Analysis/          # PHASE 2 (speech) implemented; PHASE 3 (Claude) interface only
│   │   ├── ITranscriber.cs
│   │   ├── WhisperTranscriber.cs  # + WordAssembler, ModelStore, WhisperModel
│   │   └── ICutSuggester.cs
│   └── Cutback.App/               # Avalonia
│       ├── Views/
│       ├── ViewModels/
│       └── Controls/              # TimelineControl.cs, TranscriptControl.cs
└── tests/
    ├── Cutback.Core.Tests/
    ├── Cutback.Media.Tests/       # ffmpeg-independent logic only; never runs ffmpeg
    └── Cutback.Analysis.Tests/    # word assembly, model metadata; never loads a model
```

`Cutback.Core` must stay dependency-free apart from `System.Text.Json`. If you find yourself wanting to
reference FFMpegCore or Avalonia from Core, the logic is in the wrong project.

`Cutback.Analysis` may reference `Cutback.Media` (it needs ffmpeg to decode audio) but never the
other way round, and never `Cutback.App`.

---

## Data model

A project is a JSON file (`.cutback`). Schema:

```jsonc
{
  "version": 2,
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
  "transcript": [],             // [{ "text", "start", "end", "confidence" }], filled by Transcribe
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

`origin` is `auto | manual | claude | filler` and exists so the UI can show why a cut was made and
so a re-analysis can replace its own cuts without touching anyone else's. **Re-running detection
must never discard manual edits.** What counts as a manual edit: **`Toggle` marks the segment
manual; `MoveBoundary` marks only disabled neighbours manual; `Split` marks nothing** (it inherits
origin); **`SetRange` (drag to draw a section, or click / drag in the transcript) marks the new
section manual; `Dissolve` (delete a region) marks nothing** — the region ceases to exist and its
neighbours grow over it, keeping their own origins. A split is not a decision about either half,
and locking kept regions would stop re-detection from finding new silences inside them.
`ReplaceAutoSegments` (silence) preserves every non-`auto` segment exactly and clips new cuts
around them. `ApplyFillerCuts` (filler words) dissolves the previous `filler` segments and carves
the new cuts into whatever is there; `FillerCutPlanner` drops any candidate that touches a
`manual` segment, so the two detectors leave each other's and the user's cuts alone.

Schema version 2 added the `filler` origin. The 1→2 migration is a no-op; the bump exists so an
older build refuses the file with its "newer version" message instead of a deserialisation error.

`sha256` is used to warn the user when the source file has changed or moved. It does not block opening.

### Undo

Undo is snapshot-based. `EditHistory<T>` in Core holds `Segment[]` snapshots taken *before* each edit;
a snapshot is an array of references to immutable records, so even 1000 steps cost a few MB.
`SegmentList.Restore` applies one. The view model pushes a snapshot only when an edit actually
changed the partition, and **one boundary drag is one step**: `BoundaryMove` carries a
`BoundaryDragPhase` (Begin/Update/End) so the snapshot is taken at Begin and recorded at End.
`Toggle`, `SetRange`, `Dissolve` and `ReplaceAutoSegments` (detect silence) are one step each. Detection settings
are not part of history. History clears when a project opens or closes; undo/redo mark the project
dirty like any other edit. The limit is `AppSettings.UndoHistoryLimit` (default 100, range 10–1000),
edited in File → Preferences.

### Timeline gestures

Click a segment: toggle. Right-click a segment: context menu; **Delete** dissolves the region into its
neighbours (the `MenuFlyout` is built in `TimelineControl`'s constructor, so add items there). The
**Delete / Backspace keys** do the same to the region under the pointer, via the `HoveredSegment`
property the control pushes to the view model. Drag a boundary: move it (snapped to the quietest nearby
sample on release).
**Plain drag in the body: draw a new section**, which on release takes the *opposite* state of the
segment under the press point (drag over kept footage to cut it, drag inside a cut to restore part
of it). **Shift+drag: pan.** Wheel: zoom about the cursor; Shift+wheel or horizontal wheel: pan.
Ruler: scrub.

### Transcript gestures

The transcript panel (View → Transcript) is a second editing surface over the same `SegmentList`.
A word is struck through when its `Anchor` (or, for transcripts without anchors, its midpoint)
lies in a disabled segment (`TranscriptView.IsCut`).
**Click a word**: cut it, or restore it if it is struck. **Drag across words**: the run takes the
opposite state of the word the drag started on. Both are `SetRange`, so they mark the section
manual and are one undo step. **Cmd/Ctrl+click**: play from the word. **Right-click**: Play from
here, Cut / Restore. Word edges snap to the quietest waveform bucket within ±40 ms (twice the
boundary-drag window, because Whisper timing is coarse). Export Transcript writes the *edited*
transcript (`.txt` or `.srt`) with times remapped by `OutputTimeline` so it lines up with the
exported video.

### Locating filler words

**Whisper's heuristic `Start`/`End` for a short "uh" usually lands on the pause beside it, not on
the sound.** Measured on a real recording, two of three "uh"s were timed onto silence, one with
its audio folded into the previous word's span. So "Remove filler words" does not cut the
heuristic span. `WhisperTranscriber` enables DTW alignment (`UseDtwTimeStamps` with the model's
heads preset) and each `Word` carries an `Anchor`: the DTW time of its first token, which does
fall inside the spoken word. `FillerSpanLocator` (Core, pure) then takes the burst of speech in
the 10 ms loudness envelope (`Waveform.Envelope`) that contains the anchor; when a neighbour's
heuristic span swallowed that burst it splits at the quietest point between them if that is a
real valley (under 75% of the burst's median, never within 100 ms of the burst edge), else 80 ms
before the anchor / 300 ms after it. The pipeline is `FillerDetector` → `FillerSpanLocator` (falling
back to the snapped heuristic span for words without anchors) → `FillerCutPlanner` →
`ApplyFillerCuts`, one undo step. Transcripts made before anchors existed still work, and the
status line tells the user to transcribe again for aligned cuts.

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

Write that to a temp file, invoke with `-/filter_complex <file>` (ffmpeg 7+) or
`-filter_complex_script <file>` (older), then `-map "[outv]" -map "[outa]"`. See non-negotiable 4.

Two export modes:
- **Precise** (default): re-encode, `libx264 -crf 20 -preset medium`, `aac -b:a 192k`. Frame-accurate.
  `.webm` output uses `libvpx-vp9` / `libopus` instead, since x264 cannot go in WebM.
- **Fast**: stream-copy. Each kept range's **start moves back to the preceding keyframe; its end stays
  exact** (a copied range must begin on a keyframe but can stop anywhere). Ranges that come to overlap
  are merged, so a short cut just before a long GOP can vanish. This policy replaced "snap to the
  nearest keyframe", which dropped whole kept regions on an 8 s GOP. Output only ever contains extra
  material, never less than the user kept. Pieces are stream-copied with `-ss/-to -c copy` and joined
  with the concat demuxer. Not available for `.webm`. Must be clearly labelled as approximate in the UI.

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
3. **`VideoView.MediaPlayer` must be assigned after the native control exists.** The control only
   hands VLC the window handle inside the `MediaPlayer` setter, and the handle is created on first
   layout. Assigning in the window constructor silently produces "No drawable-nsobject found" and a
   black video. Assign in `Window.Opened` via `Dispatcher.UIThread.Post(..., DispatcherPriority.Loaded)`.
4. **libVLC 3 ignores `Play()` and seeks in the `Ended` state.** `VlcVideoPlayer` calls `Stop()` when
   `EndReached` fires (from a posted UI-thread callback, never from VLC's own thread, which deadlocks)
   so that the next Play or Seek restarts the media.
5. **Preview of the edited result is not gapless.** VLC has no EDL playlist concept, so skipping a cut
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
- `AppSettings` properties use `set`, not `init`. The System.Text.Json **source generator ignores
  property initializers on init-only properties**, so a key missing from an older `settings.json`
  deserialises to 0/null. `AppSettingsStore.Load` also clamps and null-guards what it reads.
- Menu shortcut labels (`MenuItem.InputGesture`) are display-only and set in `MainWindow.axaml.cs`
  so the modifier reads Cmd on macOS and Ctrl elsewhere; the real bindings are `Window.KeyBindings`,
  registered for both `Cmd+` and `Ctrl+`.

## Testing

Unit tests cover `Cutback.Core` (the partition invariant including `SetRange`, `Dissolve` and `Restore`,
`EditHistory`, detection planning, project round-trip serialisation, version migration, source hashing,
filler detection and planning, transcript cut-state, output timeline, transcript exporters) and the
ffmpeg-independent parts of `Cutback.Media`
(`FfmpegLocator` resolution order, `PeakReducer`, waveform levels and snapping, the silencedetect and
`-progress` parsers, `FfmpegCapabilities` version parsing, `KeyframeSnapper`, and `FilterGraphBuilder`
output asserted against expected filter strings), and `WordAssembler`, model metadata and cache layout
in `Cutback.Analysis.Tests`. **Never shell out to ffmpeg in tests.** Do not attempt
to unit-test Avalonia views.

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

**Phase 1 — MVP (done).** Open a video, waveform, timeline with toggleable segments, silence
detection, save/load, export. No speech recognition, no Claude.

**Phase 2 — Filler words (implemented).** `Whisper.net` behind `ITranscriber`, word-level
timestamps from token timestamps, an editable transcript panel, filler-word cuts with their own
`filler` origin, transcript export. Known issue: **Whisper is trained to strip disfluencies.** The
shipped mitigation is a disfluent initial prompt carried into every window. If recall on real
recordings is poor, the next steps are Silero VAD to find speech islands Whisper produced no word
for, and Vosk as an alternative engine that retains fillers more reliably.

**Phase 3 — Claude analysis.** Behind `ICutSuggester`. Sends the timestamped transcript (text and
indices only — never audio) to `/v1/messages` and receives JSON cut spans for false starts, repeated
phrases, and tangents. Requires the user's own Anthropic API key, stored in the OS credential store,
never in the project file. Every suggestion is reviewable with its reason and confidence before it
applies.
