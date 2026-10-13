# Phase 2 — Gameplay Cuts in Cutback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cutback opens a Mainframe Engine recording together with its `.mfcap.json` sidecar. One click cuts idle gameplay, loading screens and pauses, and game markers are drawn on the timeline. `Cutback.Core` and `Cutback.Media` are relicensed MIT and packed as NuGet packages for the engine to consume later.

**Architecture:**
- **Core (pure):** sidecar model and reader; interval helpers; a `GameplayCutPlanner` that turns the sidecar into `PlannedCut`s; a new `SegmentOrigin.Gameplay`; and a generalised `SegmentList.ReplaceOriginCuts` so gameplay cuts replace themselves on re-run and never disturb silence, filler or manual segments.
- **App:** loads the sidecar when a video opens, exposes one command and one toolbar button, and draws markers in the timeline ruler. Settings are per user (flat `AppSettings` properties), mirroring silence detection.

**Tech Stack:** .NET 10, System.Text.Json source generation, xunit + FluentAssertions 7, Avalonia 11.3, SkiaSharp via Avalonia.Skia.

**Spec:** `docs/superpowers/specs/2026-10-12-mainframe-engine-integration-design.md` (Phase 2, and §1.7 for the sidecar format)

## Global Constraints

- `Cutback.Core` stays dependency-free apart from `System.Text.Json`. No FFMpegCore or Avalonia in Core.
- **Segments partition the timeline.** Every mutating `SegmentList` operation ends with `OnChanged()`, which asserts the invariant in Debug.
- **Re-running detection must never discard manual edits.**
- Never shell out to ffmpeg in tests. Do not unit-test Avalonia views.
- `AppSettings` properties use `set`, not `init`. Settings are flat properties, so a key missing from an old `settings.json` keeps its default.
- No hex colours in views or controls. Every colour is a named resource in `Styles/Theme.axaml`, and Skia controls resolve them with `TryFindResource`.
- Icons are Lucide (ISC), stored as `StreamGeometry` in `Styles/Icons.axaml` and listed in `THIRD-PARTY-NOTICES.md`. Icon buttons are `Classes="icon"` with the name in `ToolTip.Tip`.
- `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are on. `dotnet build` must be clean, and `dotnet format --verify-no-changes` must pass.
- FluentAssertions stays on 7.x.
- Sidecar defaults: `MinIdleMs` 3000, `PaddingMs` 500, `MinKeepMs` 1000, `MarkerWindowMs` 1500.
- Sidecar identity: `format` = `"mainframe-capture"`, supported `version` = `1`, file = `<video path>` + `".mfcap.json"`.
- Project schema becomes version **3**. The 2→3 migration is a no-op.

## Spec amendments made by this plan

Task 9 records these in the spec. Each was found while planning against the code.

1. **Manual segments clip gameplay cuts rather than veto them.** The spec copied `FillerCutPlanner`'s "drop any cut touching a manual segment". Gameplay cuts are long (seconds to minutes), so dropping a whole 40 s idle stretch because the user toggled one second inside it would surprise people. The planner subtracts manual segments from each cut instead, which is what `ReplaceAutoSegments` does for silence.
2. **Idle detection applies to live recordings only.** An offline recording (flyover, autowalk, QA script) has no player input by construction. Treating it as idle would cut the entire video. For `mode: "offline"`, only loading, pause and Skip ranges are cut.
3. **Command-line open already exists** (`App.axaml.cs`: `desktop.Args?.FirstOrDefault(File.Exists)` → `OpenVideoAsync`). Phase 2 adds nothing for it, and Phase 3 relies on it as is.

## Review Focus

1. **Sidecar spans running past the probed video duration.** The engine's clock and the muxed MP4's probed length can differ by a frame or the AAC priming delay. The planner clamps everything to `[0, duration]` and never throws. Test: Task 4, `Spans_past_the_end_are_clamped`.
2. **Offline recordings, which have no input at all.** Expected: only loading, pause and Skip are cut, never the whole video. Test: Task 4, `Offline_recordings_are_never_cut_for_idleness`.
3. **Re-running after the user restored part of a gameplay cut.** `Toggle` and `SetRange` mark the segment manual, and the re-run must leave that footage kept. Test: Task 4, `Manual_segments_are_clipped_out_of_cuts`, plus Task 2, `Rerun_replaces_only_previous_gameplay_cuts`.
4. **Sidecars that are missing, malformed or newer.** Expected: the video opens normally, a warning banner explains, and the gameplay button stays hidden. Test: Task 3, serializer error cases. The app path is checked manually in Task 6.
5. **Unsorted or overlapping span lists in a hand-edited or buggy sidecar.** Expected: they are normalised, not rejected. Test: Task 3, `Interval_helpers_normalise_unsorted_overlapping_spans` (through the planner in Task 4).

---

### Task 1: `gameplay` origin and project schema v3

**Files:**
- Modify: `src/Cutback.Core/Models/SegmentOrigin.cs`
- Modify: `src/Cutback.Core/Projects/ProjectSerializer.cs` (`CurrentVersion`)
- Modify: `src/Cutback.Core/Projects/ProjectMigrator.cs` (`Default` steps)
- Test: `tests/Cutback.Core.Tests/ProjectSerializerTests.cs`

**Interfaces:**
- Produces: `SegmentOrigin.Gameplay` (JSON `"gameplay"`); `ProjectSerializer.CurrentVersion == 3`.

- [ ] **Step 1: Write the failing tests** (append to `ProjectSerializerTests`, reusing the file's existing helpers for building a project. If there is no helper, build one inline as the existing round-trip test does.)

```csharp
[Fact]
public void Current_version_is_3()
{
    ProjectSerializer.CurrentVersion.Should().Be(3);
}

[Fact]
public void Gameplay_origin_round_trips_as_lower_case_string()
{
    var source = new SourceInfo("/v.mp4", "abc", 10.0, 1920, 1080, 60.0);
    var segments = new[]
    {
        Segment.Create(0.0, 4.0, enabled: true, SegmentOrigin.Auto),
        Segment.Create(4.0, 7.5, enabled: false, SegmentOrigin.Gameplay, "idle 3.5s"),
        Segment.Create(7.5, 10.0, enabled: true, SegmentOrigin.Auto),
    };
    var project = new CutbackProject(source, segments, [], DetectionSettings.Default);

    var json = ProjectSerializer.Serialize(project);
    var back = ProjectSerializer.Deserialize(json);

    json.Should().Contain("\"origin\": \"gameplay\"");
    back.Segments[1].Origin.Should().Be(SegmentOrigin.Gameplay);
    back.Segments[1].Reason.Should().Be("idle 3.5s");
}

