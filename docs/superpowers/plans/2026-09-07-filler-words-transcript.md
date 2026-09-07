# Filler-Word Removal and Editable Transcript Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Local Whisper.net transcription, a one-click "Remove filler words" action, an editable transcript panel with cut words struck through, and `.txt` / `.srt` export of the edited transcript.

**Architecture:** Pure logic (filler detection, cut planning, cut-state lookup, output-time mapping, exporters, a new `filler` segment origin) lives in `Cutback.Core` and is unit-tested. `Cutback.Media` gains a PCM extractor. `Cutback.Analysis` gains the Whisper.net transcriber, token-to-word assembly and a model download cache. `Cutback.App` gains a `TranscriptControl` built on Avalonia's `TextLayout`, a transcript panel, new commands and preferences. Both the timeline and the transcript edit the same `SegmentList`, so undo and the timeline stay consistent for free.

**Tech Stack:** .NET 10, Avalonia 11.3, CommunityToolkit.Mvvm source generators, Whisper.net 1.9.1 + Whisper.net.Runtime 1.9.1 (CPU), ffmpeg via the existing `FfmpegProcess` wrapper, xunit + FluentAssertions 7.

**Spec:** `docs/superpowers/specs/2026-09-07-filler-words-transcript-design.md`

## Global Constraints

