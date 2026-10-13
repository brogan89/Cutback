# Phase 4b — Clips Tab in the Mainframe Editor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Trim and export a gameplay recording without leaving the Mainframe Editor. A recording (or a `.cutback` project) opens in a **Clips** tab with:
- a gapless preview;
- Cutback's timeline gestures;
- Detect idle gameplay and Detect silence;
- Undo/Redo, Save, and Export.

The project file is the Cutback app's own format, so a project round-trips between the two. Cutback becomes optional.

**Architecture:**
- **Packages.** The editor references the MIT packages `Cutback.Core` and `Cutback.Media` (Phase 4a). `MainframeEngine` never does, so shipped games contain neither.
- **Code under `MainframeEngine.Editor/Src/Clips/`, modelled on the Music tab:**
  - `ClipDocument` holds the project and its `SegmentEditor`;
  - `ClipTimelineView` holds the zoom and pan maths;
  - `ClipTimelineCanvas` is a `Node2D` port of Cutback's `TimelineControl` draw pass, and `ClipCanvasController` is the port of its gestures;
  - `ClipPreview` decodes with ffmpeg child processes into a texture and an `AudioStreamGenerator` voice, whose consumed frames are the clock;
  - `ClipTab` holds all of the above. `ClipView` (a `[Tool]` node) places, publishes and routes input, and `ClipPanel` is the RmlUi chrome.

**Tech Stack:** .NET 10; the engine's editor (RmlUi `EditorDocument`, `SubViewport` + `UiServer.RegisterTexture`, `CanvasItem` drawing, `AudioStreamGenerator`, `EditorSession`/`EditorCommands`); Cutback.Core/Cutback.Media 0.2.0; xunit.v3 with plain `Assert`.

**Spec:** `/Users/brogan/dev/SimpleVideoEditor/docs/superpowers/specs/2026-10-12-mainframe-engine-integration-design.md`, "Phase 4 — Clips tab in the editor (engine)" (§4.1–4.5) and Acceptance 4. Phase 4a (`docs/superpowers/plans/2026-10-13-phase4a-cutback-packages.md`) supplies `SegmentEditor`, public `FfmpegProcess`, `PreviewDecoding` and `DeliveryEncoder`.

