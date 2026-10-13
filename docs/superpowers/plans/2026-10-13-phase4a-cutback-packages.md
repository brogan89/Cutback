# Phase 4a — Cutback Packages for the Clips Tab Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `Cutback.Core` and `Cutback.Media` everything the Mainframe Editor's Clips tab (Phase 4b) needs from them, and publish them to nuget.org:
- an edit session shared by both UIs;
- public ffmpeg process plumbing and preview-decoding arguments;
- an export that still works on an ffmpeg built without x264.

**Architecture:**
- **Edit session (Core).** `SegmentEditor` owns the edit and undo rules that live in the Cutback app's view model today: one edit is one undo step, a boundary drag is one step, a drawn section takes the opposite state, and dirty means "differs from the last save". The app moves onto it, so the two UIs cannot drift apart.
- **Process plumbing (Media).** `FfmpegProcess` becomes public. A new `PreviewDecoding` class builds the decode arguments for gapless preview. `DeliveryEncoder` picks the export's H.264 encoder: libx264, else the platform's hardware encoder, else libopenh264.
- **Publishing.** The release workflow pushes both packages to nuget.org once a `NUGET_API_KEY` secret exists.

**Tech Stack:** .NET 10, System.Text.Json (Core), FFMpegCore (probe only), xunit 2.9 + FluentAssertions 7.2, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-12-mainframe-engine-integration-design.md`, "Phase 4 — Clips tab", §4.1 (`EncoderSelector`) and §4.3 (`FfmpegProcess` from Cutback.Media). Open question 2 (the package feed) was answered in chat on 2026-10-13: **nuget.org**.

**Repository and branch:** `/Users/brogan/dev/SimpleVideoEditor`, new branch `feature/clips-packages` cut from `feature/phase2-gameplay-cuts` (brogan89/Cutback#7), so its PR stacks on #7. Work in place: no worktree, matching Phase 2.

## Global Constraints

- **Package boundaries (CLAUDE.md):**
  - `Cutback.Core` stays dependency-free apart from System.Text.Json.
  - `Cutback.Media` keeps only FFMpegCore.
  - Neither project may take a dependency that is not MIT-compatible.
- **Tests never shell out to ffmpeg** (CLAUDE.md "Testing"). Probing and encoding code that has to run ffmpeg stays untested glue, kept as thin as possible; everything it decides is a pure function with tests.
- **Filter graphs that grow with the cut count go in a file** (non-negotiable 4). The preview arguments use one fixed-size `-vf`/`-af` per decode, which is allowed inline.
- **Every kept audio segment gets the 8 ms fade** (non-negotiable 5), the preview included. Use `FilterGraphBuilder.FadeSeconds` and its `Math.Min(fade, length / 2)` rule.
- **Never modify the source video** (non-negotiable 2).
- **Style:**
  - Allman braces, always braced;
  - `sealed` classes; XML `<summary>` on public members that says why;
  - test names like `Undo_returns_the_state_pushed_before_the_edit`, with `// ---- section ----` comments;
  - FluentAssertions with `because` where it helps.
- **Gates per task:** `dotnet build` (warnings are errors), `dotnet test`, `dotnet format --verify-no-changes`.
- **Version:** `Directory.Build.props` `<Version>` becomes **0.2.0**. The release workflow's version job already prefers a higher props version over tag+1, so the first main build after merge publishes 0.2.0.
- **Never push** unless the user asks. Commit trailer: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **An ffmpeg that lists a hardware encoder but cannot use it** (e.g. `h264_nvenc` listed on a machine with no NVIDIA GPU). Expected: the probe-encode fails, the next candidate is tried, and export still works. Test: Task 3, `Candidates_put_libx264_first_then_hardware_then_openh264` plus the documented probe loop. The probe itself is untested glue, which the reviewer reads.
2. **An ffmpeg with no usable H.264 encoder at all.** Expected: `FfmpegException` with an actionable message naming libx264 and the install command, never a raw `Unknown encoder` line. Test: Task 3, `No_candidate_message_names_libx264`.
3. **Undo back to the saved state.** Expected: the Clips tab's title loses its `*`. Test: Task 1, `Undoing_back_to_the_saved_state_is_clean`.
4. **A boundary drag that ends where it started, or a drag interrupted by an undo.** Expected: no undo step, no stale snapshot. Tests: Task 1, `Drag_that_ends_where_it_began_leaves_no_step` and `Undo_during_a_drag_drops_the_drag_snapshot`.
5. **A kept segment shorter than 16 ms in the preview.** Expected: the fade shrinks to half the length, as in export, so ffmpeg never gets a negative `st`. Test: Task 2, `Audio_fade_shrinks_for_very_short_segments`.

---

### Task 1: `SegmentEditor` — the shared edit session (Core), and the app moves onto it

**Files:**
- Create: `src/Cutback.Core/SegmentEditor.cs`
- Create: `tests/Cutback.Core.Tests/SegmentEditorTests.cs`
- Modify: `src/Cutback.App/ViewModels/MainWindowViewModel.cs` (fields near line 27; `RunOpenAsync` near 413-416; close near 483; `ToggleSegment`, `MoveBoundary`, `CreateSection`, `CutWords`, `DeleteSegment`, `Undo`, `Redo`, `Edit`, lines ~640-786; every other `Edit(` call site, e.g. detect silence ~880, filler ~960, gameplay ~985)

**Interfaces:**
- Consumes: `SegmentList` (`Segments`, `Count`, `IndexAt`, `Toggle`, `Dissolve`, `SetRange`, `MoveBoundary`, `Restore`, `Changed`), `EditHistory<Segment[]>` (`Push`, `Undo`, `Redo`, `CanUndo`, `CanRedo`).
- Produces (Phase 4b relies on these exact names):
  ```csharp
  namespace Cutback.Core;
  public sealed class SegmentEditor
  {
      public SegmentEditor(SegmentList segments, EditHistory<Segment[]> history);
      public SegmentList Segments { get; }
      public EditHistory<Segment[]> History { get; }
      public bool IsDirty { get; }
      public bool IsDragging { get; }
      public event EventHandler? DirtyChanged;
      public bool Edit(Action<SegmentList> edit);
      public bool Toggle(int index);
      public bool Dissolve(int index);
      public bool CreateSection(double start, double end, double anchor);
      public void BeginBoundaryDrag();
      public double UpdateBoundaryDrag(int boundaryIndex, double time);
      public double EndBoundaryDrag(int boundaryIndex, double time);
      public bool Undo();
      public bool Redo();
      public void MarkSaved();
  }
  ```

- [ ] **Step 1: Write the failing tests**

`tests/Cutback.Core.Tests/SegmentEditorTests.cs`:

```csharp
using Cutback.Core.Models;

namespace Cutback.Core.Tests;

public sealed class SegmentEditorTests
{
    private static SegmentEditor ThreeSegments()
    {
        // kept [0,4) · cut [4,6) · kept [6,10)
        var list = new SegmentList(10.0,
        [
            Segment.Create(0, 4, enabled: true, SegmentOrigin.Auto),
            Segment.Create(4, 6, enabled: false, SegmentOrigin.Auto, "silence 2.00s"),
            Segment.Create(6, 10, enabled: true, SegmentOrigin.Auto),
        ]);
        return new SegmentEditor(list, new EditHistory<Segment[]>(100));
    }

    // ---- edits and undo -----------------------------------------------------------------------

    [Fact]
    public void Edit_that_changes_nothing_leaves_no_undo_step()
    {
        var editor = ThreeSegments();

        var changed = editor.Edit(_ => { });

        changed.Should().BeFalse();
        editor.History.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Edit_is_one_undo_step_and_undo_restores_the_partition()
    {
        var editor = ThreeSegments();
        var before = editor.Segments.Segments.ToArray();

        editor.Toggle(1).Should().BeTrue();
        editor.Segments.Segments[1].Enabled.Should().BeTrue();

        editor.Undo().Should().BeTrue();
        editor.Segments.Segments.Should().Equal(before);
        editor.History.CanUndo.Should().BeFalse();
        editor.History.CanRedo.Should().BeTrue();
    }

    [Fact]
    public void Redo_reapplies_an_undone_edit()
    {
        var editor = ThreeSegments();
        editor.Toggle(0);
        var after = editor.Segments.Segments.ToArray();
        editor.Undo();

        editor.Redo().Should().BeTrue();

        editor.Segments.Segments.Should().Equal(after);
    }

    [Fact]
    public void Undo_and_redo_with_nothing_to_do_return_false()
    {
        var editor = ThreeSegments();

        editor.Undo().Should().BeFalse();
        editor.Redo().Should().BeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Toggle_and_dissolve_outside_the_list_do_nothing(int index)
    {
        var editor = ThreeSegments();

        editor.Toggle(index).Should().BeFalse();
        editor.Dissolve(index).Should().BeFalse();
        editor.History.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Dissolve_is_one_undo_step()
    {
        var editor = ThreeSegments();

        editor.Dissolve(1).Should().BeTrue();

        editor.Segments.Count.Should().Be(1, "both neighbours are kept, so they merge over the removed cut");
        editor.Undo();
        editor.Segments.Count.Should().Be(3);
    }

    // ---- sections -----------------------------------------------------------------------------

    [Fact]
    public void Section_drawn_over_kept_footage_cuts_it()
    {
        var editor = ThreeSegments();

        editor.CreateSection(1, 2, anchor: 1).Should().BeTrue();

        var index = editor.Segments.IndexAt(1.5);
        editor.Segments.Segments[index].Enabled.Should().BeFalse();
        editor.Segments.Segments[index].Origin.Should().Be(SegmentOrigin.Manual);
    }

    [Fact]
    public void Section_drawn_inside_a_cut_restores_that_part()
    {
        var editor = ThreeSegments();

        editor.CreateSection(4.5, 5.5, anchor: 4.5).Should().BeTrue();

        editor.Segments.Segments[editor.Segments.IndexAt(5)].Enabled.Should().BeTrue();
    }

    [Fact]
    public void Section_anchored_outside_the_timeline_does_nothing()
    {
        var editor = ThreeSegments();

        editor.CreateSection(1, 2, anchor: 11).Should().BeFalse();
        editor.History.CanUndo.Should().BeFalse();
    }

    // ---- boundary drags -----------------------------------------------------------------------

    [Fact]
    public void Boundary_drag_is_one_undo_step()
    {
        var editor = ThreeSegments();
        var before = editor.Segments.Segments.ToArray();

        editor.BeginBoundaryDrag();
        editor.IsDragging.Should().BeTrue();
        editor.UpdateBoundaryDrag(1, 3.5);
        editor.UpdateBoundaryDrag(1, 3.0);
        editor.EndBoundaryDrag(1, 3.2).Should().BeApproximately(3.2, 1e-9);

        editor.IsDragging.Should().BeFalse();
        editor.Undo();
        editor.Segments.Segments.Should().Equal(before, "the whole drag is undone at once");
        editor.History.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Drag_that_ends_where_it_began_leaves_no_step()
    {
        var editor = ThreeSegments();

        // A press on a boundary and a release without moving (a move and back would leave the cut marked manual,
        // which is a real change: MoveBoundary marks disabled neighbours manual).
        editor.BeginBoundaryDrag();
        editor.EndBoundaryDrag(1, 4.0);

        editor.History.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Undo_during_a_drag_drops_the_drag_snapshot()
    {
        var editor = ThreeSegments();
        editor.Toggle(0);
        editor.BeginBoundaryDrag();

        editor.Undo();

        editor.IsDragging.Should().BeFalse();
        editor.History.CanRedo.Should().BeTrue();
    }

    // ---- dirty --------------------------------------------------------------------------------

    [Fact]
    public void A_new_editor_is_clean()
    {
        ThreeSegments().IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Edits_make_it_dirty_and_mark_saved_makes_it_clean()
    {
        var editor = ThreeSegments();
        var flips = 0;
        editor.DirtyChanged += (_, _) => flips++;

        editor.Toggle(1);
        editor.IsDirty.Should().BeTrue();

        editor.MarkSaved();
        editor.IsDirty.Should().BeFalse();
        flips.Should().Be(2);
    }

    [Fact]
    public void Undoing_back_to_the_saved_state_is_clean()
    {
        var editor = ThreeSegments();
        editor.Toggle(1);
        editor.Toggle(0);

        editor.Undo();
        editor.IsDirty.Should().BeTrue();
        editor.Undo();

        editor.IsDirty.Should().BeFalse("the partition equals the saved one again");
    }

    [Fact]
    public void DirtyChanged_fires_only_when_the_flag_flips()
    {
        var editor = ThreeSegments();
        var flips = 0;
        editor.DirtyChanged += (_, _) => flips++;

        editor.Toggle(1);
        editor.Toggle(0);
        editor.Toggle(2);

        flips.Should().Be(1);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Cutback.Core.Tests --filter FullyQualifiedName~SegmentEditorTests`
Expected: FAIL. The build breaks with `The type or namespace name 'SegmentEditor' could not be found`.

- [ ] **Step 3: Write the implementation**

`src/Cutback.Core/SegmentEditor.cs`:

```csharp
using Cutback.Core.Models;

namespace Cutback.Core;

/// <summary>
/// One editing surface over a <see cref="SegmentList"/>. The rules every Cutback UI follows live here, so the Cutback
/// app and Mainframe Editor's Clips tab cannot drift apart:
/// <list type="bullet">
/// <item>each edit is one undo step, and an edit that changes nothing leaves no step;</item>
/// <item>a boundary drag is one step, from Begin to End;</item>
/// <item>a drawn section takes the opposite state of the segment it was anchored in;</item>
/// <item><see cref="IsDirty"/> compares with the last save, so undoing back to the saved partition is clean.</item>
/// </list>
/// Undo snapshots are arrays of immutable <see cref="Segment"/> records, so a deep history stays cheap.
/// </summary>
public sealed class SegmentEditor
{
    private Segment[] _saved;
    private Segment[]? _dragSnapshot;
    private bool _dirty;

    /// <summary>
    /// Edits <paramref name="segments"/> with <paramref name="history"/> as its undo stack. The current partition counts as
    /// saved.
    /// </summary>
    public SegmentEditor(SegmentList segments, EditHistory<Segment[]> history)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(history);
        Segments = segments;
        History = history;
        _saved = [.. segments.Segments];
        segments.Changed += (_, _) => UpdateDirty();
    }

    public SegmentList Segments { get; }

    /// <summary>The undo stack (its <c>Changed</c> event is what a UI listens to for Undo/Redo availability).</summary>
    public EditHistory<Segment[]> History { get; }

    /// <summary>True while the partition differs from the one last marked saved.</summary>
    public bool IsDirty => _dirty;

    /// <summary>True between <see cref="BeginBoundaryDrag"/> and <see cref="EndBoundaryDrag"/>.</summary>
    public bool IsDragging => _dragSnapshot is not null;

    /// <summary>Raised when <see cref="IsDirty"/> flips.</summary>
    public event EventHandler? DirtyChanged;

    /// <summary>Runs <paramref name="edit"/> as one undo step; false (and no step) when it changed nothing.</summary>
    public bool Edit(Action<SegmentList> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var before = Segments.Segments.ToArray();
        edit(Segments);
        if (before.SequenceEqual(Segments.Segments))
        {
            return false;
        }

        History.Push(before);
        return true;
    }

    /// <summary>Flips the segment at <paramref name="index"/> (it becomes manual); false when out of range.</summary>
    public bool Toggle(int index) => index >= 0 && index < Segments.Count && Edit(s => s.Toggle(index));

    /// <summary>Removes the region at <paramref name="index"/>, its neighbours growing over it; false when out of range.</summary>
    public bool Dissolve(int index) => index >= 0 && index < Segments.Count && Edit(s => s.Dissolve(index));

    /// <summary>
    /// Makes [<paramref name="start"/>, <paramref name="end"/>) one manual section with the opposite state of the segment
    /// at <paramref name="anchor"/> (where the drag started). Drawing over kept footage cuts it, and drawing inside a cut
    /// restores that part. Returns false when the anchor is off the timeline or nothing changed.
    /// </summary>
    public bool CreateSection(double start, double end, double anchor)
    {
        var anchorIndex = Segments.IndexAt(anchor);
        if (anchorIndex < 0)
        {
            return false;
        }

        var enabled = !Segments.Segments[anchorIndex].Enabled;
        return Edit(s => s.SetRange(start, end, enabled));
    }

    /// <summary>Starts a boundary drag: the partition now is what one undo restores.</summary>
    public void BeginBoundaryDrag() => _dragSnapshot = [.. Segments.Segments];

    /// <summary>Moves the boundary live during a drag; returns the time applied after clamping.</summary>
    public double UpdateBoundaryDrag(int boundaryIndex, double time) => Segments.MoveBoundary(boundaryIndex, time);

    /// <summary>
    /// Ends a boundary drag at <paramref name="time"/> (already snapped by the caller) and records the whole drag as one
    /// undo step, or none when the partition is back where it started.
    /// </summary>
    public double EndBoundaryDrag(int boundaryIndex, double time)
    {
        var applied = Segments.MoveBoundary(boundaryIndex, time);
        if (_dragSnapshot is { } before && !before.SequenceEqual(Segments.Segments))
        {
            History.Push(before);
        }

        _dragSnapshot = null;
        return applied;
    }

    /// <summary>Restores the partition before the last edit; false when there is none.</summary>
    public bool Undo()
    {
        if (!History.CanUndo)
        {
            return false;
        }

        _dragSnapshot = null;
        Segments.Restore(History.Undo(Segments.Segments.ToArray()));
        return true;
    }

    /// <summary>Re-applies the last undone edit; false when there is none.</summary>
    public bool Redo()
    {
        if (!History.CanRedo)
        {
            return false;
        }

        _dragSnapshot = null;
        Segments.Restore(History.Redo(Segments.Segments.ToArray()));
        return true;
    }

    /// <summary>The current partition is what is on disk now.</summary>
    public void MarkSaved()
    {
        _saved = [.. Segments.Segments];
        UpdateDirty();
    }

    private void UpdateDirty()
    {
        var dirty = !_saved.SequenceEqual(Segments.Segments);
        if (dirty == _dirty)
        {
            return;
        }

        _dirty = dirty;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Cutback.Core.Tests --filter FullyQualifiedName~SegmentEditorTests`
Expected: PASS (17 tests).

- [ ] **Step 5: Move the app onto `SegmentEditor`**

In `MainWindowViewModel.cs`:
- Add the field `private SegmentEditor? _editor;` next to `_history` (line ~27) and delete `_dragSnapshot`.
- Wherever `Segments = new SegmentList(...)` is assigned (in `RunOpenAsync`, ~416), add `_editor = new SegmentEditor(Segments, _history);` right after it. Keep the existing `Segments.Changed += (_, _) => MarkDirty();`. The app keeps its own `IsDirty`, because transcript edits also dirty a project and `SegmentEditor` knows nothing of transcripts.
- Wherever `Segments` becomes null (project closed, ~483), set `_editor = null;`.
- Replace the bodies as follows (keep each `[RelayCommand]` attribute and method name):

```csharp
    [RelayCommand]
    private void ToggleSegment(int index) => _editor?.Toggle(index);

    /// <summary>A whole drag is one undo step: the snapshot is taken at Begin and recorded at End.</summary>
    [RelayCommand]
    private void MoveBoundary(BoundaryMove move)
    {
        if (_editor is not { } editor || move.BoundaryIndex < 1 || move.BoundaryIndex >= editor.Segments.Count)
        {
            return;
        }

        switch (move.Phase)
        {
            case BoundaryDragPhase.Begin:
                editor.BeginBoundaryDrag();
                break;

            case BoundaryDragPhase.Update:
                editor.UpdateBoundaryDrag(move.BoundaryIndex, move.Time);
                break;

            case BoundaryDragPhase.End:
                editor.EndBoundaryDrag(move.BoundaryIndex, move.Time);
                break;
        }
    }

    /// <summary>
    /// A range the user drew on the timeline becomes a section with the opposite state of the
    /// segment where the drag started (<see cref="SegmentEditor.CreateSection"/>).
    /// </summary>
    [RelayCommand]
    private void CreateSection(SectionRange range) => _editor?.CreateSection(range.Start, range.End, range.Anchor);
```

  - Find `DeleteSegment(int index)` (its body calls `Edit(s => s.Dissolve(index))` after a range check). Its body becomes `_editor?.Dissolve(index);`.
  - `Undo()` becomes `_editor?.Undo();` and `Redo()` becomes `_editor?.Redo();`. Keep the `CanExecute` attributes and the `CanUndo`/`CanRedo` properties.
  - The private `Edit(Action<SegmentList> edit)` becomes `private void Edit(Action<SegmentList> edit) => _editor?.Edit(edit);`. Its remaining callers (detect silence, filler words, gameplay, `CutWords`) are unchanged.

- [ ] **Step 6: Build and run the whole suite**

Run: `dotnet build && dotnet test && dotnet format --verify-no-changes`
Expected: build clean, all tests pass (Core count +17), format clean.

- [ ] **Step 7: Commit**