- `TreatWarningsAsErrors` is on with analyzers at `latest` and code style enforced in build. Every task must end with `dotnet build Cutback.sln` clean and `dotnet format Cutback.sln --verify-no-changes` clean. Run `dotnet format Cutback.sln` to fix style before committing.
- Tests never shell out to ffmpeg and never load a Whisper model.
- `Cutback.Core` references nothing but `System.Text.Json`. Dependency direction is Core ← Media ← Analysis ← App.
- Package versions live only in `Directory.Packages.props`. Every new package goes into `THIRD-PARTY-NOTICES.md` with its licence (Task 18).
- Never upload audio anywhere. Whisper runs locally. The only network call is the model download from Hugging Face through `WhisperGgmlDownloader`.
- The partition invariant: `SegmentList` mutations leave a sorted, gap-free, overlap-free partition of `[0, duration]`. Assert with `ShouldBeValidPartitionOf` in every `SegmentList` test.
- View models never reference Avalonia types. `Cutback.App/Controls` types (`WordRange`) are plain records, like the existing `BoundaryMove`.
- Whisper token `Start`/`End` are `long` centiseconds; seconds = `value / 100.0`.
- Filler edge snapping uses `Waveform.SnapToZeroCrossing(time, 0.040)`.
- Commit messages are imperative sentences; end each commit with the trailer `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Work on a branch, not `main`: `git checkout -b feature/filler-words-transcript` before Task 1.

## File map

| File | Responsibility |
|---|---|
| `src/Cutback.Core/Models/SegmentOrigin.cs` | add `Filler` |
| `src/Cutback.Core/Projects/ProjectSerializer.cs`, `ProjectMigrator.cs` | version 2, no-op 1→2 step |
| `src/Cutback.Core/SegmentList.cs` | `SetRange` origin overload, `ApplyFillerCuts`, `MergeOrigin` |
| `src/Cutback.Core/Detection/FillerDetector.cs` | word-list match |
| `src/Cutback.Core/Detection/FillerSpan.cs`, `FillerCutPlanner.cs` | spans → `PlannedCut`s |
| `src/Cutback.Core/Transcript/TranscriptView.cs` | is-cut, index-at-time |
| `src/Cutback.Core/Transcript/OutputTimeline.cs` | source → output time |
| `src/Cutback.Core/Transcript/TranscriptExporter.cs` | `.txt` and `.srt` |
| `src/Cutback.Media/PcmExtractor.cs` | ffmpeg → 16 kHz float PCM |
| `src/Cutback.Analysis/WhisperModel.cs` | enum + `WhisperModelInfo` |
| `src/Cutback.Analysis/ModelStore.cs`, `ModelDownloadException.cs` | model cache + download |
| `src/Cutback.Analysis/TokenTiming.cs`, `WordAssembler.cs` | tokens → `Word`s |
| `src/Cutback.Analysis/WhisperTranscriber.cs` | `ITranscriber` implementation |
| `tests/Cutback.Analysis.Tests/*` | new test project |
| `src/Cutback.App/Services/AppSettings.cs`, `AppSettingsStore.cs` | model + filler-list settings |
| `src/Cutback.App/ViewModels/PreferencesViewModel.cs`, `Views/PreferencesWindow.axaml` | model dropdown, filler list |
| `src/Cutback.App/ViewModels/MainWindowViewModel.cs` | transcribe, fillers, transcript state, word commands, export transcript, cancellable busy |
| `src/Cutback.App/Controls/WordRange.cs`, `TranscriptControl.cs` | the transcript surface |
| `src/Cutback.App/Views/MainWindow.axaml(.cs)` | panel, toolbar, menus, cancel button |
| `src/Cutback.App/Services/IFileDialogService.cs`, `AvaloniaFileDialogService.cs` | transcript save picker |
| `src/Cutback.App/App.axaml.cs` | construct `ModelStore` |
| `CLAUDE.md`, `THIRD-PARTY-NOTICES.md` | docs |

---

### Task 1: `SegmentOrigin.Filler` and project schema version 2

**Files:**
- Modify: `src/Cutback.Core/Models/SegmentOrigin.cs`
- Modify: `src/Cutback.Core/Projects/ProjectSerializer.cs:13`
- Modify: `src/Cutback.Core/Projects/ProjectMigrator.cs:20-23`
- Modify: `src/Cutback.Core/SegmentList.cs` (`MergeOrigin`, near the end of the file)
- Test: `tests/Cutback.Core.Tests/ProjectSerializerTests.cs`

**Interfaces:**
- Produces: `SegmentOrigin.Filler` (JSON `"filler"`); `ProjectSerializer.CurrentVersion == 2`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Cutback.Core.Tests/ProjectSerializerTests.cs` inside the class:

```csharp
    // ---- version 2: filler origin -------------------------------------------------------------

    [Fact]
    public void Current_version_is_2()
    {
        ProjectSerializer.CurrentVersion.Should().Be(2);
    }

    [Fact]
    public void Filler_origin_round_trips_as_a_lower_case_string()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 3.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(3.0, 3.4, enabled: false, SegmentOrigin.Filler, "filler: um"),
            Segment.Create(3.4, 12.04, enabled: true, SegmentOrigin.Auto),
        };
        var project = new CutbackProject(Source, segments, [], DetectionSettings.Default);

        var json = ProjectSerializer.Serialize(project);
        var loaded = ProjectSerializer.Deserialize(json);

        JsonNode.Parse(json)!["segments"]![1]!["origin"]!.GetValue<string>().Should().Be("filler");
        loaded.Segments[1].Origin.Should().Be(SegmentOrigin.Filler);
        loaded.Segments[1].Reason.Should().Be("filler: um");
    }

    [Fact]
    public void A_version_1_file_is_migrated_to_version_2_unchanged()
    {
        const string v1 = """
            {
              "version": 1,
              "source": { "path": "/abs/path/to/recording.mp4", "sha256": "0123abcd", "durationSeconds": 12.04, "width": 1920, "height": 1080, "frameRate": 30.0 },
              "segments": [
                { "id": "a", "start": 0.0, "end": 12.04, "enabled": true, "origin": "auto", "reason": null }
              ],
              "transcript": [],
              "settings": { "paddingMs": 60, "minSilenceMs": 400, "silenceThresholdDb": -34.0, "minKeepMs": 120 }
            }
            """;

        var project = ProjectSerializer.Deserialize(v1);

        project.Segments.Should().ContainSingle().Which.Id.Should().Be("a");
        JsonNode.Parse(ProjectSerializer.Serialize(project))!["version"]!.GetValue<int>().Should().Be(2);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~ProjectSerializerTests" 2>&1 | tail -20`
Expected: build error `'SegmentOrigin' does not contain a definition for 'Filler'`.

- [ ] **Step 3: Add the enum member**

Replace the body of `src/Cutback.Core/Models/SegmentOrigin.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Cutback.Core.Models;

/// <summary>
/// Who decided a segment's boundaries and state. Re-running silence detection replaces
/// <see cref="Auto"/> segments; re-running filler detection replaces <see cref="Filler"/> segments.
/// Neither touches <see cref="Manual"/> ones, and each preserves the other's cuts.
/// </summary>
public enum SegmentOrigin
{
    [JsonStringEnumMemberName("auto")]
    Auto,

    [JsonStringEnumMemberName("manual")]
    Manual,

    [JsonStringEnumMemberName("claude")]
    Claude,

    /// <summary>A cut produced by filler-word detection. Schema version 2.</summary>
    [JsonStringEnumMemberName("filler")]
    Filler,
}
```

- [ ] **Step 4: Bump the version and register the no-op migration**

In `src/Cutback.Core/Projects/ProjectSerializer.cs` change:

```csharp
    public const int CurrentVersion = 2;
```

In `src/Cutback.Core/Projects/ProjectMigrator.cs` replace the `Default` property:

```csharp
    /// <summary>The migrator used by <see cref="ProjectSerializer"/>.</summary>
    public static ProjectMigrator Default { get; } = new(
        ProjectSerializer.CurrentVersion,
        new Dictionary<int, Action<JsonObject>>
        {
            // 1 -> 2: the "filler" segment origin was added. No field changes; the bump exists so
            // an older build refuses a file it cannot read with its "newer version" message.
            [1] = static _ => { },
        });
```

Also update the remark on line 12 of that file from "No migrations exist yet." to "See <see cref="Default"/> for the registered steps." (the `<summary>` on `Default` above already changed).

- [ ] **Step 5: Teach `MergeOrigin` about `Filler`**

In `src/Cutback.Core/SegmentList.cs` replace `MergeOrigin`:

```csharp
    /// <summary>Manual wins over everything, then Claude, then Filler, then Auto.</summary>
    private static SegmentOrigin MergeOrigin(SegmentOrigin a, SegmentOrigin b)
    {
        if (a == SegmentOrigin.Manual || b == SegmentOrigin.Manual)
        {
            return SegmentOrigin.Manual;
        }

        if (a == SegmentOrigin.Claude || b == SegmentOrigin.Claude)
        {
            return SegmentOrigin.Claude;
        }

        if (a == SegmentOrigin.Filler || b == SegmentOrigin.Filler)
        {
            return SegmentOrigin.Filler;
        }

        return SegmentOrigin.Auto;
    }
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Cutback.Core.Tests 2>&1 | tail -5`
Expected: all pass (the existing `Serialize_writes_the_current_version…` test reads the constant, so it still passes).

- [ ] **Step 7: Commit**

```bash
dotnet format Cutback.sln
git add -A src/Cutback.Core tests/Cutback.Core.Tests
git commit -m "Add the filler segment origin and project schema version 2

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: `SegmentList.SetRange` with origin and `ApplyFillerCuts`

**Files:**
- Modify: `src/Cutback.Core/SegmentList.cs` (`SetRange` at ~line 143, `Dissolve` at ~line 191)
- Test: `tests/Cutback.Core.Tests/SegmentListFillerTests.cs` (new)

**Interfaces:**
- Consumes: `PlannedCut(double Start, double End, string Reason)` from `Cutback.Core.Detection`.
- Produces:
  - `public int SetRange(double start, double end, bool enabled, SegmentOrigin origin, string? reason)`
  - `public int ApplyFillerCuts(IEnumerable<PlannedCut> cuts)` returning the number of cuts applied. Removes every existing `Filler` segment first. Raises `Changed` once.

- [ ] **Step 1: Write the failing tests**

Create `tests/Cutback.Core.Tests/SegmentListFillerTests.cs`:

```csharp
using Cutback.Core.Detection;
using Cutback.Core.Models;

namespace Cutback.Core.Tests;

public sealed class SegmentListFillerTests
{
    private const double Duration = 10.0;

    private static PlannedCut Filler(double start, double end, string word = "um") => new(start, end, $"filler: {word}");

    private static PlannedCut Silence(double start, double end) => new(start, end, $"silence {end - start:0.00}s");

    [Fact]
    public void SetRange_with_origin_stamps_origin_and_reason()
    {
        var list = new SegmentList(Duration);

        var index = list.SetRange(2.0, 2.3, enabled: false, SegmentOrigin.Filler, "filler: um");

        list.Segments[index].Should().BeEquivalentTo(new { Start = 2.0, End = 2.3, Enabled = false, Origin = SegmentOrigin.Filler, Reason = "filler: um" });
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Three_argument_SetRange_is_still_manual_with_no_reason()
    {
        var list = new SegmentList(Duration);

        var index = list.SetRange(2.0, 2.3, enabled: false);

        list.Segments[index].Origin.Should().Be(SegmentOrigin.Manual);
        list.Segments[index].Reason.Should().BeNull();
    }

    [Fact]
    public void Filler_cuts_become_disabled_filler_segments_inside_kept_footage()
    {
        var list = new SegmentList(Duration);

        var applied = list.ApplyFillerCuts([Filler(2.0, 2.3), Filler(5.0, 5.4, "uh")]);

        applied.Should().Be(2);
        list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin)).Should().Equal(
            (0.0, 2.0, true, SegmentOrigin.Auto),
            (2.0, 2.3, false, SegmentOrigin.Filler),
            (2.3, 5.0, true, SegmentOrigin.Auto),
            (5.0, 5.4, false, SegmentOrigin.Filler),
            (5.4, 10.0, true, SegmentOrigin.Auto));
        list.Segments[3].Reason.Should().Be("filler: uh");
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Re_running_replaces_earlier_filler_cuts()
    {
        var list = new SegmentList(Duration);
        list.ApplyFillerCuts([Filler(2.0, 2.3)]);

        list.ApplyFillerCuts([Filler(6.0, 6.2)]);

        list.Segments.Select(s => (s.Start, s.End, s.Enabled)).Should().Equal(
            (0.0, 6.0, true),
            (6.0, 6.2, false),
            (6.2, 10.0, true));
        list.Segments.Should().NotContain(s => s.Origin == SegmentOrigin.Filler && s.Start == 2.0);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Silence_re_detection_preserves_filler_cuts()
    {
        var list = new SegmentList(Duration);
        list.ApplyFillerCuts([Filler(2.0, 2.3)]);

        list.ReplaceAutoSegments([Silence(6.0, 7.0)]);

        list.Segments.Should().Contain(s => s.Start == 2.0 && s.End == 2.3 && !s.Enabled && s.Origin == SegmentOrigin.Filler);
        list.Segments.Should().Contain(s => s.Start == 6.0 && s.End == 7.0 && !s.Enabled && s.Origin == SegmentOrigin.Auto);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Filler_re_detection_preserves_silence_cuts()
    {
        var list = new SegmentList(Duration);
        list.ReplaceAutoSegments([Silence(6.0, 7.0)]);
        list.ApplyFillerCuts([Filler(2.0, 2.3)]);

        list.ApplyFillerCuts([Filler(3.0, 3.2)]);

        list.Segments.Should().Contain(s => s.Start == 6.0 && s.End == 7.0 && !s.Enabled && s.Origin == SegmentOrigin.Auto);
        list.Segments.Should().Contain(s => s.Start == 3.0 && s.End == 3.2 && !s.Enabled && s.Origin == SegmentOrigin.Filler);
        list.Segments.Should().NotContain(s => s.Start == 2.0 && s.End == 2.3);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Removing_a_filler_cut_next_to_a_silence_cut_gives_the_footage_back_to_the_kept_side()
    {
        var list = new SegmentList(Duration);
        list.ReplaceAutoSegments([Silence(6.0, 7.0)]);
        list.ApplyFillerCuts([Filler(5.8, 6.0)]);

        list.ApplyFillerCuts([]);

        list.Segments.Select(s => (s.Start, s.End, s.Enabled)).Should().Equal(
            (0.0, 6.0, true),
            (6.0, 7.0, false),
            (7.0, 10.0, true));
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void ApplyFillerCuts_raises_Changed_once()
    {
        var list = new SegmentList(Duration);
        var raised = 0;
        list.Changed += (_, _) => raised++;

        list.ApplyFillerCuts([Filler(2.0, 2.3), Filler(5.0, 5.4)]);

        raised.Should().Be(1);
    }

    [Fact]
    public void ApplyFillerCuts_with_nothing_to_do_does_not_raise_Changed()
    {
        var list = new SegmentList(Duration);
        var raised = 0;
        list.Changed += (_, _) => raised++;

        list.ApplyFillerCuts([]);

        raised.Should().Be(0);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~SegmentListFillerTests" 2>&1 | tail -20`
Expected: build errors for the missing `SetRange` overload and `ApplyFillerCuts`.

- [ ] **Step 3: Refactor `SetRange` and `Dissolve` into cores that do not raise `Changed`, then add `ApplyFillerCuts`**

In `src/Cutback.Core/SegmentList.cs` replace the existing `SetRange` method with:

```csharp
    /// <summary>
    /// Sets the state of the time range <c>[start, end]</c>, creating boundaries at both ends when
    /// they do not already exist and collapsing everything in between into one segment. That
    /// segment is <see cref="SegmentOrigin.Manual"/>: carving out a range by hand is a decision
    /// about that range. It keeps the id of the first segment it covered and has no reason. The
    /// neighbours outside the range are left alone even when they share the new state, so the
    /// section the user just drew stays a distinct region on the timeline.
    /// </summary>
    /// <param name="start">Start in seconds; clamped to <c>[0, Duration]</c>.</param>
    /// <param name="end">End in seconds; clamped to <c>[0, Duration]</c>.</param>
    /// <param name="enabled">Whether the range is kept.</param>
    /// <returns>The index of the resulting segment, or -1 if the range was shorter than <see cref="MinSegmentLength"/> and nothing changed.</returns>
    public int SetRange(double start, double end, bool enabled) => SetRange(start, end, enabled, SegmentOrigin.Manual, null);

    /// <summary>
    /// <see cref="SetRange(double, double, bool)"/> with an explicit origin and reason, for
    /// detectors that carve a cut into the existing partition rather than re-laying it.
    /// </summary>
    public int SetRange(double start, double end, bool enabled, SegmentOrigin origin, string? reason)
    {
        var index = SetRangeCore(start, end, enabled, origin, reason);
        if (index >= 0)
        {
            OnChanged();
        }

        return index;
    }

    private int SetRangeCore(double start, double end, bool enabled, SegmentOrigin origin, string? reason)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end))
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Range bounds must be finite.");
        }

        start = Math.Clamp(start, 0, Duration);
        end = Math.Clamp(end, 0, Duration);
        if (end - start < MinSegmentLength)
        {
            return -1;
        }

        SplitAt(start);
        SplitAt(end);

        var first = IndexAt(start);
        var last = first;
        while (_segments[last].End < end)
        {
            last++;
        }

        var section = _segments[first] with
        {
            Start = start,
            End = end,
            Enabled = enabled,
            Origin = origin,
            Reason = reason,
        };
        _segments.RemoveRange(first, last - first + 1);
        _segments.Insert(first, section);
        return first;
    }

    /// <summary>
    /// Applies a fresh filler-word detection result. Every existing <see cref="SegmentOrigin.Filler"/>
    /// segment is dissolved into its neighbours first, so a re-run replaces the previous result,
    /// then each cut is carved out of whatever is there with <see cref="SegmentOrigin.Filler"/>.
    /// Auto and manual segments outside the cuts are untouched; the caller is responsible for not
    /// passing cuts that overlap manual segments (see <c>FillerCutPlanner</c>).
    /// </summary>
    /// <returns>The number of cuts applied.</returns>
    public int ApplyFillerCuts(IEnumerable<PlannedCut> cuts)
    {
        ArgumentNullException.ThrowIfNull(cuts);

        var changed = false;
        for (var i = _segments.Count - 1; i >= 0; i--)
        {
            if (_segments[i].Origin == SegmentOrigin.Filler && DissolveCore(i))
            {
                changed = true;
            }
        }

        var applied = 0;
        foreach (var cut in cuts.OrderBy(c => c.Start))
        {
            if (SetRangeCore(cut.Start, cut.End, enabled: false, SegmentOrigin.Filler, cut.Reason) >= 0)
            {
                applied++;
                changed = true;
            }
        }

        if (changed)
        {
            OnChanged();
        }

        return applied;
    }
```

Then replace the existing `Dissolve` method body so it delegates to a core:

```csharp
    public bool Dissolve(int index)
    {
        if (index < 0 || index >= _segments.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Segment index must be within 0..{_segments.Count - 1}.");
        }

        if (!DissolveCore(index))
        {
            return false;
        }

        OnChanged();
        return true;
    }

    /// <summary><see cref="Dissolve"/> without raising <see cref="Changed"/>. False if this is the only segment.</summary>
    private bool DissolveCore(int index)
    {
        if (_segments.Count < 2)
        {
            return false;
        }

        var target = _segments[index];
        var hasLeft = index > 0;
        var hasRight = index < _segments.Count - 1;

        if (hasLeft && hasRight && _segments[index - 1].Enabled == _segments[index + 1].Enabled)
        {
            var left = _segments[index - 1];
            var right = _segments[index + 1];
            _segments[index - 1] = left with
            {
                End = right.End,
                Origin = MergeOrigin(left.Origin, right.Origin),
                Reason = left.Reason ?? right.Reason,
            };
            _segments.RemoveRange(index, 2);
        }
        else if (hasLeft)
        {
            _segments[index - 1] = _segments[index - 1] with { End = target.End };
            _segments.RemoveAt(index);
        }
        else
        {
            _segments[index + 1] = _segments[index + 1] with { Start = target.Start };
            _segments.RemoveAt(index);
        }

        return true;
    }
```

Keep the existing `<summary>` doc comment above `Dissolve`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Cutback.Core.Tests 2>&1 | tail -5`
Expected: all pass, including the existing `SegmentListTests` for `SetRange` and `Dissolve`.

- [ ] **Step 5: Commit**

```bash
dotnet format Cutback.sln
git add src/Cutback.Core/SegmentList.cs tests/Cutback.Core.Tests/SegmentListFillerTests.cs
git commit -m "Let SegmentList carve filler cuts with their own origin

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: `FillerDetector`

**Files:**
- Create: `src/Cutback.Core/Detection/FillerDetector.cs`
- Test: `tests/Cutback.Core.Tests/FillerDetectorTests.cs`

**Interfaces:**
- Consumes: `Word(string Text, double Start, double End, double Confidence)`.
- Produces:
  - `public static IReadOnlyList<string> FillerDetector.DefaultWords`
  - `public static string FillerDetector.Normalize(string text)`
  - `public static IReadOnlyList<Word> FillerDetector.Find(IReadOnlyList<Word> transcript, IEnumerable<string> fillerWords)`

- [ ] **Step 1: Write the failing tests**

Create `tests/Cutback.Core.Tests/FillerDetectorTests.cs`:

```csharp
using Cutback.Core.Detection;
using Cutback.Core.Models;

namespace Cutback.Core.Tests;

public sealed class FillerDetectorTests
{
    private static Word W(string text, double start) => new(text, start, start + 0.2, 0.9);

    [Theory]
    [InlineData("Um,", "um")]
    [InlineData("  UH...", "uh")]
    [InlineData("hmm", "hmm")]
    [InlineData("\"er\"", "er")]
    [InlineData("uh-huh", "uh-huh")]
    public void Normalize_lower_cases_and_strips_surrounding_punctuation(string input, string expected)
    {
        FillerDetector.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Default_words_are_the_documented_list()
    {
        FillerDetector.DefaultWords.Should().Equal("um", "umm", "uh", "uhh", "er", "erm", "ah", "hmm", "hm", "mm");
    }

    [Fact]
    public void Find_returns_matching_words_in_timeline_order()
    {
        var transcript = new[] { W("So", 0.0), W("um,", 0.5), W("this", 1.0), W("Uh", 1.5), W("works", 2.0) };

        var found = FillerDetector.Find(transcript, FillerDetector.DefaultWords);

        found.Select(w => w.Start).Should().Equal(0.5, 1.5);
    }

    [Fact]
    public void Find_matches_the_supplied_list_not_the_default()
    {
        var transcript = new[] { W("um", 0.0), W("like", 0.5) };

        var found = FillerDetector.Find(transcript, ["Like"]);

        found.Should().ContainSingle().Which.Text.Should().Be("like");
    }

    [Fact]
    public void Hyphenated_forms_do_not_match_their_prefix()
    {
        var transcript = new[] { W("uh-huh", 0.0) };

        FillerDetector.Find(transcript, ["uh"]).Should().BeEmpty();
    }

    [Fact]
    public void Blank_entries_in_the_list_are_ignored()
    {
        var transcript = new[] { W("", 0.0), W("um", 0.5) };

        var found = FillerDetector.Find(transcript, ["", "  ", "um"]);

        found.Should().ContainSingle().Which.Start.Should().Be(0.5);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~FillerDetectorTests" 2>&1 | tail -20`
Expected: build error, `FillerDetector` does not exist.

- [ ] **Step 3: Implement**

Create `src/Cutback.Core/Detection/FillerDetector.cs`:

```csharp
using Cutback.Core.Models;

namespace Cutback.Core.Detection;

/// <summary>
/// Finds filler words ("um", "uh", …) in a transcript by exact match against a word list. Pure
/// text matching: context-dependent fillers such as "like" or "so" are deliberately not in the
/// default list, because deciding those needs the surrounding sentence (Phase 3).
/// </summary>
public static class FillerDetector
{
    public static IReadOnlyList<string> DefaultWords { get; } =
        ["um", "umm", "uh", "uhh", "er", "erm", "ah", "hmm", "hm", "mm"];

    /// <summary>Lower-cases and strips leading and trailing punctuation so "Um," matches "um". Inner hyphens survive.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var span = text.AsSpan().Trim();
        while (span.Length > 0 && char.IsPunctuation(span[0]))
        {
            span = span[1..];
        }

        while (span.Length > 0 && char.IsPunctuation(span[^1]))
        {
            span = span[..^1];
        }

        return span.ToString().ToLowerInvariant();
    }

    /// <summary>The transcript words whose normalised text is in <paramref name="fillerWords"/>, in timeline order.</summary>
    public static IReadOnlyList<Word> Find(IReadOnlyList<Word> transcript, IEnumerable<string> fillerWords)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(fillerWords);

        var set = new HashSet<string>(
            fillerWords.Select(Normalize).Where(w => w.Length > 0),
            StringComparer.Ordinal);
        if (set.Count == 0)
        {
            return [];
        }

        return transcript
            .Where(w => set.Contains(Normalize(w.Text)))
            .OrderBy(w => w.Start)
            .ToList();
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~FillerDetectorTests" 2>&1 | tail -5`
Expected: 10 passing.

- [ ] **Step 5: Commit**

```bash
dotnet format Cutback.sln
git add src/Cutback.Core/Detection/FillerDetector.cs tests/Cutback.Core.Tests/FillerDetectorTests.cs
git commit -m "Add filler-word matching over a transcript

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: `FillerCutPlanner`

**Files:**
- Create: `src/Cutback.Core/Detection/FillerSpan.cs`
- Create: `src/Cutback.Core/Detection/FillerCutPlanner.cs`
- Test: `tests/Cutback.Core.Tests/FillerCutPlannerTests.cs`

**Interfaces:**
- Consumes: `Segment`, `DetectionSettings.MinKeepMs`, `PlannedCut`.
- Produces:
  - `public readonly record struct FillerSpan(double Start, double End, string Word)`
  - `public static IReadOnlyList<PlannedCut> FillerCutPlanner.Plan(IEnumerable<FillerSpan> spans, IReadOnlyList<Segment> current, DetectionSettings settings, double duration)`; reasons are `"filler: <word>"`, merged spans join words with a space.

- [ ] **Step 1: Write the failing tests**

Create `tests/Cutback.Core.Tests/FillerCutPlannerTests.cs`:

```csharp
using Cutback.Core.Detection;
using Cutback.Core.Models;

namespace Cutback.Core.Tests;

public sealed class FillerCutPlannerTests
{
    private const double Duration = 10.0;
    private static readonly DetectionSettings Settings = DetectionSettings.Default with { MinKeepMs = 120 };

    private static FillerSpan Span(double start, double end, string word = "um") => new(start, end, word);

    private static IReadOnlyList<Segment> Partition(params (double Start, double End, bool Enabled, SegmentOrigin Origin)[] parts)
        => parts.Select(p => Segment.Create(p.Start, p.End, p.Enabled, p.Origin)).ToList();

    private static IReadOnlyList<Segment> AllKept() => Partition((0.0, Duration, true, SegmentOrigin.Auto));

    [Fact]
    public void A_span_in_kept_footage_becomes_a_cut_with_a_filler_reason()
    {
        var cuts = FillerCutPlanner.Plan([Span(2.0, 2.3)], AllKept(), Settings, Duration);

        cuts.Should().ContainSingle().Which.Should().Be(new PlannedCut(2.0, 2.3, "filler: um"));
    }

    [Fact]
    public void Spans_are_clamped_to_the_timeline_and_empty_ones_dropped()
    {
        var cuts = FillerCutPlanner.Plan([Span(-0.5, 0.2), Span(9.9, 10.5), Span(4.0, 4.0)], AllKept(), Settings, Duration);

        cuts.Select(c => (c.Start, c.End)).Should().Equal((0.0, 0.2), (9.9, 10.0));
    }

    [Fact]
    public void A_span_touching_a_manual_segment_is_dropped()
    {
        var current = Partition((0.0, 2.0, true, SegmentOrigin.Auto), (2.0, 3.0, true, SegmentOrigin.Manual), (3.0, Duration, true, SegmentOrigin.Auto));

        var cuts = FillerCutPlanner.Plan([Span(1.9, 2.1), Span(5.0, 5.2)], current, Settings, Duration);

        cuts.Select(c => c.Start).Should().Equal(5.0);
    }

    [Fact]
    public void A_span_already_inside_a_cut_is_dropped()
    {
        var current = Partition((0.0, 2.0, true, SegmentOrigin.Auto), (2.0, 3.0, false, SegmentOrigin.Auto), (3.0, Duration, true, SegmentOrigin.Auto));

        var cuts = FillerCutPlanner.Plan([Span(2.2, 2.5)], current, Settings, Duration);

        cuts.Should().BeEmpty();
    }

    [Fact]
    public void A_span_straddling_a_cut_edge_is_kept()
    {
        var current = Partition((0.0, 2.0, true, SegmentOrigin.Auto), (2.0, 3.0, false, SegmentOrigin.Auto), (3.0, Duration, true, SegmentOrigin.Auto));

        var cuts = FillerCutPlanner.Plan([Span(2.9, 3.2)], current, Settings, Duration);

        cuts.Should().ContainSingle().Which.Should().Be(new PlannedCut(2.9, 3.2, "filler: um"));
    }

    [Fact]
    public void Overlapping_and_touching_spans_merge_and_join_their_words()
    {
        var cuts = FillerCutPlanner.Plan([Span(2.0, 2.3, "um"), Span(2.3, 2.6, "uh"), Span(2.5, 2.8, "er")], AllKept(), Settings, Duration);

        cuts.Should().ContainSingle().Which.Should().Be(new PlannedCut(2.0, 2.8, "filler: um uh er"));
    }

    [Fact]
    public void A_kept_sliver_shorter_than_minKeep_between_two_spans_is_bridged()
    {
        var cuts = FillerCutPlanner.Plan([Span(2.0, 2.3), Span(2.35, 2.6, "uh")], AllKept(), Settings, Duration);

        cuts.Should().ContainSingle().Which.Should().Be(new PlannedCut(2.0, 2.6, "filler: um uh"));
    }

    [Fact]
    public void A_kept_sliver_between_a_span_and_an_existing_cut_is_bridged_on_both_sides()
    {
        var current = Partition((0.0, 2.0, true, SegmentOrigin.Auto), (2.0, 3.0, false, SegmentOrigin.Auto), (3.0, Duration, true, SegmentOrigin.Auto));

        var cuts = FillerCutPlanner.Plan([Span(1.7, 1.95), Span(3.05, 3.3, "uh")], current, Settings, Duration);

        cuts.Select(c => (c.Start, c.End)).Should().Equal((1.7, 2.0), (3.0, 3.3));
    }

    [Fact]
    public void A_sliver_is_not_bridged_over_a_manual_segment()
    {
        var current = Partition(
            (0.0, 2.0, true, SegmentOrigin.Auto),
            (2.0, 3.0, false, SegmentOrigin.Auto),
            (3.0, 3.05, true, SegmentOrigin.Manual),
            (3.05, Duration, true, SegmentOrigin.Auto));

        var cuts = FillerCutPlanner.Plan([Span(3.05, 3.3)], current, Settings, Duration);

        cuts.Should().ContainSingle().Which.Start.Should().Be(3.05);
    }

    [Fact]
    public void Slivers_at_the_ends_of_the_timeline_are_bridged()
    {
        var cuts = FillerCutPlanner.Plan([Span(0.05, 0.3), Span(9.7, 9.95, "uh")], AllKept(), Settings, Duration);

        cuts.Select(c => (c.Start, c.End)).Should().Equal((0.0, 0.3), (9.7, 10.0));
    }

    [Fact]
    public void Output_is_sorted_by_start()
    {
        var cuts = FillerCutPlanner.Plan([Span(6.0, 6.2), Span(2.0, 2.2)], AllKept(), Settings, Duration);

        cuts.Select(c => c.Start).Should().BeInAscendingOrder();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~FillerCutPlannerTests" 2>&1 | tail -20`
Expected: build error, `FillerSpan` / `FillerCutPlanner` do not exist.

- [ ] **Step 3: Implement**

Create `src/Cutback.Core/Detection/FillerSpan.cs`:

```csharp
namespace Cutback.Core.Detection;

/// <summary>A filler word's extent in the source, in seconds, with its edges already snapped by the caller.</summary>
public readonly record struct FillerSpan(double Start, double End, string Word);
```

Create `src/Cutback.Core/Detection/FillerCutPlanner.cs`:

```csharp
using Cutback.Core.Models;

namespace Cutback.Core.Detection;

/// <summary>
/// Turns filler-word spans into cuts that respect the user's existing edits: manual segments are
/// never touched, spans already inside a cut are dropped, overlapping spans merge, and kept
/// slivers shorter than the minimum keep length between a span and any neighbouring cut are
/// bridged so a filler next to a silence cut does not leave a stutter. Pure.
/// </summary>
public static class FillerCutPlanner
{
    public static IReadOnlyList<PlannedCut> Plan(
        IEnumerable<FillerSpan> spans,
        IReadOnlyList<Segment> current,
        DetectionSettings settings,
        double duration)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(duration);

        var minKeep = settings.MinKeepMs / 1000.0;

        // 1. Clamp, drop empty, drop anything touching a manual segment or already cut.
        var candidates = spans
            .Select(s => s with { Start = Math.Max(0, s.Start), End = Math.Min(duration, s.End) })
            .Where(s => s.End > s.Start)
            .Where(s => !IntersectsManual(current, s.Start, s.End))
            .Where(s => !IsAlreadyCut(current, s.Start, s.End))
            .OrderBy(s => s.Start)
            .ToList();

        // 2. Merge overlapping or touching spans.
        var merged = new List<FillerSpan>(candidates.Count);
        foreach (var span in candidates)
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
            {
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, span.End), Word = merged[^1].Word + " " + span.Word };
            }
            else
            {
                merged.Add(span);
            }
        }

        // 3. Bridge short kept slivers to the neighbouring existing cut or timeline end.
        var disabledEnds = current.Where(s => !s.Enabled).Select(s => s.End).Append(0.0).ToList();
        var disabledStarts = current.Where(s => !s.Enabled).Select(s => s.Start).Append(duration).ToList();
        for (var i = 0; i < merged.Count; i++)
        {
            var span = merged[i];
            var prevEdge = disabledEnds.Where(e => e <= span.Start).DefaultIfEmpty(double.NegativeInfinity).Max();
            var before = span.Start - prevEdge;
            if (before > 0 && before < minKeep && !IntersectsManual(current, prevEdge, span.Start))
            {
                span = span with { Start = prevEdge };
            }

            var nextEdge = disabledStarts.Where(e => e >= span.End).DefaultIfEmpty(double.PositiveInfinity).Min();
            var after = nextEdge - span.End;
            if (after > 0 && after < minKeep && !IntersectsManual(current, span.End, nextEdge))
            {
                span = span with { End = nextEdge };
            }

            merged[i] = span;
        }

        // 4. Bridge short kept slivers between consecutive spans.
        var bridged = new List<FillerSpan>(merged.Count);
        foreach (var span in merged)
        {
            if (bridged.Count > 0
                && span.Start - bridged[^1].End < minKeep
                && !IntersectsManual(current, bridged[^1].End, span.Start))
            {
                bridged[^1] = bridged[^1] with { End = Math.Max(bridged[^1].End, span.End), Word = bridged[^1].Word + " " + span.Word };
            }
            else
            {
                bridged.Add(span);
            }
        }

        return bridged.Select(s => new PlannedCut(s.Start, s.End, "filler: " + s.Word)).ToList();
    }

    private static bool IntersectsManual(IReadOnlyList<Segment> segments, double start, double end)
        => segments.Any(s => s.Origin == SegmentOrigin.Manual && s.Start < end && s.End > start);

    /// <summary>True when every segment overlapping <c>[start, end)</c> is disabled. Segments partition the timeline, so this means the whole span is cut.</summary>
    private static bool IsAlreadyCut(IReadOnlyList<Segment> segments, double start, double end)
        => segments.Where(s => s.Start < end && s.End > start).All(s => !s.Enabled);
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~FillerCutPlannerTests" 2>&1 | tail -5`
Expected: 11 passing. If `A_sliver_is_not_bridged_over_a_manual_segment` fails, check step 3's `IntersectsManual(current, prevEdge, span.Start)` guard is present.

- [ ] **Step 5: Commit**

```bash
dotnet format Cutback.sln
git add src/Cutback.Core/Detection/FillerSpan.cs src/Cutback.Core/Detection/FillerCutPlanner.cs tests/Cutback.Core.Tests/FillerCutPlannerTests.cs
git commit -m "Plan filler cuts around existing edits and cuts

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: `TranscriptView`

**Files:**
- Create: `src/Cutback.Core/Transcript/TranscriptView.cs`
- Test: `tests/Cutback.Core.Tests/TranscriptViewTests.cs`

**Interfaces:**
- Produces (namespace `Cutback.Core.Transcript`):
  - `public static bool TranscriptView.IsCut(Word word, IReadOnlyList<Segment> segments)` — midpoint rule.
  - `public static bool[] TranscriptView.CutStates(IReadOnlyList<Word> words, IReadOnlyList<Segment> segments)` — one flag per word.
  - `public static int TranscriptView.IndexAtTime(IReadOnlyList<Word> words, double seconds)` — word whose `[Start, End)` contains the time, else -1.

- [ ] **Step 1: Write the failing tests**

Create `tests/Cutback.Core.Tests/TranscriptViewTests.cs`:

```csharp
using Cutback.Core.Models;
using Cutback.Core.Transcript;

namespace Cutback.Core.Tests;

public sealed class TranscriptViewTests
{
    private static readonly IReadOnlyList<Segment> Segments =
    [
        Segment.Create(0.0, 2.0, enabled: true, SegmentOrigin.Auto),
        Segment.Create(2.0, 3.0, enabled: false, SegmentOrigin.Auto),
        Segment.Create(3.0, 10.0, enabled: true, SegmentOrigin.Auto),
    ];

    private static readonly IReadOnlyList<Word> Words =
    [
        new("hello", 0.5, 0.9, 0.9),
        new("um", 1.9, 2.3, 0.9),      // midpoint 2.1: cut
        new("uh", 2.8, 3.1, 0.9),      // midpoint 2.95: cut
        new("world", 2.9, 3.5, 0.9),   // midpoint 3.2: kept
    ];

    [Fact]
    public void A_word_is_cut_when_its_midpoint_lies_in_a_disabled_segment()
    {
        TranscriptView.IsCut(Words[0], Segments).Should().BeFalse();
        TranscriptView.IsCut(Words[1], Segments).Should().BeTrue();
        TranscriptView.IsCut(Words[2], Segments).Should().BeTrue();
        TranscriptView.IsCut(Words[3], Segments).Should().BeFalse();
    }

    [Fact]
    public void CutStates_matches_IsCut_for_every_word()
    {
        TranscriptView.CutStates(Words, Segments).Should().Equal(false, true, true, false);
    }

    [Fact]
    public void A_word_past_the_end_of_the_timeline_takes_the_last_segment()
    {
        var late = new Word("late", 9.9, 10.3, 0.9);

        TranscriptView.IsCut(late, Segments).Should().BeFalse();
    }

    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(0.89, 0)]
    [InlineData(0.9, -1)]
    [InlineData(1.0, -1)]
    [InlineData(2.0, 1)]
    [InlineData(3.4, 3)]
    [InlineData(50.0, -1)]
    public void IndexAtTime_finds_the_word_containing_the_time(double seconds, int expected)
    {
        TranscriptView.IndexAtTime(Words, seconds).Should().Be(expected);
    }

    [Fact]
    public void IndexAtTime_on_an_empty_transcript_is_minus_one()
    {
        TranscriptView.IndexAtTime([], 1.0).Should().Be(-1);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~TranscriptViewTests" 2>&1 | tail -20`
Expected: build error, namespace `Cutback.Core.Transcript` does not exist.

- [ ] **Step 3: Implement**

Create `src/Cutback.Core/Transcript/TranscriptView.cs`:

```csharp
using Cutback.Core.Models;

namespace Cutback.Core.Transcript;

/// <summary>
/// Read-only questions the transcript panel asks about words against the current partition.
/// A word is "cut" when its midpoint falls in a disabled segment: a cut that clips the very edge
/// of a word should not strike the whole word through.
/// </summary>
public static class TranscriptView
{
    public static bool IsCut(Word word, IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(segments);
        var index = SegmentIndexAt(segments, (word.Start + word.End) / 2);
        return index >= 0 && !segments[index].Enabled;
    }

    /// <summary>One flag per word, in order. Equivalent to calling <see cref="IsCut"/> for each.</summary>
    public static bool[] CutStates(IReadOnlyList<Word> words, IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(segments);
        var states = new bool[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            states[i] = IsCut(words[i], segments);
        }

        return states;
    }

    /// <summary>Index of the word whose <c>[Start, End)</c> contains <paramref name="seconds"/>, or -1. Words must be sorted by start.</summary>
    public static int IndexAtTime(IReadOnlyList<Word> words, double seconds)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count == 0 || !double.IsFinite(seconds))
        {
            return -1;
        }

        // Last word whose Start <= seconds.
        int lo = 0, hi = words.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (words[mid].Start <= seconds)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return words[lo].Start <= seconds && seconds < words[lo].End ? lo : -1;
    }

    /// <summary>Segment containing <paramref name="time"/>; times at or past the end map to the last segment, before 0 to -1.</summary>
    private static int SegmentIndexAt(IReadOnlyList<Segment> segments, double time)
    {
        if (segments.Count == 0 || time < 0)
        {
            return -1;
        }

        if (time >= segments[^1].End)
        {
            return segments.Count - 1;
        }

        int lo = 0, hi = segments.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (segments[mid].Start <= time)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~TranscriptViewTests" 2>&1 | tail -5`
Expected: 11 passing.

- [ ] **Step 5: Commit**

```bash
dotnet format Cutback.sln
git add src/Cutback.Core/Transcript/TranscriptView.cs tests/Cutback.Core.Tests/TranscriptViewTests.cs
git commit -m "Answer which transcript words are cut and which word is at a time

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: `OutputTimeline`

**Files:**
- Create: `src/Cutback.Core/Transcript/OutputTimeline.cs`
- Test: `tests/Cutback.Core.Tests/OutputTimelineTests.cs`

**Interfaces:**
- Produces: `public sealed class OutputTimeline(IReadOnlyList<Segment> segments)` with `double OutputDuration` and `double ToOutput(double sourceSeconds)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Cutback.Core.Tests/OutputTimelineTests.cs`:

```csharp
using Cutback.Core.Models;
using Cutback.Core.Transcript;

namespace Cutback.Core.Tests;

public sealed class OutputTimelineTests
{
    private static readonly IReadOnlyList<Segment> Segments =
    [
        Segment.Create(0.0, 2.0, enabled: true, SegmentOrigin.Auto),
        Segment.Create(2.0, 3.0, enabled: false, SegmentOrigin.Auto),
        Segment.Create(3.0, 6.0, enabled: true, SegmentOrigin.Auto),
        Segment.Create(6.0, 10.0, enabled: false, SegmentOrigin.Auto),
    ];

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(2.0, 2.0)]   // start of the cut maps to where the next kept region begins
    [InlineData(2.7, 2.0)]   // inside the cut: same
    [InlineData(3.0, 2.0)]
    [InlineData(4.5, 3.5)]
    [InlineData(6.0, 5.0)]   // trailing cut maps to the output end
    [InlineData(9.9, 5.0)]
    public void Source_times_map_to_output_times(double source, double expected)
    {
        new OutputTimeline(Segments).ToOutput(source).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void Output_duration_is_the_sum_of_kept_segments()
    {
        new OutputTimeline(Segments).OutputDuration.Should().BeApproximately(5.0, 1e-9);
    }

    [Fact]
    public void With_nothing_kept_everything_maps_to_zero()
    {
        var timeline = new OutputTimeline([Segment.Create(0.0, 5.0, enabled: false, SegmentOrigin.Auto)]);

        timeline.OutputDuration.Should().Be(0.0);
        timeline.ToOutput(3.0).Should().Be(0.0);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~OutputTimelineTests" 2>&1 | tail -20`
Expected: build error, `OutputTimeline` does not exist.

- [ ] **Step 3: Implement**

Create `src/Cutback.Core/Transcript/OutputTimeline.cs`:

```csharp
using Cutback.Core.Models;

namespace Cutback.Core.Transcript;

/// <summary>
/// Maps source times to the times they will have in the exported video, which contains only the
/// enabled segments back to back. A time inside a cut maps to the start of the next kept region,
/// or to the output end if nothing is kept after it.
/// </summary>
public sealed class OutputTimeline
{
    private readonly double[] _starts;
    private readonly double[] _ends;
    private readonly double[] _offsets;

    public OutputTimeline(IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var kept = segments.Where(s => s.Enabled).OrderBy(s => s.Start).ToList();
        _starts = new double[kept.Count];
        _ends = new double[kept.Count];
        _offsets = new double[kept.Count];

        var offset = 0.0;
        for (var i = 0; i < kept.Count; i++)
        {
            _starts[i] = kept[i].Start;
            _ends[i] = kept[i].End;
            _offsets[i] = offset;
            offset += kept[i].End - kept[i].Start;
        }

        OutputDuration = offset;
    }

    public double OutputDuration { get; }

    public double ToOutput(double sourceSeconds)
    {
        for (var i = 0; i < _starts.Length; i++)
        {
            if (sourceSeconds < _starts[i])
            {
                return _offsets[i];
            }

            if (sourceSeconds < _ends[i])
            {
                return _offsets[i] + (sourceSeconds - _starts[i]);
            }
        }

        return OutputDuration;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~OutputTimelineTests" 2>&1 | tail -5`
Expected: 10 passing.

- [ ] **Step 5: Commit**

```bash
dotnet format Cutback.sln
git add src/Cutback.Core/Transcript/OutputTimeline.cs tests/Cutback.Core.Tests/OutputTimelineTests.cs
git commit -m "Map source times onto the exported timeline

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: `TranscriptExporter` (plain text and SRT)

**Files:**
- Create: `src/Cutback.Core/Transcript/TranscriptExporter.cs`
- Test: `tests/Cutback.Core.Tests/TranscriptExporterTests.cs`

**Interfaces:**
- Consumes: `TranscriptView.IsCut`, `OutputTimeline`.
- Produces:
  - `public static string TranscriptExporter.ToPlainText(IReadOnlyList<Word> words, IReadOnlyList<Segment> segments)`
  - `public static string TranscriptExporter.ToSrt(IReadOnlyList<Word> words, IReadOnlyList<Segment> segments)`
  - `public static string TranscriptExporter.FormatSrtTime(double seconds)` (`HH:MM:SS,mmm`)

- [ ] **Step 1: Write the failing tests**

Create `tests/Cutback.Core.Tests/TranscriptExporterTests.cs`:

```csharp
using Cutback.Core.Models;
using Cutback.Core.Transcript;

namespace Cutback.Core.Tests;

public sealed class TranscriptExporterTests
{
    // "um" (1.0..1.2) is cut; a 3.3 s pause precedes "Bye".
    private static readonly IReadOnlyList<Segment> Segments =
    [
        Segment.Create(0.0, 1.0, enabled: true, SegmentOrigin.Auto),
        Segment.Create(1.0, 1.2, enabled: false, SegmentOrigin.Filler, "filler: um"),
        Segment.Create(1.2, 6.0, enabled: true, SegmentOrigin.Auto),
    ];

    private static readonly IReadOnlyList<Word> Words =
    [
        new("Hello", 0.0, 0.4, 0.9),
        new("there", 0.5, 0.9, 0.9),
        new("um", 1.0, 1.2, 0.9),
        new("friend", 1.3, 1.7, 0.9),
        new("Bye", 5.0, 5.3, 0.9),
    ];

    [Fact]
    public void Plain_text_omits_cut_words_and_breaks_paragraphs_on_long_pauses()
    {
        TranscriptExporter.ToPlainText(Words, Segments).Should().Be("Hello there friend\n\nBye\n");
    }

    [Fact]
    public void Plain_text_of_an_empty_transcript_is_empty()
    {
        TranscriptExporter.ToPlainText([], Segments).Should().BeEmpty();
    }

    [Fact]
    public void Srt_remaps_times_to_the_output_and_splits_cues_on_pauses()
    {
        var expected =
            "1\n00:00:00,000 --> 00:00:01,500\nHello there friend\n\n" +
            "2\n00:00:04,800 --> 00:00:05,100\nBye\n";

        TranscriptExporter.ToSrt(Words, Segments).Should().Be(expected);
    }

    [Fact]
    public void Srt_wraps_long_cues_into_two_lines_of_at_most_42_characters()
    {
        var words = Enumerable.Range(1, 12).Select(i => new Word($"word{i}", i * 0.3, i * 0.3 + 0.2, 0.9)).ToList();
        var segments = new[] { Segment.Create(0.0, 10.0, enabled: true, SegmentOrigin.Auto) };

        var srt = TranscriptExporter.ToSrt(words, segments);

        var lines = srt.Split('\n');
        lines[0].Should().Be("1");
        lines[2].Length.Should().BeLessThanOrEqualTo(42);
        lines[3].Length.Should().BeLessThanOrEqualTo(42);
        (lines[2] + " " + lines[3]).Should().Be(string.Join(' ', words.Select(w => w.Text)));
        lines[4].Should().BeEmpty();
    }

    [Fact]
    public void Srt_starts_a_new_cue_when_the_text_would_exceed_84_characters()
    {
        var words = Enumerable.Range(1, 30).Select(i => new Word("abcde", i * 0.3, i * 0.3 + 0.2, 0.9)).ToList();
        var segments = new[] { Segment.Create(0.0, 20.0, enabled: true, SegmentOrigin.Auto) };

        var srt = TranscriptExporter.ToSrt(words, segments);

        srt.Should().Contain("\n\n2\n");
        foreach (var cueText in srt.Split("\n\n").Select(block => string.Join(' ', block.Split('\n').Skip(2))))
        {
            cueText.Length.Should().BeLessThanOrEqualTo(84);
        }
    }

    [Fact]
    public void Srt_starts_a_new_cue_when_a_cue_would_exceed_five_seconds()
    {
        var words = Enumerable.Range(0, 25).Select(i => new Word("go", i * 0.5, i * 0.5 + 0.3, 0.9)).ToList();
        var segments = new[] { Segment.Create(0.0, 20.0, enabled: true, SegmentOrigin.Auto) };

        var srt = TranscriptExporter.ToSrt(words, segments);

        srt.Should().Contain("\n\n2\n").And.Contain("\n\n3\n");
    }

    [Theory]
    [InlineData(0.0, "00:00:00,000")]
    [InlineData(1.5, "00:00:01,500")]
    [InlineData(61.0, "00:01:01,000")]
    [InlineData(3661.25, "01:01:01,250")]
    [InlineData(1.1000000000000001, "00:00:01,100")]
    public void Srt_time_format(double seconds, string expected)
    {
        TranscriptExporter.FormatSrtTime(seconds).Should().Be(expected);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~TranscriptExporterTests" 2>&1 | tail -20`
Expected: build error, `TranscriptExporter` does not exist.

- [ ] **Step 3: Implement**

Create `src/Cutback.Core/Transcript/TranscriptExporter.cs`:

```csharp
using System.Globalization;
using System.Text;
using Cutback.Core.Models;

namespace Cutback.Core.Transcript;

/// <summary>
/// Writes the <em>edited</em> transcript: cut words are omitted and times are remapped onto the
/// exported video, so an SRT lines up with the export and the text reads like the finished piece.
/// </summary>
public static class TranscriptExporter
{
    /// <summary>A pause longer than this in the source starts a new paragraph.</summary>
    public const double ParagraphGapSeconds = 2.0;

    /// <summary>A pause longer than this in the source starts a new SRT cue.</summary>
    public const double CueGapSeconds = 0.7;

    public const double MaxCueSeconds = 5.0;

    public const int MaxLineChars = 42;

    public const int MaxCueChars = 2 * MaxLineChars;

    public static string ToPlainText(IReadOnlyList<Word> words, IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(segments);

        var kept = KeptWords(words, segments);
        if (kept.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < kept.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(kept[i].Start - kept[i - 1].End > ParagraphGapSeconds ? "\n\n" : " ");
            }

            sb.Append(kept[i].Text);
        }

        sb.Append('\n');
        return sb.ToString();
    }

    public static string ToSrt(IReadOnlyList<Word> words, IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(segments);

        var kept = KeptWords(words, segments);
        var timeline = new OutputTimeline(segments);

        var cues = new List<List<Word>>();
        var current = new List<Word>();
        var currentChars = 0;
        foreach (var word in kept)
        {
            if (current.Count > 0)
            {
                var gap = word.Start - current[^1].End;
                var chars = currentChars + 1 + word.Text.Length;
                var length = timeline.ToOutput(word.End) - timeline.ToOutput(current[0].Start);
                if (gap > CueGapSeconds || chars > MaxCueChars || length > MaxCueSeconds)
                {
                    cues.Add(current);
                    current = [];
                    currentChars = 0;
                }
            }

            currentChars += (current.Count > 0 ? 1 : 0) + word.Text.Length;
            current.Add(word);
        }

        if (current.Count > 0)
        {
            cues.Add(current);
        }

        var sb = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            if (i > 0)
            {
                sb.Append('\n');
            }

            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(FormatSrtTime(timeline.ToOutput(cue[0].Start)))
              .Append(" --> ")
              .Append(FormatSrtTime(timeline.ToOutput(cue[^1].End)))
              .Append('\n');
            sb.Append(Wrap(string.Join(' ', cue.Select(w => w.Text)))).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary><c>HH:MM:SS,mmm</c>, rounded to the millisecond.</summary>
    public static string FormatSrtTime(double seconds)
    {
        var totalMs = (long)Math.Round(Math.Max(0, seconds) * 1000);
        var t = TimeSpan.FromMilliseconds(totalMs);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}");
    }

    private static List<Word> KeptWords(IReadOnlyList<Word> words, IReadOnlyList<Segment> segments)
        => words.Where(w => !TranscriptView.IsCut(w, segments)).OrderBy(w => w.Start).ToList();

    /// <summary>Breaks text over <see cref="MaxLineChars"/> into two lines at the space nearest its middle.</summary>
    private static string Wrap(string text)
    {
        if (text.Length <= MaxLineChars)
        {
            return text;
        }

        var middle = text.Length / 2;
        var best = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ' && (best < 0 || Math.Abs(i - middle) < Math.Abs(best - middle)))
            {
                best = i;
            }
        }

        return best < 0 ? text : text[..best] + "\n" + text[(best + 1)..];
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~TranscriptExporterTests" 2>&1 | tail -5`
Expected: 11 passing.

- [ ] **Step 5: Commit**

```bash
dotnet format Cutback.sln
git add src/Cutback.Core/Transcript/TranscriptExporter.cs tests/Cutback.Core.Tests/TranscriptExporterTests.cs
git commit -m "Export the edited transcript as plain text or SRT

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: `PcmExtractor` (Media)

**Files:**
- Create: `src/Cutback.Media/PcmExtractor.cs`

No unit test: it shells out to ffmpeg, which tests never do. Verified end-to-end in Task 18.

**Interfaces:**
- Consumes: `FfmpegProcess.Start`, `FfmpegProcess.WaitForSuccessAsync`, `FfmpegProcess.TryKill`, `FfmpegLocation`.
- Produces: `public sealed class PcmExtractor(FfmpegLocation ffmpeg)` with `public const int SampleRate = 16000` and `Task<ReadOnlyMemory<float>> ExtractAsync(string path, double expectedDurationSeconds, IProgress<double>? progress, CancellationToken cancellationToken)`.

- [ ] **Step 1: Implement**

Create `src/Cutback.Media/PcmExtractor.cs`:

```csharp
using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Cutback.Media;

/// <summary>
/// Decodes the source's audio to 16 kHz mono 32-bit float PCM through an ffmpeg pipe, the input
/// Whisper expects, and returns the whole signal. Memory is 64 bytes per millisecond of audio,
/// about 230 MB per hour; speech recognition needs the full signal, so it is not streamed.
/// </summary>
public sealed class PcmExtractor
{
    public const int SampleRate = 16000;

    private readonly FfmpegLocation _ffmpeg;

    public PcmExtractor(FfmpegLocation ffmpeg)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        _ffmpeg = ffmpeg;
    }

    /// <param name="path">Source video. Opened read-only by ffmpeg.</param>
    /// <param name="expectedDurationSeconds">Used to size the buffer and report progress. Pass 0 if unknown.</param>
    /// <param name="progress">Fraction complete in <c>[0, 1]</c>.</param>
    /// <param name="cancellationToken">Kills ffmpeg when cancelled.</param>
    /// <exception cref="FfmpegException">ffmpeg failed, or the file produced no audio.</exception>
    public async Task<ReadOnlyMemory<float>> ExtractAsync(
        string path,
        double expectedDurationSeconds,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string[] args =
        [
            "-i", path,
            "-vn",
            "-ac", "1",
            "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "f32le",
            "-acodec", "pcm_f32le",
            "-",
        ];

        using var process = FfmpegProcess.Start(_ffmpeg.FfmpegPath, args, redirectStandardOutput: true);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var expectedSamples = expectedDurationSeconds > 0 ? (long)(expectedDurationSeconds * SampleRate) : 0;
        var samples = new float[Math.Clamp(expectedSamples + SampleRate, SampleRate, int.MaxValue - 64)];
        var count = 0;

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var carry = 0; // bytes of an incomplete float left over from the previous read
        try
        {
            var stdout = process.StandardOutput.BaseStream;
            var lastReported = -1.0;
            int read;
            while ((read = await stdout.ReadAsync(buffer.AsMemory(carry), cancellationToken).ConfigureAwait(false)) > 0)
            {
                var available = carry + read;
                var whole = available - (available % sizeof(float));
                var floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, whole));

                if (count + floats.Length > samples.Length)
                {
                    Array.Resize(ref samples, Math.Max(samples.Length * 2, count + floats.Length));
                }

                floats.CopyTo(samples.AsSpan(count));
                count += floats.Length;

                carry = available - whole;
                if (carry > 0)
                {
                    buffer.AsSpan(whole, carry).CopyTo(buffer);
                }

                if (progress is not null && expectedSamples > 0)
                {
                    var fraction = Math.Min(1.0, (double)count / expectedSamples);
                    if (fraction - lastReported >= 0.01)
                    {
                        lastReported = fraction;
                        progress.Report(fraction);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            FfmpegProcess.TryKill(process);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await FfmpegProcess.WaitForSuccessAsync(process, stderrTask, "Decoding audio for speech recognition", cancellationToken).ConfigureAwait(false);

        if (count == 0)
        {
            var stderr = await stderrTask.ConfigureAwait(false);
            throw new FfmpegException("The file produced no audio samples. It may have no audio track.", process.ExitCode, stderr);
        }

        progress?.Report(1.0);
        return samples.AsMemory(0, count);
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Cutback.Media 2>&1 | tail -3`
Expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
dotnet format Cutback.sln
git add src/Cutback.Media/PcmExtractor.cs
git commit -m "Decode 16 kHz float PCM for speech recognition

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: Whisper.net packages, `WhisperModel`, `ModelStore`, and the Analysis test project

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/Cutback.Analysis/Cutback.Analysis.csproj`
- Create: `src/Cutback.Analysis/WhisperModel.cs`
- Create: `src/Cutback.Analysis/ModelDownloadException.cs`
- Create: `src/Cutback.Analysis/ModelStore.cs`
- Create: `tests/Cutback.Analysis.Tests/Cutback.Analysis.Tests.csproj`
- Create: `tests/Cutback.Analysis.Tests/WhisperModelInfoTests.cs`
- Create: `tests/Cutback.Analysis.Tests/ModelStoreTests.cs`
- Modify: `Cutback.sln` (via `dotnet sln add`)

**Interfaces:**
- Produces (namespace `Cutback.Analysis`):
  - `public enum WhisperModel { TinyEn, BaseEn, SmallEn, MediumEn }`
  - `public sealed record WhisperModelInfo(WhisperModel Model, string Key, GgmlType GgmlType, long ApproximateBytes)` with `FileName`, `DisplayName`, static `All`, `For(WhisperModel)`, `Parse(string?)`, `Default`.
  - `public sealed class ModelStore(string cacheDirectory)` with `static CreateDefault()`, `CacheDirectory`, `PathFor(WhisperModel)`, `IsDownloaded(WhisperModel)`, `Task<string> EnsureAsync(WhisperModel, IProgress<double>?, CancellationToken)`.
  - `public sealed class ModelDownloadException : Exception`.

- [ ] **Step 1: Add the packages**

In `Directory.Packages.props`, inside the `<ItemGroup>` after the FFMpegCore line:

```xml
    <!-- Speech recognition (MIT; the runtime bundles whisper.cpp and ggml, both MIT) -->
    <PackageVersion Include="Whisper.net" Version="1.9.1" />
    <PackageVersion Include="Whisper.net.Runtime" Version="1.9.1" />
```

Replace `src/Cutback.Analysis/Cutback.Analysis.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    Phase 2: local speech recognition behind ITranscriber (Whisper.net, CPU runtime). The Phase 3
    seam ICutSuggester is still a stub. Nothing here may upload audio anywhere; the only network
    access is the one-time model download in ModelStore.
  -->

  <ItemGroup>
    <PackageReference Include="Whisper.net" />
    <PackageReference Include="Whisper.net.Runtime" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Cutback.Core\Cutback.Core.csproj" />
    <ProjectReference Include="..\Cutback.Media\Cutback.Media.csproj" />
  </ItemGroup>

</Project>
```

Run: `dotnet restore Cutback.sln 2>&1 | tail -3`
Expected: restore succeeds.

- [ ] **Step 2: Create the test project and write the failing tests**

Create `tests/Cutback.Analysis.Tests/Cutback.Analysis.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    Tests for the model-independent logic in Cutback.Analysis: token-to-word assembly, model
    metadata and the cache layout. Nothing here loads a Whisper model or runs ffmpeg.
  -->

  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
    <Using Include="FluentAssertions" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Cutback.Analysis\Cutback.Analysis.csproj" />
  </ItemGroup>

</Project>
```

Run: `dotnet sln Cutback.sln add tests/Cutback.Analysis.Tests/Cutback.Analysis.Tests.csproj --solution-folder tests`

Create `tests/Cutback.Analysis.Tests/WhisperModelInfoTests.cs`:

```csharp
using Whisper.net.Ggml;

namespace Cutback.Analysis.Tests;

public sealed class WhisperModelInfoTests
{
    [Fact]
    public void Every_model_has_info_with_a_unique_key()
    {
        var infos = Enum.GetValues<WhisperModel>().Select(WhisperModelInfo.For).ToList();

        infos.Select(i => i.Key).Should().OnlyHaveUniqueItems();
        infos.Should().OnlyContain(i => i.ApproximateBytes > 0);
        WhisperModelInfo.All.Select(i => i.Model).Should().BeEquivalentTo(Enum.GetValues<WhisperModel>());
    }

    [Fact]
    public void Base_en_is_the_default()
    {
        WhisperModelInfo.Default.Model.Should().Be(WhisperModel.BaseEn);
        WhisperModelInfo.Default.Key.Should().Be("base.en");
        WhisperModelInfo.Default.GgmlType.Should().Be(GgmlType.BaseEn);
        WhisperModelInfo.Default.FileName.Should().Be("ggml-base.en.bin");
    }

    [Theory]
    [InlineData("tiny.en", WhisperModel.TinyEn)]
    [InlineData("small.en", WhisperModel.SmallEn)]
    [InlineData("MEDIUM.EN", WhisperModel.MediumEn)]
    [InlineData("large", WhisperModel.BaseEn)]
    [InlineData("", WhisperModel.BaseEn)]
    [InlineData(null, WhisperModel.BaseEn)]
    public void Parse_maps_keys_and_falls_back_to_the_default(string? key, WhisperModel expected)
    {
        WhisperModelInfo.Parse(key).Should().Be(expected);
    }

    [Fact]
    public void Display_name_shows_the_approximate_size_in_megabytes()
    {
        WhisperModelInfo.For(WhisperModel.BaseEn).DisplayName.Should().Be("base.en (~148 MB)");
    }
}
```

Create `tests/Cutback.Analysis.Tests/ModelStoreTests.cs`:

```csharp
namespace Cutback.Analysis.Tests;

public sealed class ModelStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cutback-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Paths_live_under_the_cache_directory_and_are_named_by_model()
    {
        var store = new ModelStore(_dir);

        store.CacheDirectory.Should().Be(_dir);
        store.PathFor(WhisperModel.SmallEn).Should().Be(Path.Combine(_dir, "ggml-small.en.bin"));
    }

    [Fact]
    public void IsDownloaded_reflects_the_file_on_disk()
    {
        var store = new ModelStore(_dir);
        store.IsDownloaded(WhisperModel.BaseEn).Should().BeFalse();

        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(store.PathFor(WhisperModel.BaseEn), [1, 2, 3]);

        store.IsDownloaded(WhisperModel.BaseEn).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureAsync_returns_an_existing_file_without_touching_the_network()
    {
        var store = new ModelStore(_dir);
        Directory.CreateDirectory(_dir);
        var path = store.PathFor(WhisperModel.TinyEn);
        File.WriteAllBytes(path, [1, 2, 3]);

        var result = await store.EnsureAsync(WhisperModel.TinyEn, null, CancellationToken.None);

        result.Should().Be(path);
        File.ReadAllBytes(path).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task EnsureAsync_with_a_cancelled_token_throws_and_leaves_no_partial_file()
    {
        var store = new ModelStore(_dir);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => store.EnsureAsync(WhisperModel.TinyEn, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(_dir).Should().BeTrue();
        Directory.GetFiles(_dir).Should().BeEmpty();
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Cutback.Analysis.Tests 2>&1 | tail -20`
Expected: build errors, `WhisperModel`, `WhisperModelInfo` and `ModelStore` do not exist.

- [ ] **Step 4: Implement `WhisperModel` and `WhisperModelInfo`**

Create `src/Cutback.Analysis/WhisperModel.cs`:

```csharp
using System.Globalization;
using Whisper.net.Ggml;

namespace Cutback.Analysis;

/// <summary>The English-only Whisper models the app offers. Larger is slower and more accurate.</summary>
public enum WhisperModel
{
    TinyEn,
    BaseEn,
    SmallEn,
    MediumEn,
}

/// <param name="Key">Stable settings key and Hugging Face name, e.g. <c>base.en</c>.</param>
/// <param name="ApproximateBytes">Download size, used only to show a progress fraction.</param>
public sealed record WhisperModelInfo(WhisperModel Model, string Key, GgmlType GgmlType, long ApproximateBytes)
{
    public static IReadOnlyList<WhisperModelInfo> All { get; } =
    [
        new(WhisperModel.TinyEn, "tiny.en", GgmlType.TinyEn, 77_700_000),
        new(WhisperModel.BaseEn, "base.en", GgmlType.BaseEn, 148_000_000),
        new(WhisperModel.SmallEn, "small.en", GgmlType.SmallEn, 487_600_000),
        new(WhisperModel.MediumEn, "medium.en", GgmlType.MediumEn, 1_533_800_000),
    ];

    public static WhisperModelInfo Default => For(WhisperModel.BaseEn);

    public string FileName => $"ggml-{Key}.bin";

    public string DisplayName => string.Create(CultureInfo.InvariantCulture, $"{Key} (~{ApproximateBytes / 1_000_000} MB)");

    public static WhisperModelInfo For(WhisperModel model)
        => All.FirstOrDefault(i => i.Model == model)
           ?? throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown Whisper model.");

    /// <summary>Case-insensitive key lookup. Anything unrecognised is the default, so a hand-edited settings file cannot break the app.</summary>
    public static WhisperModel Parse(string? key)
        => All.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase))?.Model ?? WhisperModel.BaseEn;
}
```

- [ ] **Step 5: Implement `ModelDownloadException` and `ModelStore`**

Create `src/Cutback.Analysis/ModelDownloadException.cs`:

```csharp
namespace Cutback.Analysis;

/// <summary>The speech model could not be downloaded. The message is user-facing.</summary>
public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
```

Create `src/Cutback.Analysis/ModelStore.cs`:

```csharp
using Whisper.net.Ggml;

namespace Cutback.Analysis;

/// <summary>
/// The per-user cache of downloaded Whisper models. Models are fetched from Hugging Face through
/// Whisper.net's downloader on first use and never bundled with the app.
/// </summary>
public sealed class ModelStore
{
    public ModelStore(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        CacheDirectory = cacheDirectory;
    }

    /// <summary><c>&lt;ApplicationData&gt;/Cutback/models</c>, next to the settings file.</summary>
    public static ModelStore CreateDefault()
        => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cutback", "models"));

    public string CacheDirectory { get; }

    public string PathFor(WhisperModel model) => Path.Combine(CacheDirectory, WhisperModelInfo.For(model).FileName);

    public bool IsDownloaded(WhisperModel model) => File.Exists(PathFor(model));

    /// <summary>Returns the model's path, downloading it first if it is not cached.</summary>
    /// <param name="progress">Fraction of the approximate size received, in <c>[0, 1]</c>.</param>
    /// <exception cref="ModelDownloadException">The download failed. The partial file is removed.</exception>
    /// <exception cref="OperationCanceledException">Cancelled. The partial file is removed.</exception>
    public async Task<string> EnsureAsync(WhisperModel model, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var info = WhisperModelInfo.For(model);
        var path = PathFor(model);
        if (File.Exists(path))
        {
            return path;
        }

        Directory.CreateDirectory(CacheDirectory);
        var partial = path + ".part";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = await WhisperGgmlDownloader.Default
                .GetGgmlModelAsync(info.GgmlType, QuantizationType.NoQuantization, cancellationToken)
                .ConfigureAwait(false);

            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    progress?.Report(Math.Min(1.0, (double)received / info.ApproximateBytes));
                }
            }

            File.Move(partial, path, overwrite: true);
            progress?.Report(1.0);
            return path;
        }
        catch (OperationCanceledException)
        {
            TryDelete(partial);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryDelete(partial);
            throw new ModelDownloadException(
                $"Could not download the {info.Key} speech model (about {info.ApproximateBytes / 1_000_000} MB). "
                + $"Check your internet connection and try again. Models are cached in {CacheDirectory}.\n\n{ex.Message}",
                ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort; a stale .part file is harmless and overwritten next time.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Cutback.Analysis.Tests 2>&1 | tail -5`
Expected: all pass. The cancellation test must not reach the network: `ThrowIfCancellationRequested` runs before the downloader is called.

- [ ] **Step 7: Build the whole solution and commit**

Run: `dotnet build Cutback.sln 2>&1 | tail -3`
Expected: `0 Error(s)`.

```bash
dotnet format Cutback.sln
git add Directory.Packages.props Cutback.sln src/Cutback.Analysis tests/Cutback.Analysis.Tests
git commit -m "Add Whisper.net, model metadata and the model download cache

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: `TokenTiming` and `WordAssembler`

**Files:**
- Create: `src/Cutback.Analysis/TokenTiming.cs`
- Create: `src/Cutback.Analysis/WordAssembler.cs`
- Test: `tests/Cutback.Analysis.Tests/WordAssemblerTests.cs`

**Interfaces:**
- Produces:
  - `public readonly record struct TokenTiming(string Text, double Start, double End, float Probability)` (seconds).
  - `public static IReadOnlyList<Word> WordAssembler.FromTokens(IEnumerable<TokenTiming> tokens)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Cutback.Analysis.Tests/WordAssemblerTests.cs`:

```csharp
namespace Cutback.Analysis.Tests;

public sealed class WordAssemblerTests
{
    private static TokenTiming T(string text, double start, double end, float p = 0.9f) => new(text, start, end, p);

    [Fact]
    public void A_space_prefixed_token_starts_a_new_word()
    {
        var words = WordAssembler.FromTokens([T(" Hello", 0.0, 0.3), T(" world", 0.4, 0.8)]);

        words.Select(w => (w.Text, w.Start, w.End)).Should().Equal(("Hello", 0.0, 0.3), ("world", 0.4, 0.8));
    }

    [Fact]
    public void Tokens_without_a_leading_space_continue_the_current_word()
    {
        var words = WordAssembler.FromTokens([T(" un", 0.0, 0.1), T("believ", 0.1, 0.3), T("able", 0.3, 0.5)]);

        words.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Text = "unbelievable", Start = 0.0, End = 0.5 });
    }

    [Fact]
    public void Punctuation_only_tokens_attach_to_the_current_word_even_with_a_leading_space()
    {
        var words = WordAssembler.FromTokens([T(" Hello", 0.0, 0.3), T(",", 0.3, 0.3), T(" .", 0.3, 0.35), T(" world", 0.4, 0.8)]);

        words.Select(w => w.Text).Should().Equal("Hello,.", "world");
    }

    [Fact]
    public void Control_tokens_and_blank_tokens_are_skipped()
    {
        var words = WordAssembler.FromTokens([T("[_BEG_]", 0.0, 0.0), T(" um", 0.5, 0.7), T("[_TT_35]", 0.7, 0.7), T("", 0.7, 0.7), T("  ", 0.8, 0.8)]);

        words.Should().ContainSingle().Which.Text.Should().Be("um");
    }

    [Fact]
    public void The_first_token_starts_a_word_even_without_a_space()
    {
        var words = WordAssembler.FromTokens([T("Hi", 0.0, 0.2)]);

        words.Should().ContainSingle().Which.Text.Should().Be("Hi");
    }

    [Fact]
    public void Confidence_is_the_mean_token_probability()
    {
        var words = WordAssembler.FromTokens([T(" ab", 0.0, 0.1, 0.8f), T("cd", 0.1, 0.2, 0.6f)]);

        words[0].Confidence.Should().BeApproximately(0.7, 1e-6);
    }

    [Fact]
    public void A_word_with_no_duration_gets_ten_milliseconds()
    {
        var words = WordAssembler.FromTokens([T(" x", 1.0, 1.0)]);

        words[0].End.Should().BeApproximately(1.01, 1e-9);
    }

    [Fact]
    public void Empty_input_gives_no_words()
    {
        WordAssembler.FromTokens([]).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Cutback.Analysis.Tests --filter "FullyQualifiedName~WordAssemblerTests" 2>&1 | tail -20`
Expected: build error, `TokenTiming` / `WordAssembler` do not exist.

- [ ] **Step 3: Implement**

Create `src/Cutback.Analysis/TokenTiming.cs`:

```csharp
namespace Cutback.Analysis;

/// <summary>A recogniser token with its timing in seconds. Engine-neutral so word assembly can be tested without a model.</summary>
public readonly record struct TokenTiming(string Text, double Start, double End, float Probability);
```

Create `src/Cutback.Analysis/WordAssembler.cs`:

```csharp
using Cutback.Core.Models;

namespace Cutback.Analysis;

/// <summary>
/// Groups Whisper's sub-word tokens into words. Whisper marks a word boundary with a leading
/// space on the first token of the word; punctuation-only tokens belong to the word before them;
/// control tokens such as <c>[_BEG_]</c> carry no text.
/// </summary>
public static class WordAssembler
{
    /// <summary>Shortest word the timeline will accept; a zero-length word cannot be cut.</summary>
    public const double MinWordSeconds = 0.01;

    public static IReadOnlyList<Word> FromTokens(IEnumerable<TokenTiming> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var words = new List<Word>();
        var text = new System.Text.StringBuilder();
        var start = 0.0;
        var end = 0.0;
        var probabilitySum = 0.0;
        var tokenCount = 0;

        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token.Text) || token.Text.StartsWith("[_", StringComparison.Ordinal))
            {
                continue;
            }

            var trimmed = token.Text.Trim();
            var punctuationOnly = trimmed.All(char.IsPunctuation);
            var startsWord = tokenCount > 0 && char.IsWhiteSpace(token.Text[0]) && !punctuationOnly;
            if (startsWord)
            {
                Flush();
            }

            if (tokenCount == 0)
            {
                start = token.Start;
                end = token.End;
            }

            text.Append(trimmed);
            end = Math.Max(end, token.End);
            probabilitySum += token.Probability;
            tokenCount++;
        }

        Flush();
        return words;

        void Flush()
        {
            if (tokenCount == 0)
            {
                return;
            }

            var wordText = text.ToString();
            if (wordText.Length > 0)
            {
                var wordEnd = end > start ? end : start + MinWordSeconds;
                words.Add(new Word(wordText, start, wordEnd, probabilitySum / tokenCount));
            }

            text.Clear();
            probabilitySum = 0;
            tokenCount = 0;
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Cutback.Analysis.Tests --filter "FullyQualifiedName~WordAssemblerTests" 2>&1 | tail -5`
Expected: 8 passing.

- [ ] **Step 5: Commit**

```bash
dotnet format Cutback.sln
git add src/Cutback.Analysis/TokenTiming.cs src/Cutback.Analysis/WordAssembler.cs tests/Cutback.Analysis.Tests/WordAssemblerTests.cs
git commit -m "Assemble Whisper tokens into timed words

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 11: `WhisperTranscriber`

**Files:**
- Create: `src/Cutback.Analysis/WhisperTranscriber.cs`

No unit test (loads a model). Exercised in Task 18.

**Interfaces:**
- Consumes: `ITranscriber`, `PcmExtractor`, `WordAssembler`, `TokenTiming`.
- Produces: `public sealed class WhisperTranscriber(FfmpegLocation ffmpeg, string modelPath) : ITranscriber` and `public const string WhisperTranscriber.DisfluentPrompt`.

- [ ] **Step 1: Implement**

Create `src/Cutback.Analysis/WhisperTranscriber.cs`:

```csharp
using Cutback.Core.Models;
using Cutback.Media;
using Whisper.net;

namespace Cutback.Analysis;

/// <summary>
/// Local speech-to-text with Whisper.net on the CPU. Decodes the audio with ffmpeg, runs the
/// model with token timestamps, and assembles words. Whisper was trained on transcripts with the
/// disfluencies removed, so left alone it drops most "um"s; the initial prompt is written the way
/// a transcript with fillers looks, and is carried into every window, to steer it back.
/// </summary>
public sealed class WhisperTranscriber : ITranscriber
{
    public const string DisfluentPrompt =
        "Um, so, uh, I was going to, um, show you this. Er, hmm, let me, uh, think. Ah, okay, so, um, here we go.";

    /// <summary>Share of the progress bar given to audio decoding; the rest is recognition.</summary>
    private const double DecodeShare = 0.15;

    private readonly FfmpegLocation _ffmpeg;
    private readonly string _modelPath;

    public WhisperTranscriber(FfmpegLocation ffmpeg, string modelPath)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        _ffmpeg = ffmpeg;
        _modelPath = modelPath;
    }

    public async Task<IReadOnlyList<Word>> TranscribeAsync(string mediaPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);

        var samples = await new PcmExtractor(_ffmpeg)
            .ExtractAsync(mediaPath, 0, progress is null ? null : new ScaledProgress(progress, 0, DecodeShare), cancellationToken)
            .ConfigureAwait(false);

        // whisper.cpp is synchronous and CPU-bound; keep it off the caller's thread.
        var words = await Task.Run(() => RecognizeAsync(samples, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        progress?.Report(1.0);
        return words;
    }

    private async Task<List<Word>> RecognizeAsync(ReadOnlyMemory<float> samples, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var factory = WhisperFactory.FromPath(_modelPath);
        await using var processor = factory.CreateBuilder()
            .WithLanguage("en")
            .WithTokenTimestamps()
            .WithThreads(Math.Clamp(Environment.ProcessorCount, 1, 8))
            .WithPrompt(DisfluentPrompt)
            .WithCarryInitialPrompt(true)
            .WithProgressHandler(percent => progress?.Report(DecodeShare + (1 - DecodeShare) * Math.Clamp(percent, 0, 100) / 100.0))
            .Build();

        var words = new List<Word>();
        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
        {
            if (segment.Tokens is null)
            {
                continue;
            }

            words.AddRange(WordAssembler.FromTokens(segment.Tokens.Select(t =>
                new TokenTiming(t.Text ?? string.Empty, t.Start / 100.0, t.End / 100.0, t.Probability))));
        }

        words.Sort((a, b) => a.Start.CompareTo(b.Start));
        return words;
    }

    /// <summary>Maps a child's <c>[0, 1]</c> onto <c>[offset, offset + share]</c> of the parent's.</summary>
    private sealed class ScaledProgress : IProgress<double>
    {
        private readonly IProgress<double> _parent;
        private readonly double _offset;
        private readonly double _share;

        public ScaledProgress(IProgress<double> parent, double offset, double share)
        {
            _parent = parent;
            _offset = offset;
            _share = share;
        }

        public void Report(double value) => _parent.Report(_offset + _share * Math.Clamp(value, 0, 1));
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Cutback.Analysis 2>&1 | tail -3`
Expected: `0 Error(s)`. If the analyzer objects to `t.Text ?? string.Empty` because `Text` is non-nullable, drop the `?? string.Empty`.

- [ ] **Step 3: Commit**

```bash
dotnet format Cutback.sln
git add src/Cutback.Analysis/WhisperTranscriber.cs
git commit -m "Transcribe locally with Whisper.net behind ITranscriber

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 12: Settings, Preferences, and `ModelStore` in the composition root

**Files:**
- Modify: `src/Cutback.App/Services/AppSettings.cs`
- Modify: `src/Cutback.App/Services/AppSettingsStore.cs` (`Sanitize`)
- Modify: `src/Cutback.App/ViewModels/PreferencesViewModel.cs`
- Modify: `src/Cutback.App/Views/PreferencesWindow.axaml`
- Modify: `src/Cutback.App/ViewModels/MainWindowViewModel.cs` (constructor, `ShowPreferencesAsync`)
- Modify: `src/Cutback.App/App.axaml.cs`

No automated tests (App project). Manual check at the end of the task.

**Interfaces:**
- Consumes: `WhisperModel`, `WhisperModelInfo`, `ModelStore`, `FillerDetector.DefaultWords`, `FillerDetector.Normalize`.
- Produces: `AppSettings.WhisperModel` (string key), `AppSettings.FillerWords` (`IReadOnlyList<string>`); `MainWindowViewModel` constructor gains a trailing `ModelStore models` parameter and stores it in `_models`; `PreferencesViewModel(AppSettingsStore settings, ModelStore models)`; `public sealed record SpeechModelOption(WhisperModel Model, string Label)`.

- [ ] **Step 1: Extend `AppSettings`**

In `src/Cutback.App/Services/AppSettings.cs` add `using Cutback.Core.Detection;` at the top and these members after `RecentFiles`:

```csharp
    /// <summary>Key of the Whisper model used for transcription, e.g. <c>base.en</c>. See <c>WhisperModelInfo</c>.</summary>
    public string WhisperModel { get; set; } = "base.en";

    /// <summary>Words that "Remove filler words" cuts. Matched after <see cref="FillerDetector.Normalize"/>.</summary>
    public IReadOnlyList<string> FillerWords { get; set; } = FillerDetector.DefaultWords;
```

In `src/Cutback.App/Services/AppSettingsStore.cs` add `using Cutback.Analysis;` and `using Cutback.Core.Detection;` and extend `Sanitize`:

```csharp
    /// <summary>A hand-edited file must not produce values the UI cannot represent.</summary>
    private static AppSettings Sanitize(AppSettings settings) => settings with
    {
        UndoHistoryLimit = Math.Clamp(settings.UndoHistoryLimit, AppSettings.MinUndoHistoryLimit, AppSettings.MaxUndoHistoryLimit),
        RecentFiles = settings.RecentFiles ?? [],
        WhisperModel = WhisperModelInfo.For(WhisperModelInfo.Parse(settings.WhisperModel)).Key,
        FillerWords = settings.FillerWords is { Count: > 0 } words
            ? words.Select(FillerDetector.Normalize).Where(w => w.Length > 0).Distinct(StringComparer.Ordinal).ToList()
            : FillerDetector.DefaultWords,
    };
```

- [ ] **Step 2: Thread `ModelStore` through the composition root**

In `src/Cutback.App/ViewModels/MainWindowViewModel.cs`:

Add `using Cutback.Analysis;` to the usings. Add a field after `_temp`:

```csharp
    private readonly ModelStore _models;
```

Change the constructor signature and body:

```csharp
    public MainWindowViewModel(IVideoPlayer player, IFileDialogService files, IDialogService dialogs, AppSettingsStore settings, TempSession temp, ModelStore models)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(temp);
        ArgumentNullException.ThrowIfNull(models);
        _player = player;
        _files = files;
        _dialogs = dialogs;
        _settings = settings;
        _temp = temp;
        _models = models;
```

(the rest of the constructor is unchanged). Change `ShowPreferencesAsync`:

```csharp
    [RelayCommand]
    private async Task ShowPreferencesAsync()
    {
        var preferences = new PreferencesViewModel(_settings, _models);
        await _dialogs.ShowPreferencesAsync(preferences);
        _history.Limit = _settings.Current.UndoHistoryLimit;
    }
```

In `src/Cutback.App/App.axaml.cs` add `using Cutback.Analysis;` and change the view model construction:

```csharp
            var viewModel = new MainWindowViewModel(_player, new AvaloniaFileDialogService(window), new AvaloniaDialogService(window), settings, _temp, ModelStore.CreateDefault())
            {
                ErrorMessage = startupError,
            };
```

- [ ] **Step 3: Preferences view model**

Replace `src/Cutback.App/ViewModels/PreferencesViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cutback.Analysis;
using Cutback.App.Services;
using Cutback.Core.Detection;

namespace Cutback.App.ViewModels;

/// <summary>One entry in the speech-model dropdown.</summary>
public sealed record SpeechModelOption(WhisperModel Model, string Label);

/// <summary>Drives the Preferences window. Edits are staged and written to <see cref="AppSettingsStore"/> on OK.</summary>
public sealed partial class PreferencesViewModel : ViewModelBase
{
    private readonly AppSettingsStore _settings;

    public PreferencesViewModel(AppSettingsStore settings, ModelStore models)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(models);
        _settings = settings;
        UndoHistoryLimit = settings.Current.UndoHistoryLimit;

        SpeechModels = WhisperModelInfo.All
            .Select(i => new SpeechModelOption(i.Model, i.DisplayName + (models.IsDownloaded(i.Model) ? ", downloaded" : "")))
            .ToList();
        var current = WhisperModelInfo.Parse(settings.Current.WhisperModel);
        SelectedSpeechModel = SpeechModels.First(o => o.Model == current);
        FillerWordsText = string.Join(", ", settings.Current.FillerWords);
    }

    /// <summary>Raised when the window should close, whether the changes were kept or not.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>How many edits can be undone. See <see cref="AppSettings.UndoHistoryLimit"/>.</summary>
    [ObservableProperty]
    public partial int UndoHistoryLimit { get; set; }

    public IReadOnlyList<SpeechModelOption> SpeechModels { get; }

    [ObservableProperty]
    public partial SpeechModelOption? SelectedSpeechModel { get; set; }

    /// <summary>Comma-separated. Blank entries are dropped; an empty list falls back to the default.</summary>
    [ObservableProperty]
    public partial string FillerWordsText { get; set; }

    [RelayCommand]
    private void Save()
    {
        var limit = Math.Clamp(UndoHistoryLimit, AppSettings.MinUndoHistoryLimit, AppSettings.MaxUndoHistoryLimit);
        var model = SelectedSpeechModel?.Model ?? WhisperModel.BaseEn;
        var fillers = FillerWordsText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(FillerDetector.Normalize)
            .Where(w => w.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        _settings.Update(s => s with
        {
            UndoHistoryLimit = limit,
            WhisperModel = WhisperModelInfo.For(model).Key,
            FillerWords = fillers.Count > 0 ? fillers : FillerDetector.DefaultWords,
        });
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
```

- [ ] **Step 4: Preferences window**

In `src/Cutback.App/Views/PreferencesWindow.axaml`, insert between the "Undo history" `StackPanel` and the button row:

```xml
    <StackPanel Spacing="6">
      <TextBlock Text="Speech model" />
      <ComboBox ItemsSource="{Binding SpeechModels}" SelectedItem="{Binding SelectedSpeechModel}" HorizontalAlignment="Stretch">
        <ComboBox.ItemTemplate>
          <DataTemplate x:DataType="vm:SpeechModelOption">
            <TextBlock Text="{Binding Label}" />
          </DataTemplate>
        </ComboBox.ItemTemplate>
      </ComboBox>
      <TextBlock Classes="dim" FontSize="12" TextWrapping="Wrap" Text="Used by Transcribe. Larger models are more accurate and slower. A model is downloaded the first time it is used." />
    </StackPanel>

    <StackPanel Spacing="6">
      <TextBlock Text="Filler words" />
      <TextBox Text="{Binding FillerWordsText}" />
      <TextBlock Classes="dim" FontSize="12" TextWrapping="Wrap" Text="Comma-separated. Remove filler words cuts every transcript word that matches one of these." />
    </StackPanel>
```

- [ ] **Step 5: Build and check by hand**

Run: `dotnet build Cutback.sln 2>&1 | tail -3`
Expected: `0 Error(s)`.

Run: `dotnet run --project src/Cutback.App`, open File → Preferences, change the model to `small.en`, add `like` to the filler list, OK. Then `cat ~/Library/Application\ Support/Cutback/settings.json` (macOS; `%APPDATA%\Cutback\settings.json` on Windows, `~/.config/Cutback/settings.json` on Linux) and confirm `"whisperModel": "small.en"` and `"fillerWords"` ending with `"like"`. Set the model back to `base.en` and remove `like`.

- [ ] **Step 6: Commit**

```bash
dotnet format Cutback.sln
git add src/Cutback.App
git commit -m "Add speech model and filler word preferences

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 13: Transcribe command, transcript state and a cancellable busy bar

**Files:**
- Modify: `src/Cutback.App/ViewModels/MainWindowViewModel.cs`
- Modify: `src/Cutback.App/Views/MainWindow.axaml` (status bar, Edit menu)

**Interfaces:**
- Consumes: `ModelStore.EnsureAsync`, `ModelStore.IsDownloaded`, `WhisperTranscriber`, `ITranscriber`, `TranscriptView.IndexAtTime`, `ModelDownloadException`.
- Produces on the view model: `IReadOnlyList<Word> Transcript`, `bool HasTranscript`, `bool IsTranscriptOpen`, `bool IsTranscriptStale`, `int CurrentWordIndex`, `TranscribeCommand`, `ToggleTranscriptCommand`, `CancelBusyCommand`, `bool CanCancelBusy`, and `private Task<bool> TranscribeCoreAsync()` for Task 14.

- [ ] **Step 1: Busy state becomes cancellable**

In `MainWindowViewModel.cs` add a field after `_openCts`:

```csharp
    private CancellationTokenSource? _busyCts;
```

In the `// ---- busy ----` section, after `IsBusyIndeterminate`, add:

```csharp
    /// <summary>True while the running operation can be stopped from the status bar.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelBusyCommand))]
    public partial bool CanCancelBusy { get; private set; }

    [RelayCommand(CanExecute = nameof(CanCancelBusy))]
    private void CancelBusy() => _busyCts?.Cancel();
```

Replace `BeginBusy` and `EndBusy` in the helpers section:

```csharp
    private void BeginBusy(string message, bool cancellable = false)
    {
        BusyMessage = message;
        BusyProgress = double.NaN;
        _busyCts = cancellable ? new CancellationTokenSource() : null;
        CanCancelBusy = cancellable;
        IsBusy = true;
    }

    private void EndBusy()
    {
        IsBusy = false;
        CanCancelBusy = false;
        _busyCts?.Dispose();
        _busyCts = null;
        BusyMessage = null;
        BusyProgress = double.NaN;
    }
```

- [ ] **Step 2: Transcript state**

Add `using Cutback.Core.Transcript;` to the usings. Add a new section after `// ---- detection settings ----`'s last member (`LoadSettings`), before `// ---- messages ----`:

```csharp
    // ---- transcript ---------------------------------------------------------------------------

    /// <summary>Mirrors <c>Project.Transcript</c> for binding. Empty until the project is transcribed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranscript))]
    public partial IReadOnlyList<Word> Transcript { get; private set; } = [];

    public bool HasTranscript => Transcript.Count > 0;

    [ObservableProperty]
    public partial bool IsTranscriptOpen { get; set; }

    /// <summary>The source file changed after this transcript was made (the hash warning fired on open).</summary>
    [ObservableProperty]
    public partial bool IsTranscriptStale { get; private set; }

    /// <summary>Word under the playhead, or -1. Highlighted in the transcript panel.</summary>
    [ObservableProperty]
    public partial int CurrentWordIndex { get; private set; } = -1;

    [RelayCommand]
    private void ToggleTranscript() => IsTranscriptOpen = !IsTranscriptOpen;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task TranscribeAsync()
    {
        if (await TranscribeCoreAsync())
        {
            IsTranscriptOpen = true;
        }
    }

    /// <summary>
    /// Downloads the chosen model if needed, then transcribes the source. Both phases are
    /// cancellable from the status bar. Returns true if a non-empty transcript is now loaded.
    /// </summary>
    private async Task<bool> TranscribeCoreAsync()
    {
        if (Project is null)
        {
            return false;
        }

        var model = WhisperModelInfo.Parse(_settings.Current.WhisperModel);
        ErrorMessage = null;
        BeginBusy(_models.IsDownloaded(model) ? "Transcribing…" : $"Downloading the {WhisperModelInfo.For(model).Key} speech model…", cancellable: true);
        try
        {
            var ct = _busyCts!.Token;
            var ffmpeg = ResolveFfmpeg();
            var progress = new Progress<double>(p => BusyProgress = p);

            var modelPath = await _models.EnsureAsync(model, progress, ct);
            BusyMessage = "Transcribing…";
            BusyProgress = 0;

            ITranscriber transcriber = new WhisperTranscriber(ffmpeg, modelPath);
            var words = await transcriber.TranscribeAsync(Project.Source.Path, progress, ct);

            Project = Project with { Transcript = words };
            Transcript = words;
            IsTranscriptStale = false;
            CurrentWordIndex = TranscriptView.IndexAtTime(words, PositionSeconds);
            MarkDirty();
            StatusMessage = words.Count == 0 ? "No speech was recognised." : $"Transcribed {words.Count} words.";
            return words.Count > 0;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Transcription cancelled.";
            return false;
        }
        catch (Exception ex) when (ex is FfmpegNotFoundException or FfmpegException or ModelDownloadException or IOException)
        {
            ErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            EndBusy();
        }
    }
```

- [ ] **Step 3: Keep the transcript in step with open, new and playback**

In `RunOpenAsync`, after `Segments.Changed += (_, _) => MarkDirty();` add:

```csharp
            Transcript = project.Transcript;
            IsTranscriptStale = warning is not null && project.Transcript.Count > 0;
            CurrentWordIndex = -1;
```

In `NewProjectAsync`, after `Waveform = null;` add:

```csharp
        Transcript = [];
        IsTranscriptStale = false;
        CurrentWordIndex = -1;
```

In `OnPlayerPosition`, directly after `PositionSeconds = seconds;` add:

```csharp
        CurrentWordIndex = TranscriptView.IndexAtTime(Transcript, seconds);
```

Add `nameof(TranscribeCommand)` to the `[NotifyCanExecuteChangedFor(...)]` lists on both the `Project` property and the `IsBusy` property.

- [ ] **Step 4: Status-bar cancel button and Edit menu item**

In `MainWindow.axaml` replace the status/progress `Border` (the last `Grid.Row="6"` block) with:

```xml
    <!-- Status / progress -->
    <Border Grid.Row="6" Background="#242429" Padding="10,4" MinHeight="28">
      <Grid ColumnDefinitions="Auto,*,Auto">
        <TextBlock Grid.Column="0" Classes="dim" VerticalAlignment="Center" Text="{Binding BusyMessage}" IsVisible="{Binding IsBusy}" />
        <TextBlock Grid.Column="0" Classes="dim" VerticalAlignment="Center" Text="{Binding StatusMessage}" IsVisible="{Binding !IsBusy}" />
        <ProgressBar Grid.Column="1" Margin="12,0,0,0" Height="6" VerticalAlignment="Center"
                     IsVisible="{Binding IsBusy}"
                     IsIndeterminate="{Binding IsBusyIndeterminate}"
                     Minimum="0" Maximum="1" Value="{Binding BusyProgress}" />
        <Button Grid.Column="2" Content="Cancel" Command="{Binding CancelBusyCommand}" IsVisible="{Binding CanCancelBusy}"
                Margin="12,0,0,0" Padding="10,2" VerticalAlignment="Center" />
      </Grid>
    </Border>
```

In the Edit menu, after the Redo item:

```xml
        <Separator />
        <MenuItem Header="_Transcribe" Command="{Binding TranscribeCommand}" />
```

- [ ] **Step 5: Build and check by hand**

Run: `dotnet build Cutback.sln 2>&1 | tail -3`
Expected: `0 Error(s)`.

Run the app, open a short recording with speech, Edit → Transcribe. Expected: the status bar shows the model download with a progress fraction and a Cancel button on first run, then "Transcribing…" with progress, then "Transcribed N words." and the title gains the dirty marker. Save the project and confirm `"transcript"` in the `.cutback` file is non-empty. Run Transcribe again and press Cancel: status reads "Transcription cancelled." and the app stays responsive.

- [ ] **Step 6: Commit**

```bash
dotnet format Cutback.sln
git add src/Cutback.App
git commit -m "Transcribe the source with Whisper and keep the transcript in the project

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 14: Remove filler words command

**Files:**
- Modify: `src/Cutback.App/ViewModels/MainWindowViewModel.cs`
- Modify: `src/Cutback.App/Views/MainWindow.axaml` (toolbar, Edit menu)

**Interfaces:**
- Consumes: `FillerDetector.Find/Normalize`, `FillerSpan`, `FillerCutPlanner.Plan`, `SegmentList.ApplyFillerCuts`, `Waveform.SnapToZeroCrossing`, `TranscribeCoreAsync`.
- Produces: `RemoveFillerWordsCommand`; `private const double WordSnapWindowSeconds = 0.040` (reused by Task 16); `private (double Start, double End) SnapWordSpan(double start, double end)`.

- [ ] **Step 1: Implement the command**

In `MainWindowViewModel.cs`, in the `// ---- detection ----` section after `DetectSilenceAsync`, add:

```csharp
    /// <summary>Whisper word timing is coarser than a hand-placed boundary, so word edges get a wider snap window than a drag.</summary>
    private const double WordSnapWindowSeconds = 0.040;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task RemoveFillerWordsAsync()
    {
        if (Project is null || Segments is null)
        {
            return;
        }

        if (!HasTranscript && !await TranscribeCoreAsync())
        {
            return;
        }

        var fillers = FillerDetector.Find(Transcript, _settings.Current.FillerWords);
        var spans = fillers.Select(w =>
        {
            var (start, end) = SnapWordSpan(w.Start, w.End);
            return new FillerSpan(start, end, FillerDetector.Normalize(w.Text));
        });
        var cuts = FillerCutPlanner.Plan(spans, Segments.Segments, CurrentSettings, DurationSeconds);

        Edit(s => s.ApplyFillerCuts(cuts));
        IsTranscriptOpen = true;

        var removed = cuts.Sum(c => c.Duration);
        StatusMessage = cuts.Count switch
        {
            0 when fillers.Count == 0 => "No filler words found.",
            0 => "Every filler word is already cut.",
            1 => $"Removed 1 filler word, {TimeFormat.Clock(removed)} cut.",
            _ => $"Removed {cuts.Count} filler words, {TimeFormat.Clock(removed)} cut.",
        };
    }

    /// <summary>Snaps both edges of a word to the quietest nearby waveform bucket, falling back to the raw edges if snapping would collapse the word.</summary>
    private (double Start, double End) SnapWordSpan(double start, double end)
    {
        if (Waveform is not { } waveform)
        {
            return (start, end);
        }

        var snappedStart = waveform.SnapToZeroCrossing(start, WordSnapWindowSeconds);
        var snappedEnd = waveform.SnapToZeroCrossing(end, WordSnapWindowSeconds);
        return snappedEnd - snappedStart < 0.02 ? (start, end) : (snappedStart, snappedEnd);
    }
```

Add `nameof(RemoveFillerWordsCommand)` to the `[NotifyCanExecuteChangedFor(...)]` lists on the `Project` and `IsBusy` properties.

- [ ] **Step 2: Toolbar and menu**

In `MainWindow.axaml` toolbar `StackPanel`, after the "Detection settings" toggle:

```xml
        <Button Classes="toolbar" Command="{Binding RemoveFillerWordsCommand}" Content="Remove filler words" />
```

In the Edit menu after the Transcribe item:

```xml
        <MenuItem Header="Remove _Filler Words" Command="{Binding RemoveFillerWordsCommand}" />
```

- [ ] **Step 3: Build and check by hand**

Run: `dotnet build Cutback.sln 2>&1 | tail -3`
Expected: `0 Error(s)`.

Run the app with a recording that contains a few "um"s. Click "Remove filler words". Expected: short grey regions appear on the timeline where the fillers are, the status reads "Removed N filler words, …", and hovering a region's context menu still offers Delete. Press Cmd/Ctrl+Z once: every filler cut disappears (one undo step). Redo, then "Detect silence": the filler cuts survive. Run "Remove filler words" again: no duplicate regions.

- [ ] **Step 4: Commit**

```bash
dotnet format Cutback.sln
git add src/Cutback.App
git commit -m "Remove filler words from the timeline in one step

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 15: `TranscriptControl`

**Files:**
- Create: `src/Cutback.App/Controls/WordRange.cs`
- Create: `src/Cutback.App/Controls/TranscriptControl.cs`

No automated tests (Avalonia view). Exercised by hand in Task 16.

**Interfaces:**
- Consumes: `Word`, `SegmentList` (`Segments`, `Changed`), `TranscriptView.CutStates`.
- Produces:
  - `public sealed record WordRange(int First, int Last, int Anchor)` — inclusive word indices; `Anchor` is the word the gesture started on.
  - `TranscriptControl` styled properties: `Words` (`IReadOnlyList<Word>?`), `Segments` (`SegmentList?`), `CurrentWordIndex` (`int`, -1), `CutWordsCommand` (`ICommand?`, parameter `WordRange`), `SeekToWordCommand` (`ICommand?`, parameter `int`).

- [ ] **Step 1: `WordRange`**

Create `src/Cutback.App/Controls/WordRange.cs`:

```csharp
namespace Cutback.App.Controls;

/// <summary>
/// A run of transcript words the user acted on, as inclusive indices. <paramref name="Anchor"/> is
/// the word under the press point; its state decides whether the run is cut or restored.
/// </summary>
public sealed record WordRange(int First, int Last, int Anchor);
```

- [ ] **Step 2: The control**

Create `src/Cutback.App/Controls/TranscriptControl.cs`:

```csharp
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Cutback.Core;
using Cutback.Core.Models;
using Cutback.Core.Transcript;

namespace Cutback.App.Controls;

/// <summary>
/// The transcript as flowing text. Cut words are struck through and dimmed; the word under the
/// playhead is highlighted.
/// </summary>
/// <remarks>
/// Interactions:
/// <list type="bullet">
/// <item>Click a word: cut it, or restore it if it is cut.</item>
/// <item>Drag across words: cut or restore the whole run, decided by the word the drag started on.</item>
/// <item>Cmd/Ctrl+click a word: play from it. Right-click: context menu with Play from here and Cut / Restore.</item>
/// </list>
/// One <see cref="TextLayout"/> holds the whole transcript, with a style override per cut word,
/// and hit-testing goes through the layout, so there is one visual for thousands of words.
/// </remarks>
public sealed class TranscriptControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<Word>?> WordsProperty =
        AvaloniaProperty.Register<TranscriptControl, IReadOnlyList<Word>?>(nameof(Words));

    public static readonly StyledProperty<SegmentList?> SegmentsProperty =
        AvaloniaProperty.Register<TranscriptControl, SegmentList?>(nameof(Segments));

    public static readonly StyledProperty<int> CurrentWordIndexProperty =
        AvaloniaProperty.Register<TranscriptControl, int>(nameof(CurrentWordIndex), -1);

    public static readonly StyledProperty<ICommand?> CutWordsCommandProperty =
        AvaloniaProperty.Register<TranscriptControl, ICommand?>(nameof(CutWordsCommand));

    public static readonly StyledProperty<ICommand?> SeekToWordCommandProperty =
        AvaloniaProperty.Register<TranscriptControl, ICommand?>(nameof(SeekToWordCommand));

    private const double FontSize = 14;
    private const double LineHeight = 24;
    private const double DragThreshold = 4;

    private static readonly Typeface Font = new(FontFamily.Default);
    private static readonly IBrush KeptBrush = new SolidColorBrush(Color.Parse("#E4E4EA"));
    private static readonly IBrush CutBrush = new SolidColorBrush(Color.Parse("#6A6A75"));
    private static readonly IBrush CurrentBrush = new SolidColorBrush(Color.Parse("#3A5A8C"));
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.Parse("#4F7BD1"), 0.55);

    private string _text = string.Empty;
    private int[] _wordStarts = [];
    private int[] _wordLengths = [];
    private bool[] _cutStates = [];
    private TextLayout? _layout;
    private double _layoutWidth = -1;
    private SegmentList? _subscribed;

    private int _pressWord = -1;
    private int _selectionAnchor = -1;
    private int _selectionEnd = -1;
    private bool _dragging;
    private Point _pressPoint;
    private int _contextWord = -1;
    private readonly MenuItem _playItem;
    private readonly MenuItem _cutItem;

    static TranscriptControl()
    {
        AffectsRender<TranscriptControl>(CurrentWordIndexProperty);
        FocusableProperty.OverrideDefaultValue<TranscriptControl>(true);
    }

    public TranscriptControl()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);

        // Built in code so the target word can be resolved from the pointer position when the
        // menu is requested, like TimelineControl's menu.
        _playItem = new MenuItem { Header = "Play from here" };
        _playItem.Click += (_, _) => Seek(_contextWord);
        _cutItem = new MenuItem { Header = "Cut" };
        _cutItem.Click += (_, _) => CutWords(_contextWord, _contextWord, _contextWord);
        ContextFlyout = new MenuFlyout { Items = { _playItem, _cutItem } };
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
    }

    public IReadOnlyList<Word>? Words
    {
        get => GetValue(WordsProperty);
        set => SetValue(WordsProperty, value);
    }

    public SegmentList? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public int CurrentWordIndex
    {
        get => GetValue(CurrentWordIndexProperty);
        set => SetValue(CurrentWordIndexProperty, value);
    }

    public ICommand? CutWordsCommand
    {
        get => GetValue(CutWordsCommandProperty);
        set => SetValue(CutWordsCommandProperty, value);
    }

    public ICommand? SeekToWordCommand
    {
        get => GetValue(SeekToWordCommandProperty);
        set => SetValue(SeekToWordCommandProperty, value);
    }

    // ---- property changes ---------------------------------------------------------------------

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WordsProperty)
        {
            RebuildText();
        }
        else if (change.Property == SegmentsProperty)
        {
            if (_subscribed is not null)
            {
                _subscribed.Changed -= OnSegmentsChanged;
            }

            _subscribed = change.GetNewValue<SegmentList?>();
            if (_subscribed is not null)
            {
                _subscribed.Changed += OnSegmentsChanged;
            }

            RefreshCutStates();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_subscribed is not null)
        {
            _subscribed.Changed -= OnSegmentsChanged;
            _subscribed = null;
        }
    }

    private void OnSegmentsChanged(object? sender, EventArgs e) => RefreshCutStates();

    private void RebuildText()
    {
        var words = Words ?? [];
        _wordStarts = new int[words.Count];
        _wordLengths = new int[words.Count];
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            _wordStarts[i] = sb.Length;
            _wordLengths[i] = words[i].Text.Length;
            sb.Append(words[i].Text);
        }

        _text = sb.ToString();
        _cutStates = new bool[words.Count];
        _selectionAnchor = _selectionEnd = _pressWord = -1;
        _layout = null;
        RefreshCutStates();
        InvalidateMeasure();
    }

    /// <summary>Recomputes which words are cut; only rebuilds the layout when a word's state actually changed.</summary>
    private void RefreshCutStates()
    {
        var words = Words ?? [];
        var states = Segments is { } segments ? TranscriptView.CutStates(words, segments.Segments) : new bool[words.Count];
        if (states.AsSpan().SequenceEqual(_cutStates))
        {
            return;
        }

        _cutStates = states;
        _layout = null;
        InvalidateVisual();
    }

    // ---- layout -------------------------------------------------------------------------------

    private TextLayout EnsureLayout(double width)
    {
        var maxWidth = double.IsFinite(width) && width > 0 ? width : double.PositiveInfinity;
        if (_layout is not null && _layoutWidth == maxWidth)
        {
            return _layout;
        }

        var cutProperties = new GenericTextRunProperties(
            Font,
            fontRenderingEmSize: FontSize,
            textDecorations: TextDecorations.Strikethrough,
            foregroundBrush: CutBrush);
        var overrides = new List<ValueSpan<TextRunProperties>>();
        for (var i = 0; i < _wordStarts.Length; i++)
        {
            if (_cutStates[i])
            {
                overrides.Add(new ValueSpan<TextRunProperties>(_wordStarts[i], _wordLengths[i], cutProperties));
            }
        }

        _layout = new TextLayout(
            _text,
            Font,
            FontSize,
            KeptBrush,
            textWrapping: TextWrapping.Wrap,
            maxWidth: maxWidth,
            lineHeight: LineHeight,
            textStyleOverrides: overrides);
        _layoutWidth = maxWidth;
        return _layout;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_wordStarts.Length == 0)
        {
            return new Size(0, 0);
        }

        var layout = EnsureLayout(availableSize.Width);
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : layout.Width;
        return new Size(width, layout.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        EnsureLayout(finalSize.Width);
        return finalSize;
    }

    // ---- rendering ----------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_wordStarts.Length == 0)
        {
            return;
        }

        var layout = EnsureLayout(Bounds.Width);

        if (_selectionAnchor >= 0 && _selectionEnd >= 0)
        {
            foreach (var rect in WordRects(layout, Math.Min(_selectionAnchor, _selectionEnd), Math.Max(_selectionAnchor, _selectionEnd)))
            {
                context.FillRectangle(SelectionBrush, rect);
            }
        }

        var current = CurrentWordIndex;
        if (current >= 0 && current < _wordStarts.Length)
        {
            foreach (var rect in WordRects(layout, current, current))
            {
                context.FillRectangle(CurrentBrush, rect);
            }
        }

        layout.Draw(context, new Point(0, 0));
    }

    private IEnumerable<Rect> WordRects(TextLayout layout, int first, int last)
    {
        var start = _wordStarts[first];
        var length = _wordStarts[last] + _wordLengths[last] - start;
        return layout.HitTestTextRange(start, length).Select(r => r.Inflate(new Thickness(2, 1)));
    }

    // ---- hit testing --------------------------------------------------------------------------

    /// <summary>Word at a point, or -1 with no words. Whitespace resolves to the word before it.</summary>
    private int WordAt(Point p)
    {
        if (_wordStarts.Length == 0)
        {
            return -1;
        }

        var position = EnsureLayout(Bounds.Width).HitTestPoint(p).TextPosition;
        int lo = 0, hi = _wordStarts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (_wordStarts[mid] <= position)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    // ---- pointer ------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var word = WordAt(point.Position);
        if (word < 0)
        {
            return;
        }

        Focus();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            Seek(word);
            e.Handled = true;
            return;
        }

        _pressWord = word;
        _pressPoint = point.Position;
        _dragging = false;
        _selectionAnchor = _selectionEnd = word;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressWord < 0)
        {
            return;
        }

        var p = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _pressPoint.X) < DragThreshold && Math.Abs(p.Y - _pressPoint.Y) < DragThreshold)
            {
                return;
            }

            _dragging = true;
        }

        var word = WordAt(p);
        if (word >= 0 && word != _selectionEnd)
        {
            _selectionEnd = word;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressWord < 0)
        {
            return;
        }

        var first = Math.Min(_selectionAnchor, _selectionEnd);
        var last = Math.Max(_selectionAnchor, _selectionEnd);
        var anchor = _pressWord;

        _pressWord = _selectionAnchor = _selectionEnd = -1;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;

        CutWords(first, last, anchor);
        InvalidateVisual();
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        _contextWord = e.TryGetPosition(this, out var p) ? WordAt(p) : -1;
        if (_contextWord < 0)
        {
            e.Handled = true;
            return;
        }

        _cutItem.Header = _cutStates[_contextWord] ? "Restore" : "Cut";
    }

    // ---- commands -----------------------------------------------------------------------------

    private void CutWords(int first, int last, int anchor)
    {
        if (first < 0 || last < 0 || anchor < 0)
        {
            return;
        }

        var range = new WordRange(first, last, anchor);
        if (CutWordsCommand?.CanExecute(range) == true)
        {
            CutWordsCommand.Execute(range);
        }
    }

    private void Seek(int word)
    {
        if (word >= 0 && SeekToWordCommand?.CanExecute(word) == true)
        {
            SeekToWordCommand.Execute(word);
        }
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Cutback.App 2>&1 | tail -3`
Expected: `0 Error(s)`. If the compiler rejects the `GenericTextRunProperties` or `TextLayout` named arguments, open the Avalonia 11.3 signatures (`~/.nuget/packages/avalonia/11.3.*/ref/net8.0/Avalonia.Base.xml`, search `GenericTextRunProperties.#ctor` and `TextLayout.#ctor`) and match the parameter names; the intent is: typeface, em size, strike-through decoration, dim foreground for the override, and wrap, max width, line height, style overrides for the layout.