[Fact]
public void A_version_2_file_is_migrated_to_3()
{
    var source = new SourceInfo("/v.mp4", "abc", 10.0, 1920, 1080, 60.0);
    var project = CutbackProject.CreateNew(source);
    var v2 = ProjectSerializer.Serialize(project).Replace("\"version\": 3", "\"version\": 2", StringComparison.Ordinal);

    var back = ProjectSerializer.Deserialize(v2);

    back.Segments.Should().ContainSingle();
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~ProjectSerializerTests"`
Expected: a compile error, because `SegmentOrigin.Gameplay` does not exist.

- [ ] **Step 3: Implement**

`SegmentOrigin.cs`: update the summary and add the member after `Filler`.

```csharp
/// <summary>
/// Who decided a segment's boundaries and state. Re-running silence detection replaces
/// <see cref="Auto"/> segments; re-running filler detection replaces <see cref="Filler"/> segments;
/// re-running gameplay detection replaces <see cref="Gameplay"/> segments. None of them touches
/// <see cref="Manual"/> ones, and each preserves the others' cuts.
/// </summary>
```

```csharp
    /// <summary>A cut produced from a Mainframe Engine recording's sidecar (idle play, loading, pause). Schema version 3.</summary>
    [JsonStringEnumMemberName("gameplay")]
    Gameplay,
```

`ProjectSerializer.cs`: `public const int CurrentVersion = 3;`

`ProjectMigrator.cs`: add the step under the existing `[1]` entry.

```csharp
            // 2 -> 3: the "gameplay" segment origin was added. No field changes; same reason as 1 -> 2.
            [2] = static _ => { },
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Cutback.Core.Tests`
Expected: all PASS. The existing `Deserialize_rejects_a_file_from_a_newer_version` uses `CurrentVersion + 1`, so it keeps working.

- [ ] **Step 5: Commit**

```bash
git add src/Cutback.Core/Models/SegmentOrigin.cs src/Cutback.Core/Projects tests/Cutback.Core.Tests/ProjectSerializerTests.cs
git commit -m "Add the gameplay segment origin and project schema v3"
```

---

### Task 2: `SegmentList.ReplaceOriginCuts`

**Files:**
- Modify: `src/Cutback.Core/SegmentList.cs` (`ApplyFillerCuts` at ~line 498 and `DissolveFillerCore` at ~line 268)
- Test: `tests/Cutback.Core.Tests/SegmentListGameplayTests.cs` (new)

**Interfaces:**
- Consumes: `SegmentOrigin.Gameplay` (Task 1).
- Produces: `public int ReplaceOriginCuts(SegmentOrigin origin, IEnumerable<PlannedCut> cuts)`. It throws `ArgumentOutOfRangeException` for `Auto` and `Manual`. `ApplyFillerCuts(cuts)` keeps its signature and becomes `ReplaceOriginCuts(SegmentOrigin.Filler, cuts)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Cutback.Core.Detection;
using Cutback.Core.Models;

namespace Cutback.Core.Tests;

public sealed class SegmentListGameplayTests
{
    private const double Duration = 60.0;

    private static PlannedCut Idle(double start, double end) => new(start, end, $"idle {end - start:0.0}s");

    [Fact]
    public void Gameplay_cuts_become_disabled_gameplay_segments()
    {
        var list = new SegmentList(Duration);

        var applied = list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20), Idle(30, 35)]);

        applied.Should().Be(2);
        list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin)).Should().Equal(
            (0.0, 10.0, true, SegmentOrigin.Auto),
            (10.0, 20.0, false, SegmentOrigin.Gameplay),
            (20.0, 30.0, true, SegmentOrigin.Auto),
            (30.0, 35.0, false, SegmentOrigin.Gameplay),
            (35.0, 60.0, true, SegmentOrigin.Auto));
        list.Segments[1].Reason.Should().Be("idle 10.0s");
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Rerun_replaces_only_previous_gameplay_cuts()
    {
        var list = new SegmentList(Duration);
        list.ApplyFillerCuts([new PlannedCut(40.0, 40.4, "filler: um")]);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20)]);

        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(25, 28)]);

        list.Segments.Where(s => !s.Enabled).Select(s => (s.Start, s.End, s.Origin)).Should().Equal(
            (25.0, 28.0, SegmentOrigin.Gameplay),
            (40.0, 40.4, SegmentOrigin.Filler));
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Silence_redetection_keeps_gameplay_cuts()
    {
        var list = new SegmentList(Duration);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20)]);

        list.ReplaceAutoSegments([new PlannedCut(5.0, 6.0, "silence 1.00s"), new PlannedCut(15.0, 16.0, "silence 1.00s")]);

        list.Segments.Where(s => !s.Enabled).Select(s => (s.Start, s.End, s.Origin)).Should().Equal(
            (5.0, 6.0, SegmentOrigin.Auto),
            (10.0, 20.0, SegmentOrigin.Gameplay));
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Fact]
    public void Filler_rerun_keeps_gameplay_cuts()
    {
        var list = new SegmentList(Duration);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20)]);

        list.ApplyFillerCuts([new PlannedCut(40.0, 40.4, "filler: um")]);

        list.Segments.Should().Contain(s => s.Origin == SegmentOrigin.Gameplay && s.Start == 10.0 && s.End == 20.0 && !s.Enabled);
        list.ShouldBeValidPartitionOf(Duration);
    }

    [Theory]
    [InlineData(SegmentOrigin.Auto)]
    [InlineData(SegmentOrigin.Manual)]
    public void Auto_and_manual_cannot_be_replaced_in_bulk(SegmentOrigin origin)
    {
        var list = new SegmentList(Duration);

        var act = () => list.ReplaceOriginCuts(origin, []);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void An_empty_rerun_removes_earlier_gameplay_cuts_and_reports_zero()
    {
        var list = new SegmentList(Duration);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20)]);

        var applied = list.ReplaceOriginCuts(SegmentOrigin.Gameplay, []);

        applied.Should().Be(0);
        list.Segments.Should().OnlyContain(s => s.Enabled);
        list.ShouldBeValidPartitionOf(Duration);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~SegmentListGameplayTests"`
Expected: a compile error, because `ReplaceOriginCuts` does not exist.

- [ ] **Step 3: Implement.** In `SegmentList.cs`, replace the whole `ApplyFillerCuts` method (doc comment included) with the following, and rename `DissolveFillerCore` to `DissolveDetectorCutCore` (its only caller is the loop below).

```csharp
    /// <summary>
    /// Applies a fresh filler-word detection result: <see cref="ReplaceOriginCuts"/> with
    /// <see cref="SegmentOrigin.Filler"/>. The caller is responsible for not passing cuts that
    /// overlap manual segments (see <c>FillerCutPlanner</c>).
    /// </summary>
    /// <returns>The number of cuts applied.</returns>
    public int ApplyFillerCuts(IEnumerable<PlannedCut> cuts) => ReplaceOriginCuts(SegmentOrigin.Filler, cuts);

    /// <summary>
    /// Applies a fresh result from a detector that carves cuts into the existing partition
    /// (filler words, gameplay). Every existing segment of <paramref name="origin"/> is dissolved
    /// into its neighbours first, so a re-run replaces the previous result, then each cut is carved
    /// out of whatever is there with that origin. Segments of other origins outside the cuts are
    /// untouched; the caller is responsible for not passing cuts that overlap manual segments.
    /// Dissolving never merges two kept neighbours of different origins into one: that would spread
    /// a neighbour's <see cref="SegmentOrigin.Manual"/> origin over the other's footage and lock it
    /// from future re-detection. Same-origin neighbours still merge, exactly as <see cref="Dissolve"/>
    /// would.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="origin"/> is <see cref="SegmentOrigin.Auto"/>
    /// (re-laid by <see cref="ReplaceAutoSegments"/>) or <see cref="SegmentOrigin.Manual"/> (never replaced in bulk).</exception>
    /// <returns>The number of cuts applied.</returns>
    public int ReplaceOriginCuts(SegmentOrigin origin, IEnumerable<PlannedCut> cuts)
    {
        ArgumentNullException.ThrowIfNull(cuts);
        if (origin is SegmentOrigin.Auto or SegmentOrigin.Manual)
        {
            throw new ArgumentOutOfRangeException(nameof(origin), origin,
                "Auto segments are re-laid by ReplaceAutoSegments, and manual segments are never replaced in bulk.");
        }

        var changed = false;
        for (var i = _segments.Count - 1; i >= 0; i--)
        {
            if (_segments[i].Origin == origin && DissolveDetectorCutCore(i))
            {
                changed = true;
            }
        }

        var applied = 0;
        foreach (var cut in cuts.OrderBy(c => c.Start))
        {
            if (SetRangeCore(cut.Start, cut.End, enabled: false, origin, cut.Reason) >= 0)
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

Update the XML summary on `DissolveDetectorCutCore` (if it has one) to say "a detector's cut" instead of "a filler cut". Then search for any other mention: `grep -rn "DissolveFillerCore" src tests` must print nothing.

- [ ] **Step 4: Run all Core tests**

Run: `dotnet test tests/Cutback.Core.Tests`
Expected: all PASS, including the unchanged `SegmentListFillerTests`.

- [ ] **Step 5: Commit**

```bash
git add src/Cutback.Core/SegmentList.cs tests/Cutback.Core.Tests/SegmentListGameplayTests.cs
git commit -m "Generalise filler cut replacement into SegmentList.ReplaceOriginCuts"
```

---

### Task 3: Capture sidecar model and reader

**Files:**
- Create: `src/Cutback.Core/Capture/CaptureSidecar.cs`
- Create: `src/Cutback.Core/Capture/CaptureSidecarFile.cs` (internal DTO + JSON context)
- Create: `src/Cutback.Core/Capture/CaptureSidecarSerializer.cs`
- Create: `src/Cutback.Core/Capture/CaptureSidecarFormatException.cs`
- Create: `tests/Cutback.Core.Tests/Fixtures/sample.mfcap.json`
- Modify: `tests/Cutback.Core.Tests/Cutback.Core.Tests.csproj` (copy fixtures to output)
- Test: `tests/Cutback.Core.Tests/CaptureSidecarSerializerTests.cs`

**Interfaces:**
- Produces:
  - `CaptureSpan(double Start, double End, string? Label = null)`
  - `CaptureMarker(double Time, string Label)`
  - `CaptureSidecar` with its members (below)
  - `CaptureSidecarSerializer.Deserialize(string json) : CaptureSidecar`
  - `CaptureSidecarSerializer.TryLoadForVideoAsync(string videoPath, CancellationToken ct) : Task<CaptureSidecar?>`
  - `CaptureSidecar.PathFor(string videoPath) : string`
  - `CaptureSidecarFormatException : Exception`

- [ ] **Step 1: Add the fixture.** This is the exact minute drawn in the spec's planner diagram, and Task 4 reuses it. A byte-identical copy goes into the engine in the Phase 1 plan.

`tests/Cutback.Core.Tests/Fixtures/sample.mfcap.json`:

```json
{
  "format": "mainframe-capture",
  "version": 1,
  "video": "forest-20261012-143005.mp4",
  "engineVersion": "0.42.0",
  "game": "Forest",
  "scene": "res://Scenes/Forest.scn",
  "mode": "live",
  "frameRate": 60,
  "width": 1920,
  "height": 1080,
  "durationSeconds": 60.0,
  "droppedFrames": 0,
  "input": [
    { "start": 0.0, "end": 8.0 },
    { "start": 8.5, "end": 14.0 },
    { "start": 22.0, "end": 31.0 },
    { "start": 44.0, "end": 52.0 },
    { "start": 53.0, "end": 56.0 }
  ],
  "loading": [ { "start": 14.5, "end": 19.0 } ],
  "paused": [ { "start": 56.0, "end": 60.0 } ],
  "keep": [ { "start": 32.0, "end": 35.0, "label": "cutscene" } ],
  "skip": [],
  "markers": [
    { "time": 26.0, "label": "boss intro" },
    { "time": 41.0, "label": "chest opens" }
  ]
}
```

Add to `Cutback.Core.Tests.csproj`, inside a new `<ItemGroup>`:

```xml
    <None Include="Fixtures\**" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 2: Write the failing tests**

```csharp
using Cutback.Core.Capture;

namespace Cutback.Core.Tests;

public sealed class CaptureSidecarSerializerTests
{
    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.mfcap.json"));

    [Fact]
    public void Reads_the_shared_fixture()
    {
        var capture = CaptureSidecarSerializer.Deserialize(Fixture());

        capture.Mode.Should().Be(CaptureMode.Live);
        capture.DurationSeconds.Should().Be(60.0);
        capture.FrameRate.Should().Be(60);
        capture.Input.Should().HaveCount(5);
        capture.Loading.Should().Equal(new CaptureSpan(14.5, 19.0));
        capture.Keep.Should().Equal(new CaptureSpan(32.0, 35.0, "cutscene"));
        capture.Skip.Should().BeEmpty();
        capture.Markers.Should().Equal(new CaptureMarker(26.0, "boss intro"), new CaptureMarker(41.0, "chest opens"));
    }

    [Fact]
    public void Missing_lists_read_as_empty()
    {
        const string json = """{ "format": "mainframe-capture", "version": 1, "mode": "offline", "durationSeconds": 5 }""";

        var capture = CaptureSidecarSerializer.Deserialize(json);

        capture.Mode.Should().Be(CaptureMode.Offline);
        capture.Input.Should().BeEmpty();
        capture.Markers.Should().BeEmpty();
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        const string json = """{ "format": "mainframe-capture", "version": 1, "mode": "live", "durationSeconds": 5, "futureThing": { "a": 1 } }""";

        CaptureSidecarSerializer.Deserialize(json).DurationSeconds.Should().Be(5);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "format": "something-else", "version": 1, "mode": "live", "durationSeconds": 5 }""")]
    [InlineData("""{ "format": "mainframe-capture", "mode": "live", "durationSeconds": 5 }""")]
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "mode": "sideways", "durationSeconds": 5 }""")]
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "mode": "live", "durationSeconds": 0 }""")]
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "mode": "live", "durationSeconds": 5, "input": [ { "start": 3, "end": 2 } ] }""")]
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "mode": "live", "durationSeconds": 5, "markers": [ { "time": -1, "label": "x" } ] }""")]
    public void Malformed_sidecars_are_rejected_with_a_readable_message(string json)
    {
        var act = () => CaptureSidecarSerializer.Deserialize(json);

        act.Should().Throw<CaptureSidecarFormatException>().Which.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_newer_version_is_refused_with_an_update_hint()
    {
        const string json = """{ "format": "mainframe-capture", "version": 2, "mode": "live", "durationSeconds": 5 }""";

        var act = () => CaptureSidecarSerializer.Deserialize(json);

        act.Should().Throw<CaptureSidecarFormatException>().WithMessage("*newer*Update Cutback*");
    }

    [Fact]
    public void Path_for_appends_the_suffix_to_the_full_video_name()
    {
        CaptureSidecar.PathFor("/r/forest.mp4").Should().Be("/r/forest.mp4.mfcap.json");
    }

    [Fact]
    public async Task Try_load_returns_null_when_there_is_no_sidecar()
    {
        var video = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp4");

        var capture = await CaptureSidecarSerializer.TryLoadForVideoAsync(video, CancellationToken.None);

        capture.Should().BeNull();
    }

    [Fact]
    public async Task Try_load_reads_the_sidecar_next_to_the_video()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var video = Path.Combine(dir.FullName, "clip.mp4");
            await File.WriteAllTextAsync(CaptureSidecar.PathFor(video), Fixture());

            var capture = await CaptureSidecarSerializer.TryLoadForVideoAsync(video, CancellationToken.None);

            capture!.Markers.Should().HaveCount(2);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~CaptureSidecarSerializerTests"`
Expected: a compile error, because the namespace `Cutback.Core.Capture` does not exist.

- [ ] **Step 4: Implement the model** (`CaptureSidecar.cs`)

```csharp
namespace Cutback.Core.Capture;

/// <summary>A span of the recording's timeline, in seconds. <see cref="Label"/> is set for Keep and Skip spans.</summary>
public sealed record CaptureSpan(double Start, double End, string? Label = null);

/// <summary>A point the game marked as interesting, in seconds.</summary>
public sealed record CaptureMarker(double Time, string Label);

/// <summary><see cref="Live"/>: a person played in real time. <see cref="Offline"/>: a fixed-timestep, scripted render with no player input.</summary>
public enum CaptureMode
{
    Live,
    Offline,
}

/// <summary>
/// What Mainframe Engine knew about a recording, read from the <c>.mfcap.json</c> file written next
/// to the video. The engine owns the format (its <c>docs/design/recording.md</c>); this is the
/// reader's view of version 1. All times are seconds on the video's own timeline.
/// </summary>
public sealed record CaptureSidecar(
    CaptureMode Mode,
    double DurationSeconds,
    double FrameRate,
    int Width,
    int Height,
    string? Game,
    string? Scene,
    IReadOnlyList<CaptureSpan> Input,
    IReadOnlyList<CaptureSpan> Loading,
    IReadOnlyList<CaptureSpan> Paused,
    IReadOnlyList<CaptureSpan> Keep,
    IReadOnlyList<CaptureSpan> Skip,
    IReadOnlyList<CaptureMarker> Markers)
{
    public const string FormatName = "mainframe-capture";

    /// <summary>The newest sidecar version this build understands.</summary>
    public const int SupportedVersion = 1;

    public const string FileSuffix = ".mfcap.json";

    /// <summary>The sidecar path for a video: the full file name plus <see cref="FileSuffix"/>.</summary>
    public static string PathFor(string videoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        return videoPath + FileSuffix;
    }
}
```

`CaptureSidecarFormatException.cs`:

```csharp
namespace Cutback.Core.Capture;

/// <summary>A <c>.mfcap.json</c> file that is not a readable version-1 Mainframe capture sidecar.</summary>
public sealed class CaptureSidecarFormatException : Exception
{
    public CaptureSidecarFormatException(string message)
        : base(message)
    {
    }

    public CaptureSidecarFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
```

`CaptureSidecarFile.cs`: every member is nullable, because the source generator ignores initialisers, and the serializer turns missing values into defaults or errors.

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cutback.Core.Capture;

internal sealed record CaptureSpanFile(double? Start, double? End, string? Label);

internal sealed record CaptureMarkerFile(double? Time, string? Label);

/// <summary>The on-disk shape of a <c>.mfcap.json</c> file. See <see cref="CaptureSidecar"/>.</summary>
internal sealed record CaptureSidecarFile(
    string? Format,
    int? Version,
    string? Video,
    string? EngineVersion,
    string? Game,
    string? Scene,
    string? Mode,
    double? FrameRate,
    int? Width,
    int? Height,
    double? DurationSeconds,
    int? DroppedFrames,
    List<CaptureSpanFile>? Input,
    List<CaptureSpanFile>? Loading,
    List<CaptureSpanFile>? Paused,
    List<CaptureSpanFile>? Keep,
    List<CaptureSpanFile>? Skip,
    List<CaptureMarkerFile>? Markers);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(CaptureSidecarFile))]