```bash
git add src/Cutback.Core/SegmentEditor.cs tests/Cutback.Core.Tests/SegmentEditorTests.cs src/Cutback.App/ViewModels/MainWindowViewModel.cs
git commit -m "Move the edit and undo rules into Core as SegmentEditor

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Public `FfmpegProcess` and `PreviewDecoding` (Media)

**Files:**
- Modify: `src/Cutback.Media/FfmpegProcess.cs` (class becomes `public`; summary updated)
- Create: `src/Cutback.Media/Preview/PreviewDecoding.cs`
- Create: `tests/Cutback.Media.Tests/PreviewDecodingTests.cs`

**Interfaces:**
- Consumes: `FilterGraphBuilder.F(double)` (internal, same assembly) and `FilterGraphBuilder.FadeSeconds`.
- Produces (Phase 4b relies on these exact names):
  ```csharp
  namespace Cutback.Media;
  public static class FfmpegProcess   // Start, StartRaw, WaitForSuccessAsync, TryKill: signatures unchanged
  namespace Cutback.Media.Preview;
  public static class PreviewDecoding
  {
      public const int MaxWidth = 960;
      public const double MaxFrameRate = 30;
      public static (int Width, int Height) PreviewSize(int sourceWidth, int sourceHeight, int maxWidth = MaxWidth);
      public static double PreviewFrameRate(double sourceFrameRate);
      public static IReadOnlyList<string> VideoArguments(string source, double start, double end, int width, int height, double frameRate);
      public static IReadOnlyList<string> AudioArguments(string source, double start, double end, int sampleRate);
      public static IReadOnlyList<string> FrameArguments(string source, double time, int width, int height);
  }
  ```
  `FfmpegProcess.Start` prepends `-nostdin -hide_banner -loglevel error`, so none of these argument lists includes them.

- [ ] **Step 1: Write the failing tests**

`tests/Cutback.Media.Tests/PreviewDecodingTests.cs`:

```csharp
using Cutback.Media.Preview;

namespace Cutback.Media.Tests;

public sealed class PreviewDecodingTests
{
    // ---- size and rate ------------------------------------------------------------------------

    [Theory]
    [InlineData(1920, 1080, 960, 540)]
    [InlineData(3840, 2160, 960, 540)]
    [InlineData(1280, 720, 960, 540)]
    [InlineData(640, 360, 640, 360)]
    [InlineData(721, 405, 720, 404)]
    [InlineData(1000, 1000, 960, 960)]
    public void PreviewSize_caps_the_width_keeps_the_aspect_and_stays_even(int w, int h, int expectedW, int expectedH)
    {
        PreviewDecoding.PreviewSize(w, h).Should().Be((expectedW, expectedH));
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-4, 4)]
    public void PreviewSize_rejects_a_degenerate_source(int w, int h)
    {
        var act = () => PreviewDecoding.PreviewSize(w, h);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(60.0, 30.0)]
    [InlineData(30.0, 30.0)]
    [InlineData(29.97, 29.97)]
    [InlineData(24.0, 24.0)]
    [InlineData(0.0, 30.0)]
    [InlineData(double.NaN, 30.0)]
    public void PreviewFrameRate_is_the_source_rate_capped_at_30(double source, double expected)
    {
        PreviewDecoding.PreviewFrameRate(source).Should().Be(expected);
    }

    // ---- arguments ----------------------------------------------------------------------------

    [Fact]
    public void VideoArguments_seek_scale_and_emit_raw_rgba()
    {
        var args = PreviewDecoding.VideoArguments("/r/a.mp4", 4.86, 12.04, 960, 540, 30);

        args.Should().Equal(
            "-ss", "4.86", "-i", "/r/a.mp4", "-t", "7.18", "-an",
            "-vf", "fps=30,scale=960:540:flags=bilinear",
            "-pix_fmt", "rgba", "-f", "rawvideo", "-");
    }

    [Fact]
    public void AudioArguments_fade_each_end_and_emit_stereo_float()
    {
        var args = PreviewDecoding.AudioArguments("/r/a.mp4", 4.86, 12.04, 48000);

        args.Should().Equal(
            "-ss", "4.86", "-i", "/r/a.mp4", "-t", "7.18", "-vn",
            "-af", "afade=t=in:st=0:d=0.008,afade=t=out:st=7.172:d=0.008",
            "-ac", "2", "-ar", "48000", "-f", "f32le", "-");
    }

    [Fact]
    public void Audio_fade_shrinks_for_very_short_segments()
    {
        var args = PreviewDecoding.AudioArguments("/r/a.mp4", 1.0, 1.01, 48000);

        args.Should().ContainInOrder("-af", "afade=t=in:st=0:d=0.005,afade=t=out:st=0.005:d=0.005");
    }

    [Fact]
    public void FrameArguments_decode_one_scaled_frame()
    {
        var args = PreviewDecoding.FrameArguments("/r/a.mp4", 3.5, 960, 540);

        args.Should().Equal(
            "-ss", "3.5", "-i", "/r/a.mp4", "-frames:v", "1", "-an",
            "-vf", "scale=960:540:flags=bilinear",
            "-pix_fmt", "rgba", "-f", "rawvideo", "-");
    }

    [Fact]
    public void Arguments_reject_an_empty_range()
    {
        var video = () => PreviewDecoding.VideoArguments("/r/a.mp4", 5, 5, 960, 540, 30);
        var audio = () => PreviewDecoding.AudioArguments("/r/a.mp4", 6, 5, 48000);

        video.Should().Throw<ArgumentOutOfRangeException>();
        audio.Should().Throw<ArgumentOutOfRangeException>();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Cutback.Media.Tests --filter FullyQualifiedName~PreviewDecodingTests`
Expected: FAIL, because the namespace `Cutback.Media.Preview` does not exist.

- [ ] **Step 3: Make `FfmpegProcess` public**

In `src/Cutback.Media/FfmpegProcess.cs`, change `internal static class FfmpegProcess` to `public static class FfmpegProcess` and replace its summary with:

```csharp
/// <summary>
/// Runs ffmpeg tools as child processes with redirected streams. Public so other front ends (Mainframe Editor's Clips
/// tab) decode and probe with the same preamble and error reporting. <see cref="Start"/> always prepends
/// <c>-nostdin -hide_banner -loglevel error</c>, and stderr is always redirected: read it (<c>StandardError.ReadToEndAsync</c>)
/// or ffmpeg can block on a full pipe. For binary output read <c>StandardOutput.BaseStream</c>.
/// </summary>
```

Add `<summary>` lines to `Start` ("Starts ffmpeg with the preamble; stdout is redirected when asked."), `WaitForSuccessAsync` (keep its existing summary), and `TryKill` ("Kills the process tree if it is still running; never throws."). These are needed because `GenerateDocumentationFile` is on and the members are now public. CS1591 is suppressed, but the repository documents public APIs.

- [ ] **Step 4: Write `PreviewDecoding`**

`src/Cutback.Media/Preview/PreviewDecoding.cs`:

```csharp
using System.Globalization;
using Cutback.Media.Export;

namespace Cutback.Media.Preview;

/// <summary>
/// ffmpeg arguments for an editor preview that plays the edit gaplessly: one video and one audio decode per kept
/// segment (raw RGBA frames and 32-bit float stereo on stdout), plus a single frame for scrubbing. Each filter is a
/// fixed size, so passing it inline cannot grow with the cut count (unlike the export graph, which goes in a file).
/// Audio gets the export's 8 ms fades, so the preview sounds like the result. Pass the lists to
/// <see cref="FfmpegProcess.Start"/>, which adds the preamble.
/// </summary>
public static class PreviewDecoding
{
    /// <summary>Preview frames are at most this wide: enough for an editor pane, a quarter of the upload cost of 1080p.</summary>
    public const int MaxWidth = 960;

    /// <summary>Preview frames are decoded at no more than this rate.</summary>
    public const double MaxFrameRate = 30;

    /// <summary>The preview size: <paramref name="maxWidth"/> wide at most, the source aspect, both sides even.</summary>
    public static (int Width, int Height) PreviewSize(int sourceWidth, int sourceHeight, int maxWidth = MaxWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        var width = Even(Math.Min(sourceWidth, maxWidth));
        var height = Even(sourceHeight * (double)width / sourceWidth);
        return (width, height);
    }

    /// <summary>The source rate capped at <see cref="MaxFrameRate"/>; an unknown rate previews at the cap.</summary>
    public static double PreviewFrameRate(double sourceFrameRate) =>
        double.IsFinite(sourceFrameRate) && sourceFrameRate > 0 ? Math.Min(sourceFrameRate, MaxFrameRate) : MaxFrameRate;

    /// <summary>Raw RGBA frames of [<paramref name="start"/>, <paramref name="end"/>) at the preview size and rate.</summary>
    public static IReadOnlyList<string> VideoArguments(string source, double start, double end, int width, int height, double frameRate)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        var length = Length(start, end);
        return
        [
            "-ss", FilterGraphBuilder.F(start), "-i", source, "-t", FilterGraphBuilder.F(length), "-an",
            "-vf", $"fps={FilterGraphBuilder.F(frameRate)},scale={width}:{height}:flags=bilinear",
            "-pix_fmt", "rgba", "-f", "rawvideo", "-",
        ];
    }

    /// <summary>Interleaved stereo float samples of [<paramref name="start"/>, <paramref name="end"/>), faded at each end.</summary>
    public static IReadOnlyList<string> AudioArguments(string source, double start, double end, int sampleRate)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var length = Length(start, end);
        var fade = Math.Min(FilterGraphBuilder.FadeSeconds, length / 2);
        var f = FilterGraphBuilder.F(fade);
        return
        [
            "-ss", FilterGraphBuilder.F(start), "-i", source, "-t", FilterGraphBuilder.F(length), "-vn",
            "-af", $"afade=t=in:st=0:d={f},afade=t=out:st={FilterGraphBuilder.F(length - fade)}:d={f}",
            "-ac", "2", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "-",
        ];
    }

    /// <summary>One raw RGBA frame at <paramref name="time"/>, for scrubbing while paused.</summary>
    public static IReadOnlyList<string> FrameArguments(string source, double time, int width, int height)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        return
        [
            "-ss", FilterGraphBuilder.F(Math.Max(0, time)), "-i", source, "-frames:v", "1", "-an",
            "-vf", $"scale={width}:{height}:flags=bilinear",
            "-pix_fmt", "rgba", "-f", "rawvideo", "-",
        ];
    }

    private static double Length(double start, double end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (!(end > start))
        {
            throw new ArgumentOutOfRangeException(nameof(end), end, "The range must end after it starts.");
        }

        return end - start;
    }

    private static int Even(double value) => Math.Max(2, 2 * (int)Math.Round(value / 2, MidpointRounding.AwayFromZero));
}
```

Check 721×405: `Even(720)` gives 720, then `405 × 720 / 721 = 404.44`, `/2 = 202.2`, rounded to 202, `×2` gives 404 ✓.
Check the short segment: length 0.01, fade `min(0.008, 0.005) = 0.005`, so the fade-out starts at `0.005` ✓.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Cutback.Media.Tests --filter FullyQualifiedName~PreviewDecodingTests`
Expected: PASS (20 tests).