- [ ] **Step 4: Commit**

```bash
dotnet format Cutback.sln
git add src/Cutback.App/Controls/WordRange.cs src/Cutback.App/Controls/TranscriptControl.cs
git commit -m "Add the transcript control with struck-through cut words

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 16: Transcript panel, word commands, toolbar and View menu

**Files:**
- Modify: `src/Cutback.App/ViewModels/MainWindowViewModel.cs`
- Modify: `src/Cutback.App/Views/MainWindow.axaml`

**Interfaces:**
- Consumes: `WordRange`, `TranscriptView.IsCut`, `SnapWordSpan` (Task 14), `SegmentList.SetRange(start, end, enabled)`.
- Produces: `CutWordsCommand` (`WordRange`), `SeekToWordCommand` (`int`).

- [ ] **Step 1: View model commands**

In `MainWindowViewModel.cs`, in the `// ---- editing ----` section after `CreateSection`, add:

```csharp
    /// <summary>
    /// A click or drag in the transcript. The run takes the opposite state of the anchor word:
    /// clicking a kept word cuts it, clicking a struck word restores it. Marked manual, like a
    /// section drawn on the timeline, and one undo step.
    /// </summary>
    [RelayCommand]
    private void CutWords(WordRange range)
    {
        if (Segments is null || Transcript.Count == 0)
        {
            return;
        }

        var lastIndex = Transcript.Count - 1;
        var first = Math.Clamp(Math.Min(range.First, range.Last), 0, lastIndex);
        var last = Math.Clamp(Math.Max(range.First, range.Last), 0, lastIndex);
        var anchor = Math.Clamp(range.Anchor, first, last);

        var enabled = TranscriptView.IsCut(Transcript[anchor], Segments.Segments);
        var (start, end) = SnapWordSpan(Transcript[first].Start, Transcript[last].End);
        Edit(s => s.SetRange(start, end, enabled));
    }

    /// <summary>Cmd/Ctrl+click or "Play from here" in the transcript.</summary>
    [RelayCommand]
    private void SeekToWord(int index)
    {
        if (index >= 0 && index < Transcript.Count)
        {
            Seek(Transcript[index].Start);
        }
    }
```