internal sealed partial class CaptureSidecarJsonContext : JsonSerializerContext
{
}
```

`CaptureSidecarSerializer.cs`:

```csharp
using System.Globalization;
using System.Text.Json;

namespace Cutback.Core.Capture;

/// <summary>Reads Mainframe Engine capture sidecars. Cutback never writes them.</summary>
public static class CaptureSidecarSerializer
{
    /// <exception cref="CaptureSidecarFormatException">Not a readable version-1 sidecar.</exception>
    public static CaptureSidecar Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        CaptureSidecarFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, CaptureSidecarJsonContext.Default.CaptureSidecarFile);
        }
        catch (JsonException ex)
        {
            throw new CaptureSidecarFormatException("The Mainframe recording data next to this video is not valid JSON.", ex);
        }

        if (file is null || file.Format != CaptureSidecar.FormatName)
        {
            throw new CaptureSidecarFormatException("The .mfcap.json file next to this video is not Mainframe recording data.");
        }

        if (file.Version is not { } version || version < 1)
        {
            throw new CaptureSidecarFormatException("The Mainframe recording data next to this video has no valid version.");
        }

        if (version > CaptureSidecar.SupportedVersion)
        {
            throw new CaptureSidecarFormatException(string.Create(CultureInfo.InvariantCulture,
                $"This video was recorded by a newer Mainframe Engine (recording data version {version}, this build reads up to {CaptureSidecar.SupportedVersion}). Update Cutback to use its gameplay data."));
        }

        var mode = file.Mode switch
        {
            "live" => CaptureMode.Live,
            "offline" => CaptureMode.Offline,
            _ => throw new CaptureSidecarFormatException($"The Mainframe recording data has an unknown mode \"{file.Mode}\"."),
        };

        if (file.DurationSeconds is not { } duration || !double.IsFinite(duration) || duration <= 0)
        {
            throw new CaptureSidecarFormatException("The Mainframe recording data has no valid duration.");
        }

        return new CaptureSidecar(
            mode,
            duration,
            file.FrameRate ?? 0,
            file.Width ?? 0,
            file.Height ?? 0,
            file.Game,
            file.Scene,
            Spans(file.Input, "input"),
            Spans(file.Loading, "loading"),
            Spans(file.Paused, "paused"),
            Spans(file.Keep, "keep"),
            Spans(file.Skip, "skip"),
            Markers(file.Markers));
    }

    /// <summary>The sidecar next to <paramref name="videoPath"/>, or null when there is none.</summary>
    /// <exception cref="CaptureSidecarFormatException">A sidecar exists but cannot be read.</exception>
    /// <exception cref="IOException">The file exists but could not be opened.</exception>
    public static async Task<CaptureSidecar?> TryLoadForVideoAsync(string videoPath, CancellationToken ct)
    {
        var path = CaptureSidecar.PathFor(videoPath);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return Deserialize(json);
    }

    private static List<CaptureSpan> Spans(List<CaptureSpanFile>? spans, string name)
    {
        var result = new List<CaptureSpan>(spans?.Count ?? 0);
        foreach (var s in spans ?? [])
        {
            if (s.Start is not { } start || s.End is not { } end
                || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start)
            {
                throw new CaptureSidecarFormatException($"The Mainframe recording data has an invalid \"{name}\" span.");
            }

            result.Add(new CaptureSpan(start, end, s.Label));
        }

        return result;
    }

    private static List<CaptureMarker> Markers(List<CaptureMarkerFile>? markers)
    {
        var result = new List<CaptureMarker>(markers?.Count ?? 0);
        foreach (var m in markers ?? [])
        {
            if (m.Time is not { } time || !double.IsFinite(time) || time < 0)
            {
                throw new CaptureSidecarFormatException("The Mainframe recording data has an invalid marker.");
            }

            result.Add(new CaptureMarker(time, m.Label ?? ""));
        }

        return result;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~CaptureSidecarSerializerTests"`
Expected: all PASS. If the build complains about `ConfigureAwait` style (CA2007 is not enabled in this repo), match whatever `ProjectSerializer.LoadAsync` does.

- [ ] **Step 6: Commit**

```bash
git add src/Cutback.Core/Capture tests/Cutback.Core.Tests/Fixtures tests/Cutback.Core.Tests/Cutback.Core.Tests.csproj tests/Cutback.Core.Tests/CaptureSidecarSerializerTests.cs
git commit -m "Read Mainframe Engine capture sidecars"
```

---

### Task 4: Interval helpers, shared cut merging and `GameplayCutPlanner`

**Files:**
- Create: `src/Cutback.Core/Detection/Intervals.cs` (internal)
- Create: `src/Cutback.Core/Detection/CutMerger.cs` (internal; extracted from `SilenceCutPlanner`)
- Modify: `src/Cutback.Core/Detection/SilenceCutPlanner.cs` (steps 2–3 call `CutMerger.Merge`)
- Create: `src/Cutback.Core/Models/GameplaySettings.cs`
- Create: `src/Cutback.Core/Detection/GameplayCutPlanner.cs`
- Test: `tests/Cutback.Core.Tests/GameplayCutPlannerTests.cs`

**Interfaces:**
- Consumes: `CaptureSidecar`, `CaptureSpan`, `CaptureMarker`, `CaptureMode` (Task 3); `Segment`, `SegmentOrigin`, `PlannedCut`, `SegmentList.MinSegmentLength`.
- Produces:
  - `public sealed record GameplaySettings(int MinIdleMs, int PaddingMs, int MinKeepMs, int MarkerWindowMs)` with `static GameplaySettings Default` = `(3000, 500, 1000, 1500)`
  - `public static IReadOnlyList<PlannedCut> GameplayCutPlanner.Plan(CaptureSidecar capture, IReadOnlyList<Segment> current, GameplaySettings settings, double duration)`
  - Reasons look like `"idle 3.5s"`, `"loading + idle 7.0s"`, `"paused 4.0s"` and `"skip: menu 2.0s"`, always with one decimal place and invariant culture.

- [ ] **Step 1: Refactor `SilenceCutPlanner` with no change in behaviour.** Create `CutMerger.cs` from steps 2 and 3 of `SilenceCutPlanner.Plan`, moved verbatim:

```csharp
namespace Cutback.Core.Detection;

/// <summary>
/// The cut-merging rules every planner shares: overlapping or touching cuts join, kept gaps shorter
/// than the minimum keep length are bridged, and a short kept sliver at either end of the file is
/// absorbed. Pure.
/// </summary>
internal static class CutMerger
{
    public static List<(double Start, double End)> Merge(IEnumerable<(double Start, double End)> cuts, double minKeep, double duration)
    {
        var merged = new List<(double Start, double End)>();
        foreach (var cut in cuts.OrderBy(c => c.Start))
        {
            if (merged.Count > 0 && cut.Start - merged[^1].End < minKeep)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, cut.End));
            }
            else
            {
                merged.Add(cut);
            }
        }

        // A short kept sliver at either end of the file is just as bad as one in the middle.
        if (merged.Count > 0)
        {
            if (merged[0].Start > 0 && merged[0].Start < minKeep)
            {
                merged[0] = (0, merged[0].End);
            }

            if (merged[^1].End < duration && duration - merged[^1].End < minKeep)
            {
                merged[^1] = (merged[^1].Start, duration);
            }
        }

        return merged;
    }
}
```

In `SilenceCutPlanner.Plan`, replace the code from `// 2. Merge overlapping…` through the end of step 3 with:

```csharp
        // 2. Merge, bridge short kept gaps, absorb slivers at the ends.
        var merged = CutMerger.Merge(cuts, minKeep, duration);
```

and leave the final `return merged.Select(...)` unchanged.

- [ ] **Step 2: Run the existing silence tests (the refactor gate)**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~SilenceCutPlannerTests"`
Expected: all PASS, unchanged.

- [ ] **Step 3: Write the failing planner tests**

```csharp
using Cutback.Core.Capture;
using Cutback.Core.Detection;
using Cutback.Core.Models;

namespace Cutback.Core.Tests;

public sealed class GameplayCutPlannerTests
{
    private static CaptureSidecar Fixture()
        => CaptureSidecarSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.mfcap.json")));

    private static CaptureSidecar Capture(
        double duration,
        CaptureSpan[]? input = null,
        CaptureSpan[]? loading = null,
        CaptureSpan[]? paused = null,
        CaptureSpan[]? keep = null,
        CaptureSpan[]? skip = null,
        CaptureMarker[]? markers = null,
        CaptureMode mode = CaptureMode.Live)
        => new(mode, duration, 60, 1920, 1080, "Game", "Scene",
            input ?? [], loading ?? [], paused ?? [], keep ?? [], skip ?? [], markers ?? []);

    private static IReadOnlyList<Segment> Untouched(double duration) => new SegmentList(duration).Segments;

    private static (double, double, string)[] Shape(IReadOnlyList<PlannedCut> cuts)
        => cuts.Select(c => (Math.Round(c.Start, 6), Math.Round(c.End, 6), c.Reason)).ToArray();

    [Fact]
    public void The_spec_diagram_minute_plans_the_three_drawn_cuts()
    {
        var cuts = GameplayCutPlanner.Plan(Fixture(), Untouched(60), GameplaySettings.Default, 60);

        Shape(cuts).Should().Equal(
            (14.5, 21.5, "loading + idle 7.0s"),
            (35.5, 39.0, "idle 3.5s"),
            (56.0, 60.0, "paused 4.0s"));
    }

    [Fact]
    public void Idle_shorter_than_min_idle_is_left_alone()
    {
        var capture = Capture(30, input: [new(0, 10), new(12.9, 30)]); // 2.9 s gap

        GameplayCutPlanner.Plan(capture, Untouched(30), GameplaySettings.Default, 30).Should().BeEmpty();
    }

    [Fact]
    public void Idle_of_exactly_min_idle_is_cut_after_padding()
    {
        var capture = Capture(30, input: [new(0, 10), new(13, 30)]); // 3.0 s gap

        Shape(GameplayCutPlanner.Plan(capture, Untouched(30), GameplaySettings.Default, 30))
            .Should().Equal((10.5, 12.5, "idle 2.0s"));
    }

    [Fact]
    public void Keep_beats_skip()
    {
        var capture = Capture(30, input: [new(0, 30)], skip: [new(10, 20, "menu")], keep: [new(12, 14, "cutscene")]);

        Shape(GameplayCutPlanner.Plan(capture, Untouched(30), GameplaySettings.Default, 30)).Should().Equal(
            (10.0, 12.0, "skip: menu 2.0s"),
            (14.0, 20.0, "skip: menu 6.0s"));
    }

    [Fact]
    public void Markers_do_not_override_skip()
    {
        var capture = Capture(30, input: [new(0, 30)], loading: [new(10, 20)], markers: [new(15, "x")]);

        Shape(GameplayCutPlanner.Plan(capture, Untouched(30), GameplaySettings.Default, 30))
            .Should().Equal((10.0, 20.0, "loading 10.0s"));
    }

    [Fact]
    public void Skip_without_a_label_reads_skip()
    {
        var capture = Capture(30, input: [new(0, 30)], skip: [new(10, 12)]);

        GameplayCutPlanner.Plan(capture, Untouched(30), GameplaySettings.Default, 30).Single().Reason.Should().Be("skip 2.0s");
    }

    [Fact]
    public void Offline_recordings_are_never_cut_for_idleness()
    {
        var capture = Capture(60, mode: CaptureMode.Offline, loading: [new(0, 2)]);

        Shape(GameplayCutPlanner.Plan(capture, Untouched(60), GameplaySettings.Default, 60))
            .Should().Equal((0.0, 2.0, "loading 2.0s"));
    }

    [Fact]
    public void Live_recording_with_no_input_is_cut_entirely()
    {
        // Idle [0,20] pads to [0.5,19.5]; the 0.5 s kept slivers at either end are under MinKeep,
        // so the shared end-of-file rule absorbs them, exactly as silence detection does.
        var capture = Capture(20);

        Shape(GameplayCutPlanner.Plan(capture, Untouched(20), GameplaySettings.Default, 20))
            .Should().Equal((0.0, 20.0, "idle 20.0s"));
    }

    [Fact]
    public void Manual_segments_are_clipped_out_of_cuts()
    {
        var list = new SegmentList(40);
        list.SetRange(15, 18, enabled: true); // the user restored part of an earlier idle cut
        var capture = Capture(40, input: [new(0, 10), new(30, 40)]);

        Shape(GameplayCutPlanner.Plan(capture, list.Segments, GameplaySettings.Default, 40)).Should().Equal(
            (10.5, 15.0, "idle 4.5s"),
            (18.0, 29.5, "idle 11.5s"));
    }

    [Fact]
    public void Spans_past_the_end_are_clamped()
    {
        var capture = Capture(10, input: [new(0, 4)], paused: [new(8, 12)], markers: [new(11, "late")]);

        var cuts = GameplayCutPlanner.Plan(capture, Untouched(10), GameplaySettings.Default, 10);

        cuts.Should().OnlyContain(c => c.Start >= 0 && c.End <= 10);
        Shape(cuts).Should().Equal((4.5, 10.0, "idle + paused 5.5s"));
    }

    [Fact]
    public void Unsorted_overlapping_input_spans_are_normalised()
    {
        var capture = Capture(30, input: [new(20, 30), new(0, 6), new(4, 10)]);

        Shape(GameplayCutPlanner.Plan(capture, Untouched(30), GameplaySettings.Default, 30))
            .Should().Equal((10.5, 19.5, "idle 9.0s"));
    }

    [Fact]
    public void Kept_gaps_shorter_than_min_keep_are_bridged()
    {
        var capture = Capture(30, input: [new(0, 5), new(10, 10.2), new(15, 30)]);
        var settings = GameplaySettings.Default with { PaddingMs = 0 };

        Shape(GameplayCutPlanner.Plan(capture, Untouched(30), settings, 30))
            .Should().Equal((5.0, 15.0, "idle 10.0s"));
    }
}
```

Trace `Spans_past_the_end_are_clamped`: idle is `[4,10]`. The marker window `[9.5,12.5]` leaves `[4,9.5]`, which is 5.5 s ≥ 3 s, so after padding it is `[4.5,9.0]`. Paused `[8,10]` is clamped. Merged: `[4.5,10]`, and the pieces sorted by start are idle (4.5) then paused (8), so the reason is `idle + paused 5.5s`.

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/Cutback.Core.Tests --filter "FullyQualifiedName~GameplayCutPlannerTests"`
Expected: a compile error, because `GameplayCutPlanner` and `GameplaySettings` do not exist.

- [ ] **Step 5: Implement**

`Models/GameplaySettings.cs`:

```csharp
namespace Cutback.Core.Models;

/// <summary>Parameters for cutting a Mainframe Engine recording from its sidecar. Edited per user.</summary>
/// <param name="MinIdleMs">Stretches with no player input shorter than this are left alone.</param>
/// <param name="PaddingMs">Kept on each side of an idle cut, as a lead-in and lead-out. Loading, pause and Skip edges are exact and not padded.</param>
/// <param name="MinKeepMs">Kept fragments shorter than this between two cuts are removed with them.</param>
/// <param name="MarkerWindowMs">Footage this close to a game marker is never cut for idleness.</param>
public sealed record GameplaySettings(int MinIdleMs, int PaddingMs, int MinKeepMs, int MarkerWindowMs)
{
    public static GameplaySettings Default { get; } = new(MinIdleMs: 3000, PaddingMs: 500, MinKeepMs: 1000, MarkerWindowMs: 1500);
}
```

`Detection/Intervals.cs`:

```csharp
namespace Cutback.Core.Detection;

/// <summary>Set operations on half-open time ranges, in seconds. Inputs may be unsorted and overlapping; outputs never are. Pure.</summary>
internal static class Intervals
{
    public static List<(double Start, double End)> Normalize(IEnumerable<(double Start, double End)> spans)
    {
        var result = new List<(double Start, double End)>();
        foreach (var s in spans.Where(s => s.End > s.Start).OrderBy(s => s.Start))
        {
            if (result.Count > 0 && s.Start <= result[^1].End)
            {
                result[^1] = (result[^1].Start, Math.Max(result[^1].End, s.End));
            }
            else
            {
                result.Add(s);
            }
        }

        return result;
    }

    /// <summary>Clamps every span to <c>[0, duration]</c> and drops what vanishes.</summary>
    public static IEnumerable<(double Start, double End)> Clamp(IEnumerable<(double Start, double End)> spans, double duration)
        => spans.Select(s => (Start: Math.Max(0, s.Start), End: Math.Min(duration, s.End))).Where(s => s.End > s.Start);

    /// <summary>The parts of <c>[0, duration]</c> not covered by <paramref name="spans"/>.</summary>
    public static List<(double Start, double End)> Complement(IEnumerable<(double Start, double End)> spans, double duration)
        => Subtract([(0, duration)], spans);

    /// <summary>The parts of <paramref name="from"/> not covered by <paramref name="remove"/>.</summary>
    public static List<(double Start, double End)> Subtract(IEnumerable<(double Start, double End)> from, IEnumerable<(double Start, double End)> remove)
    {
        var holes = Normalize(remove);
        var result = new List<(double Start, double End)>();
        foreach (var (start, end) in Normalize(from))
        {
            var cursor = start;
            foreach (var hole in holes)
            {
                if (hole.End <= cursor)
                {
                    continue;
                }

                if (hole.Start >= end)
                {
                    break;
                }

                if (hole.Start > cursor)
                {
                    result.Add((cursor, hole.Start));
                }

                cursor = Math.Max(cursor, hole.End);
            }

            if (cursor < end)
            {
                result.Add((cursor, end));
            }
        }

        return result;
    }
}
```

`Detection/GameplayCutPlanner.cs`:

```csharp
using System.Globalization;
using Cutback.Core.Capture;
using Cutback.Core.Models;

namespace Cutback.Core.Detection;

/// <summary>
/// Turns a Mainframe Engine recording's sidecar into cuts. Idle stretches (no player input, live
/// recordings only) longer than the minimum are cut with padding; loading screens, pauses and the
/// game's Skip spans are cut exactly; Keep spans and the windows around markers are never cut for
/// idleness, and Keep also beats Skip. Results are merged with the shared minimum-keep rules, and
/// manual segments are clipped out so a re-run never undoes the user's edits. Pure.
/// </summary>
public static class GameplayCutPlanner
{
    private sealed record Piece(double Start, double End, string Kind);

    public static IReadOnlyList<PlannedCut> Plan(
        CaptureSidecar capture,
        IReadOnlyList<Segment> current,
        GameplaySettings settings,
        double duration)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(duration);

        var pad = settings.PaddingMs / 1000.0;
        var minIdle = settings.MinIdleMs / 1000.0;
        var minKeep = settings.MinKeepMs / 1000.0;
        var window = settings.MarkerWindowMs / 1000.0;

        var keep = Intervals.Clamp(capture.Keep.Select(Range), duration).ToList();
        var pieces = new List<Piece>();

        // Skip-like ranges first, so a merged cut's reason names them before "idle".
        AddExact(pieces, capture.Loading, _ => "loading", keep, duration);
        AddExact(pieces, capture.Paused, _ => "paused", keep, duration);
        AddExact(pieces, capture.Skip, s => s.Label is { Length: > 0 } label ? "skip: " + label : "skip", keep, duration);

        if (capture.Mode == CaptureMode.Live)
        {
            var protectedRanges = keep.Concat(capture.Markers.Select(m => (m.Time - window, m.Time + window)));
            var idle = Intervals.Subtract(Intervals.Complement(Intervals.Clamp(capture.Input.Select(Range), duration), duration), protectedRanges);
            foreach (var (start, end) in idle.Where(i => i.End - i.Start >= minIdle))
            {
                var (s, e) = (Math.Max(0, start + pad), Math.Min(duration, end - pad));
                if (e > s)
                {
                    pieces.Add(new Piece(s, e, "idle"));
                }
            }
        }

        var merged = CutMerger.Merge(pieces.Select(p => (p.Start, p.End)), minKeep, duration);
        var manual = current.Where(s => s.Origin == SegmentOrigin.Manual).Select(s => (s.Start, s.End)).ToList();

        var result = new List<PlannedCut>();
        foreach (var (start, end) in Intervals.Subtract(merged, manual))
        {
            if (end - start >= SegmentList.MinSegmentLength)
            {
                result.Add(new PlannedCut(start, end, Reason(pieces, start, end)));
            }
        }

        return result;
    }

    private static (double Start, double End) Range(CaptureSpan s) => (s.Start, s.End);

    private static void AddExact(List<Piece> pieces, IEnumerable<CaptureSpan> spans, Func<CaptureSpan, string> kind,
        List<(double Start, double End)> keep, double duration)
    {
        foreach (var span in spans)
        {
            foreach (var (s, e) in Intervals.Subtract(Intervals.Clamp([Range(span)], duration), keep))
            {
                pieces.Add(new Piece(s, e, kind(span)));
            }
        }
    }

    private static string Reason(List<Piece> pieces, double start, double end)
    {
        var kinds = pieces
            .Where(p => p.Start < end && p.End > start)
            .OrderBy(p => p.Start)
            .Select(p => p.Kind)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var label = kinds.Count > 0 ? string.Join(" + ", kinds) : "gameplay";
        return string.Create(CultureInfo.InvariantCulture, $"{label} {end - start:0.0}s");
    }
}
```

Two notes. First, `OrderBy` is stable. In the diagram minute, loading `[14.5,19]` and the padded idle `[14.5,21.5]` both start at 14.5, and because loading is added first the reason is `loading + idle`. Second, in `Keep_beats_skip` the skip `[10,20]` minus keep `[12,14]` gives two pieces. `MinKeepMs` is 1000 and the kept gap is 2 s, so they are not bridged.

- [ ] **Step 6: Run all Core tests**

Run: `dotnet test tests/Cutback.Core.Tests`
Expected: all PASS. If a planner test fails, trace it by hand against the step-5 algorithm before changing the expected value. Every expected value above was traced that way.

- [ ] **Step 7: Commit**

```bash
git add src/Cutback.Core/Detection src/Cutback.Core/Models/GameplaySettings.cs tests/Cutback.Core.Tests/GameplayCutPlannerTests.cs
git commit -m "Plan gameplay cuts from a capture sidecar; share cut merging with silence"
```

---

### Task 5: Gameplay settings in AppSettings and Preferences

**Files:**
- Modify: `src/Cutback.App/Services/AppSettings.cs`
- Modify: `src/Cutback.App/Services/AppSettingsStore.cs` (`Sanitize`)
- Modify: `src/Cutback.App/ViewModels/PreferencesViewModel.cs`
- Modify: `src/Cutback.App/Views/PreferencesWindow.axaml`

**Interfaces:**
- Consumes: `GameplaySettings` (Task 4).
- Produces: `AppSettings.Gameplay : GameplaySettings` (derived, `[JsonIgnore]`); `PreferencesViewModel.GameplayTab = 2`. `TranscriptionTab` becomes `3`.

The app has no test project and the repo rule is not to unit-test Avalonia views, so this task is verified by a clean build and a manual check.

- [ ] **Step 1: AppSettings.** Add the bounds next to the silence constants:

```csharp
    // Bounds for the gameplay detection fields. The Preferences NumericUpDowns mirror them.
    public const int MinGameplayMinIdleMs = 500;
    public const int MaxGameplayMinIdleMs = 60_000;
    public const int MinGameplayPaddingMs = 0;
    public const int MaxGameplayPaddingMs = 2000;
    public const int MinGameplayMinKeepMs = 0;
    public const int MaxGameplayMinKeepMs = 10_000;
    public const int MinGameplayMarkerWindowMs = 0;
    public const int MaxGameplayMarkerWindowMs = 10_000;
