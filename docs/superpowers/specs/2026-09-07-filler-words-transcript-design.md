# Filler-word removal and editable transcript — design

Date: 2026-09-07. Status: approved in conversation, ready for an implementation plan.

## Goal

Add Phase 2 of the roadmap: local speech recognition with Whisper.net, a one-click "Remove filler
words" action, an editable transcript panel in which cut words appear struck through (as in Loom),
and transcript export as plain text or SRT matching the edited video.

Out of scope: Claude analysis (Phase 3), Vosk, Silero VAD, GPU runtimes, multilingual models,
transcript-driven text editing (retyping words). The transcript is a second *editing surface* for
the same segment list, not a text editor.

## Decisions taken

| Question | Decision |
|---|---|
| Speech engine | Whisper.net 1.9.1 (MIT) with `Whisper.net.Runtime` CPU natives. Ships `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `macos-x64`, `macos-arm64`. |
| Disfluency recovery | Prime the initial prompt with disfluent text. If recall proves poor on real recordings, the Silero VAD gap heuristic is a follow-up, not part of this work. |
| Model file | Downloaded on first use into a per-user cache, never bundled. Default `base.en`; `tiny.en`, `small.en`, `medium.en` selectable in Preferences. English-only models. |
| Transcript interactivity | Editable (Option A): click a kept word to cut it, click a struck word to restore it, drag across words to cut or restore the run. Cmd/Ctrl+click or the context menu seeks the playhead. (A double-click was considered for seeking but would first fire the single-click toggle, so a modifier click is used instead.) |
| Export | `.txt` and `.srt`, always the *edited* transcript: cut words omitted, times remapped to the output video. |
| Filler-cut identity | New `SegmentOrigin.Filler`. Silence re-detection preserves filler cuts; filler re-detection preserves silence cuts; both preserve manual edits. Project schema bumps to version 2. |

## Architecture

```
Cutback.Core        FillerDetector, FillerCutPlanner, TranscriptView, OutputTimeline,
                    TranscriptExporter, SegmentOrigin.Filler, SegmentList.ApplyFillerCuts,
                    project v2 migration
Cutback.Media       PcmExtractor (ffmpeg -> 16 kHz mono float32 in memory)
Cutback.Analysis    WhisperTranscriber : ITranscriber, WordAssembler, WhisperModel, ModelStore
                    (now references Cutback.Media and the Whisper.net packages)
Cutback.App         TranscriptControl, transcript panel, Transcribe / Remove filler words /
                    Export Transcript commands, model choice + filler list in Preferences,
                    cancellable busy state