- [ ] **Step 2: The panel**

In `MainWindow.axaml`, change the video row grid to three columns:

```xml
    <Grid Grid.Row="3" ColumnDefinitions="*,Auto,Auto">
```

and after the detection settings `Border` (which stays at `Grid.Column="1"`), before the closing `</Grid>` of that row, add:

```xml
      <!-- Transcript -->
      <Border Grid.Column="2" Width="340" Background="#202024" BorderBrush="#2E2E35" BorderThickness="1,0,0,0"
              IsVisible="{Binding IsTranscriptOpen}">
        <Grid RowDefinitions="Auto,Auto,*" Margin="16">
          <StackPanel Grid.Row="0" Spacing="8">
            <TextBlock Text="Transcript" FontWeight="SemiBold" FontSize="15" />
            <TextBlock Classes="dim" TextWrapping="Wrap" FontSize="12"
                       Text="Click a word to cut or restore it. Drag to select a run. Cmd/Ctrl+click to play from a word." />
            <Button Content="Remove filler words" Command="{Binding RemoveFillerWordsCommand}" IsVisible="{Binding HasTranscript}" />
          </StackPanel>

          <Border Grid.Row="1" Background="#5A4A1E" Padding="10,8" Margin="0,10,0,0" IsVisible="{Binding IsTranscriptStale}">
            <StackPanel Spacing="6">
              <TextBlock TextWrapping="Wrap" FontSize="12" Foreground="#FFF1C8"
                         Text="The source video changed since this transcript was made. Cut words may not line up." />
              <Button Content="Transcribe again" Command="{Binding TranscribeCommand}" />
            </StackPanel>
          </Border>

          <StackPanel Grid.Row="2" Spacing="10" VerticalAlignment="Center" IsVisible="{Binding !HasTranscript}">
            <TextBlock Text="No transcript yet" HorizontalAlignment="Center" />
            <Button Content="Transcribe" Command="{Binding TranscribeCommand}" HorizontalAlignment="Center" />
            <TextBlock Classes="dim" TextWrapping="Wrap" FontSize="12" TextAlignment="Center"
                       Text="Runs on this computer. The first run downloads a ~150 MB speech model." />
          </StackPanel>

          <ScrollViewer Grid.Row="2" Margin="0,12,0,0" IsVisible="{Binding HasTranscript}" HorizontalScrollBarVisibility="Disabled">
            <controls:TranscriptControl Words="{Binding Transcript}"
                                        Segments="{Binding Segments}"
                                        CurrentWordIndex="{Binding CurrentWordIndex}"
                                        CutWordsCommand="{Binding CutWordsCommand}"
                                        SeekToWordCommand="{Binding SeekToWordCommand}" />
          </ScrollViewer>
        </Grid>
      </Border>
```