```

and the properties after `MinKeepMs`:

```csharp
    // Gameplay detection (Mainframe Engine recordings). Flat for the same reason as silence detection.

    /// <summary>See <see cref="GameplaySettings.MinIdleMs"/>.</summary>
    public int GameplayMinIdleMs { get; set; } = GameplaySettings.Default.MinIdleMs;

    /// <summary>See <see cref="GameplaySettings.PaddingMs"/>.</summary>
    public int GameplayPaddingMs { get; set; } = GameplaySettings.Default.PaddingMs;

    /// <summary>See <see cref="GameplaySettings.MinKeepMs"/>.</summary>
    public int GameplayMinKeepMs { get; set; } = GameplaySettings.Default.MinKeepMs;

    /// <summary>See <see cref="GameplaySettings.MarkerWindowMs"/>.</summary>
    public int GameplayMarkerWindowMs { get; set; } = GameplaySettings.Default.MarkerWindowMs;

    /// <summary>The four gameplay values as the record the planner takes. Derived, not persisted.</summary>
    [JsonIgnore]
    public GameplaySettings Gameplay => new(GameplayMinIdleMs, GameplayPaddingMs, GameplayMinKeepMs, GameplayMarkerWindowMs);
```

- [ ] **Step 2: Sanitize.** Append these four lines to the `settings with { … }` in `AppSettingsStore.Sanitize`:

```csharp
        GameplayMinIdleMs = Math.Clamp(settings.GameplayMinIdleMs, AppSettings.MinGameplayMinIdleMs, AppSettings.MaxGameplayMinIdleMs),
        GameplayPaddingMs = Math.Clamp(settings.GameplayPaddingMs, AppSettings.MinGameplayPaddingMs, AppSettings.MaxGameplayPaddingMs),
        GameplayMinKeepMs = Math.Clamp(settings.GameplayMinKeepMs, AppSettings.MinGameplayMinKeepMs, AppSettings.MaxGameplayMinKeepMs),
        GameplayMarkerWindowMs = Math.Clamp(settings.GameplayMarkerWindowMs, AppSettings.MinGameplayMarkerWindowMs, AppSettings.MaxGameplayMarkerWindowMs),