- [ ] **Step 6: Full gates and commit**

Run: `dotnet build && dotnet test && dotnet format --verify-no-changes`

```bash
git add src/Cutback.Media/FfmpegProcess.cs src/Cutback.Media/Preview/PreviewDecoding.cs tests/Cutback.Media.Tests/PreviewDecodingTests.cs
git commit -m "Make FfmpegProcess public and add preview decoding arguments

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `DeliveryEncoder` — export without libx264 (Media), with the app passing the source size

**Files:**
- Create: `src/Cutback.Media/Export/DeliveryEncoder.cs`
- Create: `tests/Cutback.Media.Tests/DeliveryEncoderTests.cs`
- Modify: `src/Cutback.Media/Export/ExportRequest.cs` (three optional parameters)
- Modify: `src/Cutback.Media/Export/Exporter.cs` (`ExportPreciseAsync`, `CodecArguments`)
- Modify: `src/Cutback.App/ViewModels/ExportViewModel.cs:118` (pass the source size and rate)
- Modify: `src/Cutback.Media/Export/ExportMode.cs:5` (doc comment mentions libx264 only)

**Interfaces:**
- Consumes: `FfmpegProcess.Start`/`WaitForSuccessAsync`, `FfmpegLocation.FfmpegPath`, `FfmpegException(string, int, string)`.
- Produces:
  ```csharp
  namespace Cutback.Media.Export;
  public static class DeliveryEncoder
  {
      public const string Software = "libx264";
      public const string Fallback = "libopenh264";
      public static IReadOnlyList<string> ParseEncoders(string output);
      public static IReadOnlyList<string> Candidates(OSPlatform os, IReadOnlyCollection<string> available);
      public static long TargetBitrate(int width, int height, double frameRate);
      public static IReadOnlyList<string> VideoArguments(string encoder, long bitrate);
      public static IReadOnlyList<string> ProbeArguments(string encoder);
      public static string NoEncoderMessage(OSPlatform os);
      public static Task<string> SelectAsync(FfmpegLocation ffmpeg, CancellationToken cancellationToken);
  }
  public sealed record ExportRequest(string SourcePath, IReadOnlyList<Segment> Segments, double DurationSeconds,
      string OutputPath, ExportMode Mode, int Width = 0, int Height = 0, double FrameRate = 0);
  ```

- [ ] **Step 1: Write the failing tests**

`tests/Cutback.Media.Tests/DeliveryEncoderTests.cs`:

```csharp
using System.Runtime.InteropServices;
using Cutback.Media.Export;

namespace Cutback.Media.Tests;

public sealed class DeliveryEncoderTests
{
    private const string EncodersOutput = """
        Encoders:
         V..... = Video
         A..... = Audio
         ------
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D libopenh264          OpenH264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D h264_videotoolbox    VideoToolbox H.264 Encoder (codec h264)
         V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
         A....D aac                  AAC (Advanced Audio Coding)
        """;

    // ---- parsing and order --------------------------------------------------------------------