- [ ] **Step 3: Toolbar toggle and View menu**

In the toolbar `StackPanel`, after the "Remove filler words" button from Task 14:

```xml
        <ToggleButton Classes="toolbar" IsChecked="{Binding IsTranscriptOpen}" Content="Transcript" />
```

In the `Menu`, after the Edit `MenuItem`:

```xml
      <MenuItem Header="_View">
        <MenuItem Header="_Transcript" ToggleType="CheckBox" IsChecked="{Binding IsTranscriptOpen, Mode=TwoWay}" />
      </MenuItem>
```

- [ ] **Step 4: Build and check by hand**

Run: `dotnet build Cutback.sln 2>&1 | tail -3`
Expected: `0 Error(s)`.

Run the app, open a transcribed project (or transcribe one). Check:
1. Toolbar "Transcript" opens the panel to the right of the video; the View menu item shows a check mark and toggles it too.
2. Words that fall in grey timeline regions are struck through and dim. "Remove filler words" strikes the "um"s.
3. Click a kept word: it becomes struck and a short grey region appears on the timeline. Click it again: restored. Each is one undo step.
4. Drag across five words starting on a kept word: all five are struck as one region. Drag starting on a struck word: the run is restored.
5. Play: the highlighted word follows the audio.
6. Cmd/Ctrl+click a word: the playhead jumps to it. Right-click: "Play from here" and "Cut" / "Restore".
7. Toggle a region on the timeline: the transcript updates immediately.
8. Resize the window: text reflows, no horizontal scroll bar.