```

- [ ] **Step 3: PreferencesViewModel.**
  - Set the tab constants to `GeneralTab = 0`, `DetectionTab = 1`, `GameplayTab = 2`, `TranscriptionTab = 3`.
  - Run `grep -rn "TranscriptionTab" src`. Every user must still mean the Transcription tab; they do, because they use the constant.
  - In the constructor, after `MinKeepMs = …`:

```csharp
        GameplayMinIdleMs = settings.Current.GameplayMinIdleMs;
        GameplayPaddingMs = settings.Current.GameplayPaddingMs;
        GameplayMinKeepMs = settings.Current.GameplayMinKeepMs;
        GameplayMarkerWindowMs = settings.Current.GameplayMarkerWindowMs;
```

  Add the properties and a reset command after `ResetDetection`:

```csharp
    // Gameplay detection. Bounds live on AppSettings and are mirrored by the NumericUpDowns.

    [ObservableProperty]
    public partial int GameplayMinIdleMs { get; set; }

    [ObservableProperty]
    public partial int GameplayPaddingMs { get; set; }

    [ObservableProperty]
    public partial int GameplayMinKeepMs { get; set; }

    [ObservableProperty]
    public partial int GameplayMarkerWindowMs { get; set; }

    /// <summary>Puts the four gameplay fields back to <see cref="GameplaySettings.Default"/>. Nothing is saved until OK.</summary>
    [RelayCommand]
    private void ResetGameplay()
    {
        var g = GameplaySettings.Default;
        GameplayMinIdleMs = g.MinIdleMs;
        GameplayPaddingMs = g.PaddingMs;
        GameplayMinKeepMs = g.MinKeepMs;
        GameplayMarkerWindowMs = g.MarkerWindowMs;
    }