    [Fact]
    public void ParseEncoders_lists_encoder_names_and_skips_the_legend()
    {
        DeliveryEncoder.ParseEncoders(EncodersOutput).Should().Equal(
            "libx264", "libopenh264", "h264_videotoolbox", "h264_nvenc", "aac");
    }

    [Fact]
    public void Candidates_put_libx264_first_then_hardware_then_openh264()
    {
        var available = DeliveryEncoder.ParseEncoders(EncodersOutput);

        DeliveryEncoder.Candidates(OSPlatform.OSX, available).Should().Equal("libx264", "h264_videotoolbox", "libopenh264");
        DeliveryEncoder.Candidates(OSPlatform.Linux, available).Should().Equal("libx264", "h264_nvenc", "libopenh264");
    }

    [Fact]
    public void Candidates_on_windows_try_every_vendor_in_order()
    {
        string[] available = ["h264_mf", "h264_amf", "h264_qsv", "h264_nvenc", "libopenh264"];

        DeliveryEncoder.Candidates(OSPlatform.Windows, available).Should().Equal(
            "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libopenh264");
    }

    [Fact]
    public void Candidates_are_empty_when_no_h264_encoder_is_built_in()
    {
        DeliveryEncoder.Candidates(OSPlatform.OSX, ["aac", "libvpx-vp9"]).Should().BeEmpty();
    }

    // ---- arguments ----------------------------------------------------------------------------

    [Fact]
    public void Libx264_keeps_the_constant_quality_settings()
    {
        DeliveryEncoder.VideoArguments("libx264", 12_000_000).Should().Equal(
            "-c:v", "libx264", "-crf", "20", "-preset", "medium", "-pix_fmt", "yuv420p");
    }

    [Fact]
    public void Other_encoders_get_a_bitrate_target()
    {
        DeliveryEncoder.VideoArguments("h264_videotoolbox", 12_000_000).Should().Equal(
            "-c:v", "h264_videotoolbox", "-b:v", "12000000", "-maxrate", "18000000", "-bufsize", "24000000", "-pix_fmt", "yuv420p");
    }

    [Theory]
    [InlineData(1920, 1080, 60.0, 12_441_600)]
    [InlineData(1280, 720, 30.0, 2_764_800)]
    [InlineData(3840, 2160, 60.0, 49_766_400)]
    [InlineData(320, 180, 30.0, 2_000_000)]
    [InlineData(0, 0, 0.0, 12_000_000)]
    public void TargetBitrate_is_a_tenth_of_a_bit_per_pixel_clamped(int w, int h, double fps, long expected)
    {
        DeliveryEncoder.TargetBitrate(w, h, fps).Should().Be(expected);
    }

    [Fact]
    public void ProbeArguments_encode_one_tiny_frame_to_nowhere()
    {
        DeliveryEncoder.ProbeArguments("h264_nvenc").Should().Equal(
            "-f", "lavfi", "-i", "color=s=256x256:d=0.1", "-c:v", "h264_nvenc", "-f", "null", "-");
    }

    [Fact]
    public void No_candidate_message_names_libx264()
    {
        DeliveryEncoder.NoEncoderMessage(OSPlatform.OSX).Should().Contain("libx264").And.Contain("brew install ffmpeg");
        DeliveryEncoder.NoEncoderMessage(OSPlatform.Windows).Should().Contain("winget install");
        DeliveryEncoder.NoEncoderMessage(OSPlatform.Linux).Should().Contain("package manager");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Cutback.Media.Tests --filter FullyQualifiedName~DeliveryEncoderTests`
Expected: FAIL (`DeliveryEncoder` not found).

- [ ] **Step 3: Write `DeliveryEncoder`**

`src/Cutback.Media/Export/DeliveryEncoder.cs`:

```csharp
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Cutback.Media.Export;

/// <summary>
/// Picks the H.264 encoder for a precise export. The order is libx264 (constant quality, the best result), then the
/// platform's hardware encoder, then libopenh264 (in LGPL ffmpeg builds), so export works on an ffmpeg built without
/// x264. ffmpeg lists hardware encoders even on machines without the hardware, so each candidate is probe-encoded
/// before use, the same way Mainframe Engine's recorder does it. The choice is cached per ffmpeg binary.
/// </summary>
public static class DeliveryEncoder
{
    public const string Software = "libx264";
    public const string Fallback = "libopenh264";

    /// <summary>Bits per pixel per frame for encoders without a constant-quality mode we can rely on.</summary>
    private const double BitsPerPixel = 0.1;
    private const long MinBitrate = 2_000_000;
    private const long MaxBitrate = 80_000_000;
    private const long UnknownBitrate = 12_000_000;

    private static readonly ConcurrentDictionary<string, Task<string>> Selected = new(StringComparer.Ordinal);

    /// <summary>Encoder names from <c>ffmpeg -encoders</c> (lines like <c> V....D libx264  …</c>; the legend is skipped).</summary>
    public static IReadOnlyList<string> ParseEncoders(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return
        [
            .. output.Split('\n')
                .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] is 'V' or 'A' or 'S' && parts[1] != "=")
                .Select(parts => parts[1]),
        ];
    }

    /// <summary>The encoders to try, best first, that this ffmpeg was built with.</summary>
    public static IReadOnlyList<string> Candidates(OSPlatform os, IReadOnlyCollection<string> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        string[] hardware = os == OSPlatform.OSX ? ["h264_videotoolbox"]
            : os == OSPlatform.Windows ? ["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf"]
            : ["h264_nvenc"];
        return [.. new[] { Software }.Concat(hardware).Append(Fallback).Where(available.Contains)];
    }

    /// <summary>A bitrate for <paramref name="width"/>×<paramref name="height"/> at <paramref name="frameRate"/> (unknown: 12 Mbit/s).</summary>
    public static long TargetBitrate(int width, int height, double frameRate)
    {
        if (width <= 0 || height <= 0 || !(frameRate > 0))
        {
            return UnknownBitrate;
        }

        return Math.Clamp((long)Math.Round(width * (double)height * frameRate * BitsPerPixel), MinBitrate, MaxBitrate);
    }