- [ ] **Step 5: Commit**

```bash
dotnet format Cutback.sln
git add src/Cutback.App
git commit -m "Show the transcript beside the video and edit cuts from it

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 17: Export Transcript

**Files:**
- Modify: `src/Cutback.App/Services/IFileDialogService.cs`
- Modify: `src/Cutback.App/Services/AvaloniaFileDialogService.cs`
- Modify: `src/Cutback.App/ViewModels/MainWindowViewModel.cs`
- Modify: `src/Cutback.App/Views/MainWindow.axaml` (File menu)
- Modify: `src/Cutback.App/Views/MainWindow.axaml.cs` (shortcut hint)

**Interfaces:**
- Consumes: `TranscriptExporter.ToPlainText/ToSrt`, `NormalizeExtension`.
- Produces: `IFileDialogService.PickTranscriptTargetAsync(string suggestedName)`; `ExportTranscriptCommand`.

- [ ] **Step 1: File picker**

In `IFileDialogService.cs` add:

```csharp
    /// <param name="suggestedName">Default file name, without directory or extension.</param>
    /// <returns>The chosen <c>.txt</c> or <c>.srt</c> path, or null if the user cancelled.</returns>
    Task<string?> PickTranscriptTargetAsync(string suggestedName);
```

In `AvaloniaFileDialogService.cs` add:

```csharp
    public async Task<string?> PickTranscriptTargetAsync(string suggestedName)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export transcript",
            SuggestedFileName = suggestedName,
            DefaultExtension = "txt",
            FileTypeChoices =
            [
                new FilePickerFileType("Plain text") { Patterns = ["*.txt"] },
                new FilePickerFileType("SubRip subtitles") { Patterns = ["*.srt"] },
            ],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }
```

- [ ] **Step 2: The command**

In `MainWindowViewModel.cs` add `using System.Text;` to the usings and, in the `// ---- export ----` section after `ExportAsync`, add:

```csharp
    private bool CanExportTranscript => HasProject && HasTranscript && !IsBusy;

    /// <summary>Writes the edited transcript: cut words omitted, times matching the exported video. Format follows the extension.</summary>
    [RelayCommand(CanExecute = nameof(CanExportTranscript))]
    private async Task ExportTranscriptAsync()
    {
        if (Project is null || Segments is null || !HasTranscript)
        {
            return;
        }

        var path = await _files.PickTranscriptTargetAsync(ProjectName);
        if (path is null)
        {
            return;
        }

        var isSrt = string.Equals(Path.GetExtension(path), ".srt", StringComparison.OrdinalIgnoreCase);
        if (!isSrt)
        {
            path = NormalizeExtension(path, ".txt");
        }

        var segments = Segments.Segments;
        var text = isSrt
            ? TranscriptExporter.ToSrt(Transcript, segments)
            : TranscriptExporter.ToPlainText(Transcript, segments);

        try
        {
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), CancellationToken.None);
            StatusMessage = $"Exported {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Could not write the transcript.\n\n{ex.Message}";
        }
    }
```

Add `nameof(ExportTranscriptCommand)` to the `[NotifyCanExecuteChangedFor(...)]` lists on the `Project` and `IsBusy` properties, and add `[NotifyCanExecuteChangedFor(nameof(ExportTranscriptCommand))]` to the `Transcript` property (alongside its existing `NotifyPropertyChangedFor`).