```

  In `Save()`, add to the `s with { … }`:

```csharp
            GameplayMinIdleMs = Math.Clamp(GameplayMinIdleMs, AppSettings.MinGameplayMinIdleMs, AppSettings.MaxGameplayMinIdleMs),
            GameplayPaddingMs = Math.Clamp(GameplayPaddingMs, AppSettings.MinGameplayPaddingMs, AppSettings.MaxGameplayPaddingMs),
            GameplayMinKeepMs = Math.Clamp(GameplayMinKeepMs, AppSettings.MinGameplayMinKeepMs, AppSettings.MaxGameplayMinKeepMs),
            GameplayMarkerWindowMs = Math.Clamp(GameplayMarkerWindowMs, AppSettings.MinGameplayMarkerWindowMs, AppSettings.MaxGameplayMarkerWindowMs),
```

  Add `using Cutback.Core.Models;` if it is not already present.

- [ ] **Step 4: PreferencesWindow.axaml.** Insert a new `TabItem` between the closing `</TabItem>` of "Silence detection" and `<TabItem Header="Transcription">`:

```xml
      <TabItem Header="Gameplay detection">
        <StackPanel Classes="tab" Spacing="14">
          <!-- Bounds mirror the AppSettings.MinGameplay* / MaxGameplay* constants. -->
          <StackPanel Spacing="4">
            <TextBlock Text="Minimum idle (ms)" />
            <NumericUpDown Value="{Binding GameplayMinIdleMs}" Minimum="500" Maximum="60000" Increment="250" FormatString="0" />
            <TextBlock Classes="dim hint" Text="Stretches with no player input shorter than this are left alone." />
          </StackPanel>

          <StackPanel Spacing="4">
            <TextBlock Text="Padding (ms)" />
            <NumericUpDown Value="{Binding GameplayPaddingMs}" Minimum="0" Maximum="2000" Increment="50" FormatString="0" />
            <TextBlock Classes="dim hint" Text="Footage kept on each side of an idle cut as a lead-in. Loading screens and pauses are cut exactly." />
          </StackPanel>

          <StackPanel Spacing="4">
            <TextBlock Text="Minimum keep (ms)" />
            <NumericUpDown Value="{Binding GameplayMinKeepMs}" Minimum="0" Maximum="10000" Increment="100" FormatString="0" />
            <TextBlock Classes="dim hint" Text="Kept moments shorter than this between two cuts are removed with them." />
          </StackPanel>

          <StackPanel Spacing="4">
            <TextBlock Text="Marker window (ms)" />
            <NumericUpDown Value="{Binding GameplayMarkerWindowMs}" Minimum="0" Maximum="10000" Increment="100" FormatString="0" />
            <TextBlock Classes="dim hint" Text="Footage this close to a marker the game placed is never cut for idleness." />
          </StackPanel>

          <Button Content="Reset to defaults" Command="{Binding ResetGameplayCommand}" HorizontalAlignment="Left" />

          <TextBlock Classes="dim hint"
                     Text="Used by Detect idle gameplay on Mainframe Engine recordings. Re-running it replaces its own cuts but never touches segments you have edited by hand." />
        </StackPanel>
      </TabItem>
```

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/Cutback.App/Services src/Cutback.App/ViewModels/PreferencesViewModel.cs src/Cutback.App/Views/PreferencesWindow.axaml
git commit -m "Add gameplay detection settings to Preferences"
```

---

### Task 6: Load the sidecar and add Detect Idle Gameplay

**Files:**
- Modify: `src/Cutback.App/ViewModels/MainWindowViewModel.cs`
- Modify: `src/Cutback.App/Views/MainWindow.axaml` (toolbar button, Edit menu item)
- Modify: `src/Cutback.App/Styles/Icons.axaml`
- Modify: `THIRD-PARTY-NOTICES.md`