    /// <summary>The video half of the codec arguments for <paramref name="encoder"/>.</summary>
    public static IReadOnlyList<string> VideoArguments(string encoder, long bitrate)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoder);
        if (encoder == Software)
        {
            return ["-c:v", Software, "-crf", "20", "-preset", "medium", "-pix_fmt", "yuv420p"];
        }

        return
        [
            "-c:v", encoder,
            "-b:v", bitrate.ToString(CultureInfo.InvariantCulture),
            "-maxrate", (bitrate * 3 / 2).ToString(CultureInfo.InvariantCulture),
            "-bufsize", (bitrate * 2).ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
        ];
    }

    /// <summary>Encodes one 256×256 frame to nowhere: fails fast when the encoder's hardware or library is missing.</summary>
    public static IReadOnlyList<string> ProbeArguments(string encoder) =>
        ["-f", "lavfi", "-i", "color=s=256x256:d=0.1", "-c:v", encoder, "-f", "null", "-"];

    /// <summary>What to tell the user when no H.264 encoder works.</summary>
    public static string NoEncoderMessage(OSPlatform os)
    {
        var install = os == OSPlatform.OSX ? "Install one with \"brew install ffmpeg\"."
            : os == OSPlatform.Windows ? "Install one with \"winget install Gyan.FFmpeg\"."
            : "Install the ffmpeg package from your distribution's package manager.";
        return $"This ffmpeg has no working H.264 encoder (it was built without {Software}, and no hardware encoder or {Fallback} works). {install}";
    }

    /// <summary>
    /// The best working encoder for <paramref name="ffmpeg"/>, cached per binary.
    /// </summary>
    /// <exception cref="FfmpegException">None works (the message is <see cref="NoEncoderMessage"/>).</exception>
    public static Task<string> SelectAsync(FfmpegLocation ffmpeg, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        var task = Selected.GetOrAdd(ffmpeg.FfmpegPath, _ => SelectUncachedAsync(ffmpeg, cancellationToken));
        if (task.IsFaulted || task.IsCanceled)
        {
            Selected.TryRemove(new KeyValuePair<string, Task<string>>(ffmpeg.FfmpegPath, task));
        }

        return task;
    }

    private static async Task<string> SelectUncachedAsync(FfmpegLocation ffmpeg, CancellationToken ct)
    {
        var listing = await RunAsync(ffmpeg, ["-encoders"], ct).ConfigureAwait(false);
        var os = CurrentOs();
        foreach (var candidate in Candidates(os, ParseEncoders(listing.Output)))
        {
            var probe = await RunAsync(ffmpeg, ProbeArguments(candidate), ct).ConfigureAwait(false);
            if (probe.ExitCode == 0)
            {
                return candidate;
            }
        }

        throw new FfmpegException(NoEncoderMessage(os), -1, string.Empty);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(FfmpegLocation ffmpeg, IEnumerable<string> args, CancellationToken ct)
    {
        using var process = FfmpegProcess.Start(ffmpeg.FfmpegPath, args, redirectStandardOutput: true);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            FfmpegProcess.TryKill(process);
            throw;
        }

        await stderr.ConfigureAwait(false);
        return (process.ExitCode, await stdout.ConfigureAwait(false));
    }

    private static OSPlatform CurrentOs() =>
        OperatingSystem.IsMacOS() ? OSPlatform.OSX : OperatingSystem.IsWindows() ? OSPlatform.Windows : OSPlatform.Linux;
}
```

Check the bitrates:
- 1920×1080×60×0.1 = 12,441,600 ✓.
- 1280×720×30×0.1 = 2,764,800 ✓.
- 3840×2160×60×0.1 = 49,766,400 ✓.
- 320×180×30×0.1 = 172,800, clamped to 2,000,000 ✓.

The `-encoders` listing goes to stdout. The `-loglevel error` from the preamble does not suppress it.

- [ ] **Step 4: Run the encoder tests**

Run: `dotnet test tests/Cutback.Media.Tests --filter FullyQualifiedName~DeliveryEncoderTests`
Expected: PASS (13 tests).

- [ ] **Step 5: Use it in `Exporter` and pass the source size from the app**

`ExportRequest.cs`: append three optional parameters, with `<param>` docs:

```csharp
/// <param name="Width">Source width in pixels, for the bitrate of encoders without constant quality (0: unknown).</param>
/// <param name="Height">Source height in pixels (0: unknown).</param>
/// <param name="FrameRate">Source frame rate (0: unknown).</param>
public sealed record ExportRequest(
    string SourcePath,
    IReadOnlyList<Segment> Segments,
    double DurationSeconds,
    string OutputPath,
    ExportMode Mode,
    int Width = 0,
    int Height = 0,
    double FrameRate = 0);
```

`Exporter.ExportPreciseAsync`: after `var capabilities = …`, choose the encoder for H.264 containers and pass it to `CodecArguments`:

```csharp
        var encoder = extension == ".webm" ? null : await DeliveryEncoder.SelectAsync(_ffmpeg, ct).ConfigureAwait(false);
        var bitrate = DeliveryEncoder.TargetBitrate(request.Width, request.Height, request.FrameRate);
        …
        args.AddRange(CodecArguments(extension, encoder, bitrate));
```

Replace `CodecArguments` with:

```csharp
    private static IEnumerable<string> CodecArguments(string extension, string? encoder, long bitrate)
    {
        if (extension == ".webm")
        {
            return ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k"];
        }

        IEnumerable<string> video = DeliveryEncoder.VideoArguments(encoder ?? DeliveryEncoder.Software, bitrate);
        IEnumerable<string> audio = ["-c:a", "aac", "-b:a", "192k"];
        return extension is ".mp4" or ".mov" ? [.. video, .. audio, "-movflags", "+faststart"] : [.. video, .. audio];
    }
```

`ExportViewModel.cs:118`: pass the source dimensions:

```csharp
            var request = new ExportRequest(_project.Source.Path, _segments, _project.Source.DurationSeconds, OutputPath,
                IsFast ? ExportMode.Fast : ExportMode.Precise, _project.Source.Width, _project.Source.Height, _project.Source.FrameRate);
```

`ExportMode.cs:5`: wherever its comment says Precise re-encodes with libx264, make it "re-encodes (libx264 when available, else the platform's hardware encoder or libopenh264; see `DeliveryEncoder`)".

- [ ] **Step 6: Full gates and commit**

Run: `dotnet build && dotnet test && dotnet format --verify-no-changes`

```bash
git add src/Cutback.Media/Export tests/Cutback.Media.Tests/DeliveryEncoderTests.cs src/Cutback.App/ViewModels/ExportViewModel.cs
git commit -m "Export with a hardware encoder or OpenH264 when ffmpeg has no libx264

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Publish to nuget.org, version 0.2.0, a local feed for engine development, and docs

**Files:**
- Modify: `.github/workflows/release.yml` (`release` job: push the packages)
- Modify: `Directory.Build.props:18` (`<Version>0.2.0</Version>`)
- Create: `src/Cutback.Core/README.md`, `src/Cutback.Media/README.md` (package readmes)
- Modify: `src/Cutback.Core/Cutback.Core.csproj`, `src/Cutback.Media/Cutback.Media.csproj` (`PackageReadmeFile`)
- Create: `scripts/pack-local.sh`
- Modify: `CLAUDE.md` (Export "Precise" bullet; Licensing paragraph on packages; project layout adds `Preview/` and `SegmentEditor.cs`; Undo section names `SegmentEditor`)
- Modify: `README.md` (export encoder sentence near line 69; the packages paragraph if one exists, else one line under the licence section)
- Modify: `docs/superpowers/specs/2026-10-12-mainframe-engine-integration-design.md` (Open question 2 answered; §4.1 names `DeliveryEncoder`; §4.3 says `FfmpegProcess` and `PreviewDecoding`)

**Interfaces:**
- Produces: packages `Cutback.Core` and `Cutback.Media` at version 0.2.0 on nuget.org after the user's one-time setup; `scripts/pack-local.sh <version>` writes both to `artifacts/local-feed/`.

- [ ] **Step 1: Bump the version and add package readmes**

`Directory.Build.props`: change `<Version>0.1.0</Version>` to `<Version>0.2.0</Version>`.

`src/Cutback.Core/README.md`:

```markdown
# Cutback.Core

The pure editing logic of [Cutback](https://github.com/brogan89/Cutback), a dead-air and filler-word trimmer for screen
recordings. No UI and no ffmpeg:

- `SegmentList`, a partition of the timeline into kept and removed segments, with the operations that preserve it;
- `SegmentEditor`, the shared edit session: undo, boundary drags as one step, and dirty tracking;
- the silence, filler-word and gameplay cut planners;
- `.cutback` project files (`ProjectSerializer`), and the Mainframe Engine `.mfcap.json` capture sidecar reader.

MIT licensed. Used by Cutback and by Mainframe Editor's Clips tab.
```

`src/Cutback.Media/README.md`:

```markdown
# Cutback.Media

The ffmpeg side of [Cutback](https://github.com/brogan89/Cutback). It runs the user's ffmpeg as a child process and
never links or bundles it:

- locating ffmpeg (`FfmpegLocator`) and probing media (`MediaProbe`);
- waveforms, silence detection and PCM extraction;
- preview decode arguments (`PreviewDecoding`);
- precise and fast export (`Exporter`, with the H.264 encoder chosen by `DeliveryEncoder`).

MIT licensed. Depends on Cutback.Core and FFMpegCore (MIT).
```

In each of the two `.csproj` files, add `<PackageReadmeFile>README.md</PackageReadmeFile>` to the property group with `PackageId`. Add `<None Include="README.md" Pack="true" PackagePath="\" />` next to the existing `LICENSE` item.

- [ ] **Step 2: Push the packages from the release job**

In `.github/workflows/release.yml`'s `release` job:
- Add `NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}` to the job's `env:` block.
- Add these steps after the `gh release create` step. Pushing after the GitHub release means a failed build leg never leaves packages published without a release.

```yaml
      - uses: actions/setup-dotnet@v6
        if: env.NUGET_API_KEY != ''
        with:
          global-json-file: global.json

      - name: Push packages to nuget.org
        if: env.NUGET_API_KEY != ''
        run: |
          for package in dist/*.nupkg; do
            dotnet nuget push "$package" --api-key "$NUGET_API_KEY" --source https://api.nuget.org/v3/index.json --skip-duplicate
          done

      - name: Note the missing nuget.org key
        if: env.NUGET_API_KEY == ''
        run: echo "::warning::NUGET_API_KEY is not set, so Cutback.Core and Cutback.Media were attached to the release but not pushed to nuget.org."
```

Read the job first to confirm that `dist/` is where `actions/download-artifact` puts the merged artifacts. The survey found `path: dist` with `merge-multiple: true`. If the job checks out with `actions/checkout`, `global.json` is present for `setup-dotnet`. If it does not, use `dotnet-version: 10.0.x` instead of `global-json-file`.

- [ ] **Step 3: A local feed for engine development**

`scripts/pack-local.sh`:

```bash
#!/usr/bin/env bash
# Packs Cutback.Core and Cutback.Media into artifacts/local-feed so Mainframe Engine can build its Clips tab against
# unreleased changes. Usage: scripts/pack-local.sh 0.2.0-local.1
# The engine side points NuGet at the folder from a nuget.config outside its repository (see the Phase 4b plan).
set -euo pipefail
version="${1:?usage: scripts/pack-local.sh <version, e.g. 0.2.0-local.1>}"
root="$(cd "$(dirname "$0")/.." && pwd)"
feed="$root/artifacts/local-feed"
mkdir -p "$feed"
for project in src/Cutback.Core src/Cutback.Media; do
  dotnet pack "$root/$project" -c Release -p:Version="$version" -o "$feed" --nologo -v q
done
echo "Packed $version into $feed"
```

Run `chmod +x scripts/pack-local.sh`. Check that `artifacts/` is git-ignored with `git check-ignore -q artifacts/local-feed`. If it is not, add `artifacts/` to `.gitignore`.

Run: `scripts/pack-local.sh 0.2.0-local.1 && ls artifacts/local-feed`
Expected: `Cutback.Core.0.2.0-local.1.nupkg` and `Cutback.Media.0.2.0-local.1.nupkg`.

Then run `unzip -l artifacts/local-feed/Cutback.Media.0.2.0-local.1.nupkg | grep -E "README|LICENSE"`.
Expected: both files are at the package root.

- [ ] **Step 4: Docs**

**`CLAUDE.md`:**
- **Export section, the "Precise" bullet.** Make it: "Precise (default): re-encode. Video is `libx264 -crf 20 -preset medium` when this ffmpeg has it; otherwise `DeliveryEncoder` probe-encodes the platform's hardware encoder (`h264_videotoolbox`; `h264_nvenc`/`qsv`/`amf`/`mf`), then `libopenh264`, at a bitrate of 0.1 bit per pixel per frame. Audio is `aac -b:a 192k`." Keep the `.webm` sentence.
- **Licensing, the paragraph on Core and Media.** Add: "Both are published to nuget.org by the release workflow when the `NUGET_API_KEY` secret is set. `scripts/pack-local.sh <version>` packs them into `artifacts/local-feed` for building Mainframe Engine against unreleased changes."
- **Project layout tree.** Add `SegmentEditor.cs` under `Cutback.Core/`, with the comment `# the edit session: undo steps, drags, dirty`, and `Preview/` under `Cutback.Media/`, with the comment `# decode arguments for gapless editor previews`.
- **Undo subsection.** After its first paragraph, add: "The rules live in `SegmentEditor` (Core), which the app's view model and Mainframe Editor's Clips tab both use, so they cannot drift apart."

**`README.md`:** wherever export says it uses libx264, add "(or the platform's hardware encoder or OpenH264 when your ffmpeg has no x264)".

**Spec:**
- Open question 2 becomes struck through: "~~**Package feed.**~~ Resolved 2026-10-13: nuget.org, pushed by the release workflow (`NUGET_API_KEY`)."
- §4.1's `EncoderSelector` paragraph becomes `DeliveryEncoder` (the name avoids confusion with the engine's recorder-side `EncoderSelector`). Note the bitrate rule.
- §4.3 becomes: "through `FfmpegProcess` (public since 0.2.0) and the arguments from `PreviewDecoding`".

- [ ] **Step 5: Gates and commit**

Run: `dotnet build && dotnet test && dotnet format --verify-no-changes`

```bash
git add .github/workflows/release.yml Directory.Build.props src/Cutback.Core src/Cutback.Media scripts/pack-local.sh CLAUDE.md README.md docs/superpowers/specs/2026-10-12-mainframe-engine-integration-design.md .gitignore
git commit -m "Publish Cutback.Core and Cutback.Media 0.2.0 to nuget.org

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Hand-off to the user (after the branch is reviewed)

The engine's Phase 4b can build against `artifacts/local-feed` straight away. Its PR can merge only after 0.2.0 is on nuget.org, which needs these steps from the user:

1. Create a nuget.org account (or sign in), and an API key scoped to push `Cutback.Core` and `Cutback.Media`. Both are new package ids.
2. Add it to brogan89/Cutback as the repository secret `NUGET_API_KEY`.
3. Merge #7 and this branch's PR to `main`. The release workflow then publishes v0.2.0 with both packages attached and pushed.