- [ ] **Step 3: Menu item and shortcut hint**

In `MainWindow.axaml` File menu, after the Export item:

```xml
        <MenuItem x:Name="ExportTranscriptMenuItem" Header="Export _Transcript…" Command="{Binding ExportTranscriptCommand}" />
```

In `Window.KeyBindings` add:

```xml
    <KeyBinding Gesture="Cmd+Shift+E" Command="{Binding ExportTranscriptCommand}" />
    <KeyBinding Gesture="Ctrl+Shift+E" Command="{Binding ExportTranscriptCommand}" />
```

In `MainWindow.axaml.cs` `ShowShortcutHints`, after the `ExportMenuItem` line:

```csharp
        ExportTranscriptMenuItem.InputGesture = KeyGesture.Parse($"{mod}+Shift+E");
```

- [ ] **Step 4: Build and check by hand**

Run: `dotnet build Cutback.sln 2>&1 | tail -3`
Expected: `0 Error(s)`.

Run the app with a transcribed project that has at least one cut word. File → Export Transcript…, save as `.txt`: open it, cut words are absent and pauses over two seconds are paragraph breaks. Export again as `.srt`, export the video with File → Export…, then in VLC open the video and add the `.srt` as a subtitle track: cues line up with the speech after the cut. Confirm the menu item is disabled when there is no transcript.

- [ ] **Step 5: Commit**

```bash
dotnet format Cutback.sln
git add src/Cutback.App
git commit -m "Export the edited transcript as text or SRT

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 18: Documentation, licence notices, and the published-build smoke test

**Files:**
- Modify: `CLAUDE.md`
- Modify: `THIRD-PARTY-NOTICES.md`

- [ ] **Step 1: `CLAUDE.md` non-negotiable 7**

Replace item 7 under "Non-negotiables" with:

```markdown
7. **Claude analysis is out of scope for the current phase.** `ICutSuggester` stays a stub. Speech
   recognition (Phase 2) is implemented in `Cutback.Analysis` with Whisper.net and runs entirely on
   the user's machine; the only network access in the app is the one-time model download.
```

- [ ] **Step 2: `CLAUDE.md` stack table and deferred list**

Add this row to the Stack table after the FFmpeg row:

```markdown
| Speech recognition | `Whisper.net`, `Whisper.net.Runtime` | CPU runtime only, **pinned to 1.9.x**. Models are downloaded on first use into `<ApplicationData>/Cutback/models` by `ModelStore`, never bundled. Token `Start`/`End` are `long` centiseconds. The initial prompt is primed with disfluent text (and carried into every window) because Whisper otherwise drops "um"s. |
```

Replace the "Deferred to a later phase" paragraph with:

```markdown
Deferred to a later phase, do not add yet: `Vosk`, `Microsoft.ML.OnnxRuntime`, Silero VAD. Reach for
them only if disfluent prompting proves to miss too many fillers on real recordings.
```

- [ ] **Step 3: `CLAUDE.md` project layout**

Replace the `Cutback.Core`, `Cutback.Analysis`, `Cutback.App` and `tests` parts of the tree so it reads:

```
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

After the sentence "`Cutback.Core` must stay dependency-free apart from `System.Text.Json`. …" add:

```markdown
`Cutback.Analysis` may reference `Cutback.Media` (it needs ffmpeg to decode audio) but never the
other way round, and never `Cutback.App`.
```

- [ ] **Step 4: `CLAUDE.md` data model**

In the JSON schema change `"version": 1` to `"version": 2`, the `origin` comment values to include `filler`, and the transcript line to:

```jsonc
  "transcript": [],             // [{ "text", "start", "end", "confidence" }], filled by Transcribe
```

Replace the paragraph beginning "`origin` is `auto | manual | claude` and exists so…" with:

```markdown
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
```

- [ ] **Step 5: `CLAUDE.md` transcript gestures and roadmap**

After the "### Timeline gestures" section add:

```markdown
### Transcript gestures

The transcript panel (View → Transcript) is a second editing surface over the same `SegmentList`.
A word is struck through when its midpoint lies in a disabled segment (`TranscriptView.IsCut`).
**Click a word**: cut it, or restore it if it is struck. **Drag across words**: the run takes the
opposite state of the word the drag started on. Both are `SetRange`, so they mark the section
manual and are one undo step. **Cmd/Ctrl+click**: play from the word. **Right-click**: Play from
here, Cut / Restore. Word edges snap to the quietest waveform bucket within ±40 ms (twice the
boundary-drag window, because Whisper timing is coarse). "Remove filler words" runs Transcribe
first if there is no transcript, then `FillerDetector` → snap → `FillerCutPlanner` →
`ApplyFillerCuts`, one undo step. Export Transcript writes the *edited* transcript (`.txt` or
`.srt`) with times remapped by `OutputTimeline` so it lines up with the exported video.
```

Replace the Phase 2 roadmap paragraph with:

```markdown
**Phase 2 — Filler words (implemented).** `Whisper.net` behind `ITranscriber`, word-level
timestamps from token timestamps, an editable transcript panel, filler-word cuts with their own
`filler` origin, transcript export. Known issue: **Whisper is trained to strip disfluencies.** The
shipped mitigation is a disfluent initial prompt carried into every window. If recall on real
recordings is poor, the next steps are Silero VAD to find speech islands Whisper produced no word
for, and Vosk as an alternative engine that retains fillers more reliably.
```

Update the "**Phase 1 — MVP (current).**" label to "**Phase 1 — MVP (done).**" and the Testing paragraph's list of covered areas to add "filler detection and planning, transcript cut-state, output timeline, transcript exporters" for Core and "`WordAssembler`, model metadata and cache layout in `Cutback.Analysis.Tests`" after the Media list.

- [ ] **Step 6: `THIRD-PARTY-NOTICES.md`**

Add to the NuGet table after the FFMpegCore row:

```markdown
| Whisper.net | 1.9.1 | MIT | Cutback.Analysis |
| Whisper.net.Runtime (bundles whisper.cpp and ggml) | 1.9.1 | MIT (whisper.cpp MIT, ggml MIT) | Cutback.Analysis |
| Microsoft.Extensions.AI.Abstractions, Microsoft.Bcl.AsyncInterfaces (transitive via Whisper.net) | 10.x | MIT | Cutback.Analysis |
```

Add a new section before "## Fonts":

```markdown
## Downloaded at runtime

| Asset | Licence | Notes |
|---|---|---|
| OpenAI Whisper ggml model weights (`ggml-*.en.bin`) | MIT | Fetched from Hugging Face on first use into the per-user model cache. Not bundled with the application. |
```

- [ ] **Step 7: Full verification**

Run, in order, and confirm each is clean:

```bash
dotnet format Cutback.sln --verify-no-changes
```

```bash
dotnet build Cutback.sln -c Release
```

```bash
dotnet test Cutback.sln -c Release --no-build
```

Expected: no formatting changes, `0 Warning(s) 0 Error(s)`, all tests in the three test projects pass.

- [ ] **Step 8: Published-build smoke test**

Publish for this machine the way the release workflow does (macOS shown; use `win-x64` / `linux-x64` and the matching `natives` value from `.github/workflows/release.yml` on other hosts):

```bash
dotnet publish src/Cutback.App -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:PublishTrimmed=false -o /tmp/cutback-publish
```

Run `/tmp/cutback-publish/Cutback`, open a recording, Transcribe, Remove filler words, export a `.srt`. Expected: transcription works from the published bundle (this proves `Whisper.net.Runtime`'s native libraries are laid out where the single-file host finds them). If it fails with a `DllNotFoundException` for `whisper`, check that `libwhisper.dylib` and the `libggml-*` files sit beside the executable or under `runtimes/osx-arm64/native/`, and add `-p:IncludeNativeLibrariesForSelfExtract=true` for that RID in `release.yml` if the self-extract layout is what works.

- [ ] **Step 9: Commit and open the PR**

```bash
git add CLAUDE.md THIRD-PARTY-NOTICES.md
git commit -m "Document the transcript feature and Whisper.net licensing

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push -u origin feature/filler-words-transcript
```

Open a pull request against `main` titled "Add Whisper transcription, filler-word removal and an editable transcript" whose body summarises the spec's "Decisions taken" table and the manual checks performed, ending with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. CI runs the build and tests on all three OSes and dry-runs the release packaging, which will surface any native-library layout problem on the other platforms.

---

## Self-review

**Spec coverage.** Transcription (PcmExtractor T8, ModelStore T9, WordAssembler T10, WhisperTranscriber T11); data model (`Filler` origin and v2 T1, `SetRange` overload and `ApplyFillerCuts` T2, FillerDetector T3, FillerCutPlanner T4, TranscriptView T5, OutputTimeline T6, TranscriptExporter T7); settings and Preferences T12; transcribe command, transcript state, cancellable busy T13; Remove filler words T14; TranscriptControl T15; panel, word commands, toolbar and View menu T16; Export Transcript T17; docs, notices and packaging T18. The spec's "Manual segments are untouchable" rule is enforced in the planner (T4) rather than in `ApplyFillerCuts`, as the spec says.

**Deviation from spec, deliberate.** The spec listed `Transcribe` as a toolbar button; the plan puts it in the Edit menu and the panel's empty state to keep the toolbar to the two actions the user reaches for daily (Remove filler words, Transcript). The spec's `ITranscriber` receives no duration, so decoding progress inside the transcriber is only reported at completion; the model's own progress covers 85% of the bar.

**Type consistency.** `FillerSpan(Start, End, Word)` in T4 is what T14 constructs. `WordRange(First, Last, Anchor)` in T15 is what T16 consumes. `SnapWordSpan` is defined in T14 and used in T16. `TranscribeCoreAsync` is defined in T13 and used in T14. `WhisperModelInfo.Parse/For/Key/DisplayName` (T9) are what T12 and T13 call. `ModelStore.IsDownloaded/EnsureAsync/CreateDefault` (T9) are what T12/T13 call. `TranscriptView.IndexAtTime/IsCut/CutStates` (T5) match T13/T15/T16. `TranscriptExporter.ToPlainText/ToSrt` (T7) match T17.