**Interfaces:**
- Consumes: `CaptureSidecarSerializer.TryLoadForVideoAsync`, `CaptureSidecarFormatException` (Task 3); `GameplayCutPlanner.Plan` (Task 4); `SegmentList.ReplaceOriginCuts` (Task 2); `AppSettings.Gameplay` (Task 5).
- Produces:
  - `MainWindowViewModel.Capture : CaptureSidecar?` (observable)
  - `HasCapture : bool`
  - `DetectIdleGameplayCommand`
  - Task 7 binds `Capture.Markers`.

- [ ] **Step 1: View-model state.** Next to the `Waveform` property, add:

```csharp
    /// <summary>The Mainframe Engine sidecar next to the source video, or null for any other video.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCapture))]
    [NotifyCanExecuteChangedFor(nameof(DetectIdleGameplayCommand))]
    public partial CaptureSidecar? Capture { get; private set; }

    public bool HasCapture => Capture is not null;
```

  Add `nameof(DetectIdleGameplayCommand)` to both existing `[NotifyCanExecuteChangedFor(...)]` lists, on `Project` (~line 66) and on `IsBusy` (~line 249). Add the CanExecute helper next to `CanEdit`:

```csharp
    private bool CanDetectGameplay => CanEdit && Capture is not null;
```

  Add `using Cutback.Core.Capture;`.

- [ ] **Step 2: Load the sidecar when opening.** In `RunOpenAsync`, directly after `var (project, path, warning) = await load(ct);`:

```csharp
            var (capture, captureWarning) = await LoadCaptureAsync(project.Source.Path, ct);
```

  In the assignment block, replace `StatusMessage = null;` and `WarningMessage = warning;` with:

```csharp
            Capture = capture;
            StatusMessage = capture is null
                ? null
                : $"Mainframe recording · {capture.Markers.Count} marker{(capture.Markers.Count == 1 ? "" : "s")}";
            WarningMessage = JoinWarnings(warning, captureWarning);
```

  Add the helpers below `RunOpenAsync`:

```csharp
    /// <summary>The recording sidecar, or a warning when one exists but cannot be used. The video opens either way.</summary>
    private static async Task<(CaptureSidecar? Capture, string? Warning)> LoadCaptureAsync(string videoPath, CancellationToken ct)
    {
        try
        {
            return (await CaptureSidecarSerializer.TryLoadForVideoAsync(videoPath, ct), null);
        }
        catch (Exception ex) when (ex is CaptureSidecarFormatException or IOException or UnauthorizedAccessException)
        {
            return (null, ex.Message + " Detect idle gameplay is unavailable for this video.");
        }
    }

    private static string? JoinWarnings(params string?[] warnings)
    {
        var present = warnings.Where(w => !string.IsNullOrEmpty(w)).ToArray();
        return present.Length == 0 ? null : string.Join(" ", present);
    }
```

  In the close/new-project reset block (~line 447, the one that sets `Project = null; … Waveform = null;`), add `Capture = null;` after `Waveform = null;`.

- [ ] **Step 3: The command.** Add it after `RemoveFillerWordsAsync`:

```csharp
    [RelayCommand(CanExecute = nameof(CanDetectGameplay))]
    private void DetectIdleGameplay()
    {
        if (Project is null || Segments is null || Capture is null)
        {
            return;
        }

        ErrorMessage = null;
        var cuts = GameplayCutPlanner.Plan(Capture, Segments.Segments, _settings.Current.Gameplay, DurationSeconds);
        Edit(s => s.ReplaceOriginCuts(SegmentOrigin.Gameplay, cuts));

        var removed = cuts.Sum(c => c.Duration);
        StatusMessage = cuts.Count == 0
            ? "No idle gameplay found with the current settings."
            : $"Cut {cuts.Count} stretch{(cuts.Count == 1 ? "" : "es")} of idle gameplay, {TimeFormat.Clock(removed)} removed.";
    }
```

- [ ] **Step 4: Icon.** Append to `Icons.axaml`, before `</ResourceDictionary>`. First check the geometry against https://lucide.dev/icons/gamepad-2 (copy the SVG and rewrite each `<line>` as `M x1 y1 L x2 y2`). The expected result is:

```xml
  <!-- gamepad-2: Detect idle gameplay -->
  <StreamGeometry x:Key="IconGamepad2">M6 11h4 M8 9v4 M15 12h.01 M18 10h.01 M17.32 5H6.68a4 4 0 0 0-3.978 3.59c-.006.052-.01.101-.017.152C2.604 9.416 2 14.456 2 16a3 3 0 0 0 3 3c1 0 1.5-.5 2-1l1.414-1.414A2 2 0 0 1 9.828 16h4.344a2 2 0 0 1 1.414.586L17 18c.5.5 1 1 2 1a3 3 0 0 0 3-3c0-1.545-.604-6.584-.685-7.258-.007-.05-.011-.1-.017-.151A4 4 0 0 0 17.32 5z</StreamGeometry>
```

  In `THIRD-PARTY-NOTICES.md`, add `gamepad-2` to the Lucide row's icon list: `(… audio-lines, message-square-x, captions, upload, play, pause, film, gamepad-2)`.

- [ ] **Step 5: Toolbar and menu.** In `MainWindow.axaml`, after the Remove filler words button:

```xml
          <Button Classes="icon" Command="{Binding DetectIdleGameplayCommand}" IsVisible="{Binding HasCapture}"
                  ToolTip.Tip="Detect idle gameplay">
            <Path Data="{StaticResource IconGamepad2}" />
          </Button>
```

  In the Edit menu, after `Remove _Filler Words`:

```xml
        <MenuItem Header="Detect Idle _Gameplay" Command="{Binding DetectIdleGameplayCommand}" IsVisible="{Binding HasCapture}" />
```

- [ ] **Step 6: Build and run the tests**

Run: `dotnet build && dotnet test`
Expected: Build succeeded with 0 warnings; all tests PASS.

- [ ] **Step 7: Manual check.** Use the scripted-hook technique from the dev-environment memory, or run the app by hand.
  1. Make a 60 s test clip with ffmpeg:
     `ffmpeg -f lavfi -i testsrc=s=1280x720:r=60:d=60 -f lavfi -i sine=d=60 -shortest -c:v libx264 -c:a aac /tmp/forest.mp4`
  2. Copy the fixture next to it:
     `cp tests/Cutback.Core.Tests/Fixtures/sample.mfcap.json /tmp/forest.mp4.mfcap.json`
  3. Open `/tmp/forest.mp4`. Check:
     - the status reads `Mainframe recording · 2 markers`;
     - the gamepad button is visible;
     - clicking it gives the three cuts from the spec diagram, with reasons visible on the timeline;
     - Undo removes all three in one step.
  4. Toggle the 35.5–39.0 cut back on, then click the button again. That stretch stays kept.
  5. Write `{"format":"mainframe-capture","version":2,"mode":"live","durationSeconds":60}` into the sidecar and reopen. The video opens, the warning banner mentions a newer Mainframe Engine, and the button is hidden.
  6. Open any other video. The button is hidden and there is no warning.

- [ ] **Step 8: Commit**

```bash
git add src/Cutback.App THIRD-PARTY-NOTICES.md
git commit -m "Detect idle gameplay in Mainframe Engine recordings"
```

---

### Task 7: Markers in the timeline ruler

**Files:**
- Modify: `src/Cutback.App/Controls/TimelineControl.cs`
- Modify: `src/Cutback.App/Styles/Theme.axaml`
- Modify: `src/Cutback.App/Views/MainWindow.axaml` (binding)

**Interfaces:**
- Consumes: `CaptureMarker` (Task 3); `MainWindowViewModel.Capture` (Task 6).
- Produces: `TimelineControl.Markers` (`IReadOnlyList<CaptureMarker>?` styled property).

- [ ] **Step 1: Colour resource.** In `Theme.axaml`, after `TimelineHint`:

```xml
  <Color x:Key="TimelineMarker">#B48CFF</Color>
```

- [ ] **Step 2: Property.** Next to the other `AvaloniaProperty.Register` calls in `TimelineControl`:

```csharp
    public static readonly StyledProperty<IReadOnlyList<CaptureMarker>?> MarkersProperty =
        AvaloniaProperty.Register<TimelineControl, IReadOnlyList<CaptureMarker>?>(nameof(Markers));

    /// <summary>Points of interest from a Mainframe Engine recording, drawn in the ruler. Null for other videos.</summary>
    public IReadOnlyList<CaptureMarker>? Markers
    {
        get => GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }
```

  Add `MarkersProperty` to the `AffectsRender<TimelineControl>(…)` call in the static constructor, and add `using Cutback.Core.Capture;`.

- [ ] **Step 3: Snapshot and palette.**
  - Add `IReadOnlyList<CaptureMarker>? Markers` to the `Snapshot` record, after `PendingSection`, and pass `Markers` at that position in `Render`.
  - Add `SKColor Marker` as the last member of `Palette`.
  - In `Fallback`, add `Marker: new SKColor(0xB4, 0x8C, 0xFF)`.
  - In `FromResources`, add `Resolve(host, "TimelineMarker", f.Marker)` as the last argument.

- [ ] **Step 4: Draw.** In `DrawOperation`, add the method below. Call it immediately after the existing `DrawRuler(canvas, paint, w, bodyTop);` call (~line 756) as `DrawMarkers(canvas, paint, bodyTop);`.