```

Dependency direction stays Core ← Media ← Analysis ← App. Core remains free of everything but
System.Text.Json.

## 1. Transcription

### PcmExtractor (Media)

Runs `ffmpeg -i <src> -vn -ac 1 -ar 16000 -f f32le -acodec pcm_f32le -` through `FfmpegProcess`
with stdout redirected, exactly like `WaveformExtractor`, and accumulates samples into a growable
`float[]`. Reports progress as bytes read over expected bytes. Throws `FfmpegException` if no
samples arrive. Memory is 64 bytes per millisecond, about 230 MB per hour; this is accepted for the
target use (screen recordings well under an hour). Whisper needs the whole signal, so streaming is
not attempted.

### WhisperModel and ModelStore (Analysis)

```csharp
public enum WhisperModel { TinyEn, BaseEn, SmallEn, MediumEn }
```

`WhisperModelInfo` maps each value to its display name (`base.en`), settings key (`base.en`),
Whisper.net `GgmlType`, and approximate size in bytes (used only for the download progress
fraction). Parsing an unknown settings key yields `BaseEn`.

`ModelStore` owns the cache directory, default
`<ApplicationData>/Cutback/models/`, file `ggml-<key>.bin`.

- `string PathFor(WhisperModel)` and `bool IsDownloaded(WhisperModel)`.
- `Task<string> EnsureAsync(WhisperModel, IProgress<double>?, CancellationToken)`: returns the
  path if present, otherwise downloads through `WhisperGgmlDownloader.Default.GetGgmlModelAsync`
  into `<file>.part`, copying in 64 KB chunks, reporting `bytes / approximateSize` clamped to 1,
  then moves the file into place. Cancellation or failure deletes the `.part`. Network failures
  surface as `ModelDownloadException` with a message that names the model, the cache folder and
  the fact that a network connection is needed.

### WhisperTranscriber (Analysis)

Implements the existing `ITranscriber`. Constructor takes `FfmpegLocation` and the resolved model
path (the store is consulted by the app first, so the transcriber never downloads).

`TranscribeAsync`:

1. Extract PCM with `PcmExtractor`; progress maps to `[0, 0.15]`.
2. `WhisperFactory.FromPath(modelPath)`, builder with `.WithLanguage("en")`,
   `.WithTokenTimestamps()`, `.WithThreads(min(ProcessorCount, 8))`, `.WithPrompt(DisfluentPrompt)`,
   `.WithProgressHandler(p => progress 0.15 + 0.85 * p / 100)`.
3. `await foreach (var segment in processor.ProcessAsync(samples, ct))` collect
   `WordAssembler.FromTokens(segment.Tokens)`.
4. Return the sorted word list.

`DisfluentPrompt` is a constant sentence written the way a transcript with fillers looks, e.g.
`"Um, so, uh, I was going to, um, show you this. Er, hmm, let me, uh, think."` It is the only
mitigation for Whisper's disfluency suppression in this phase.

The factory and processor are disposed after use; the model is not kept resident between runs.

### WordAssembler (Analysis, pure)

Turns a Whisper segment's token array into `Word`s:

- Tokens whose text starts with `[_` (control tokens such as `[_BEG_]`, `[_TT_…]`) are skipped.
- A token that starts with whitespace begins a new word; otherwise it continues the current one.
  The first real token of a segment always begins a word.
- Tokens containing only punctuation attach to the current word.
- Word text is the trimmed concatenation. Words that end up empty are dropped.
- `Start` is the first token's start, `End` is the last token's end, both in seconds. If
  `End <= Start`, `End = Start + 0.01`.
- `Confidence` is the mean token probability.

This is where the unit tests for token grouping live; the Whisper model never runs in tests.

## 2. Data model and detection (Core)

### SegmentOrigin.Filler

Adds `Filler` with JSON name `filler`. `ProjectSerializer.CurrentVersion` becomes 2 and a no-op
migration step is registered from 1 to 2 (the field set is unchanged; the bump exists so an older
build refuses a file that may contain the new origin with its existing "saved by a newer version"
message instead of a deserialisation error). Round-trip tests cover a filler segment.

`SegmentList.MergeOrigin` treats `Filler` like `Auto` when deciding the merged origin of a dissolve
(manual wins over everything; otherwise the left origin is kept).

### FillerDetector

```csharp
public static class FillerDetector
{
    public static IReadOnlyList<string> DefaultWords { get; }  // um, umm, uh, uhh, er, erm, ah, hmm, hm, mm
    public static string Normalize(string wordText);            // lower-case, trim, strip surrounding punctuation
    public static IReadOnlyList<Word> Find(IReadOnlyList<Word> transcript, IReadOnlyCollection<string> fillerWords);
}
```

`Find` returns the transcript words whose normalised text is in the (normalised) filler set, in
timeline order. Hyphenated forms such as `uh-huh` do not match `uh` because normalisation strips
only leading and trailing punctuation.

### FillerCutPlanner

```csharp
public static IReadOnlyList<PlannedCut> Plan(
    IEnumerable<FillerSpan> spans,          // (Start, End, WordText) already edge-snapped by the caller
    IReadOnlyList<Segment> current,         // the live partition
    DetectionSettings settings,
    double duration);
```

Rules, in order:

1. Clamp to `[0, duration]`; drop spans with `End <= Start`.
2. Drop any span that intersects a `Manual` segment. The user has decided about that footage.
3. Drop any span that lies entirely inside already-disabled segments. It is already cut.
4. Merge overlapping or touching spans.
5. Bridge kept slivers shorter than `MinKeepMs` between a span and a neighbouring disabled segment
   (of any origin) or another span, by extending the span over the sliver, so a filler next to a
   silence cut does not leave a 30 ms stutter.
6. Emit `PlannedCut(start, end, "filler: <word>")`.

Padding is *not* subtracted: the span is the word itself, and the caller has already snapped each
edge to the quietest waveform bucket within ±40 ms (`SnapToZeroCrossing(time, 0.040)`, twice the
boundary-drag window because ASR timing is coarser than a hand-placed boundary).

### SegmentList changes

- `SetRange(start, end, enabled, origin, reason)` overload. The existing three-argument form calls
  it with `Manual, null`.
- `ApplyFillerCuts(IEnumerable<PlannedCut> cuts)`: dissolves every existing `Filler` segment (so a
  re-run replaces the previous result), then `SetRange(cut.Start, cut.End, false, Filler, cut.Reason)`
  for each cut. One `Changed` event at the end. Asserts the partition in Debug like every other
  mutation.
- `ReplaceAutoSegments` is unchanged: `Filler` is not `Auto`, so silence re-detection locks filler
  cuts exactly as it locks manual ones.

### TranscriptView

```csharp
public static bool IsCut(Word word, IReadOnlyList<Segment> segments);   // midpoint falls in a disabled segment
public static int IndexAtTime(IReadOnlyList<Word> words, double seconds); // word containing the time, else -1
```

### OutputTimeline

Maps a source time to the time it will have in the exported video: the sum of the durations of the
enabled segments before it plus the offset into the enabled segment containing it. Built once from
the partition. Source times inside a disabled segment map to the start of the next kept region.

### TranscriptExporter

```csharp
public static string ToPlainText(IReadOnlyList<Word> words, IReadOnlyList<Segment> segments);
public static string ToSrt(IReadOnlyList<Word> words, IReadOnlyList<Segment> segments);
```

Both consider only words for which `TranscriptView.IsCut` is false.

Plain text: words joined by single spaces; a blank line starts a new paragraph when the *source*
gap between consecutive kept words exceeds 2 s. Trailing newline.

SRT: cues are built greedily. A new cue starts when the source gap to the next word exceeds 0.7 s,
when adding the word would push the cue text past 84 characters, or when the cue would exceed 5 s.
A cue's text longer than 42 characters is wrapped into two lines at the space nearest its middle.
Times use `OutputTimeline` and the `HH:MM:SS,mmm` format. Cues are numbered from 1. Both exporters
use invariant culture and `\n` line endings.

## 3. Application

### Settings

`AppSettings` gains `WhisperModel` (string key, default `base.en`) and `FillerWords`
(`IReadOnlyList<string>`, default `FillerDetector.DefaultWords`). Both use `set`, and
`AppSettingsStore.Sanitize` replaces an unknown model key with the default and a null or empty
filler list with the default.

Preferences window gains a "Speech model" dropdown (name plus approximate size, and "downloaded" or
"will download on first use") and a "Filler words" text box (comma-separated).

### View model

New state: `Transcript` (`IReadOnlyList<Word>`, mirrors `Project.Transcript`), `HasTranscript`,
`IsTranscriptOpen`, `IsTranscriptStale` (true when the source-hash warning fired on open),
`CurrentWordIndex` (updated from `OnPlayerPosition` through `TranscriptView.IndexAtTime`).

Busy state becomes cancellable: `BusyCancelCommand` and `CanCancelBusy`; the status bar shows a
Cancel button while a cancellable operation runs. Transcription and model download are cancellable;
open, silence detection and save are unchanged.

Commands:

- `TranscribeCommand` (`CanEdit`): ensures the model (`BusyMessage` "Downloading speech model…"
  with fraction), then transcribes ("Transcribing…" with fraction). On success sets
  `Project = Project with { Transcript = words }`, marks dirty, clears `IsTranscriptStale`, opens
  the panel, and reports "Transcribed N words." Errors (`FfmpegNotFoundException`,
  `FfmpegException`, `ModelDownloadException`, `IOException`) go to `ErrorMessage`.
- `RemoveFillerWordsCommand` (`CanEdit`): runs `TranscribeCommand`'s work first if there is no
  transcript. Then `FillerDetector.Find` with the settings' word list, snaps each word's edges with
  `Waveform.SnapToZeroCrossing(t, 0.040)`, `FillerCutPlanner.Plan`, and
  `Edit(s => s.ApplyFillerCuts(cuts))` so the whole run is one undo step. Status reports
  "Removed N filler words, M:SS cut." or "No filler words found."
- `ExportTranscriptCommand` (`HasProject && HasTranscript && !IsBusy`): save dialog with `.txt` and
  `.srt` filters, default name `<project or video name>.txt`; writes with `TranscriptExporter`
  according to the chosen extension. Menu: File → Export Transcript…
- `ToggleTranscriptCommand`: View menu item and a toolbar toggle button "Transcript".
- `CutWordsCommand(WordRange range)`: `range` is an inclusive word-index pair plus the anchor
  index. `enabled = TranscriptView.IsCut(words[anchor])` (restore if the anchor is cut, cut
  otherwise). Start is `words[first].Start`, end is `words[last].End`, both snapped as above, then
  `Edit(s => s.SetRange(start, end, enabled))`, which marks the section manual, exactly like drawing
  on the timeline.
- `SeekToWordCommand(int index)`: `Seek(words[index].Start)`.

### Transcript panel (MainWindow)

A third column in the video row, 340 px wide, same styling as the detection settings panel, visible
when `IsTranscriptOpen`. Contents:

- Header "Transcript", a dim hint "Click a word to cut or restore it. Drag to select a run.
  Cmd/Ctrl+click to play from a word.", and a "Remove filler words" button.
- A warning line "Source file changed since this transcript was made" with a "Transcribe again"
  button when `IsTranscriptStale`.
- Empty state when there is no transcript: "No transcript yet" and a "Transcribe" button with the
  note "Runs on this computer. The first run downloads a ~150 MB speech model."
- Otherwise a `ScrollViewer` containing the `TranscriptControl`.

### TranscriptControl

A custom `Control` in `Cutback.App/Controls`, following `TimelineControl`'s pattern of styled
properties plus commands.

Properties: `Words`, `Segments` (the `SegmentList`, subscribing to `Changed`), `CurrentWordIndex`,
`CutWordsCommand`, `SeekToWordCommand`.

Rendering builds one Avalonia `TextLayout` from the words joined by single spaces, with
`MaxWidth = Bounds.Width`, and a `ValueSpan<TextRunProperties>` override per cut word that sets
strike-through decoration and a dimmed foreground. A prefix array maps character offsets to word
indices. The layout is rebuilt only when words, segment states, or width change; segment changes
that do not alter any word's cut state do not trigger a rebuild. The control's desired height is
the layout height. Behind the current word a soft highlight rectangle is drawn from
`HitTestTextRange`; behind the selection a stronger one.

Pointer handling: press records the word under the pointer via `HitTestPoint` (whitespace resolves
to the nearest word), move extends the selection when the pointer has moved past a small threshold,
release with no movement fires `CutWordsCommand` for the single word, release after a drag fires it
for the range with the press word as anchor. A press with Cmd or Ctrl held fires `SeekToWordCommand`
instead. A right-click opens a `MenuFlyout` with "Play from here", "Cut" / "Restore" (label chosen
from the word's state), built in the constructor like the timeline's menu.

### Toolbar and menus

Toolbar: "Remove filler words" button after "Detect silence"; "Transcript" toggle after
"Detection settings". File menu: "Export Transcript…" under "Export…". View menu (new):
"Transcript" (checkable). Edit menu unchanged.

### App composition

`App.axaml.cs` constructs `ModelStore` and passes it to the view model. The view model builds a
`WhisperTranscriber` per run from the resolved ffmpeg location and model path. `ITranscriber` is
what the view model calls, so tests or a future engine can substitute it.

### Packaging

`Whisper.net.Runtime` copies native libraries through its build targets. The release workflow
already publishes single-file self-contained builds per RID; the plan includes a manual
`dotnet publish -r osx-arm64` smoke test that runs transcription from the published bundle before
the feature is considered done, and the CI publish dry-run will fail loudly if the targets break.

## 4. Documentation

- `CLAUDE.md`: non-negotiable 7 becomes "Claude analysis is out of scope; speech recognition is
  Phase 2 and lives in Cutback.Analysis"; stack table gains the Whisper.net row and the rule that
  Analysis may reference Media; project layout, data model (version 2, `filler` origin), a
  "Transcript gestures" section, and the roadmap are updated.
- `THIRD-PARTY-NOTICES.md`: Whisper.net and Whisper.net.Runtime 1.9.1 (MIT; bundles whisper.cpp
  and ggml, both MIT), Microsoft.Extensions.AI.Abstractions and Microsoft.Bcl.AsyncInterfaces
  (MIT), and a "Downloaded at runtime" row for the OpenAI Whisper ggml weights (MIT), which are not
  bundled.

## 5. Testing

Never runs ffmpeg or Whisper.

Core tests: `FillerDetectorTests` (normalisation, default list, hyphenated non-match),
`FillerCutPlannerTests` (each rule above), `SegmentListFillerTests` (`ApplyFillerCuts` keeps the
partition, replaces earlier filler cuts, preserves manual and auto segments; `ReplaceAutoSegments`
preserves filler cuts; `SetRange` with origin), `TranscriptViewTests`, `OutputTimelineTests`,
`TranscriptExporterTests` (plain text paragraphs, SRT cue splitting, wrapping, time format,
remapping across a cut), `ProjectSerializerTests` additions (version 2 written, version 1 file
migrates, `filler` origin round-trips).

Analysis tests: new project `tests/Cutback.Analysis.Tests` with `WordAssemblerTests` (space-prefixed
token starts a word, punctuation attaches, control tokens skipped, timing and confidence rules) and
`WhisperModelInfoTests` / `ModelStoreTests` (key parsing, path layout in a temp directory,
`IsDownloaded`).

App: no automated tests, per project convention. Manual verification: transcribe a real recording,
remove fillers, click and drag in the transcript, undo, export `.srt` and load it in VLC over the
exported video.

## Known limits accepted

- Whisper word timing is approximate; waveform snapping and the transcript's manual controls are
  the correction mechanism.
- Whole-file audio is held in memory during transcription.
- Priming may still miss fillers. If so, Silero VAD is the next step (the roadmap already names
  it), not a change to this design.
- CPU inference only. `base.en` transcribes roughly 5–10× faster than real time on a recent laptop.