**Repository and branch:** every path below is relative to the engine worktree `/Users/brogan/mainframe/mainframe-engine/.claude/worktrees/clips-tab`, branch `feature/clips-tab`. The branch is cut from `feature/editor-recording` (Mainframe-Games/mainframe-engine#63), so its PR stacks on Phase 3. After creating the worktree, run `git submodule update --init Plugins/Spine`.

## Global Constraints

- **Package versions:**
  - During development: `Cutback.Core` and `Cutback.Media` `0.2.0-local.1`, restored from `/Users/brogan/dev/SimpleVideoEditor/artifacts/local-feed`, which `scripts/pack-local.sh 0.2.0-local.1` in the Cutback repo writes.
  - The feed is wired by `/Users/brogan/mainframe/mainframe-engine/.claude/worktrees/nuget.config`. It sits outside every repository, so it is never committed (Task 1 writes it).
  - Once 0.2.0 is on nuget.org, the versions become `0.2.0` and that file is deleted. That is a hand-off step, not part of this plan's tasks.
- **Dependency rule:** the engine's CLAUDE.md says new NuGet packages are discussed first. These two were agreed in the spec (§4.1) and in chat on 2026-10-13. Only `MainframeEngine.Editor` references them.
- **ffmpeg:** found by Cutback.Media's `FfmpegLocator`. The setting is **Editor Settings › ffmpeg** (`EditorSettings.FfmpegPath`), else `MAINFRAME_FFMPEG`, then PATH, then common folders. ffmpeg and ffprobe must be together. When ffmpeg is missing, show the same actionable message as Phase 3 (`FfmpegNotFoundException.Message`) in a dialog. Never crash.
- **Name clash:** both `MainframeEngine.FfmpegNotFoundException` and `Cutback.Media.FfmpegNotFoundException` exist. In Clips code, use the Cutback one via `using Cutback.Media;`, and qualify the engine one if a file needs both.
- **Preview limits (`PreviewDecoding`):** at most 960 px wide; at most 30 fps; audio `f32le`, 2 channels, at `AudioServer.SampleRate` (48000 in the editor).
- **Gapless rule:** the next kept range's video decoder starts **1.0 s** (`ClipPlaybackPlan.PrefetchSeconds`) before the current range ends. Audio is decoded range after range into one generator ring of **1.0 s**, so it never has a gap.
- **Timeline constants (ported from Cutback's `TimelineControl`):**
  - ruler 22 dp;
  - boundary grab `|dx| < 6` dp below the ruler;
  - drag threshold 4 dp;
  - minimum view span `min(0.25 s, duration)`;
  - snap window 0.020 s (`Waveform.SnapToZeroCrossing`);
  - wheel zoom factor `1.25^(-deltaY)` about the cursor;
  - horizontal wheel pan `deltaX × span × 0.1`;
  - the ruler's `NiceIntervals` and its 80 dp label spacing.
- **Colours (ported from Cutback's `Theme.axaml`):**

  | Key | Value | Key | Value | Key | Value |
  |---|---|---|---|---|---|
  | ruler | #18181C | body | #121215 | kept | #1D2E4D |
  | kept hover | #26406A | removed | #1F1F24 | removed hover | #2A2A31 |
  | kept wave | #6FA3F0 | removed wave | #45454E | boundary | #C8C8D2 at alpha 0xB0 |
  | active boundary | #FFD166 | section fill | #FFD166 at alpha 0x38 | playhead | #FF5C5C |
  | tick | #4E4E58 | label | #9A9AA6 | reason | #7C7C88 |
  | hint | #60606A | marker | #B48CFF | | |
- **The source video is opened read-only and never modified.** Export writes a new file. Saving writes only the `.cutback`.
- **Project file:** `ProjectSerializer` from Cutback.Core is the only writer. The default name is `<video name>.cutback` next to the video.
- **Not ported (spec §4.4):** transcript, Whisper and filler words. Gameplay and silence settings use `GameplaySettings.Default` and `DetectionSettings.Default`, with no settings UI (parked).
- **Engine conventions:**
  - file-scoped namespaces; `sealed` by default; dense `<summary>` docs explaining why;
  - tests in plain xunit `Assert` with PascalCase sentence names;
  - tests touching global state use `[Collection(nameof(SerialEditor))]`.
- **Gates per task:** `just build`, the focused `dotnet test Tests/MainframeEngine.Editor.Tests --filter …`, `just format-check`. Before the final review, also run `just test`, the Release `dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false`, and `just test-render`.
- **Never push** (the user decides). Work only in the clips-tab worktree. Commit trailer: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Spec amendments made by this plan

Task 12 records these in the spec:
1. **The QA capture is reviewed by eye, not compared to a golden in CI.** This matches `record.qa`. CI has no ffmpeg, so a CI golden would show neither waveform nor preview.
2. **Audio is decoded range after range into one ring,** not by a second process started a second early. The 1 s ring already covers ffmpeg's startup, and a single producer keeps the ring's single-producer rule. Video does use the spec's one-second prefetch.
3. **File › Open becomes "Open…"** and accepts scenes, `.cutback` projects and videos.
4. **Detection settings are the Cutback defaults** in the tab. A settings UI is parked.

## Review Focus

1. **The tab closes, or another tab activates, while preview or export runs.** Expected:
   - every decoder process is killed and the voice stops;
   - closing cancels the export, and switching tabs only pauses the preview;
   - no ffmpeg is left running.

   Tests: Task 7, `DisposeKillsEveryDecoder`, with a fake decoder factory; Task 8, `DeactivatingAClipTabPausesItsPreview`.
2. **Editing while the preview plays.** For example, toggling a region ahead of the playhead. Expected: playback restarts from the current position with the new plan, so a newly cut region is skipped. Test: Task 7, `ASegmentEditRebuildsThePlanFromThePlayhead`.
3. **A recording without its sidecar, a damaged sidecar, or a video with no audio.** Expected:
   - no sidecar: it opens, and Detect idle gameplay is disabled;
   - a bad sidecar: it opens with a warning in the status line;
   - no audio: it is refused with "has no audio track" (export needs audio).

   Tests: Task 2, `ABadSidecarOpensWithAWarning`, `NoSidecarMeansNoGameplayDetection`; the no-audio refusal is glue in `OpenAsync`, so the reviewer reads it.
4. **A `.cutback` whose source moved or changed.** Expected: a missing source is refused with a message that names the path; a changed hash opens with a warning. Tests: Task 2, `AProjectWhoseSourceIsMissingIsRefused`, `AChangedSourceOpensWithAWarning`.
5. **Undo, Redo and Save while a boundary drag is in progress, or the pointer leaves the window mid-drag.** Expected: the drag ends as one step, unsnapped, and nothing is left half-applied. Test: Task 5, `CancelDuringABoundaryDragRecordsOneStep`.

---

### Task 1: Packages, the ffmpeg setting and file kinds

**Files:**
- Create (outside the repository): `/Users/brogan/mainframe/mainframe-engine/.claude/worktrees/nuget.config`
- Modify: `Directory.Packages.props` (a new `<!-- Editor -->` group)
- Modify: `MainframeEngine.Editor/MainframeEngine.Editor.csproj` (two `PackageReference`s)
- Create: `MainframeEngine.Editor/Src/Clips/ClipsFfmpeg.cs`
- Modify: `MainframeEngine.Editor/Src/Settings/EditorSettings.cs` (`FfmpegPath`, next to `CutbackPath` at ~59/110/136/151/232)
- Modify: `MainframeEngine.Editor/Src/UI/EditorSettingsDialog.cs` (~140, a field like `cutback`) and `MainframeEngine.Editor/Content/Editor/editor_settings.rml` (the matching row)
- Modify: `MainframeEngine.Editor/Src/FileSystem/FileKind.cs` (`Video`, `Clips`), `MainframeEngine.Editor/Src/UI/EditorIcons.cs` (~85-100)
- Modify: `THIRD_PARTY_NOTICES.md` (summary table rows)
- Test: `Tests/MainframeEngine.Editor.Tests/Clips/ClipsFfmpegTests.cs`, and extend the existing file-kind tests (find them with `grep -rln "FileKinds.Of" Tests/MainframeEngine.Editor.Tests`)

**Interfaces:**
- Produces:
  ```csharp
  namespace MainframeEngine.Editor.Clips;
  public static class ClipsFfmpeg
  {
      public const string EnvironmentVariable = "MAINFRAME_FFMPEG";
      public static string? ConfiguredPath(string? setting, string? environment);
      public static Cutback.Media.FfmpegLocation Locate(string? setting);   // throws Cutback.Media.FfmpegNotFoundException
  }
  // EditorSettings: public string? FfmpegPath { get; set; }
  // FileKind.Video (.mp4 .mov .mkv .webm), FileKind.Clips (.cutback)
  ```

- [ ] **Step 1: The local feed and the references**

In the Cutback repository (Phase 4a, Task 4), run `scripts/pack-local.sh 0.2.0-local.1`. Then write `/Users/brogan/mainframe/mainframe-engine/.claude/worktrees/nuget.config`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<!-- Development only, outside every repository: Cutback.* comes from the local feed until 0.2.0 is on nuget.org. -->
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="cutback-local" value="/Users/brogan/dev/SimpleVideoEditor/artifacts/local-feed" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
    <packageSource key="cutback-local"><package pattern="Cutback.*" /></packageSource>
  </packageSourceMapping>
</configuration>
```

The mapping matters. Central package management with two sources and no mapping raises NU1507, which `-warnaserror` turns into an error.

`Directory.Packages.props`: after the engine group, add:

```xml
  <!-- Editor only (the Clips tab; MIT, never referenced by the runtime) -->
  <ItemGroup>
    <PackageVersion Include="Cutback.Core" Version="0.2.0-local.1" />
    <PackageVersion Include="Cutback.Media" Version="0.2.0-local.1" />
  </ItemGroup>
```

`MainframeEngine.Editor.csproj`: add a new `ItemGroup` with `<PackageReference Include="Cutback.Core" />` and `<PackageReference Include="Cutback.Media" />`.

Run: `dotnet restore MainframeEngine.slnx && just build`
Expected: restore takes Cutback.* from the local feed (`cutback-local`). The build is clean.

Run `dotnet list MainframeEngine.Editor package --include-transitive | grep -iE "cutback|ffmpeg|instances"`. Note every transitive package for the notices: expect FFMpegCore and Instances.

- [ ] **Step 2: Write the failing tests**

`Tests/MainframeEngine.Editor.Tests/Clips/ClipsFfmpegTests.cs`:

```csharp
using MainframeEngine.Editor.Clips;

namespace MainframeEngine.Editor.Tests.Clips;

public sealed class ClipsFfmpegTests
{
    [Fact]
    public void TheEditorSettingWinsOverTheEnvironment() =>
        Assert.Equal("/opt/ffmpeg/bin", ClipsFfmpeg.ConfiguredPath("/opt/ffmpeg/bin", "/env/ffmpeg"));

    [Fact]
    public void TheEnvironmentIsUsedWhenTheSettingIsBlank() =>
        Assert.Equal("/env/ffmpeg", ClipsFfmpeg.ConfiguredPath("  ", "/env/ffmpeg"));

    [Fact]
    public void NeitherMeansTheLocatorSearches() => Assert.Null(ClipsFfmpeg.ConfiguredPath(null, ""));
}
```

Add to the existing file-kind tests:

```csharp
    [Theory]
    [InlineData("a.mp4", FileKind.Video)]
    [InlineData("a.MOV", FileKind.Video)]
    [InlineData("a.mkv", FileKind.Video)]
    [InlineData("a.webm", FileKind.Video)]
    [InlineData("a.cutback", FileKind.Clips)]
    public void RecordingsAndClipProjectsHaveTheirOwnKinds(string path, FileKind kind) =>
        Assert.Equal(kind, FileKinds.Of(path, isDirectory: false));
```

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~ClipsFfmpegTests|FullyQualifiedName~RecordingsAndClipProjects"`
Expected: FAIL (does not compile).

- [ ] **Step 3: Implement**

`MainframeEngine.Editor/Src/Clips/ClipsFfmpeg.cs`:

```csharp
using Cutback.Media;

namespace MainframeEngine.Editor.Clips;

/// <summary>
/// Finds ffmpeg for the Clips tab with Cutback.Media's <see cref="FfmpegLocator"/> (ffmpeg and ffprobe together). The
/// order is Editor Settings › ffmpeg, then <c>MAINFRAME_FFMPEG</c> (the variable the recorder honours), then PATH, then
/// the usual install folders. The editor runs ffmpeg as a separate program and never links it (ADR 0190).
/// </summary>
public static class ClipsFfmpeg
{
    public const string EnvironmentVariable = "MAINFRAME_FFMPEG";

    /// <summary>The path to hand the locator: the setting, else the environment variable, else null (search).</summary>
    public static string? ConfiguredPath(string? setting, string? environment) =>
        !string.IsNullOrWhiteSpace(setting) ? setting.Trim()
        : !string.IsNullOrWhiteSpace(environment) ? environment.Trim()
        : null;

    /// <exception cref="FfmpegNotFoundException">No ffmpeg/ffprobe pair; the message names the install command.</exception>
    public static FfmpegLocation Locate(string? setting) =>
        new FfmpegLocator(ConfiguredPath(setting, Environment.GetEnvironmentVariable(EnvironmentVariable))).Locate();
}
```

- **`EditorSettings.FfmpegPath`:** mirror `CutbackPath` exactly: the property, the load/save JSON key `ffmpegPath`, the sanitising (trim, empty to null) and the defaults.
- **Settings dialog:** add a text field below Cutback, labelled "ffmpeg", with the hint "Folder or file; blank searches PATH". It binds `ffmpeg` the same way `cutback` is bound at `EditorSettingsDialog.cs:140`.
- **`FileKind`:** add `Video` and `Clips` to the enum. In `FileKinds.Of` map `.mp4 .mov .mkv .webm` to `Video` and `.cutback` to `Clips`, case-insensitively like the others. Check the `switch`es on `FileKind` in `LooksLikeAssetPath` and `FilePeek.IsPeeked`: neither new kind is an asset or peeked.
- **`EditorIcons` (~85-100):** `.cutback` gets `"video"` and the four video extensions get `"movie"`. Both icons are already in the atlas; `IconTests` fails if they were not.
- **`THIRD_PARTY_NOTICES.md` summary table:** add these rows. Every transitive MIT package found in Step 1 gets a row in the same style.

```
| [Cutback.Core](https://github.com/brogan89/Cutback) | 0.2.0 | MIT | NuGet package `Cutback.Core` | editor only: `Cutback.Core.dll` (Clips tab) |
| [Cutback.Media](https://github.com/brogan89/Cutback) | 0.2.0 | MIT | NuGet package `Cutback.Media` | editor only: `Cutback.Media.dll` (Clips tab; runs the user's ffmpeg, links none) |
| [FFMpegCore](https://github.com/rosenbjerg/FFMpegCore) | 5.4.0 | MIT | NuGet package `FFMpegCore` (via Cutback.Media) | editor only: `FFMpegCore.dll` (ffprobe wrapper) |
```

- [ ] **Step 4: Run the tests, then the gates**

Run the Step 2 filter (PASS), then `just build && just format-check`.

- [ ] **Step 5: Commit**

```bash
git add Directory.Packages.props MainframeEngine.Editor THIRD_PARTY_NOTICES.md Tests/MainframeEngine.Editor.Tests
git commit -m "Reference Cutback.Core and Cutback.Media in the editor; ffmpeg setting and video file kinds

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `ClipDocument` — open, edit, detect, save

**Files:**
- Create: `MainframeEngine.Editor/Src/Clips/ClipDocument.cs`
- Create: `MainframeEngine.Editor/Src/Clips/ClipFormat.cs`
- Test: `Tests/MainframeEngine.Editor.Tests/Clips/ClipDocumentTests.cs`

**Interfaces:**
- Consumes: `CutbackProject`, `SourceInfo`, `SegmentList`, `SegmentEditor`, `EditHistory<Segment[]>`, `ProjectSerializer` (`Serialize`, `SaveAsync`, `LoadAsync`, `FileExtension`), `SourceHash`, `CaptureSidecar`, `CaptureSidecarSerializer` (`Deserialize`, `TryLoadForVideoAsync`), `GameplayCutPlanner.Plan`, `GameplaySettings.Default`, `SilenceCutPlanner.Plan`, `DetectionSettings.Default`, `SilenceSpan`, `MediaProbe`, `Waveform` (all from Cutback.Core / Cutback.Media 0.2.0).
- Produces:
  ```csharp
  namespace MainframeEngine.Editor.Clips;
  public sealed class ClipDocument
  {
      public const int HistoryLimit = 100;
      public static ClipDocument FromProject(CutbackProject project, string? projectPath, CaptureSidecar? capture, IReadOnlyList<string> warnings);
      public static Task<ClipDocument> OpenAsync(string path, FfmpegLocation ffmpeg, CancellationToken cancellationToken);
      public CutbackProject Project { get; }
      public string? FilePath { get; }               // the .cutback, null until first saved
      public string SourcePath { get; }
      public double Duration { get; }
      public SegmentList Segments { get; }
      public SegmentEditor Editor { get; }
      public CaptureSidecar? Capture { get; }
      public Waveform? Waveform { get; set; }        // filled in when the background extraction finishes
      public IReadOnlyList<string> Warnings { get; }
      public int Version { get; }                    // +1 on every partition change
      public bool IsDirty { get; }                   // Editor.IsDirty
      public event Action? Changed;                  // every partition change
      public event Action? DirtyChanged;
      public string DefaultProjectPath { get; }      // <video dir>/<video name>.cutback
      public void Save(string? path = null);         // throws InvalidOperationException when unnamed and path is null
      public string DetectIdleGameplay();            // status line; requires Capture
      public string ApplySilence(IReadOnlyList<SilenceSpan> spans);   // status line
      public double SnapTime(double time);           // Waveform?.SnapToZeroCrossing(time, 0.020) ?? time
  }
  public static class ClipFormat { public static string Clock(double seconds); }   // "m:ss.t", "h:mm:ss.t" from an hour
  ```

- [ ] **Step 1: Write the failing tests**

`Tests/MainframeEngine.Editor.Tests/Clips/ClipDocumentTests.cs`:

```csharp
using Cutback.Core;
using Cutback.Core.Capture;
using Cutback.Core.Detection;
using Cutback.Core.Models;
using Cutback.Core.Projects;
using MainframeEngine.Editor.Clips;

namespace MainframeEngine.Editor.Tests.Clips;

public sealed class ClipDocumentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("clips-doc-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    internal static CutbackProject Project(string sourcePath, double duration = 60) =>
        CutbackProject.CreateNew(new SourceInfo(sourcePath, new string('0', 64), duration, 1920, 1080, 60));

    internal const string Sidecar = """
        { "format": "mainframe-capture", "version": 1, "video": "a.mp4", "engineVersion": "0.42.0", "game": "Demo",
          "scene": "res://Scenes/Main.mscene", "mode": "live", "frameRate": 60, "width": 1920, "height": 1080,
          "durationSeconds": 60.0, "droppedFrames": 0,
          "input": [ { "start": 0.0, "end": 14.0 }, { "start": 20.0, "end": 60.0 } ],
          "loading": [ { "start": 14.5, "end": 19.0 } ], "paused": [], "keep": [], "skip": [],
          "markers": [ { "time": 30.0, "label": "boss" } ] }
        """;

    [Fact]
    public void ANewDocumentIsCleanAndUnnamed()
    {
        var doc = ClipDocument.FromProject(Project(Path.Combine(_dir, "a.mp4")), null, null, []);

        Assert.False(doc.IsDirty);
        Assert.Null(doc.FilePath);
        Assert.Equal(Path.Combine(_dir, "a.cutback"), doc.DefaultProjectPath);
        Assert.Equal(60, doc.Duration);
        Assert.Single(doc.Segments.Segments);
    }

    [Fact]
    public void AnEditMakesItDirtyAndBumpsTheVersion()
    {
        var doc = ClipDocument.FromProject(Project(Path.Combine(_dir, "a.mp4")), null, null, []);
        var dirtyFlips = 0;
        doc.DirtyChanged += () => dirtyFlips++;

        doc.Editor.CreateSection(10, 20, anchor: 10);

        Assert.True(doc.IsDirty);
        Assert.Equal(1, dirtyFlips);
        Assert.True(doc.Version > 0);
    }

    [Fact]
    public void SaveWritesTheCutbackFormatAndAddsTheExtension()
    {
        var doc = ClipDocument.FromProject(Project(Path.Combine(_dir, "a.mp4")), null, null, []);
        doc.Editor.CreateSection(10, 20, anchor: 10);

        doc.Save(Path.Combine(_dir, "trim"));

        var path = Path.Combine(_dir, "trim.cutback");
        Assert.Equal(path, doc.FilePath);
        Assert.False(doc.IsDirty);
        var expected = ProjectSerializer.Serialize(doc.Project);
        Assert.Equal(expected, File.ReadAllText(path));
        Assert.Equal(3, doc.Project.Segments.Count); // kept · cut · kept
    }

    [Fact]
    public async Task AProjectSavedHereLoadsInCutbackUnchanged()
    {
        var doc = ClipDocument.FromProject(Project(Path.Combine(_dir, "a.mp4")), null, null, []);
        doc.Editor.CreateSection(5, 7, anchor: 5);
        doc.Save(Path.Combine(_dir, "a.cutback"));

        var loaded = await ProjectSerializer.LoadAsync(Path.Combine(_dir, "a.cutback"), CancellationToken.None);

        Assert.Equal(doc.Segments.Segments, loaded.Segments);
        Assert.Equal(doc.Project.Source, loaded.Source);
    }

    [Fact]
    public void SavingAnUnnamedDocumentWithoutAPathThrows()
    {
        var doc = ClipDocument.FromProject(Project(Path.Combine(_dir, "a.mp4")), null, null, []);

        Assert.Throws<InvalidOperationException>(() => doc.Save());
    }

    [Fact]
    public void DetectIdleGameplayCutsTheLoadingScreenAsOneUndoStep()
    {
        var capture = CaptureSidecarSerializer.Deserialize(Sidecar);
        var doc = ClipDocument.FromProject(Project(Path.Combine(_dir, "a.mp4")), null, capture, []);

        var status = doc.DetectIdleGameplay();

        Assert.StartsWith("Cut ", status, StringComparison.Ordinal);
        Assert.Contains(doc.Segments.Segments, s => !s.Enabled && s.Origin == SegmentOrigin.Gameplay && s.Start <= 14.5 && s.End >= 19.0);
        Assert.True(doc.Editor.Undo());
        Assert.Single(doc.Segments.Segments);
    }

    [Fact]
    public void NoSidecarMeansNoGameplayDetection()
    {
        var doc = ClipDocument.FromProject(Project(Path.Combine(_dir, "a.mp4")), null, null, []);

        Assert.Throws<InvalidOperationException>(() => doc.DetectIdleGameplay());
    }

    [Fact]
    public void ApplySilenceCutsPaddedSpansAndReportsThem()
    {
        var doc = ClipDocument.FromProject(Project(Path.Combine(_dir, "a.mp4")), null, null, []);

        var status = doc.ApplySilence([new SilenceSpan(10, 12), new SilenceSpan(30, 31)]);

        Assert.Equal("Detected 2 silences, 0:02.8 removed.", status); // each span loses 60 ms per side
        Assert.Equal(5, doc.Segments.Count);
        Assert.All(doc.Segments.Segments.Where(s => !s.Enabled), s => Assert.Equal(SegmentOrigin.Auto, s.Origin));
    }

    [Fact]
    public async Task AProjectWhoseSourceIsMissingIsRefused()
    {
        var path = Path.Combine(_dir, "gone.cutback");
        await ProjectSerializer.SaveAsync(Project(Path.Combine(_dir, "missing.mp4")), path, CancellationToken.None);
        var ffmpeg = new Cutback.Media.FfmpegLocation("/x/ffmpeg", "/x/ffprobe", "/x", Cutback.Media.FfmpegLocationSource.Path);

        var e = await Assert.ThrowsAsync<FileNotFoundException>(() => ClipDocument.OpenAsync(path, ffmpeg, CancellationToken.None));

        Assert.Contains("missing.mp4", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangedSourceOpensWithAWarning()
    {
        var video = Path.Combine(_dir, "a.mp4");
        await File.WriteAllBytesAsync(video, [1, 2, 3]);
        var path = Path.Combine(_dir, "a.cutback");
        await ProjectSerializer.SaveAsync(Project(video), path, CancellationToken.None); // hash "000…" never matches
        var ffmpeg = new Cutback.Media.FfmpegLocation("/x/ffmpeg", "/x/ffprobe", "/x", Cutback.Media.FfmpegLocationSource.Path);

        var doc = await ClipDocument.OpenAsync(path, ffmpeg, CancellationToken.None); // a .cutback is not re-probed

        Assert.Contains(doc.Warnings, w => w.Contains("changed", StringComparison.Ordinal));
        Assert.Equal(path, doc.FilePath);
    }

    [Fact]
    public async Task ABadSidecarOpensWithAWarning()
    {
        var video = Path.Combine(_dir, "a.mp4");
        await File.WriteAllBytesAsync(video, [1, 2, 3]);
        await File.WriteAllTextAsync(CaptureSidecar.PathFor(video), "{ not json");
        var path = Path.Combine(_dir, "a.cutback");
        await ProjectSerializer.SaveAsync(Project(video), path, CancellationToken.None);
        var ffmpeg = new Cutback.Media.FfmpegLocation("/x/ffmpeg", "/x/ffprobe", "/x", Cutback.Media.FfmpegLocationSource.Path);

        var doc = await ClipDocument.OpenAsync(path, ffmpeg, CancellationToken.None);

        Assert.Null(doc.Capture);
        Assert.Contains(doc.Warnings, w => w.Contains("sidecar", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0.0, "0:00.0")]
    [InlineData(2.84, "0:02.8")]
    [InlineData(65.31, "1:05.3")]
    [InlineData(3725.0, "1:02:05.0")]
    public void ClockFormatsMinutesSecondsAndTenths(double seconds, string expected) =>
        Assert.Equal(expected, ClipFormat.Clock(seconds));
}
```

The `ApplySilence` arithmetic, with padding at 60 ms per side:
- `[10,12]` becomes `[10.06, 11.94]`, which is 1.88 s.
- `[30,31]` becomes `[30.06, 30.94]`, which is 0.88 s.
- The total is 2.76 s, which displays as `0:02.8`. Five segments remain: kept · cut · kept · cut · kept.

The `Project` helper writes a fake hash of 64 zeros. `SourceHash.MatchesAsync` of a 3-byte file never equals it, so the warning test is deterministic.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter FullyQualifiedName~ClipDocumentTests`
Expected: FAIL (does not compile).

- [ ] **Step 3: Implement**

`MainframeEngine.Editor/Src/Clips/ClipFormat.cs`:

```csharp
using System.Globalization;

namespace MainframeEngine.Editor.Clips;

/// <summary>Durations as the Clips tab shows them: <c>m:ss.t</c>, or <c>h:mm:ss.t</c> from an hour.</summary>
public static class ClipFormat
{
    public static string Clock(double seconds)
    {
        var tenths = (long)Math.Round(Math.Max(0, seconds) * 10, MidpointRounding.AwayFromZero);
        var t = tenths % 10;
        var s = tenths / 10 % 60;
        var m = tenths / 600 % 60;
        var h = tenths / 36000;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}.{t}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}.{t}");
    }
}
```

`MainframeEngine.Editor/Src/Clips/ClipDocument.cs`:

```csharp
using Cutback.Core;
using Cutback.Core.Capture;
using Cutback.Core.Detection;
using Cutback.Core.Models;
using Cutback.Core.Projects;
using Cutback.Media;

namespace MainframeEngine.Editor.Clips;

/// <summary>
/// The Clips tab's document: a Cutback project (<c>.cutback</c>, the Cutback app's own format, so a project
/// round-trips) over a recording that is opened read-only and never modified. Edits go through Cutback.Core's
/// <see cref="SegmentEditor"/>, so they follow the same undo and dirty rules as the app. The recording's
/// <c>.mfcap.json</c> sidecar, when present and valid, enables Detect idle gameplay; a bad one is a warning, never a
/// failed open.
/// </summary>
public sealed class ClipDocument
{
    public const int HistoryLimit = 100;

    private ClipDocument(CutbackProject project, string? projectPath, CaptureSidecar? capture, IReadOnlyList<string> warnings)
    {
        Project = project;
        FilePath = projectPath is null ? null : Path.GetFullPath(projectPath);
        Capture = capture;
        Warnings = warnings;
        Segments = new SegmentList(project.Source.DurationSeconds, project.Segments);
        Editor = new SegmentEditor(Segments, new EditHistory<Segment[]>(HistoryLimit));
        Segments.Changed += (_, _) =>
        {
            Version++;
            Changed?.Invoke();
        };
        Editor.DirtyChanged += (_, _) => DirtyChanged?.Invoke();
    }

    public CutbackProject Project { get; private set; }

    /// <summary>The <c>.cutback</c> file, or null until the document is first saved.</summary>
    public string? FilePath { get; private set; }

    public string SourcePath => Project.Source.Path;

    public double Duration => Project.Source.DurationSeconds;

    public SegmentList Segments { get; }

    public SegmentEditor Editor { get; }

    public CaptureSidecar? Capture { get; }

    /// <summary>The recording's waveform, set on the main thread once the background extraction finishes.</summary>
    public Waveform? Waveform { get; set; }

    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Bumped on every partition change (edit, undo, redo), so views redraw only when needed.</summary>
    public int Version { get; private set; }

    public bool IsDirty => Editor.IsDirty;

    public event Action? Changed;

    public event Action? DirtyChanged;

    /// <summary><c>&lt;video dir&gt;/&lt;video name&gt;.cutback</c>: where Save first suggests.</summary>
    public string DefaultProjectPath => Path.ChangeExtension(SourcePath, ProjectSerializer.FileExtension);

    public static ClipDocument FromProject(CutbackProject project, string? projectPath, CaptureSidecar? capture, IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(warnings);
        return new ClipDocument(project, projectPath, capture, warnings);
    }

    /// <summary>
    /// Opens a <c>.cutback</c> project or a video. A project keeps its saved source info and is not re-probed; it is
    /// refused when its source is missing and opens with a warning when the source changed. A video is probed (it must
    /// have audio, since export splices audio), hashed and starts as one kept segment. The sidecar is read either way.
    /// </summary>
    public static async Task<ClipDocument> OpenAsync(string path, FfmpegLocation ffmpeg, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(ffmpeg);
        var full = Path.GetFullPath(path);
        var warnings = new List<string>();
        CutbackProject project;
        string? projectPath = null;
        if (string.Equals(Path.GetExtension(full), ProjectSerializer.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            project = await ProjectSerializer.LoadAsync(full, cancellationToken).ConfigureAwait(false);
            projectPath = full;
            if (!File.Exists(project.Source.Path))
                throw new FileNotFoundException($"The recording this project trims is missing: {project.Source.Path}", project.Source.Path);
            if (!await SourceHash.MatchesAsync(project.Source.Path, project.Source.Sha256, cancellationToken).ConfigureAwait(false))
                warnings.Add($"{Path.GetFileName(project.Source.Path)} has changed since this project was saved; check the cuts.");
        }
        else
        {
            var info = await new MediaProbe(ffmpeg).ProbeAsync(full, cancellationToken).ConfigureAwait(false);
            if (!info.HasAudio)
                throw new InvalidOperationException($"{Path.GetFileName(full)} has no audio track, so it cannot be trimmed here.");
            var hash = await SourceHash.ComputeAsync(full, cancellationToken).ConfigureAwait(false);
            project = CutbackProject.CreateNew(new SourceInfo(full, hash, info.DurationSeconds, info.Width, info.Height, info.FrameRate));
        }

        CaptureSidecar? capture = null;
        try
        {
            capture = await CaptureSidecarSerializer.TryLoadForVideoAsync(project.Source.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is CaptureSidecarFormatException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"The recording's sidecar could not be read ({e.Message}); Detect idle gameplay is off.");
        }

        return new ClipDocument(project, projectPath, capture, warnings);
    }

    /// <summary>Writes the project (to <paramref name="path"/> for Save As; <c>.cutback</c> is added when missing).</summary>
    public void Save(string? path = null)
    {
        var target = path ?? FilePath ?? throw new InvalidOperationException("The clip has no project file yet; choose where to save it.");
        target = Path.GetFullPath(target);
        if (!target.EndsWith(ProjectSerializer.FileExtension, StringComparison.OrdinalIgnoreCase))
            target += ProjectSerializer.FileExtension;
        var snapshot = Project with { Segments = Segments.Segments.ToList() };
        ProjectSerializer.SaveAsync(snapshot, target, CancellationToken.None).GetAwaiter().GetResult(); // a few KB, written atomically
        Project = snapshot;
        FilePath = target;
        Editor.MarkSaved();
    }

    /// <summary>Detect idle gameplay (one undo step). Returns the status line.</summary>
    public string DetectIdleGameplay()
    {
        var capture = Capture ?? throw new InvalidOperationException("This recording has no Mainframe Engine sidecar.");
        var cuts = GameplayCutPlanner.Plan(capture, Segments.Segments, GameplaySettings.Default, Duration);
        Editor.Edit(s => s.ReplaceOriginCuts(SegmentOrigin.Gameplay, cuts));
        var removed = cuts.Sum(c => c.Duration);
        return cuts.Count == 0
            ? "No idle gameplay found."
            : $"Cut {cuts.Count} stretch{(cuts.Count == 1 ? "" : "es")} of idle gameplay, {ClipFormat.Clock(removed)} removed.";
    }

    /// <summary>Turns detected silences into padded auto cuts (one undo step; manual edits survive). Returns the status line.</summary>
    public string ApplySilence(IReadOnlyList<SilenceSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var settings = DetectionSettings.Default;
        var cuts = SilenceCutPlanner.Plan(spans, settings, Duration);
        Editor.Edit(s => s.ReplaceAutoSegments(cuts));
        Project = Project with { Settings = settings };
        var removed = cuts.Sum(c => c.Duration);
        return cuts.Count == 0
            ? "No silence found."
            : $"Detected {cuts.Count} silence{(cuts.Count == 1 ? "" : "s")}, {ClipFormat.Clock(removed)} removed.";
    }

    /// <summary>Snaps a boundary to the quietest sample within 20 ms (the raw time until the waveform is loaded).</summary>
    public double SnapTime(double time) => Waveform?.SnapToZeroCrossing(time, 0.020) ?? time;
}
```

`Project with { Settings = settings }` does not change the partition, so it does not dirty the document. That is acceptable: the next save records it.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter FullyQualifiedName~ClipDocumentTests`
Expected: PASS (15).

- [ ] **Step 5: Gates and commit**

```bash
just build && just format-check
git add MainframeEngine.Editor/Src/Clips Tests/MainframeEngine.Editor.Tests/Clips
git commit -m "Add ClipDocument: open, edit, detect and save Cutback projects in the editor

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `ClipTimelineView` — zoom, pan, hit-testing and the ruler (pure)

**Files:**
- Create: `MainframeEngine.Editor/Src/Clips/ClipTimelineView.cs`
- Test: `Tests/MainframeEngine.Editor.Tests/Clips/ClipTimelineViewTests.cs`

**Interfaces:**
- Consumes: `Segment`, `SegmentList.IndexAt`.
- Produces:
  ```csharp
  public sealed class ClipTimelineView
  {
      public const float RulerHeight = 22, BoundaryGrab = 6, DragThreshold = 4;
      public const double MinSpanSeconds = 0.25, WheelZoomBase = 1.25, WheelPanFraction = 0.1, FollowLead = 0.1, LabelSpacing = 80;
      public static readonly double[] NiceIntervals;
      public double Duration { get; }  public double Start { get; }  public double Span { get; }  public double End { get; }
      public float Width { get; set; }                     // dp, at least 1
      public int Version { get; }                          // +1 on every view change
      public void Reset(double duration);
      public void SetView(double start, double span);
      public double TimeToX(double time);  public double XToTime(double x);  public double SecondsPerPixel { get; }
      public double ClampTime(double time);
      public void ZoomAbout(double x, double wheelDeltaY);
      public void Pan(double seconds);
      public bool KeepVisible(double time);
      public int BoundaryAt(IReadOnlyList<Segment> segments, float x, float y);
      public int SegmentAt(SegmentList segments, float x, float y);
      public static (double Major, double Minor) RulerIntervals(double pixelsPerSecond);
  }
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
using Cutback.Core;
using Cutback.Core.Models;
using MainframeEngine.Editor.Clips;

namespace MainframeEngine.Editor.Tests.Clips;

public sealed class ClipTimelineViewTests
{
    private static ClipTimelineView View(double duration = 100, float width = 1000)
    {
        var view = new ClipTimelineView { Width = width };
        view.Reset(duration);
        return view;
    }

    private static SegmentList ThreeSegments() => new(10,
    [
        Segment.Create(0, 4, true, SegmentOrigin.Auto),
        Segment.Create(4, 6, false, SegmentOrigin.Auto),
        Segment.Create(6, 10, true, SegmentOrigin.Auto),
    ]);

    [Fact]
    public void ResetShowsTheWholeRecording()
    {
        var view = View();
        Assert.Equal(0, view.Start);
        Assert.Equal(100, view.Span);
        Assert.Equal(500, view.TimeToX(50), 6);
        Assert.Equal(50, view.XToTime(500), 6);
        Assert.Equal(0.1, view.SecondsPerPixel, 9);
    }

    [Fact]
    public void SetViewClampsTheSpanAndTheStart()
    {
        var view = View();
        view.SetView(-5, 0.01);
        Assert.Equal(0, view.Start);
        Assert.Equal(0.25, view.Span);
        view.SetView(99, 10);
        Assert.Equal(90, view.Start);
        view.SetView(0, 500);
        Assert.Equal(100, view.Span);
    }

    [Fact]
    public void TheMinimumSpanNeverExceedsAShortRecording()
    {
        var view = View(duration: 0.1);
        view.SetView(0, 0.01);
        Assert.Equal(0.1, view.Span, 9);
    }

    [Fact]
    public void WheelZoomKeepsTheTimeUnderTheCursor()
    {
        var view = View();
        view.ZoomAbout(500, wheelDeltaY: 1); // 1.25^-1 = 0.8
        Assert.Equal(80, view.Span, 6);
        Assert.Equal(10, view.Start, 6);
        Assert.Equal(50, view.XToTime(500), 6);
    }

    [Fact]
    public void PanMovesTheViewWithinTheRecording()
    {
        var view = View();
        view.SetView(20, 10);
        view.Pan(-30);
        Assert.Equal(0, view.Start);
    }

    [Fact]
    public void KeepVisibleRecentresOnlyWhenThePlayheadLeavesTheView()
    {
        var view = View();
        view.SetView(0, 10);
        Assert.False(view.KeepVisible(5));
        Assert.True(view.KeepVisible(15));
        Assert.Equal(14, view.Start, 6); // 15 − 0.1 × 10
    }

    [Fact]
    public void EveryViewChangeBumpsTheVersion()
    {
        var view = View();
        var v = view.Version;
        view.SetView(10, 10);
        Assert.True(view.Version > v);
    }

    [Fact]
    public void BoundaryHitsNeedToBeBelowTheRulerAndWithinSixDp()
    {
        var segments = ThreeSegments();
        var view = View(duration: 10); // 100 dp per second; boundaries at x 400 and 600
        Assert.Equal(1, view.BoundaryAt(segments.Segments, 404, 50));
        Assert.Equal(-1, view.BoundaryAt(segments.Segments, 406, 50));
        Assert.Equal(2, view.BoundaryAt(segments.Segments, 597, 50));
        Assert.Equal(-1, view.BoundaryAt(segments.Segments, 400, 10)); // in the ruler
    }

    [Fact]
    public void SegmentHitsUseTheTimeUnderThePointer()
    {
        var segments = ThreeSegments();
        var view = View(duration: 10);
        Assert.Equal(0, view.SegmentAt(segments, 100, 50));
        Assert.Equal(1, view.SegmentAt(segments, 500, 50));
        Assert.Equal(2, view.SegmentAt(segments, 999, 50));
        Assert.Equal(-1, view.SegmentAt(segments, 500, 10));
        Assert.Equal(-1, view.SegmentAt(segments, 1001, 50));
    }

    [Theory]
    [InlineData(1000.0, 0.1, 0.02)]
    [InlineData(10.0, 10.0, 2.0)]
    [InlineData(1.0, 120.0, 30.0)]
    [InlineData(0.01, 3600.0, 900.0)]
    public void RulerIntervalsKeepLabelsAtLeast80DpApart(double pxPerSecond, double major, double minor)
    {
        var (ma, mi) = ClipTimelineView.RulerIntervals(pxPerSecond);
        Assert.Equal(major, ma, 9);
        Assert.Equal(minor, mi, 9);
    }
}
```

The `RulerIntervals` cases:
- At 1000 px/s, the interval is 0.1 (0.1 × 1000 = 100 ≥ 80), with a minor of 0.1 / 5.
- At 10 px/s, 5 × 10 = 50 is too small, so the interval is 10, with a minor of 2.
- At 1 px/s, 60 is too small, so the interval is 120, with a minor of 120 / 4 because it is ≥ 60.
- At 0.01 px/s, no interval reaches 80 px, so the last one (3600) is used, with a minor of 900.

- [ ] **Step 2: Run to verify failure**, then implement:

```csharp
using Cutback.Core;
using Cutback.Core.Models;

namespace MainframeEngine.Editor.Clips;

/// <summary>
/// The Clips timeline's visible window and its coordinate maths, in dp. This is a port of the Cutback app's
/// <c>TimelineControl</c> constants so the two timelines feel the same. It is pure, so the canvas, the controller and
/// the tests share one definition.
/// </summary>
public sealed class ClipTimelineView
{
    public const float RulerHeight = 22;
    public const float BoundaryGrab = 6;
    public const float DragThreshold = 4;
    public const double MinSpanSeconds = 0.25;
    public const double WheelZoomBase = 1.25;
    public const double WheelPanFraction = 0.1;
    public const double FollowLead = 0.1;
    public const double LabelSpacing = 80;

    public static readonly double[] NiceIntervals = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200, 1800, 3600];

    private float _width = 1;

    public double Duration { get; private set; }

    public double Start { get; private set; }

    public double Span { get; private set; } = 1;

    public double End => Start + Span;

    /// <summary>The canvas width in dp (at least 1).</summary>
    public float Width
    {
        get => _width;
        set => _width = MathF.Max(1, value);
    }

    public int Version { get; private set; }

    public double SecondsPerPixel => Span / Width;

    /// <summary>Shows the whole recording (zoom is not saved).</summary>
    public void Reset(double duration)
    {
        Duration = Math.Max(0, duration);
        SetView(0, Duration);
    }

    public void SetView(double start, double span)
    {
        var min = Math.Min(MinSpanSeconds, Duration);
        span = Math.Clamp(span, Math.Max(min, 1e-6), Math.Max(Duration, 1e-6));
        start = Math.Clamp(start, 0, Math.Max(0, Duration - span));
        if (start == Start && span == Span)
            return;
        Start = start;
        Span = span;
        Version++;
    }

    public double TimeToX(double time) => (time - Start) / Span * Width;

    public double XToTime(double x) => Start + x / Width * Span;

    public double ClampTime(double time) => Math.Clamp(time, 0, Duration);

    /// <summary>Wheel zoom about <paramref name="x"/>: the time under the cursor stays put.</summary>
    public void ZoomAbout(double x, double wheelDeltaY)
    {
        var anchor = XToTime(x);
        var fraction = x / Width;
        var span = Span * Math.Pow(WheelZoomBase, -wheelDeltaY);
        SetView(anchor - fraction * Math.Clamp(span, Math.Min(MinSpanSeconds, Duration), Duration), span);
    }

    public void Pan(double seconds) => SetView(Start + seconds, Span);

    /// <summary>While playing: re-centres when <paramref name="time"/> leaves the view. Returns whether it moved.</summary>
    public bool KeepVisible(double time)
    {
        if (Span >= Duration || (time >= Start && time <= End))
            return false;
        var version = Version;
        SetView(time - FollowLead * Span, Span);
        return Version != version;
    }

    /// <summary>The boundary (index ≥ 1, the start of segment i) within <see cref="BoundaryGrab"/> dp of x, below the ruler; else -1.</summary>
    public int BoundaryAt(IReadOnlyList<Segment> segments, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (y < RulerHeight)
            return -1;
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 1; i < segments.Count; i++)
        {
            var d = Math.Abs(TimeToX(segments[i].Start) - x);
            if (d < BoundaryGrab && d < bestDistance)
            {
                best = i;
                bestDistance = d;
            }
        }

        return best;
    }

    /// <summary>The segment under (x, y), below the ruler and inside the canvas; else -1.</summary>
    public int SegmentAt(SegmentList segments, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (y < RulerHeight || x < 0 || x > Width)
            return -1;
        return segments.IndexAt(ClampTime(XToTime(x)));
    }

    /// <summary>The first nice interval at least <see cref="LabelSpacing"/> dp wide, and its minor tick (a fifth, or a quarter from a minute).</summary>
    public static (double Major, double Minor) RulerIntervals(double pixelsPerSecond)
    {
        var major = NiceIntervals[^1];
        foreach (var interval in NiceIntervals)
            if (interval * pixelsPerSecond >= LabelSpacing)
            {
                major = interval;
                break;
            }

        return (major, major >= 60 ? major / 4 : major / 5);
    }
}
```

- [ ] **Step 3: Run the tests (PASS, 13), gates, commit**

```bash
git add MainframeEngine.Editor/Src/Clips/ClipTimelineView.cs Tests/MainframeEngine.Editor.Tests/Clips/ClipTimelineViewTests.cs
git commit -m "Add ClipTimelineView: the Clips timeline's zoom, pan, hit-testing and ruler maths

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: `EditorCanvas` base and `ClipTimelineCanvas` (the draw pass)

**Files:**
- Create: `MainframeEngine.Editor/Src/UI/EditorCanvas.cs` (the dp helpers moved out of `SongCanvas`)
- Modify: `MainframeEngine.Editor/Src/Music/SongCanvases.cs` (`SongCanvas : EditorCanvas`; the moved members are deleted)
- Create: `MainframeEngine.Editor/Src/Clips/ClipCanvasState.cs`
- Create: `MainframeEngine.Editor/Src/Clips/ClipTimelineCanvas.cs`
- Test: `Tests/MainframeEngine.Editor.Tests/Clips/ClipTimelineCanvasTests.cs` (column/bucket maths only)

**Interfaces:**
- Consumes: `ClipDocument` (Task 2), `ClipTimelineView` (Task 3), `Waveform.LevelFor`, `WaveformLevel.Peaks`/`SecondsPerBucket`, `CaptureSidecar.Markers`.
- Produces:
  ```csharp
  namespace MainframeEngine.Editor;   // EditorCanvas: shared by song and clip canvases
  [Tool] public abstract class EditorCanvas : Node2D
  {
      public float PixelScale { get; set; }  public Vector2 ViewSize { get; set; }
      protected static Font? TextFont { get; }
      protected void Box(...); protected void Outline(...); protected void Line(...); protected void Text(...);
      protected static Vector4 Rgb(int r, int g, int b, float a = 1); protected static Vector4 WithAlpha(Vector4 c, float a);
  }
  namespace MainframeEngine.Editor.Clips;
  public sealed class ClipCanvasState
  {
      public int HoverSegment { get; set; } = -1;  public int HoverBoundary { get; set; } = -1;  public int DragBoundary { get; set; } = -1;
      public (double Start, double End)? PendingSection { get; set; }
      public double Playhead { get; set; }
  }
  [Tool] public sealed class ClipTimelineCanvas : EditorCanvas
  {
      public ClipTimelineCanvas(ClipDocument document, ClipTimelineView view, ClipCanvasState state);
      public static (int First, int Last) BucketsForColumn(double viewStart, double secondsPerPixel, double secondsPerBucket, int column, int bucketCount);
  }
  ```

- [ ] **Step 1: Extract `EditorCanvas`**

Move these members from `SongCanvas` (`SongCanvases.cs:14-82`) into `MainframeEngine.Editor/Src/UI/EditorCanvas.cs`, in namespace `MainframeEngine.Editor`, with the same bodies: `_font`, `_fontLoaded`, `PixelScale`, `ViewSize`, `TextFont`, `Box`, `Outline`, `Line`, `Text`, `Rgb`, `WithAlpha`. Declare the class `[Tool] public abstract class EditorCanvas : Node2D`, with the summary: "Base of the editor's canvas views drawn into SubViewports (song, clips): helpers that take dp and draw in framebuffer pixels (PixelScale), so text stays sharp on HiDPI screens."

`SongCanvas` becomes `public abstract class SongCanvas : EditorCanvas` and keeps its song-only members (`Colors`, `BarLabels`, `Palette`, the colour constants, `Tab`, `Controller`, `Song`, `TrackColor`, `TryParse`, `BarLabel`, `PlayheadTick`). Add `using MainframeEngine.Editor;` if the namespace needs it.

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter FullyQualifiedName~Music`
Expected: PASS, unchanged.

- [ ] **Step 2: Write the failing test for the waveform column maths**

```csharp
using MainframeEngine.Editor.Clips;

namespace MainframeEngine.Editor.Tests.Clips;

public sealed class ClipTimelineCanvasTests
{
    [Fact]
    public void AColumnCoversTheBucketsUnderItsPixel()
    {
        // 0.01 s per pixel, 0.002 s per bucket: five buckets per column
        Assert.Equal((0, 4), ClipTimelineCanvas.BucketsForColumn(0, 0.01, 0.002, column: 0, bucketCount: 1000));
        Assert.Equal((50, 54), ClipTimelineCanvas.BucketsForColumn(0, 0.01, 0.002, column: 10, bucketCount: 1000));
    }

    [Fact]
    public void ColumnsPastTheEndAreEmptyAndColumnsNeverSpanZeroBuckets()
    {
        Assert.Equal((-1, -1), ClipTimelineCanvas.BucketsForColumn(0, 0.01, 0.002, column: 500, bucketCount: 1000));
        Assert.Equal((5, 5), ClipTimelineCanvas.BucketsForColumn(0, 0.001, 0.002, column: 10, bucketCount: 1000)); // zoomed in: one bucket
    }
}
```

- [ ] **Step 3: Implement `ClipCanvasState` and `ClipTimelineCanvas`**

`ClipCanvasState.cs`:

```csharp
namespace MainframeEngine.Editor.Clips;

/// <summary>What the controller tells the canvas to highlight (hover, the dragged boundary, the section being drawn, the playhead).</summary>
public sealed class ClipCanvasState
{
    public int HoverSegment { get; set; } = -1;

    public int HoverBoundary { get; set; } = -1;

    public int DragBoundary { get; set; } = -1;

    public (double Start, double End)? PendingSection { get; set; }

    /// <summary>Source seconds.</summary>
    public double Playhead { get; set; }
}
```

`ClipTimelineCanvas.cs` is a port of Cutback's `TimelineControl.DrawOperation.Render`. Draw in this order, in dp, with the Global Constraints colours:

```csharp
using System.Globalization;
using System.Numerics;
using Cutback.Media;

namespace MainframeEngine.Editor.Clips;

/// <summary>
/// The Clips timeline, drawn with the engine's 2D canvas into the tab's SubViewport. This is a port of the Cutback app's
/// <c>TimelineControl</c> draw pass, so the two look the same. In order, it draws:
/// <list type="bullet">
/// <item>kept and removed segments, with hover;</item>
/// <item>the reason for each removed segment wider than 70 dp;</item>
/// <item>the waveform, one column per device pixel, coloured by the segment under it;</item>
/// <item>the boundaries, with the hovered or dragged one highlighted;</item>
/// <item>the section being drawn;</item>
/// <item>the ruler;</item>
/// <item>the Mainframe Engine markers;</item>
/// <item>the playhead.</item>
/// </list>
/// </summary>
[Tool]
public sealed class ClipTimelineCanvas : EditorCanvas
{
    private static readonly Vector4 RulerBackground = Rgb(0x18, 0x18, 0x1C);
    private static readonly Vector4 BodyBackground = Rgb(0x12, 0x12, 0x15);
    private static readonly Vector4 Kept = Rgb(0x1D, 0x2E, 0x4D);
    private static readonly Vector4 KeptHover = Rgb(0x26, 0x40, 0x6A);
    private static readonly Vector4 Removed = Rgb(0x1F, 0x1F, 0x24);
    private static readonly Vector4 RemovedHover = Rgb(0x2A, 0x2A, 0x31);
    private static readonly Vector4 KeptWave = Rgb(0x6F, 0xA3, 0xF0);
    private static readonly Vector4 RemovedWave = Rgb(0x45, 0x45, 0x4E);
    private static readonly Vector4 Boundary = Rgb(0xC8, 0xC8, 0xD2, 0xB0 / 255f);
    private static readonly Vector4 BoundaryActive = Rgb(0xFF, 0xD1, 0x66);
    private static readonly Vector4 SectionFill = Rgb(0xFF, 0xD1, 0x66, 0x38 / 255f);
    private static readonly Vector4 Playhead = Rgb(0xFF, 0x5C, 0x5C);
    private static readonly Vector4 Tick = Rgb(0x4E, 0x4E, 0x58);
    private static readonly Vector4 Label = Rgb(0x9A, 0x9A, 0xA6);
    private static readonly Vector4 ReasonText = Rgb(0x7C, 0x7C, 0x88);
    private static readonly Vector4 Hint = Rgb(0x60, 0x60, 0x6A);
    private static readonly Vector4 Marker = Rgb(0xB4, 0x8C, 0xFF);

    private readonly ClipDocument _document;
    private readonly ClipTimelineView _view;
    private readonly ClipCanvasState _state;

    public ClipTimelineCanvas(ClipDocument document, ClipTimelineView view, ClipCanvasState state)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>Waveform buckets [First, Last] under one pixel column; (-1, -1) past the end. Always at least one bucket.</summary>
    public static (int First, int Last) BucketsForColumn(double viewStart, double secondsPerPixel, double secondsPerBucket, int column, int bucketCount)
    {
        var first = (int)Math.Floor((viewStart + column * secondsPerPixel) / secondsPerBucket);
        if (first >= bucketCount || first < 0)
            return (-1, -1);
        var last = (int)Math.Floor((viewStart + (column + 1) * secondsPerPixel) / secondsPerBucket) - 1;
        return (first, Math.Clamp(last, first, bucketCount - 1));
    }

    protected override void OnDraw()
    {
        var w = ViewSize.X;
        var h = ViewSize.Y;
        Box(0, 0, w, ClipTimelineView.RulerHeight, RulerBackground);
        Box(0, ClipTimelineView.RulerHeight, w, h - ClipTimelineView.RulerHeight, BodyBackground);
        if (_document.Duration <= 0)
        {
            Text("Open a recording to see its timeline", 12, h / 2, 13, Hint);
            return;
        }

        DrawSegments(w, h);
        DrawWaveform(w, h);
        DrawBoundaries(h);
        DrawPendingSection(h);
        DrawRuler(w);
        DrawMarkers();
        DrawPlayhead(h);
    }

    // DrawSegments: one Box per segment from TimeToX(Start) to TimeToX(End), clipped to [0, w], below the ruler. The
    // colour is Kept/Removed, or the Hover variant when i == _state.HoverSegment. A removed segment wider than 70 dp
    // with a Reason draws Text(reason, x0 + 6, top + 14, 11, ReasonText); skip any text that would start past w.
    //
    // DrawWaveform: if _document.Waveform is null, return. Otherwise:
    //   - level = waveform.LevelFor(_view.SecondsPerPixel / PixelScale), i.e. per device pixel;
    //   - for each device pixel column c in [0, w * PixelScale):
    //       - (first, last) = BucketsForColumn(_view.Start, spp / PixelScale, level.SecondsPerBucket, c, level.Peaks.Count);
    //       - compute min/max over the peaks in [first, last];
    //       - draw a line at x = c / PixelScale from mid − max·scale to mid − min·scale, where
    //         mid = RulerHeight + (h − RulerHeight) / 2, scale = ((h − RulerHeight) / 2 − 4) / 32768,
    //         and each half is at least 1 / PixelScale dp;
    //       - the colour is KeptWave or RemovedWave by the segment under the column's time, advancing a segment index
    //         monotonically (never IndexAt per column).
    //
    // DrawBoundaries: for i ≥ 1, a line at x = TimeToX(segments[i].Start), rounded to a device-pixel centre, below the
    // ruler. Use Boundary at 1 dp, or BoundaryActive at 2 dp for i == HoverBoundary or i == DragBoundary.
    //
    // DrawPendingSection: when PendingSection is set, a Box with SectionFill and 1 dp BoundaryActive edges.
    //
    // DrawRuler:
    //   - (major, minor) = RulerIntervals(1 / SecondsPerPixel); ticks use integer indices
    //     (k from floor(Start / minor) to ceil(End / minor)) so they never drift;
    //   - a tick is a major when k % round(major / minor) == 0: 10 dp tall from the ruler bottom, with
    //     Text(ClipFormat.Ruler(t), x + 3, 11, 10, Label); otherwise 5 dp tall;
    //   - a 1 dp Tick line along the ruler bottom.
    //
    // DrawMarkers: for each _document.Capture?.Markers inside the view, a downward triangle 8 dp wide from
    // y = RulerHeight − 9 to RulerHeight − 1 in Marker (two Lines per row, or three Boxes stepping in), and its label
    // with Text(label, x + 6, RulerHeight − 2, 10, Marker).
    //
    // DrawPlayhead: x = TimeToX(_state.Playhead). A 1.5 dp Playhead line over the full height and a downward triangle
    // head 12 dp wide and 8 dp tall at the top. Skip it when off-screen.
}
```

Write each `Draw*` method from its comment above (replace the comments with code). Two notes:
- `CanvasItem` has no triangle primitive, so draw triangles as horizontal `Line`s per dp row. That is 8 rows each, which is cheap.
- Add `public static string Ruler(double seconds)` to `ClipFormat`, returning `m:ss`, or `h:mm:ss` from an hour. Add a theory to `ClipDocumentTests`: `0 → "0:00"`, `75 → "1:15"`, `3725 → "1:02:05"`.

- [ ] **Step 4: Tests, gates, commit**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Clips|FullyQualifiedName~Music"`
Expected: PASS.

```bash
git add MainframeEngine.Editor/Src Tests/MainframeEngine.Editor.Tests/Clips
git commit -m "Add the Clips timeline canvas, sharing the dp drawing helpers with the song tab

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: `ClipCanvasController` — the timeline gestures

**Files:**
- Create: `MainframeEngine.Editor/Src/Clips/ClipCanvasController.cs`
- Test: `Tests/MainframeEngine.Editor.Tests/Clips/ClipCanvasControllerTests.cs`

**Interfaces:**
- Consumes: `ClipDocument` (`Editor`, `Segments`, `SnapTime`), `ClipTimelineView`, `ClipCanvasState`, `EditorModifiers` (`MainframeEngine.Editor`), `Silk.NET.Input.Key`.
- Produces:
  ```csharp
  public enum ClipGesture { None, Pending, Scrub, Pan, Boundary, Section }
  public sealed class ClipCanvasController
  {
      public ClipCanvasController(ClipDocument document, ClipTimelineView view, ClipCanvasState state);
      public ClipGesture Gesture { get; }
      public Action<double>? Seek { get; set; }                        // source seconds
      public Action<int, float, float>? ShowSegmentMenu { get; set; }  // segment index, local dp x, y
      public void Hover(Vector2 p);
      public void Press(Vector2 p, EditorModifiers modifiers);
      public void Drag(Vector2 p, EditorModifiers modifiers);
      public void Release(Vector2 p);
      public void CancelGesture();
      public void RightClick(Vector2 p);
      public void Wheel(Vector2 p, Vector2 delta, EditorModifiers modifiers);
      public bool DeleteHovered();
      public bool Key(Key key, EditorModifiers modifiers);
  }
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Numerics;
using Cutback.Core;
using Cutback.Core.Models;
using MainframeEngine.Editor.Clips;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests.Clips;

public sealed class ClipCanvasControllerTests
{
    // 10 s over 1000 dp: 100 dp per second; the body starts at y 22
    private readonly ClipDocument _doc;
    private readonly ClipTimelineView _view = new() { Width = 1000 };
    private readonly ClipCanvasState _state = new();
    private readonly ClipCanvasController _controller;
    private readonly List<double> _seeks = [];

    public ClipCanvasControllerTests()
    {
        var project = ClipDocumentTests.Project("/r/a.mp4", duration: 10) with
        {
            Segments =
            [
                Segment.Create(0, 4, true, SegmentOrigin.Auto),
                Segment.Create(4, 6, false, SegmentOrigin.Auto),
                Segment.Create(6, 10, true, SegmentOrigin.Auto),
            ],
        };
        _doc = ClipDocument.FromProject(project, null, null, []);
        _view.Reset(10);
        _controller = new ClipCanvasController(_doc, _view, _state) { Seek = _seeks.Add };
    }

    private void Click(float x, float y, EditorModifiers m = EditorModifiers.None)
    {
        _controller.Press(new Vector2(x, y), m);
        _controller.Release(new Vector2(x, y));
    }

    [Fact]
    public void AClickTogglesTheSegmentUnderIt()
    {
        Click(500, 50);
        Assert.True(_doc.Segments.Segments[1].Enabled);
        Assert.Equal(SegmentOrigin.Manual, _doc.Segments.Segments[1].Origin);
    }

    [Fact]
    public void ASmallWobbleIsStillAClick()
    {
        _controller.Press(new Vector2(500, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(503, 50), EditorModifiers.None);
        _controller.Release(new Vector2(503, 50));
        Assert.True(_doc.Segments.Segments[1].Enabled);
    }

    [Fact]
    public void DraggingABoundaryMovesItAsOneUndoStep()
    {
        _controller.Press(new Vector2(402, 50), EditorModifiers.None);
        Assert.Equal(ClipGesture.Boundary, _controller.Gesture);
        Assert.Equal(1, _state.DragBoundary);
        _controller.Drag(new Vector2(380, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(350, 50), EditorModifiers.None);
        _controller.Release(new Vector2(350, 50));

        Assert.Equal(3.5, _doc.Segments.Segments[1].Start, 6); // no waveform: unsnapped
        Assert.Equal(-1, _state.DragBoundary);
        Assert.True(_doc.Editor.Undo());
        Assert.Equal(4, _doc.Segments.Segments[1].Start, 6);
        Assert.False(_doc.Editor.Undo());
    }

    [Fact]
    public void DraggingInTheBodyDrawsASectionWithTheOppositeState()
    {
        _controller.Press(new Vector2(100, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(300, 50), EditorModifiers.None);
        Assert.Equal(ClipGesture.Section, _controller.Gesture);
        Assert.Equal((1.0, 3.0), _state.PendingSection);
        _controller.Release(new Vector2(300, 50));

        var cut = _doc.Segments.Segments[_doc.Segments.IndexAt(2)];
        Assert.False(cut.Enabled);
        Assert.Equal(1, cut.Start, 6);
        Assert.Equal(3, cut.End, 6);
        Assert.Null(_state.PendingSection);
    }

    [Fact]
    public void ADragRightToLeftDrawsTheSameSection()
    {
        _controller.Press(new Vector2(300, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(100, 50), EditorModifiers.None);
        _controller.Release(new Vector2(100, 50));
        Assert.False(_doc.Segments.Segments[_doc.Segments.IndexAt(2)].Enabled);
    }

    [Fact]
    public void ShiftDragPans()
    {
        _view.SetView(2, 5); // 0.005 s per dp
        _controller.Press(new Vector2(500, 50), EditorModifiers.Shift);
        _controller.Drag(new Vector2(600, 50), EditorModifiers.Shift);
        Assert.Equal(ClipGesture.Pan, _controller.Gesture);
        Assert.Equal(1.5, _view.Start, 6);
        _controller.Release(new Vector2(600, 50));
        Assert.False(_doc.Editor.History.CanUndo);
    }

    [Fact]
    public void PressingTheRulerScrubs()
    {
        _controller.Press(new Vector2(250, 10), EditorModifiers.None);
        _controller.Drag(new Vector2(300, 10), EditorModifiers.None);
        _controller.Release(new Vector2(300, 10));
        Assert.Equal([2.5, 3.0], _seeks);
        Assert.False(_doc.Editor.History.CanUndo);
    }

    [Fact]
    public void TheWheelZoomsAboutTheCursorAndShiftWheelPans()
    {
        _controller.Wheel(new Vector2(500, 50), new Vector2(0, 1), EditorModifiers.None);
        Assert.Equal(8, _view.Span, 6);
        Assert.Equal(1, _view.Start, 6);
        _controller.Wheel(new Vector2(500, 50), new Vector2(0, 1), EditorModifiers.Shift);
        Assert.Equal(1.8, _view.Start, 6); // pans by deltaY × span × 0.1
    }

    [Fact]
    public void HoverTracksTheBoundaryFirstThenTheSegment()
    {
        _controller.Hover(new Vector2(401, 50));
        Assert.Equal(1, _state.HoverBoundary);
        Assert.Equal(-1, _state.HoverSegment);
        _controller.Hover(new Vector2(500, 50));
        Assert.Equal(-1, _state.HoverBoundary);
        Assert.Equal(1, _state.HoverSegment);
    }

    [Fact]
    public void DeleteDissolvesTheHoveredSegment()
    {
        _controller.Hover(new Vector2(500, 50));
        Assert.True(_controller.Key(Key.Delete, EditorModifiers.None));
        Assert.Single(_doc.Segments.Segments);
        _controller.Hover(new Vector2(500, 5)); // over the ruler: nothing hovered
        Assert.False(_controller.Key(Key.Backspace, EditorModifiers.None));
    }

    [Fact]
    public void RightClickOnASegmentAsksForItsMenu()
    {
        (int Index, float X, float Y)? asked = null;
        _controller.ShowSegmentMenu = (i, x, y) => asked = (i, x, y);
        _controller.RightClick(new Vector2(500, 50));
        Assert.Equal((1, 500f, 50f), asked);
        asked = null;
        _controller.RightClick(new Vector2(500, 5));
        Assert.Null(asked);
    }

    [Fact]
    public void CancelDuringABoundaryDragRecordsOneStep()
    {
        _controller.Press(new Vector2(402, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(350, 50), EditorModifiers.None);
        _controller.CancelGesture();

        Assert.Equal(ClipGesture.None, _controller.Gesture);
        Assert.False(_doc.Editor.IsDragging);
        Assert.Equal(3.5, _doc.Segments.Segments[1].Start, 6);
        Assert.True(_doc.Editor.Undo());
        Assert.Equal(4, _doc.Segments.Segments[1].Start, 6);
    }

    [Fact]
    public void CancelWhileDrawingDropsTheSection()
    {
        _controller.Press(new Vector2(100, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(300, 50), EditorModifiers.None);
        _controller.CancelGesture();
        Assert.Null(_state.PendingSection);
        Assert.False(_doc.Editor.History.CanUndo);
    }
}
```

The wheel case: zooming at x = 500 with delta 1 sets the span to 8. The anchor at 5 s stays under the cursor, so the start becomes 5 − 0.5 × 8 = 1. Shift with a Y delta of 1 then pans by 1 × 8 × 0.1 = 0.8, so the start becomes 1.8.

- [ ] **Step 2: Run to verify failure**, then implement:

```csharp
using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Clips;

public enum ClipGesture { None, Pending, Scrub, Pan, Boundary, Section }

/// <summary>
/// The Clips timeline's gestures, ported from the Cutback app's <c>TimelineControl</c> so the two behave the same.
/// Positions are dp, local to the canvas.
/// <list type="bullet">
/// <item>Click a segment: toggle it.</item>
/// <item>Drag a boundary: move it, snapped to the quietest sample on release; one undo step.</item>
/// <item>Drag in the body: draw a section with the opposite state of where the drag started.</item>
/// <item>Shift+drag: pan.</item>
/// <item>Press or drag in the ruler: scrub.</item>
/// <item>Wheel: zoom about the cursor. Shift or a horizontal wheel: pan.</item>
/// <item>Right-click: the segment's menu.</item>
/// <item>Delete/Backspace: dissolve the hovered segment.</item>
/// </list>
/// </summary>
public sealed class ClipCanvasController
{
    private readonly ClipDocument _doc;
    private readonly ClipTimelineView _view;
    private readonly ClipCanvasState _state;
    private Vector2 _press;
    private double _anchor;
    private double _panStart;

    public ClipCanvasController(ClipDocument document, ClipTimelineView view, ClipCanvasState state)
    {
        _doc = document ?? throw new ArgumentNullException(nameof(document));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public ClipGesture Gesture { get; private set; }

    /// <summary>Called with source seconds when the ruler is scrubbed.</summary>
    public Action<double>? Seek { get; set; }

    /// <summary>Called on a right-click over a segment: (segment index, local x, local y).</summary>
    public Action<int, float, float>? ShowSegmentMenu { get; set; }

    public void Hover(Vector2 p)
    {
        if (Gesture != ClipGesture.None)
            return;
        var boundary = _view.BoundaryAt(_doc.Segments.Segments, p.X, p.Y);
        _state.HoverBoundary = boundary;
        _state.HoverSegment = boundary >= 0 ? -1 : _view.SegmentAt(_doc.Segments, p.X, p.Y);
    }

    public void Press(Vector2 p, EditorModifiers modifiers)
    {
        CancelGesture();
        _press = p;
        if (p.Y < ClipTimelineView.RulerHeight)
        {
            Gesture = ClipGesture.Scrub;
            Seek?.Invoke(_view.ClampTime(_view.XToTime(p.X)));
            return;
        }

        var boundary = _view.BoundaryAt(_doc.Segments.Segments, p.X, p.Y);
        if (boundary >= 0)
        {
            Gesture = ClipGesture.Boundary;
            _state.DragBoundary = boundary;
            _doc.Editor.BeginBoundaryDrag();
            return;
        }

        Gesture = ClipGesture.Pending;
    }

    public void Drag(Vector2 p, EditorModifiers modifiers)
    {
        switch (Gesture)
        {
            case ClipGesture.Scrub:
                Seek?.Invoke(_view.ClampTime(_view.XToTime(p.X)));
                break;
            case ClipGesture.Boundary:
                _doc.Editor.UpdateBoundaryDrag(_state.DragBoundary, _view.XToTime(p.X));
                break;
            case ClipGesture.Pending when Math.Abs(p.X - _press.X) > ClipTimelineView.DragThreshold:
                if ((modifiers & EditorModifiers.Shift) != 0)
                {
                    Gesture = ClipGesture.Pan;
                    _panStart = _view.Start;
                }
                else
                {
                    Gesture = ClipGesture.Section;
                    _anchor = _view.ClampTime(_view.XToTime(_press.X));
                }

                Drag(p, modifiers);
                break;
            case ClipGesture.Section:
                var t = _view.ClampTime(_view.XToTime(p.X));
                _state.PendingSection = (Math.Min(_anchor, t), Math.Max(_anchor, t));
                break;
            case ClipGesture.Pan:
                _view.SetView(_panStart - (p.X - _press.X) * _view.SecondsPerPixel, _view.Span);
                break;
        }
    }

    public void Release(Vector2 p)
    {
        switch (Gesture)
        {
            case ClipGesture.Pending:
                _doc.Editor.Toggle(_view.SegmentAt(_doc.Segments, p.X, p.Y));
                break;
            case ClipGesture.Boundary:
                _doc.Editor.EndBoundaryDrag(_state.DragBoundary, _doc.SnapTime(_view.XToTime(p.X)));
                break;
            case ClipGesture.Section when _state.PendingSection is { } section:
                var start = _view.ClampTime(_doc.SnapTime(section.Start));
                var end = _view.ClampTime(_doc.SnapTime(section.End));
                if (end > start)
                    _doc.Editor.CreateSection(start, end, _anchor);
                break;
        }

        Reset();
    }

    /// <summary>The pointer was lost (or another tab activated): a boundary drag ends where it is, unsnapped, as one step; a section is dropped.</summary>
    public void CancelGesture()
    {
        if (Gesture == ClipGesture.Boundary && _doc.Editor.IsDragging)
            _doc.Editor.EndBoundaryDrag(_state.DragBoundary, _doc.Segments.Segments[_state.DragBoundary].Start);
        Reset();
    }

    public void RightClick(Vector2 p)
    {
        var segment = _view.SegmentAt(_doc.Segments, p.X, p.Y);
        if (segment >= 0)
        {
            _state.HoverSegment = segment;
            ShowSegmentMenu?.Invoke(segment, p.X, p.Y);
        }
    }

    public void Wheel(Vector2 p, Vector2 delta, EditorModifiers modifiers)
    {
        if (delta.X != 0)
            _view.Pan(delta.X * _view.Span * ClipTimelineView.WheelPanFraction);
        else if ((modifiers & EditorModifiers.Shift) != 0)
            _view.Pan(delta.Y * _view.Span * ClipTimelineView.WheelPanFraction);
        else if (delta.Y != 0)
            _view.ZoomAbout(p.X, delta.Y);
    }

    /// <summary>Dissolves the hovered segment into its neighbours (Delete, Backspace, or the menu's Delete).</summary>
    public bool DeleteHovered()
    {
        var index = _state.HoverSegment;
        if (!_doc.Editor.Dissolve(index))
            return false;
        _state.HoverSegment = -1;
        return true;
    }

    public bool Key(Key key, EditorModifiers modifiers) => key switch
    {
        Silk.NET.Input.Key.Delete or Silk.NET.Input.Key.Backspace when modifiers == EditorModifiers.None => DeleteHovered(),
        _ => false,
    };

    private void Reset()
    {
        Gesture = ClipGesture.None;
        _state.DragBoundary = -1;
        _state.PendingSection = null;
    }
}
```

Check `Toggle(-1)`: when the click lands outside the segments, `SegmentEditor.Toggle` returns false and does nothing ✓.

- [ ] **Step 3: Tests (PASS, 13), gates, commit**

```bash
git add MainframeEngine.Editor/Src/Clips/ClipCanvasController.cs Tests/MainframeEngine.Editor.Tests/Clips/ClipCanvasControllerTests.cs
git commit -m "Add the Clips timeline gestures (toggle, boundary drag, sections, pan, scrub, zoom, delete)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: `ClipPlaybackPlan` — what plays when (pure)

**Files:**
- Create: `MainframeEngine.Editor/Src/Clips/ClipPlaybackPlan.cs`
- Test: `Tests/MainframeEngine.Editor.Tests/Clips/ClipPlaybackPlanTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public readonly record struct PlayRange(double Start, double End) { public double Length => End - Start; }
  public sealed class ClipPlaybackPlan
  {
      public const double PrefetchSeconds = 1.0;
      public static ClipPlaybackPlan Build(IReadOnlyList<Segment> segments, double from);
      public IReadOnlyList<PlayRange> Ranges { get; }
      public double OutputDuration { get; }
      public double OutputStartOf(int range);
      public int RangeAt(double output);              // -1 when finished
      public double SourceTimeAt(double output);      // the last range's end once finished
      public bool IsFinished(double output);
      public bool ShouldPrefetch(int range, double output);   // start range+1's decoder now?
  }
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
using Cutback.Core.Models;
using MainframeEngine.Editor.Clips;

namespace MainframeEngine.Editor.Tests.Clips;

public sealed class ClipPlaybackPlanTests
{
    // kept [0,4) · cut [4,6) · kept [6,10)
    private static readonly Segment[] Segments =
    [
        Segment.Create(0, 4, true, SegmentOrigin.Auto),
        Segment.Create(4, 6, false, SegmentOrigin.Auto),
        Segment.Create(6, 10, true, SegmentOrigin.Auto),
    ];

    [Fact]
    public void FromTheStartEveryKeptSegmentPlaysInOrder()
    {
        var plan = ClipPlaybackPlan.Build(Segments, 0);
        Assert.Equal([new PlayRange(0, 4), new PlayRange(6, 10)], plan.Ranges);
        Assert.Equal(8, plan.OutputDuration, 9);
        Assert.Equal(4, plan.OutputStartOf(1), 9);
    }

    [Fact]
    public void FromInsideAKeptSegmentItsFirstRangeStartsAtThePlayhead()
    {
        var plan = ClipPlaybackPlan.Build(Segments, 2);
        Assert.Equal([new PlayRange(2, 4), new PlayRange(6, 10)], plan.Ranges);
    }

    [Fact]
    public void FromInsideACutPlaybackStartsAtTheNextKeptSegment()
    {
        var plan = ClipPlaybackPlan.Build(Segments, 5);
        Assert.Equal([new PlayRange(6, 10)], plan.Ranges);
    }

    [Fact]
    public void FromTheEndThereIsNothingToPlay()
    {
        var plan = ClipPlaybackPlan.Build(Segments, 10);
        Assert.Empty(plan.Ranges);
        Assert.True(plan.IsFinished(0));
        Assert.Equal(-1, plan.RangeAt(0));
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(3.0, 3.0)]
    [InlineData(4.0, 6.0)]
    [InlineData(5.0, 7.0)]
    [InlineData(8.0, 10.0)]
    [InlineData(9.0, 10.0)]
    public void OutputTimeMapsOverTheCut(double output, double source)
    {
        Assert.Equal(source, ClipPlaybackPlan.Build(Segments, 0).SourceTimeAt(output), 9);
    }

    [Fact]
    public void TheBoundaryBelongsToTheNextRange()
    {
        var plan = ClipPlaybackPlan.Build(Segments, 0);
        Assert.Equal(0, plan.RangeAt(3.999));
        Assert.Equal(1, plan.RangeAt(4));
        Assert.True(plan.IsFinished(8));
    }

    [Fact]
    public void TheNextDecoderStartsOneSecondEarly()
    {
        var plan = ClipPlaybackPlan.Build(Segments, 0);
        Assert.False(plan.ShouldPrefetch(0, 2.9));
        Assert.True(plan.ShouldPrefetch(0, 3.0));
        Assert.False(plan.ShouldPrefetch(1, 7.5)); // the last range has nothing after it
    }

    [Fact]
    public void SliversShorterThanAMillisecondAreSkipped()
    {
        Segment[] segments = [Segment.Create(0, 4, true, SegmentOrigin.Auto), Segment.Create(4, 10, false, SegmentOrigin.Auto)];
        Assert.Empty(ClipPlaybackPlan.Build(segments, 3.9995).Ranges);
    }
}
```

- [ ] **Step 2: Run to verify failure**, then implement:

```csharp
using Cutback.Core;
using Cutback.Core.Models;

namespace MainframeEngine.Editor.Clips;

/// <summary>A kept stretch of the source, in source seconds.</summary>
public readonly record struct PlayRange(double Start, double End)
{
    public double Length => End - Start;
}

/// <summary>
/// What the preview plays from a given playhead: the kept segments from there on, laid end to end on an output
/// timeline. The audio clock counts output seconds, and this maps them back to the source frame to show. The next
/// range's video decoder starts <see cref="PrefetchSeconds"/> before the current range ends, so the switch has no gap.
/// </summary>
public sealed class ClipPlaybackPlan
{
    public const double PrefetchSeconds = 1.0;

    private readonly PlayRange[] _ranges;
    private readonly double[] _outputStarts;

    private ClipPlaybackPlan(PlayRange[] ranges)
    {
        _ranges = ranges;
        _outputStarts = new double[ranges.Length];
        var t = 0.0;
        for (var i = 0; i < ranges.Length; i++)
        {
            _outputStarts[i] = t;
            t += ranges[i].Length;
        }

        OutputDuration = t;
    }

    public IReadOnlyList<PlayRange> Ranges => _ranges;

    public double OutputDuration { get; }

    public static ClipPlaybackPlan Build(IReadOnlyList<Segment> segments, double from)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var ranges = new List<PlayRange>();
        foreach (var segment in segments)
        {
            if (!segment.Enabled || segment.End <= from)
                continue;
            var start = Math.Max(segment.Start, from);
            if (segment.End - start >= SegmentList.MinSegmentLength)
                ranges.Add(new PlayRange(start, segment.End));
        }

        return new ClipPlaybackPlan([.. ranges]);
    }

    public double OutputStartOf(int range) => _outputStarts[range];

    public bool IsFinished(double output) => output >= OutputDuration;

    public int RangeAt(double output)
    {
        if (IsFinished(output))
            return -1;
        for (var i = _ranges.Length - 1; i >= 0; i--)
            if (output >= _outputStarts[i])
                return i;
        return 0;
    }

    public double SourceTimeAt(double output)
    {
        if (_ranges.Length == 0)
            return 0;
        var i = RangeAt(Math.Max(0, output));
        return i < 0 ? _ranges[^1].End : _ranges[i].Start + (Math.Max(0, output) - _outputStarts[i]);
    }

    public bool ShouldPrefetch(int range, double output) =>
        range >= 0 && range + 1 < _ranges.Length && output >= _outputStarts[range] + _ranges[range].Length - PrefetchSeconds;
}
```

For an empty plan, the Build-from-10 test expects `SourceTimeAt` to return 0. The preview handles that case by not playing at all.

- [ ] **Step 3: Tests (PASS, 13), gates, commit**

```bash
git add MainframeEngine.Editor/Src/Clips/ClipPlaybackPlan.cs Tests/MainframeEngine.Editor.Tests/Clips/ClipPlaybackPlanTests.cs
git commit -m "Add ClipPlaybackPlan: the kept ranges from the playhead on an output timeline

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: `ClipPreview` — decoders, texture, audio voice and the clock

**Files:**
- Create: `MainframeEngine.Editor/Src/Clips/ClipDecoders.cs` (the process seam)
- Create: `MainframeEngine.Editor/Src/Clips/ClipPreview.cs`
- Test: `Tests/MainframeEngine.Editor.Tests/Clips/ClipPreviewTests.cs` (with a fake decoder factory and no audio server)

**Interfaces:**
- Consumes: `PreviewDecoding` (all members), `FfmpegProcess.Start`/`TryKill`, `ClipPlaybackPlan`, `AudioServer` (`SampleRate`, `PlayOneShot`, `GetGeneratorPlayback`, `Stop`, `IsPlaying`), `AudioStreamGenerator`, `AudioStreamGeneratorPlayback` (`PushFrames`, `FramesAvailable`, `FramesConsumed`, `ClearBuffer`), `AudioBusLayout.MasterBus`, `ProcessMode.Always`, `SubViewport`, `Sprite2D`, `Texture2D.FromPixels`/`SetPixels`.
- Produces:
  ```csharp
  /// <summary>One ffmpeg decode streaming raw bytes from stdout (or a fake in tests).</summary>
  public interface IClipDecoder : IDisposable
  {
      Stream Output { get; }      // raw bytes; ends at EOF
      bool Failed { get; }        // exited non-zero (stderr is logged by the implementation)
  }
  public interface IClipDecoderFactory { IClipDecoder Start(IReadOnlyList<string> arguments); }
  public sealed class FfmpegClipDecoderFactory(string ffmpegPath) : IClipDecoderFactory;
  public sealed class ClipPreview : IDisposable
  {
      public ClipPreview(IClipDecoderFactory decoders, Func<AudioServer?> audio, string sourcePath, int sourceWidth, int sourceHeight, double sourceFrameRate, int number);
      public SubViewport Viewport { get; }
      public int Width { get; }  public int Height { get; }
      public bool IsPlaying { get; }
      public double Position { get; }                  // source seconds
      public void Play(IReadOnlyList<Segment> segments, double from, int segmentsVersion = 0);
      public void Pause();
      public void Seek(double time);                   // shows that frame (a one-frame decode, latest wins)
      public void Update(IReadOnlyList<Segment> segments, int segmentsVersion);   // main thread, every frame
      internal int LiveDecoders { get; }               // for tests
      public void Dispose();
  }
  ```

**Design (the implementer follows it exactly):**
- **Video.** One `VideoStream` per started range. A reader thread reads exactly `Width × Height × 4` bytes per frame into buffers from a pool of 6 preallocated `byte[]`s. Each frame goes on a `ConcurrentQueue<(double OutputTime, byte[] Frame)>`, where `OutputTime = plan.OutputStartOf(range) + n / frameRate`.
  - A full pool blocks the reader (`SemaphoreSlim`), which back-pressures ffmpeg.
  - `Update` takes the latest queued frame whose `OutputTime ≤ clock`, returns older buffers to the pool, and calls `_texture.SetPixels(frame)` at most once per `Update`.
  - Range `r + 1`'s stream starts when `plan.ShouldPrefetch(r, clock)` is true. A stream is disposed (process killed) when the clock passes its range's end.
- **Audio.** When an `AudioServer` exists, a single background task decodes range after range with `PreviewDecoding.AudioArguments(source, r.Start, r.End, server.SampleRate)`. It pushes into one `AudioStreamGenerator { MixRate = server.SampleRate, BufferSeconds = 1.0f }` voice started with `PlayOneShot(stream, AudioBusLayout.MasterBus, priority: int.MaxValue, processMode: ProcessMode.Always)`.
  - The task reads 4096-frame chunks (`4096 × 8` bytes) and calls `PushFrames` once `FramesAvailable ≥ chunk`, sleeping 2 ms otherwise.
  - Keep a byte remainder so pushes are whole stereo frames.
- **Clock.** With audio, `output = (playback.FramesConsumed − consumedAtStart) / sampleRate`. Without an `AudioServer` (headless tests, no device), use a `Stopwatch` from `Play`. `Position = plan.SourceTimeAt(output)`. When `plan.IsFinished(output)`, `Pause()` at the last range's end.
- **Edits while playing.** If `Update`'s `segmentsVersion` differs from the one given to `Play`, call `Play(segments, Position, segmentsVersion)`. That restarts the plan from the playhead. `ClipTab` always passes `Document.Version`.
- **Pause, Dispose and Play.** Each cancels the audio task, kills every decoder (`IClipDecoder.Dispose` kills the process tree), and stops the voice (`AudioServer.Stop(handle)`). Then `ClearBuffer()`.
- **Seek while paused.** Runs `PreviewDecoding.FrameArguments` once. If a seek is already decoding, remember only the newest target and run it when the current one finishes (latest wins).
- **The texture.** `Texture2D.FromPixels(Width, Height, black)` once. A `Sprite2D { Texture = _texture, Centered = false }` is the only child of `Viewport = new SubViewport { Name = $"ClipPreview{number}", Disable3D = true, UpdateMode = Disabled, Width = Width, Height = Height, ClearColor = Color.FromArgb(255, 0, 0, 0) }`. `(Width, Height) = PreviewDecoding.PreviewSize(sourceWidth, sourceHeight)`.
- **Failures.** A decoder whose `Failed` is true logs `[Clips] Preview decode failed: …` once and stops playback. Preview problems are never dialogs.

- [ ] **Step 1: Write the failing tests (with a fake factory)**

```csharp
using Cutback.Core.Models;
using MainframeEngine.Editor.Clips;

namespace MainframeEngine.Editor.Tests.Clips;

[Collection(nameof(SerialEditor))]
public sealed class ClipPreviewTests
{
    private sealed class FakeDecoder(byte[] bytes) : IClipDecoder
    {
        public Stream Output { get; } = new MemoryStream(bytes);
        public bool Failed => false;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeFactory(int frameBytes) : IClipDecoderFactory
    {
        public List<(IReadOnlyList<string> Args, FakeDecoder Decoder)> Started { get; } = [];

        public IClipDecoder Start(IReadOnlyList<string> arguments)
        {
            var decoder = new FakeDecoder(new byte[frameBytes * 90]); // 3 s of frames at 30 fps
            Started.Add((arguments, decoder));
            return decoder;
        }
    }

    private static readonly Segment[] Segments =
    [
        Segment.Create(0, 4, true, SegmentOrigin.Auto),
        Segment.Create(4, 6, false, SegmentOrigin.Auto),
        Segment.Create(6, 10, true, SegmentOrigin.Auto),
    ];

    private static ClipPreview Preview(FakeFactory factory) => new(factory, () => null, "/r/a.mp4", 640, 360, 30, number: 1);

    [Fact]
    public void ThePreviewIsScaledToAtMost960WideWithTheSourceAspect()
    {
        using var preview = new ClipPreview(new FakeFactory(4), () => null, "/r/a.mp4", 1920, 1080, 60, 1);
        Assert.Equal((960, 540), (preview.Width, preview.Height));
        Assert.Equal(960, preview.Viewport.Width);
    }

    [Fact]
    public void PlayStartsTheFirstRangesVideoDecoder()
    {
        var factory = new FakeFactory(640 * 360 * 4);
        using var preview = Preview(factory);

        preview.Play(Segments, 2);

        Assert.True(preview.IsPlaying);
        var args = Assert.Single(factory.Started).Args;
        Assert.Equal(["-ss", "2", "-i", "/r/a.mp4", "-t", "2"], args.Take(6));
    }

    [Fact]
    public void PauseKillsEveryDecoder()
    {
        var factory = new FakeFactory(640 * 360 * 4);
        using var preview = Preview(factory);
        preview.Play(Segments, 0);

        preview.Pause();

        Assert.False(preview.IsPlaying);
        Assert.All(factory.Started, s => Assert.True(s.Decoder.Disposed));
        Assert.Equal(0, preview.LiveDecoders);
    }

    [Fact]
    public void DisposeKillsEveryDecoder()
    {
        var factory = new FakeFactory(640 * 360 * 4);
        var preview = Preview(factory);
        preview.Play(Segments, 0);

        preview.Dispose();

        Assert.All(factory.Started, s => Assert.True(s.Decoder.Disposed));
    }

    [Fact]
    public void ASegmentEditRebuildsThePlanFromThePlayhead()
    {
        var factory = new FakeFactory(640 * 360 * 4);
        using var preview = Preview(factory);
        preview.Play(Segments, 0);
        Segment[] edited = [Segment.Create(0, 1, true, SegmentOrigin.Auto), Segment.Create(1, 10, false, SegmentOrigin.Manual)];

        preview.Update(edited, segmentsVersion: 1);

        Assert.Equal(2, factory.Started.Count);
        Assert.True(factory.Started[0].Decoder.Disposed);
    }

    [Fact]
    public void SeekWhilePausedDecodesOneFrame()
    {
        var factory = new FakeFactory(640 * 360 * 4);
        using var preview = Preview(factory);

        preview.Seek(3.5);

        var args = Assert.Single(factory.Started).Args;
        Assert.Contains("-frames:v", args);
        Assert.Equal(3.5, preview.Position, 9);
    }
}
```

Note on `ASegmentEditRebuildsThePlanFromThePlayhead`: the edit cuts everything after 1 s while the playhead is at about 0 (no audio, so the stopwatch has barely moved). The plan restarts as `[0, 1)`, so a second decoder starts and the first is killed. Also check whether `Texture2D.FromPixels`, `Sprite2D` and `SubViewport` can be created outside a `SceneTree`. The song tab creates its viewports in its constructor before they enter the tree, which suggests they can. If not, build the test on `SessionHost` (`SessionFixture.cs`) and add the viewport to `Host`.

- [ ] **Step 2: Run to verify failure, then implement `ClipDecoders.cs` and `ClipPreview.cs` per the design above.**

`FfmpegClipDecoder` notes:
- It wraps `Cutback.Media.FfmpegProcess.Start(ffmpegPath, args, redirectStandardOutput: true)`.
- It drains stderr with `process.StandardError.ReadToEndAsync()` into a field.
- Its `Output` is `process.StandardOutput.BaseStream`.
- `Failed` is `process.HasExited && process.ExitCode != 0`. It logs the last stderr line once.
- `Dispose` calls `FfmpegProcess.TryKill(process)` and then `process.Dispose()`.

The `ClipPreview` reader threads are `Task.Factory.StartNew(…, TaskCreationOptions.LongRunning)` with a `CancellationToken`. On cancel they stop reading and exit. They never touch the texture.

- [ ] **Step 3: Tests (PASS, 6), gates, commit**

```bash
git add MainframeEngine.Editor/Src/Clips Tests/MainframeEngine.Editor.Tests/Clips/ClipPreviewTests.cs
git commit -m "Add ClipPreview: gapless ffmpeg preview into a texture, with the audio voice as its clock

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: `ClipTab` and session wiring — open, save, close, undo, File › Open

**Files:**
- Create: `MainframeEngine.Editor/Src/Clips/ClipTab.cs`
- Create: `MainframeEngine.Editor/Src/Clips/ClipJob.cs`
- Modify: `MainframeEngine.Editor/Src/Session/EditorSession.cs`:
  - `_clipsCounter` and `ActiveClips` next to `_songCounter`/`ActiveSong` (26/53);
  - `OpenClips` and `SaveClips` after `SaveSong` (~285);
  - the `Close` branch (386-387), the `Activate` branch (442-443), `Tick` (458-459), `Dispose` (694-697).
- Modify: `MainframeEngine.Editor/Src/EditorCommands.cs`:
  - `Run` (71) and a new `RunClips`;
  - `OpenClips` and `SaveClips` like `OpenSong`/`SaveSong` (210-257);
  - `CloseTab` (399-407): generalise `CloseSong` into `CloseWithPrompt`;
  - `SaveAll` (324-381);
  - `OpenScene` (277-291): becomes "Open…";
  - the File/Edit menu enable flags (~723-745).
- Modify: `MainframeEngine.Editor/Src/UI/FileSystemPanel.cs` (`Open` ~410-450; the unsaved badge at 221)
- Modify: `MainframeEngine.Editor/Src/Qa/EditorQaScript.cs:195-201` (`open` accepts videos and `.cutback`)
- Test: `Tests/MainframeEngine.Editor.Tests/Clips/ClipTabTests.cs`

**Interfaces:**
- Consumes: Tasks 2–7.
- Produces:
  ```csharp
  public sealed class ClipJob<T> : IDisposable
  {
      public static ClipJob<T> Start(string name, Func<IProgress<double>, CancellationToken, Task<T>> work);
      public string Name { get; }  public Task<T> Task { get; }  public double Progress { get; }
      public bool IsDone { get; }  public bool IsCancelled { get; }  public void Cancel();  public void Dispose();
  }
  public sealed class ClipTab : IEditorTab
  {
      internal ClipTab(ClipDocument document, ClipPreview preview, FfmpegLocation? ffmpeg, int number);
      public ClipDocument Document { get; }  public ClipPreview Preview { get; }  public FfmpegLocation? Ffmpeg { get; }
      public ClipTimelineView View { get; }  public ClipCanvasState State { get; }  public ClipCanvasController Controller { get; }
      public ClipTimelineCanvas Timeline { get; }  public SubViewport TimelineViewport { get; }
      public string? Status { get; set; }         // the status line (warnings at open, detection results)
      public ClipJob<Waveform>? WaveformJob { get; }
      public ClipJob<string>? DetectJob { get; set; }    // Detect silence (Task 9)
      public int ViewVersion { get; }  public void Touch();
      public void TogglePlay();  public void Stop();  public void Seek(double time);
      internal void Tick();       // main thread, while active
      internal void Deactivate(); // pause, cancel the gesture, viewports off
      internal event Action<ClipTab>? DirtyChanged;
      // IEditorTab: FilePath => Document.FilePath; DisplayName; Title (with *); IsDirty; IconClasses "icon icon-video icon-audio"; Tooltip
  }
  // EditorSession
  public ClipTab? ActiveClips { get; }
  public ClipTab OpenClips(string path, FfmpegLocation ffmpeg);     // activates an open tab for the same project or source
  public void SaveClips(ClipTab tab, string? path = null);
  // EditorCommands
  public void OpenClips(string path);                // locates ffmpeg, opens behind the splash, reports errors
  public void SaveClips(ClipTab tab, bool saveAs, Action<bool>? then);
  public void OpenAny(string path);                  // .mscene → scene, .cutback/video → clips, .msong → song, .rml → UI preview
  ```

- [ ] **Step 1: Write the failing tests**

`Tests/MainframeEngine.Editor.Tests/Clips/ClipTabTests.cs`. Use `SessionHost` (`SessionFixture.cs`) like `SongTabTests`. Write a `.cutback` whose source is a small dummy file, so `OpenClips` needs no ffmpeg: a `.cutback` is not probed.

```csharp
using Cutback.Core.Projects;
using MainframeEngine.Editor.Clips;

namespace MainframeEngine.Editor.Tests.Clips;

[Collection(nameof(SerialEditor))]
public sealed class ClipTabTests : IDisposable
{
    private static readonly Cutback.Media.FfmpegLocation NoFfmpeg = new("/x/ffmpeg", "/x/ffprobe", "/x", Cutback.Media.FfmpegLocationSource.Path);
    private readonly SessionHost _host = new();

    public void Dispose() => _host.Dispose();

    private string ClipProject(string name = "trim")
    {
        var dir = Directory.CreateTempSubdirectory("clips-tab-").FullName; // recordings live outside the project
        var video = Path.Combine(dir, name + ".mp4");
        File.WriteAllBytes(video, [0]);
        var path = Path.Combine(dir, name + ".cutback");
        ProjectSerializer.SaveAsync(ClipDocumentTests.Project(video), path, CancellationToken.None).GetAwaiter().GetResult();
        return path;
    }

    [Fact]
    public void OpeningAProjectAddsAnActiveClipTab()
    {
        var tab = _host.Session.OpenClips(ClipProject(), NoFfmpeg);
        Assert.Same(tab, _host.Session.ActiveTab);
        Assert.Same(tab, _host.Session.ActiveClips);
        Assert.Equal("trim.cutback", tab.Title);
        Assert.Equal("icon icon-video icon-audio", tab.IconClasses);
    }

    [Fact]
    public void OpeningTheSameProjectTwiceActivatesTheFirstTab()
    {
        var path = ClipProject();
        var first = _host.Session.OpenClips(path, NoFfmpeg);
        _host.Session.NewScene();
        Assert.Same(first, _host.Session.OpenClips(path, NoFfmpeg));
        Assert.Single(_host.Session.Tabs.OfType<ClipTab>());
    }

    [Fact]
    public void ARecordingOutsideTheProjectDoesNotSwitchProjects()
    {
        var root = _host.Session.ProjectRoot;
        _host.Session.OpenClips(ClipProject(), NoFfmpeg);
        Assert.Equal(root, _host.Session.ProjectRoot);
    }

    [Fact]
    public void EditsShowAStarAndSavingClearsIt()
    {
        var tab = _host.Session.OpenClips(ClipProject(), NoFfmpeg);
        var changes = 0;
        _host.Session.ScenesChanged += () => changes++;

        tab.Document.Editor.CreateSection(10, 20, anchor: 10);
        Assert.Equal("trim.cutback*", tab.Title);
        Assert.True(changes > 0);

        _host.Session.SaveClips(tab);
        Assert.Equal("trim.cutback", tab.Title);
    }

    [Fact]
    public void DeactivatingAClipTabPausesItsPreview()
    {
        var tab = _host.Session.OpenClips(ClipProject(), NoFfmpeg);
        tab.TogglePlay(); // starts (its decoder fails without ffmpeg, which only logs)
        _host.Session.NewScene();
        Assert.False(tab.Preview.IsPlaying);
    }

    [Fact]
    public void ClosingTheTabDisposesItsPreview()
    {
        var tab = _host.Session.OpenClips(ClipProject(), NoFfmpeg);
        _host.Session.Close(tab);
        Assert.Empty(_host.Session.Tabs.OfType<ClipTab>());
        Assert.True(tab.TimelineViewport.IsFreed);
    }
}
```

`DeactivatingAClipTabPausesItsPreview` spawns `/x/ffmpeg`, which does not exist. `FfmpegClipDecoderFactory.Start` must therefore catch `Win32Exception`, log it, and return a decoder whose `Failed` is true. That is also the real behaviour when a configured ffmpeg disappears. If the test is flaky because of process timing, assert only `!tab.Preview.IsPlaying` after `NewScene`, which is deterministic.

- [ ] **Step 2: Run to verify failure, then implement.**

`ClipJob<T>`: model it on `SongRenderJob` (Task.Run, a volatile progress field written by a private `IProgress<double>` sink, a CTS, Cancel and Dispose).

```csharp
namespace MainframeEngine.Editor.Clips;

/// <summary>A background Clips job (waveform, silence detection, export) polled by the main thread, like <c>SongRenderJob</c>.</summary>
public sealed class ClipJob<T> : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private double _progress;

    private ClipJob(string name) => Name = name;

    public string Name { get; }

    public Task<T> Task { get; private set; } = null!;

    public double Progress => Volatile.Read(ref _progress);

    public bool IsDone => Task.IsCompleted;

    public bool IsCancelled => _cancel.IsCancellationRequested;

    public static ClipJob<T> Start(string name, Func<IProgress<double>, CancellationToken, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var job = new ClipJob<T>(name);
        var sink = new Sink(job);
        job.Task = System.Threading.Tasks.Task.Run(() => work(sink, job._cancel.Token));
        return job;
    }

    public void Cancel() => _cancel.Cancel();

    public void Dispose() => _cancel.Dispose();

    private sealed class Sink(ClipJob<T> job) : IProgress<double>
    {
        public void Report(double value) => Volatile.Write(ref job._progress, Math.Clamp(value, 0, 1));
    }
}
```

`ClipTab`:
- The constructor:
  - creates `View` (`Reset(document.Duration)`), `State`, `Controller` (with `Seek = Seek`), and `Timeline`;
  - creates `TimelineViewport = new SubViewport { Name = $"ClipTimeline{number}", Disable3D = true, UpdateMode = SubViewportUpdateMode.Disabled, ClearColor = Color.FromArgb(255, 0x12, 0x12, 0x15), Width = 16, Height = 16 }` with `Timeline` as its child;
  - sets `Status = string.Join(" ", document.Warnings)`, or null when there are none;
  - when `ffmpeg` is not null, starts `WaveformJob = ClipJob<Waveform>.Start("waveform", (p, ct) => new WaveformExtractor(ffmpeg).ExtractAsync(document.SourcePath, document.Duration, p, ct))`.
- `DisplayName` is the file name of `Document.FilePath`, else of the source.
- `Tooltip` is `{DisplayName} — clip ({unsaved changes|saved}) — {FilePath ?? SourcePath}`.
- `DirtyChanged` is raised from `Document.DirtyChanged`.
- `Tick()`:
  - when the `WaveformJob` is done, set `Document.Waveform` on success, put "Waveform unavailable: …" in `Status` on failure, then dispose the job and null it;
  - call `Preview.Update(Document.Segments.Segments, Document.Version)`;
  - set `State.Playhead = Preview.Position`;
  - when playing, `View.KeepVisible(Preview.Position)`.
- `TogglePlay()`:
  - if playing, pause;
  - else play from `State.Playhead`, first rewinding to 0 when the playhead is at the end (`State.Playhead >= Document.Duration − 0.01`).
- `Stop()` pauses and seeks to 0.
- `Seek(t)` sets `State.Playhead = t`. When playing it calls `Preview.Play(…, t)`, else `Preview.Seek(t)`.
- `Deactivate()` pauses, calls `Controller.CancelGesture()`, and sets `TimelineViewport.UpdateMode` and `Preview.Viewport.UpdateMode` to `Disabled`.
- `Dispose()`:
  - cancels and disposes the jobs;
  - calls `Preview.Dispose()`;
  - calls `Free()` on both viewports if they are not `IsFreed`.

`EditorSession.OpenClips(path, ffmpeg)`:
- Get the full path.
- If an open `ClipTab` has `PathsEqual(tab.Document.FilePath, full)` or `PathsEqual(tab.Document.SourcePath, full)`, activate it and return it.
- Otherwise:
  - `var document = ClipDocument.OpenAsync(full, ffmpeg, CancellationToken.None).GetAwaiter().GetResult();`
  - `var preview = new ClipPreview(new FfmpegClipDecoderFactory(ffmpeg.FfmpegPath), () => _host.Tree?.Servers.Get<AudioServer>(), document.SourcePath, document.Project.Source.Width, document.Project.Source.Height, document.Project.Source.FrameRate, ++_clipsCounter);`
  - `var tab = new ClipTab(document, preview, ffmpeg, _clipsCounter);`
  - `_host.AddChild(tab.TimelineViewport); _host.AddChild(tab.Preview.Viewport);`
  - Hook `DirtyChanged` to `ScenesChanged`, add the tab, raise `ScenesChanged`, and call `Activate`.
- **Never call `UseProjectOf`.** Recordings live in the user-data folder, outside the project, and changing projects would throw while scene tabs are open.
- A sync-wait is safe here: the main thread has no synchronisation context, and `OpenAsync` uses `ConfigureAwait(false)` throughout.

`EditorSession.SaveClips(tab, path)` calls `tab.Document.Save(path)` and raises `ScenesChanged`.

Add the `Close`, `Activate`, `Tick` and `Dispose` branches, mirroring `SongTab`:
- `Close` unhooks `DirtyChanged`.
- `Activate` calls `clips.Deactivate()` when the previously active tab was a `ClipTab`.
- `Tick` calls `if (ActiveTab is ClipTab clips) clips.Tick();`.

`EditorCommands`:
- In `Run`, before the switch: `if (Session.ActiveTab is ClipTab clips && RunClips(clips, id)) return true;`.
- `RunClips` handles:
  - `file.save` → `SaveClips(clips, false, null)`; `file.save_as` → `SaveClips(clips, true, null)`;
  - `edit.undo` → `clips.Document.Editor.Undo()`; `edit.redo` → `Redo()`; `edit.history` → `true`;
  - `edit.delete` → `clips.Controller.DeleteHovered()`;
  - `clips.play` → `clips.TogglePlay()`; `clips.stop` → `clips.Stop()`;
  - `default: return false`.
- `SaveClips(tab, saveAs, then)`: when `saveAs` is false and the document has a `FilePath`, save directly through `TrySaveClips`. Otherwise show `FilePickerModel(FilePickerMode.Save, Path.GetDirectoryName(tab.Document.FilePath ?? tab.Document.DefaultProjectPath)!, ["*.cutback"], Path.GetFileName(tab.Document.FilePath ?? tab.Document.DefaultProjectPath))` with the title "Save Clip Project". `TrySaveClips` mirrors `TrySaveSong`.
- `OpenClips(path)`:

  ```csharp
  try { var ffmpeg = ClipsFfmpeg.Locate(Settings.FfmpegPath); _workspace.RunWithSplash($"Opening {Path.GetFileName(path)}…", () => { try { Session.OpenClips(path, ffmpeg); } catch (Exception e) when (IsRecoverable(e)) { ReportError($"Could not open {Path.GetFileName(path)}", e); } }); }
  catch (Cutback.Media.FfmpegNotFoundException e) { ReportError("The Clips tab needs ffmpeg", e); }
  ```

  Use whatever `EditorCommands` uses to read settings (`_workspace.Settings`). Check that `IsRecoverable` covers `MediaProbeException`, `ProjectFormatException` and `InvalidPartitionException`. If it does not, add them in this task.
- `CloseTab`: replace the `SongTab { IsDirty: true }` branch with `else if (tab is SongTab { IsDirty: true } or ClipTab { IsDirty: true }) CloseWithPrompt(tab);`. `CloseWithPrompt` is `CloseSong` generalised: the button-0 save calls `SaveSong` or `SaveClips` by type.
- `SaveAll` (both overloads): add `ClipTab { IsDirty: true }` beside `SongTab`. A clip tab with no `FilePath` goes through Save As.
- `OpenScene` becomes `Open`:
  - picker filters `["*.mscene", "*.cutback", "*.mp4", "*.mov", "*.mkv", "*.webm"]`, title "Open";
  - the callback calls `OpenAny(path)`, which dispatches by extension: `.cutback` and videos → `OpenClips`; `.msong` → `OpenSong`; `.rml` → the UI-preview path; else the existing scene open.
  - Keep `file.open` as the command id. Rename the menu label from "Open Scene…" to "Open…" if it is labelled so.
- Menu flags: Save and Save As are enabled when `Session.ActiveClips is not null`, too.

`FileSystemPanel.Open`: `case FileKind.Video: case FileKind.Clips: Workspace.Commands.OpenClips(entry.FullPath); break;`. At line 221, add `or Clips.ClipTab` to the unsaved-badge pattern.

`EditorQaScript` `open` verb: `.cutback` and the four video extensions call `workspace.Commands.OpenClips(path)`.

- [ ] **Step 3: Run the tests (PASS, 6), then the whole editor suite**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests`
Expected: PASS. Fix any test that asserted the File › Open filter or the "Open Scene" label.

- [ ] **Step 4: Gates and commit**

```bash
just build && just format-check
git add MainframeEngine.Editor/Src Tests/MainframeEngine.Editor.Tests
git commit -m "Add the Clips tab to the session: open, save, close and undo; File › Open takes recordings

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: `ClipPanel`, `clips.rml` and `ClipView` — the tab on screen, detection and playback

**Files:**
- Create: `MainframeEngine.Editor/Content/Editor/clips.rml`
- Create: `MainframeEngine.Editor/Src/Clips/ClipPanel.cs`
- Create: `MainframeEngine.Editor/Src/Clips/ClipView.cs`
- Modify: `MainframeEngine.Editor/Src/EditorWorkspace.cs`:
  - `ClipPanel` and `ClipView` properties beside `SongPanel`/`SongView` (187-190);
  - construct them in `OnReady` beside 235-238 and 275-276;
  - `ApplyLayout`: `ClipPanel.SetRect(Layout.ViewportImage)` beside 451;
  - `OnUnhandledInput`: `ClipView.HandleKey` beside 618.
- Modify: `MainframeEngine.Editor/Src/EditorCommands.cs` (`RunClips`: `clips.detect_gameplay`, `clips.detect_silence`)
- Modify: `Tests/MainframeEngine.Editor.Tests/WorkspaceTests.cs:~70` (panel document count 9 → 10)
- Test: `Tests/MainframeEngine.Editor.Tests/Clips/ClipWorkspaceTests.cs` (on `HeadlessEditor`, like `SongWorkspaceTests`)

**Interfaces:**
- Consumes: `ClipTab` (Task 8), `ClipJob<T>`, `SilenceDetector(FfmpegLocation).DetectAsync(path, duration, DetectionSettings.Default, progress, ct)`, `ClipDocument.ApplySilence`/`DetectIdleGameplay`.
- Produces:
  ```csharp
  [Tool] public sealed class ClipView : Node
  {
      public const string TimelineTexture = "clip-timeline", PreviewTexture = "clip-preview";
      public ClipView(EditorWorkspace workspace);
      public ClipTab? Tab { get; }
      public bool HandleKey(Key key, EditorModifiers modifiers);
      public void DetectSilence(ClipTab tab);      // starts tab.DetectJob; a second call cancels it
  }
  public sealed class ClipPanel : EditorDocument
  {
      public ClipPanel(EditorWorkspace workspace);
      public LayoutRect PreviewRect { get; }  public LayoutRect TimelineRect { get; }
      public void SetImages(bool preview, bool timeline);
      public void Tick(ClipTab? tab);
  }
  ```

- [ ] **Step 1: Write the failing workspace tests**

`ClipWorkspaceTests` on `HeadlessEditor` (see `SongWorkspaceTests` for setup):
- `OpeningAClipShowsThePanelAndHidesItForOtherTabs`: `Workspace.Session.OpenClips(project, NoFfmpeg)` followed by `Tick(2)` makes `Workspace.ClipPanel.Visible` true. `Session.NewScene()` followed by `Tick(2)` makes it false.
- `DetectGameplayIsDisabledWithoutASidecar`: the data-model value `gameplay_enabled` is false. Read it through a public `ClipPanel.CanDetectGameplay` property that the panel binds.
- `DetectGameplayCommandCutsAndSetsTheStatus`: write the sidecar from `ClipDocumentTests.Sidecar` next to the dummy video before opening, then run `Workspace.Commands.Execute("clips.detect_gameplay")`. The tab's `Status` starts with "Cut ", and `edit.undo` reverts it.
- `SpaceTogglesPlaybackAndDeleteDissolvesTheHoveredRegion`: `Key(Key.Space)` makes `tab.Preview.IsPlaying` true. Then set `tab.State.HoverSegment = 0` after `CreateSection(10, 20, anchor: 10)` and press `Key(Key.Delete)`. The segment count drops by 1 (the section dissolves into its neighbours).

Update `WorkspaceTests` to `Assert.Equal(10, W.PanelLayer.Documents.Count); // with the song and clips panels`.

- [ ] **Step 2: Run to verify failure, then implement.**

`clips.rml`: model it on `song.rml`. It links `icons.rcss` and `theme.rcss`, has `<body class="panel-doc clips-doc" data-model="clips">`, sets `pointer-events: none` on the body with `auto` only on the toolbar, and uses the song tab's colours. The layout, top to bottom:
- **`#clips-toolbar`**, 34 dp, made of `button.tool-button`s with icons from the atlas:
  - Play/Pause: `data-event-click="play()"`, icon `player-play`/`player-pause` via `data-if`, tooltip "Play / Pause (Space)";
  - Stop: `stop()`, `player-stop`;
  - a separator;
  - "Detect idle gameplay": `detect_gameplay()`, icon `device-gamepad-2` if it is in the atlas (check `Content/icons/icons.txt`), else `player-skip-forward`. It is disabled with `data-attr-disabled="!gameplay_enabled"`, and its tooltip explains that it needs a Mainframe Engine recording;
  - "Detect silence": `detect_silence()`, label `{{silence_label}}`, which shows "Cancel n %" while running, like the song tab's render button;
  - a separator;
  - Undo and Redo: `data-command="edit.undo"` / `"edit.redo"`;
  - a flexible spacer;
  - a `{{kept}}` label, e.g. "Kept 4:12.3 of 6:00.0";
  - Export: `export()`, label `{{export_label}}`, class `rendering` while exporting (Task 10 binds it; in this task it calls `Workspace.Commands.Execute("clips.export")`, which returns false until Task 10).
- **`#clips-status`**, one line, `{{status}}`, shown with `data-if="status != ''"` in the `.notice` style.
- **`#clips-preview`**, `flex: 1 1 60%`, centring `<img id="clips-preview-image"/>`. The panel sizes it in code to fit the area at the video's aspect.
- **`#clips-timeline`**, `flex: 0 0 160dp`, `class="canvas"`, with `<img id="clips-timeline-image"/>` at 100% × 100%.

`ClipPanel` is modelled on `SongPanel`:
- It binds `playing`, `gameplay_enabled`, `silence_label`, `detecting`, `export_label`, `exporting`, `status` and `kept`, with events `play`, `stop`, `detect_gameplay`, `detect_silence` and `export`. Each event calls `Workspace.Commands.Execute("clips.…")`.
- `Tick(tab)`:
  - sets `Visible`;
  - sets `PreviewRect = RectOf("clips-preview")` and `TimelineRect = RectOf("clips-timeline")`;
  - fits the preview image: compute the largest `w × h` with the preview's aspect inside `PreviewRect`, and set the image's `width`, `height`, `left` and `top` style properties in dp, only when they change;
  - updates the bindings only when their values change, and dirties the model.
- `kept` is `$"Kept {ClipFormat.Clock(FilterGraphBuilder.KeptDuration(tab.Document.Segments.Segments))} of {ClipFormat.Clock(tab.Document.Duration)}"`. Recompute it only when `Document.Version` changes.

`ClipView` is modelled on `SongView`:
- `OnProcess`:
  - `PollJobs()`;
  - switch to the active `ClipTab`;
  - `_workspace.ClipPanel.Tick(tab)`;
  - set `tab.View.Width = TimelineRect.Width`;
  - `Place` the timeline viewport and canvas exactly as `SongView.Place` does, and set the preview viewport to `Always` while it shows;
  - `Publish` both textures with `ui.RegisterTexture(TimelineTexture, tab.TimelineViewport)` and `ui.RegisterTexture(PreviewTexture, tab.Preview.Viewport)`.
- `Switch` cancels any gesture, unregisters both textures, and calls `SetImages(false, false)`. It sets `tab.Controller.ShowSegmentMenu` to show `_workspace.Popup.Show([new MenuItem("Delete", "clips.delete_segment", "Del", Icon: "trash")], windowX, windowY, onCommand: id => { if (id == "clips.delete_segment") tab.Controller.DeleteHovered(); })`. The window position is `TimelineRect.X + x, TimelineRect.Y + y`.
- `OnInput` is the same as `SongView.OnInput`, with one area, the timeline:
  - left press and release go to `Controller.Press`/`Release`;
  - motion calls `Controller.Drag` while a gesture is active, else `Controller.Hover`;
  - a right press calls `Controller.RightClick`;
  - the wheel calls `Controller.Wheel`;
  - every handled event calls `SetInputAsHandled`;
  - motion that leaves the timeline area during a gesture keeps routing, so the drag continues;
  - a left release with no gesture is ignored.
- `HandleKey`: Space calls `tab.TogglePlay()` and Home calls `tab.Seek(0)`, then it falls back to `Controller.Key`. It returns false when no clip tab is active.
- `DetectSilence(tab)`:
  - if `tab.DetectJob` is running, cancel it;
  - else if `tab.Ffmpeg` is null, `ReportError` with ffmpeg not found;
  - else `tab.DetectJob = ClipJob<string>.Start("silence", async (p, ct) => { var spans = await new SilenceDetector(ffmpeg).DetectAsync(tab.Document.SourcePath, tab.Document.Duration, DetectionSettings.Default, p, ct); return … })`.

  Return the spans themselves, not the status line: make the job `ClipJob<IReadOnlyList<SilenceSpan>>`, so `ApplySilence` runs on the main thread when the job completes.
- `PollJobs`: when a detect job completes, apply it on the main thread with `tab.Status = tab.Document.ApplySilence(spans)`. A cancelled job leaves the status as "Silence detection cancelled." A failed one calls `ReportError("Detect silence failed", e)`.

`EditorCommands.RunClips` gains:
- `clips.detect_gameplay`: set `clips.Status = clips.Document.DetectIdleGameplay()` when `Capture` is not null, else set the status "This recording has no Mainframe Engine sidecar.". Return `true`.
- `clips.detect_silence`: call `_workspace.ClipView.DetectSilence(clips)` and return `true`.

- [ ] **Step 3: Tests (PASS), the whole editor suite, gates, commit**

```bash
dotnet test Tests/MainframeEngine.Editor.Tests && just build && just format-check
git add MainframeEngine.Editor Tests/MainframeEngine.Editor.Tests
git commit -m "Show the Clips tab: preview, timeline, toolbar, Detect idle gameplay and Detect silence

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Export from the Clips tab

**Files:**
- Create: `MainframeEngine.Editor/Src/Clips/ClipExport.cs`
- Modify: `MainframeEngine.Editor/Src/Clips/ClipView.cs` (`Export`, polling, one `TempSession` disposed in `OnExitTree`), `ClipPanel.cs` (export label and progress), `ClipTab.cs` (`ExportJob`; `Dispose` cancels it), `EditorCommands.cs` (`clips.export`)
- Test: `Tests/MainframeEngine.Editor.Tests/Clips/ClipExportTests.cs`

**Interfaces:**
- Consumes: `Exporter(FfmpegLocation, TempSession).ExportAsync(ExportRequest, IProgress<ExportProgress>, ct)`, `ExportRequest(SourcePath, Segments, DurationSeconds, OutputPath, ExportMode.Precise, Width, Height, FrameRate)`, `TempSession`.
- Produces:
  ```csharp
  public static class ClipExport
  {
      public static string DefaultPath(string sourcePath, Func<string, bool> exists);   // "<name>-cut.mp4", then "-cut-2", "-cut-3"…
      public static ExportRequest Request(ClipDocument document, string outputPath);
  }
  // ClipTab: public ClipJob<string>? ExportJob { get; set; }   (result: the output path)
  // ClipView: public void Export(ClipTab tab);   // picker → job; a second call cancels
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
using Cutback.Media.Export;
using MainframeEngine.Editor.Clips;

namespace MainframeEngine.Editor.Tests.Clips;

public sealed class ClipExportTests
{
    [Fact]
    public void TheDefaultIsNextToTheRecordingAndNeverOverwrites()
    {
        var taken = new HashSet<string> { "/r/run-cut.mp4", "/r/run-cut-2.mp4" };
        Assert.Equal("/r/run-cut-3.mp4", ClipExport.DefaultPath("/r/run.mp4", taken.Contains));
        Assert.Equal("/r/run-cut.mp4", ClipExport.DefaultPath("/r/run.mp4", _ => false));
    }

    [Fact]
    public void TheRequestIsPreciseAndCarriesTheSourceSizeForTheEncoder()
    {
        var doc = ClipDocument.FromProject(ClipDocumentTests.Project("/r/run.mp4"), null, null, []);
        doc.Editor.CreateSection(10, 20, anchor: 10);

        var request = ClipExport.Request(doc, "/r/run-cut.mp4");

        Assert.Equal(ExportMode.Precise, request.Mode);
        Assert.Equal((1920, 1080, 60.0), (request.Width, request.Height, request.FrameRate));
        Assert.Equal(3, request.Segments.Count);
        Assert.Equal("/r/run.mp4", request.SourcePath);
    }
}
```

The default path uses `Path.Combine` and `Path.GetDirectoryName`. Those use `/` on macOS and Linux, where CI runs these tests. If the Windows leg runs them, compare with `Path.Combine("/r", "run-cut-3.mp4")` instead of the literal.

- [ ] **Step 2: Implement.**

`ClipExport` is two static methods, ported from Cutback's `ExportViewModel.SuggestOutputPath`:

```csharp
public static string DefaultPath(string sourcePath, Func<string, bool> exists)
{
    var dir = Path.GetDirectoryName(sourcePath) ?? string.Empty;
    var stem = Path.GetFileNameWithoutExtension(sourcePath) + "-cut";
    var candidate = Path.Combine(dir, stem + ".mp4");
    for (var i = 2; exists(candidate) && i < 1000; i++)
        candidate = Path.Combine(dir, $"{stem}-{i}.mp4");
    return candidate;
}

public static ExportRequest Request(ClipDocument document, string outputPath) => new(
    document.SourcePath, [.. document.Segments.Segments], document.Duration, outputPath, ExportMode.Precise,
    document.Project.Source.Width, document.Project.Source.Height, document.Project.Source.FrameRate);
```

`ClipView.Export(tab)`:
- If `tab.ExportJob` is running, cancel it.
- Else, if `FilterGraphBuilder.KeptDuration(...)` is 0, set the status "Every segment is removed; there is nothing to export.".
- Else, with no ffmpeg, call `ReportError`.
- Else pause the preview and show the save picker: `FilePickerModel(FilePickerMode.Save, dir, ["*.mp4", "*.mov", "*.mkv", "*.webm"], name)` with the default from `DefaultPath(…, File.Exists)` and the title "Export Clip". On accept:

  ```csharp
  var request = ClipExport.Request(tab.Document, path);
  tab.ExportJob = ClipJob<string>.Start("export", async (p, ct) =>
  {
      await new Exporter(ffmpeg, _temp).ExportAsync(request, new Progress<ExportProgress>(x => p.Report(x.Fraction)), ct);
      return path;
  });
  ```

  `Progress<T>` captures no synchronisation context on the worker, so it reports inline. If that is not true here, use a small sink class like `ClipJob`'s.

`PollJobs` gains the export:
- On success, `Log.Info($"[Clips] Exported {path}")` and show `Message` "Export finished" with the file name and the folder, buttons `Show in Folder` and `Close`. Show in Folder calls `OsShell.Reveal(path)`, as `RecordingNotifier` does.
- On cancel, set the status "Export cancelled.".
- On failure, call `ReportError("Export failed", e)`. `FfmpegException.Message` already carries the tail of stderr.

`ClipPanel` shows `export_label = job is null ? "Export" : $"Cancel {pct} %"` with the `rendering` class, like the song render button. `clips.export` in `RunClips` calls `_workspace.ClipView.Export(clips)`.

`ClipTab.Dispose` cancels `ExportJob`. Closing the tab cancels the export, which deletes the partial output (`Exporter` deletes on cancel).

- [ ] **Step 3: Tests (PASS, 2), editor suite, gates, commit**

```bash
git add MainframeEngine.Editor/Src/Clips Tests/MainframeEngine.Editor.Tests/Clips/ClipExportTests.cs MainframeEngine.Editor/Src/EditorCommands.cs
git commit -m "Export trimmed recordings from the Clips tab

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: "Open in Clips Tab" in the recording-finished dialog

**Files:**
- Modify: `MainframeEngine.Editor/Src/Play/RecordingNotifier.cs` (a new `Action<string> openInClips` constructor parameter; `ShowSaved` buttons and callback)
- Modify: `MainframeEngine.Editor/Src/EditorWorkspace.cs:121-129` (pass `path => Commands.OpenClips(path)`)
- Modify: `Tests/MainframeEngine.Editor.Tests/Play/RecordingNotifierTests.cs` (the harness and button assertions)

**Interfaces:**
- Produces: the Saved dialog's buttons become `["Open in Clips Tab", "Open in Cutback", "Show in Folder", "Close"]`, with `DefaultButton = 0` and `CancelButton = 3`.

- [ ] **Step 1: Update the tests first**

In `RecordingNotifierTests`:
- `Harness.Create` passes a recording `openInClips` delegate.
- The buttons assertion becomes the list above.
- Existing callback-index tests shift by one: Open in Cutback is 1 and Show in Folder is 2.
- Add:

```csharp
    [Fact]
    public void OpenInClipsTabOpensTheRecordingInTheEditor()
    {
        var h = Harness.Create();
        h.Notifier.OnFinished(new EditorLinkRecording(EditorLinkRecordingState.Stopped, "/u/r/a.mp4", 83.5f, 0, 2, ""));
        h.Dialogs.Single().Callback!(0, "");
        Assert.Equal(["/u/r/a.mp4"], h.OpenedInClips);
    }
```

The harness field names (`Dialogs`, `Notifier`) are whatever the existing harness uses; add `OpenedInClips` the same way.

- [ ] **Step 2: Run (FAIL), implement, run (PASS)**

`RecordingNotifier`:
- add the parameter `Action<string> openInClips` after `show`;
- `ShowSaved`: `Buttons = ["Open in Clips Tab", "Open in Cutback", "Show in Folder", "Close"], DefaultButton = 0, CancelButton = 3`;
- the callback: `0` → `openInClips(path)`, `1` → `OpenInCutback(path)`, `2` → `reveal(path)`.

Update the class summary: the Clips tab is the default, and Cutback stays one click away. `EditorWorkspace` passes `path => Commands.OpenClips(path)`.

- [ ] **Step 3: Gates and commit**

```bash
git add MainframeEngine.Editor/Src/Play/RecordingNotifier.cs MainframeEngine.Editor/Src/EditorWorkspace.cs Tests/MainframeEngine.Editor.Tests/Play/RecordingNotifierTests.cs
git commit -m "Offer Open in Clips Tab when a recording is saved

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: QA fixture and script, docs, ADR and spec amendments

**Files:**
- Create: `build/make-clips-fixture.sh`
- Create: `Tests/QA/Fixtures/clips/sample.mp4`, `Tests/QA/Fixtures/clips/sample.mp4.mfcap.json`
- Create: `Tests/QA/clips.qa`
- Create: `memory/decisions/0191-clips-tab-on-cutback-packages.md`
- Modify: `docs/design/editor.md` (a "Clips tab" section after "Song tab", ~619)
- Modify: `docs/design/recording.md` (the dialog's buttons; replace "the planned Clips tab" at line 7)
- Modify: `CLAUDE.md` (the editor section: the Clips tab, its packages, ffmpeg setting)
- Modify: `memory/context/progress-log.md` (a "Resume here" entry)
- Modify (Cutback repo, `feature/phase2-gameplay-cuts`): `docs/superpowers/specs/2026-10-12-mainframe-engine-integration-design.md` (the four amendments at the top of this plan)

- [ ] **Step 1: The fixture**

`build/make-clips-fixture.sh`:

```bash
#!/usr/bin/env bash
# Regenerates Tests/QA/Fixtures/clips/sample.mp4: 12 s of 320x180 test pattern with a tone that drops out from 4 to 6 s
# (silence to detect), a fake "loading" stretch from 7 to 9 s in the sidecar, and a marker at 10 s. Small enough to commit.
set -euo pipefail
out="$(cd "$(dirname "$0")/.." && pwd)/Tests/QA/Fixtures/clips/sample.mp4"
mkdir -p "$(dirname "$out")"
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i "testsrc2=s=320x180:r=30:d=12" \
  -f lavfi -i "sine=f=440:r=48000:d=12" \
  -af "volume=enable='between(t,4,6)':volume=0" \
  -c:v libx264 -preset veryfast -crf 30 -pix_fmt yuv420p -g 30 -c:a aac -b:a 64k -movflags +faststart -shortest "$out"
ls -l "$out"
```

If this machine's ffmpeg has no libx264, use `-c:v h264_videotoolbox -b:v 300k` instead. Phase 4a's `DeliveryEncoder` makes the same choice, and the fixture only needs to be H.264.

`sample.mp4.mfcap.json` follows the schema of the `sample.mfcap.json` fixture in `Tests/MainframeEngine.Tests` (find it with `find Tests -name 'sample.mfcap.json'`):
- `"video": "sample.mp4"`, `"mode": "live"`, `"frameRate": 30`, `"width": 320`, `"height": 180`, `"durationSeconds": 12.0`;
- `input`: `[0,3]` and `[9.5,12]`;
- `loading`: `[7,9]`;
- `markers`: `{ "time": 10.0, "label": "qa marker" }`.

Run the script and check that the MP4 is under 400 KB. Commit both files.

- [ ] **Step 2: `Tests/QA/clips.qa`**

Model it on `record.qa`: header comments say it needs a display and ffmpeg and is not run in CI. The script:

```
# Clips tab: open a recording, detect, edit, play, and capture each step for review.
# Needs a display and ffmpeg (just qa-editor Tests/QA/clips.qa); not run in CI.
wait-for project 300
open Tests/QA/Fixtures/clips/sample.mp4
wait 120
capture clips-01-opened
command clips.detect_gameplay
wait 10
capture clips-02-gameplay
command clips.detect_silence
wait 180
capture clips-03-silence
command edit.undo
wait 10
capture clips-04-undo
command clips.play
wait 90
capture clips-05-playing
command clips.stop
command file.close
cancel
quit
```

The final `file.close` raises the unsaved-changes prompt, and `cancel` answers it. Check that `cancel` answers the message dialog, as it does in `record.qa`. If it does not, use `command edit.undo` until the tab is clean before closing.

Run: `just qa-editor Tests/QA/clips.qa`

Then **look at the five captures** in `artifacts/qa-editor/`. Expected:
- The timeline shows the waveform with its 4–6 s dropout.
- After detect gameplay, 7–9 s is grey, with the reason "loading", and idle cuts appear where there is no input.
- After detect silence, about 4–6 s is grey.
- The preview image shows the test pattern.
- The playing capture shows the playhead moved past the loading cut.

Fix what is wrong before going on. The controller runs this step itself.

- [ ] **Step 3: Docs**

**`memory/decisions/0191-clips-tab-on-cutback-packages.md`:**
- *Context:* promo footage needs trimming in the editor (Cutback spec Phase 4).
- *Decision:* the editor references the MIT `Cutback.Core` and `Cutback.Media` from nuget.org. The runtime never does. Preview and export run the user's ffmpeg as child processes (ADR 0190). The project file is Cutback's.
- *Alternatives:*
  - hosting Cutback's Avalonia UI was rejected (two UI stacks, GPL app);
  - forking the logic was rejected (drift);
  - a G16 `VideoPlayer` was rejected (it targets AV1/WebM for games, while recordings are H.264).
- *Consequences:* the editor gains three managed dependencies (Cutback.Core, Cutback.Media, FFMpegCore). Preview re-uploads a texture per displayed frame, at most 960×540 at 30 fps. Transcript features stay in Cutback.

**`docs/design/editor.md`:** add a "Clips tab" section of 25–40 lines in the Song tab's style. It covers:
- what opens it: File › Open, the FileSystem panel, and Open in Clips Tab;
- the layout;
- the gestures (copy the list from `ClipCanvasController`'s summary);
- detection and export;
- the preview design: decoders per kept range, the one-second video prefetch, and the audio ring as the clock;
- the ffmpeg setting;
- the fact that projects are Cutback's `.cutback`;
- what is not ported.

**`docs/design/recording.md`:** the finished dialog lists Open in Clips Tab first. Line 7 now names the Clips tab as built.

**`CLAUDE.md`** (engine): one paragraph in the editor area. The Clips tab is `MainframeEngine.Editor/Src/Clips/`, built on Cutback.Core and Cutback.Media (MIT, editor-only) and never referenced by the runtime. ffmpeg comes from Editor Settings › ffmpeg or `MAINFRAME_FFMPEG`.

**`progress-log.md`:** a "Resume here" entry. It names the branch, the PR stack (#62, then #63, then this branch), the package hand-off (versions are `0.2.0-local.1` until nuget.org has 0.2.0, then bump and delete `.claude/worktrees/nuget.config`), the gates, the QA result, and the parked items:
- detection settings UI;
- Fast export;
- passing the ffmpeg setting to games;
- rename handling for open `.cutback` files.

**Cutback spec:** record the four amendments listed under "Spec amendments made by this plan". Commit on `feature/phase2-gameplay-cuts` in the Cutback repository.

- [ ] **Step 4: Final gates and commit**

```bash
just build && just test && just format-check
dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false
just test-render
git add build/make-clips-fixture.sh Tests/QA memory docs CLAUDE.md
git commit -m "Document the Clips tab and add its QA script and fixture recording

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Hand-off (after review; needs the user)

1. Phase 4a's hand-off publishes Cutback.Core and Cutback.Media 0.2.0 to nuget.org.
2. In this worktree, bump both `PackageVersion`s to `0.2.0` and delete `/Users/brogan/mainframe/mainframe-engine/.claude/worktrees/nuget.config`. Run `dotnet restore` and `just test`, then commit "Use Cutback 0.2.0 from nuget.org".
3. Push and open the PR, based on `feature/editor-recording` (#63), when the user says so.