```csharp
        private void DrawMarkers(SKCanvas canvas, SKPaint paint, float bodyTop)
        {
            if (_s.Markers is not { Count: > 0 } markers)
            {
                return;
            }

            var style = paint.Style;
            paint.Style = SKPaintStyle.Fill;
            paint.IsAntialias = true;
            paint.Color = _s.Palette.Marker;
            paint.TextSize = 10;
            foreach (var marker in markers)
            {
                if (marker.Time < _s.ViewStart || marker.Time > _s.ViewEnd)
                {
                    continue;
                }

                var x = (float)TimeToX(marker.Time);
                using var head = new SKPath();
                head.MoveTo(x - 4, bodyTop - 9);
                head.LineTo(x + 4, bodyTop - 9);
                head.LineTo(x, bodyTop - 1);
                head.Close();
                canvas.DrawPath(head, paint);
                canvas.DrawText(marker.Label, x + 6, bodyTop - 2, paint);
            }

            paint.Style = style;
        }
```

- [ ] **Step 5: Bind.** In `MainWindow.axaml`, add to the `<controls:TimelineControl …>` attributes:

```xml
                                Markers="{Binding Capture.Markers}"
```

- [ ] **Step 6: Build**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 7: Manual check.** Reopen `/tmp/forest.mp4` from Task 6:
  - purple triangles labelled "boss intro" and "chest opens" sit at 26 s and 41 s in the ruler;
  - zooming keeps them anchored to their times;
  - a video without a sidecar shows none.

  Capture a screenshot with the `RenderTargetBitmap` technique for the PR.

- [ ] **Step 8: Commit**

```bash
git add src/Cutback.App
git commit -m "Draw Mainframe recording markers in the timeline ruler"
```

---

### Task 8: Relicense and pack Cutback.Core and Cutback.Media as MIT

**Files:**
- Create: `src/Cutback.Core/LICENSE`, `src/Cutback.Media/LICENSE`
- Modify: `src/Cutback.Core/Cutback.Core.csproj`, `src/Cutback.Media/Cutback.Media.csproj`
- Modify: `.github/workflows/release.yml` (new `packages` job; the `release` job needs it)

**Interfaces:**
- Produces: NuGet packages `Cutback.Core` and `Cutback.Media`, versioned like the app (`-p:Version=$VERSION`) and attached to each GitHub Release. Phase 4 consumes them. Pushing to nuget.org is not done here; that is open question 2 in the spec.

- [ ] **Step 1: Licence files.** Write the standard MIT text into both files:

```text
MIT License

Copyright (c) 2026 Cutback contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

- [ ] **Step 2: Package metadata.** Add a `PropertyGroup` to `Cutback.Core.csproj`. It overrides the GPL default in `Directory.Build.props`:

```xml
  <!-- MIT, unlike the app (see LICENSE here and CLAUDE.md › Licensing), so Mainframe Engine's editor can use it. -->
  <PropertyGroup>
    <IsPackable>true</IsPackable>
    <PackageId>Cutback.Core</PackageId>
    <Description>Cutback's editing core: the segment partition, undo history, silence/filler/gameplay cut planning and .cutback project files.</Description>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <RepositoryUrl>https://github.com/brogan89/Cutback</RepositoryUrl>
  </PropertyGroup>
```

  Do the same in `Cutback.Media.csproj`, with `PackageId` `Cutback.Media` and the description `Cutback's ffmpeg layer: probing, waveforms, silencedetect, filter graphs and export, all through the ffmpeg command line.`

- [ ] **Step 3: Pack locally and check the licence**

```bash
dotnet pack src/Cutback.Core -c Release -o /tmp/cutback-pkgs
dotnet pack src/Cutback.Media -c Release -o /tmp/cutback-pkgs
unzip -p /tmp/cutback-pkgs/Cutback.Core.0.1.0.nupkg Cutback.Core.nuspec | grep -i license
unzip -p /tmp/cutback-pkgs/Cutback.Media.0.1.0.nupkg Cutback.Media.nuspec | grep -i -e license -e Cutback.Core
```

Expected: both show `<license type="expression">MIT</license>`, and Media lists a dependency on `Cutback.Core`.

- [ ] **Step 4: Release workflow.**
  - Add a job after `build`, at the same indentation. It runs on pull requests too, so packaging breakage shows up in the dry run.
  - Add it to the `release` job's needs: `needs: [version, build, packages]`.
  - The existing `download-artifact` with `merge-multiple: true` collects `*.nupkg` into `dist/`, so the Checksums and Release steps attach them unchanged.

```yaml
  packages:
    name: NuGet packages (MIT libraries)
    needs: version
    runs-on: ubuntu-latest
    env:
      VERSION: ${{ needs.version.outputs.version }}
    steps:
      - uses: actions/checkout@v7

      - uses: actions/setup-dotnet@v6
        with:
          global-json-file: global.json

      - name: Pack
        run: |
          for project in src/Cutback.Core src/Cutback.Media; do
            dotnet pack "$project" -c Release -p:Version="$VERSION" -p:ContinuousIntegrationBuild=true -o artifacts/packages
          done

      - uses: actions/upload-artifact@v7
        with:
          name: Cutback-${{ env.VERSION }}-packages
          path: artifacts/packages/*.nupkg
          if-no-files-found: error
```

- [ ] **Step 5: Validate the workflow syntax**

Run: `python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/release.yml'))" && echo ok`
Expected: `ok`. The real check is the PR dry run, where the `packages` job must go green.

- [ ] **Step 6: Commit**

```bash
git add src/Cutback.Core src/Cutback.Media .github/workflows/release.yml
git commit -m "Relicense Cutback.Core and Cutback.Media as MIT and pack them on release"
```

---

### Task 9: Docs and spec amendments

**Files:**
- Modify: `CLAUDE.md`
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-10-12-mainframe-engine-integration-design.md`

- [ ] **Step 1: CLAUDE.md.** Make each of these edits:
  - **Project layout tree:** under `Cutback.Core/`, add `│   │   ├── Capture/               # Mainframe Engine .mfcap.json sidecar reader` and add `GameplayCutPlanner` to the `Detection/` comment.
  - **Data model:** change `"version": 2` to `"version": 3` in the JSON example. Change "`origin` is `auto | manual | claude | filler`" to "`auto | manual | claude | filler | gameplay`". After the sentence ending "…so the two detectors leave each other's and the user's cuts alone.", add:

    > `ReplaceOriginCuts(Gameplay)` (Detect idle gameplay) works the same way as filler cuts, but `GameplayCutPlanner` *clips* manual segments out of its cuts instead of dropping the cut, because gameplay cuts are long.

  - Replace the "Schema version 2 added the `filler` origin…" paragraph with:

    > Schema version 2 added the `filler` origin and version 3 the `gameplay` origin. Both migrations are no-ops; the bumps exist so an older build refuses the file with its "newer version" message instead of a deserialisation error.

  - **New section** after "Locating filler words":

    > ### Mainframe Engine recordings
    >
    > A video recorded by Mainframe Engine has a sidecar next to it: `<video file name>.mfcap.json`
    > (format `mainframe-capture`, version 1; the engine owns it, see the integration spec). It lists
    > player-input spans, loading and pause spans, the game's Keep/Skip spans and markers, on the
    > video's own timeline. `CaptureSidecarSerializer.TryLoadForVideoAsync` reads it when the video
    > opens; a bad or newer sidecar is a warning, never a failed open. **Detect idle gameplay** runs
    > `GameplayCutPlanner` → `ReplaceOriginCuts(Gameplay)`, one undo step. Idle detection applies to
    > `live` recordings only (offline renders have no input); loading, pause and Skip are cut exactly,
    > Keep beats Skip, and markers protect ±`MarkerWindowMs` from idle cuts. Settings live in
    > `AppSettings` as `Gameplay*` and in File → Preferences → Gameplay detection. The fixture
    > `tests/Cutback.Core.Tests/Fixtures/sample.mfcap.json` is byte-identical to the engine's copy; do
    > not edit one without the other.

  - **Licensing:** append:

    > **Exception: `Cutback.Core` and `Cutback.Media` are MIT** (each has its own `LICENSE` and
    > `PackageLicenseExpression`), packed as NuGet packages on every release, so Mainframe Engine's
    > MIT editor can use them. Neither may take a dependency that is not MIT-compatible. Cutback
    > never bundles or links ffmpeg; it runs the user's.

  - **Testing:** add "capture sidecar reading, gameplay cut planning, `ReplaceOriginCuts`" to the `Cutback.Core` list.

- [ ] **Step 2: README.md.** Add a short section:

```markdown
### Mainframe Engine recordings

Videos recorded with Mainframe Engine's `--record` come with a `.mfcap.json` file that says when the
player was idle, when a loading screen or pause menu was up, and what the game marked. Open the video
and click **Detect idle gameplay** (the gamepad button) to cut all of that in one step; markers
appear in the timeline ruler. Tune it in Preferences → Gameplay detection.
```

- [ ] **Step 3: Spec amendments.** In the spec's §2.2 `GameplayCutPlanner` list:
  - replace step 6 with "Subtract `Manual` segments from every cut (clip, do not drop: gameplay cuts are long). Drop pieces shorter than `SegmentList.MinSegmentLength`.";
  - add to step 1: "**Live recordings only**; an offline recording has no input, so only steps 4–6 apply."

  In §2.3, replace the **Command-line open** bullet with: "**Command-line open** already exists (`App.axaml.cs` opens the first existing path in `desktop.Args`), and Phase 3 relies on it unchanged."

- [ ] **Step 4: Format and final verification**

Run: `dotnet format --verify-no-changes && dotnet build && dotnet test`
Expected: no format changes, Build succeeded with 0 warnings, and all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add CLAUDE.md README.md docs/superpowers/specs/2026-10-12-mainframe-engine-integration-design.md
git commit -m "Document Mainframe Engine recordings, gameplay cuts and the MIT libraries"
```
