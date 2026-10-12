# Phase 1 — Mainframe Engine Gameplay Recorder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `GameHost --record out.mp4` and `SceneTree.Recorder` record clean, constant-frame-rate gameplay with sample-synced audio, plus a `.mfcap.json` sidecar describing input, loading, pause and game markers. The engine stays MIT by running ffmpeg as a child process.

**Architecture:**
- **New `MainframeEngine/Src/Recording/` folder.** Pure units (schedulers, span builder, argument builders, encoder choice, sidecar writer, streaming WAV) do the thinking and carry the tests. A `GameRecorder` orchestrates them behind an `IRecordingBackend`; the real backend drives ffmpeg.
- **Video** comes from a new internal frame-stream on `VulkanRenderer`: one host-visible buffer per frame in flight, read after `BeginFrame`'s existing fence wait, so there is no stall.
- **Audio** comes from a tap after the master mix in `AudioMixRoot` (live), or from `RenderNullDevice` once per update (offline).

**Tech Stack:** .NET 10, Silk.NET Vulkan, SoundFlow (via `AudioServer`), System.Text.Json source generation, xunit.v3 with plain `Assert`, and an external ffmpeg CLI.

**Spec:** `/Users/brogan/dev/SimpleVideoEditor/docs/superpowers/specs/2026-10-12-mainframe-engine-integration-design.md` (Phase 1, §1.1–1.8)

**Repository:** every path below is relative to `/Users/brogan/mainframe/mainframe-engine` unless it is absolute. Work on a branch there, e.g. `feature/gameplay-recorder`.

## Global Constraints

- The engine never links, bundles or P/Invokes an encoder. ffmpeg is only ever a child process found on the user's machine (spec: "The engine never links or bundles an encoder").
- The audio callback stays lock-free and allocation-free (`docs/design/audio.md`). The tap uses `SpscRing<float>.EnqueueBatch` only.
- Engine conventions:
  - file-scoped namespaces; public front types in `MainframeEngine`, internals may use `MainframeEngine.Audio` etc.;
  - `sealed` by default;
  - dense `<summary>` docs that explain why;
  - `Log.Info($"[Recorder] …")` tags.
- Tests:
  - `Tests/MainframeEngine.Tests/Recording/`, namespace `MainframeEngine.Tests.Recording`;
  - `public sealed class XxxTests`, test names as PascalCase sentences, plain xunit `Assert`;
  - internals are visible to the test project.
- Gates: `just build` (warnings are errors), `just test`, `just format-check`.
- Sidecar: format `"mainframe-capture"`, version `1`, file = output path + `".mfcap.json"`, all times in seconds on the output video's timeline (spec §1.7).
- Input: an event holds the input span open for 250 ms; a held key or button keeps it open; mouse motion counts above 2 px; a gamepad axis counts above 0.25 magnitude; input spans with gaps under 100 ms merge.
- Bitrate `width · height · fps · 0.25` bits/s; GOP = fps (one second); intermediate `.mkv` in `<out>.partial/`; mux to `.mp4` with AAC 192k and `+faststart`.
- Mode: `--fixed-fps` present ⇒ offline, otherwise live.

## Spec amendments made by this plan

Task 12 records these in both the spec and the engine docs.

1. **Two stream buffers, not three.** `BeginFrame` already waits on `_inFlightFences[_currentFrame]` before reusing a frame slot. So a readback buffer indexed by frame slot is complete exactly when that wait returns, and one buffer per frame in flight (`MaxFramesInFlight` = 2) is enough.
2. **Streaming WAV writer.** `MainframeEngine.Audio.WavWriter` writes a whole buffer at once, so the recorder adds a `StreamingWavWriter` that patches the RIFF sizes when it finishes.
3. **The audio length is fixed at Stop.** In both modes the WAV is padded or truncated to `round(videoFrames · sampleRate / fps)` before muxing, so video and audio lengths always match.
4. **Odd window sizes.** `yuv420p` needs even dimensions, so the encoder arguments crop one pixel when a dimension is odd.

## Review Focus

1. **Odd pixel dimensions** (a resizable window at 1081 px, a fractional content scale). The encoder must not refuse to start. Test: Task 5, `OddDimensionsAreCroppedToEven`.
2. **The game stops or crashes mid-recording** (editor Stop, window close, exception). Expected: the recording is finalised on close, or `.partial/` keeps a playable MKV and the error names it. Test: Task 10, `StopDuringCloseFinalisesSynchronously`, plus `BackendFailureKeepsPartialPathInError`.
3. **The window resizes or goes fullscreen while recording.** Expected: the recording stops cleanly with "window size changed", keeping everything so far. Test: Task 10, `StreamBreakStopsWithReason`. The renderer side is checked manually in Task 8.
4. **Starting a recording while the previous one is still muxing.** Expected: a clear `InvalidOperationException`, never two ffmpeg pipelines writing the same path. Test: Task 10, `StartWhileFinalisingThrows`.
5. **A held key with no new events for many seconds** (holding W to walk). Expected: never treated as idle. Test: Task 3, `HeldKeyKeepsSpanOpenWithoutEvents`.

---

### Task 1: Command-line flags

**Files:**
- Modify: `MainframeEngine/Src/Project/GameHostOptions.cs`
- Test: `Tests/MainframeEngine.Tests/Recording/RecordingOptionsTests.cs` (new)

**Interfaces:**
- Produces `GameHostOptions` properties:
  - `string? RecordPath`
  - `float RecordScale` (0 = unset)
  - `string? FfmpegPath`
  - derived `bool RecordOffline => RecordPath is not null && FixedFps > 0`
- `Apply` sets:
  - `EnableFrameCapture = true` when recording;
  - `ContentScale = RecordScale` when it is greater than 0;
  - `Audio.Device = AudioDeviceMode.NullManual` when `RecordOffline`.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace MainframeEngine.Tests.Recording;

/// <summary>--record, --record-scale and --ffmpeg parsing and how they shape the engine options.</summary>
public sealed class RecordingOptionsTests
{
    private static EngineOptions Defaults() => new ProjectSettings { Name = "T" }.ToEngineOptions();

    [Fact]
    public void RecordTakesAPathAndEnablesFrameCapture()
    {
        var options = GameHostOptions.Parse(["--record", "out.mp4"]);
        Assert.Equal("out.mp4", options.RecordPath);
        Assert.False(options.RecordOffline);
        Assert.True(options.Apply(Defaults()).EnableFrameCapture);
    }

    [Fact]
    public void RecordWithFixedFpsIsOfflineAndUsesTheManualNullDevice()
    {
        var options = GameHostOptions.Parse(["--record", "out.mp4", "--fixed-fps", "60"]);
        Assert.True(options.RecordOffline);
        Assert.Equal(AudioDeviceMode.NullManual, options.Apply(Defaults()).Audio.Device);
    }

    [Fact]
    public void LiveRecordingKeepsTheRealAudioDevice()
    {
        var options = GameHostOptions.Parse(["--record", "out.mp4"]);
        Assert.Equal(AudioDeviceMode.Auto, options.Apply(Defaults()).Audio.Device);
    }

    [Fact]
    public void RecordScaleSetsTheContentScale()
    {
        var options = GameHostOptions.Parse(["--record", "o.mp4", "--record-scale", "2"]);
        Assert.Equal(2f, options.Apply(Defaults()).ContentScale);
    }

    [Fact]
    public void FfmpegPathIsKept()
    {
        Assert.Equal("/opt/ff", GameHostOptions.Parse(["--ffmpeg", "/opt/ff"]).FfmpegPath);
    }

    [Theory]
    [InlineData("--record")]
    [InlineData("--record-scale")]
    [InlineData("--ffmpeg")]
    public void FlagsWithoutAValueThrow(string flag)
    {
        Assert.Throws<ArgumentException>(() => GameHostOptions.Parse([flag]));
    }

    [Fact]
    public void RecordScaleOutOfRangeThrows()
    {
        Assert.Throws<ArgumentException>(() => GameHostOptions.Parse(["--record-scale", "9"]));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~RecordingOptionsTests"`
Expected: a compile error, because `RecordPath` is not defined.

- [ ] **Step 3: Implement.** In `GameHostOptions.cs`:
  - Add three rows to the `<list type="table">` in the class summary:

```csharp
/// <item><term><c>--record &lt;file.mp4&gt;</c></term><description>record gameplay with ffmpeg until quit (live; offline with <c>--fixed-fps</c>) and write <c>file.mp4.mfcap.json</c></description></item>
/// <item><term><c>--record-scale &lt;n&gt;</c></term><description>fixed content scale for the recording (a 1920×1080-point window at 2 is 4K)</description></item>
/// <item><term><c>--ffmpeg &lt;path&gt;</c></term><description>the ffmpeg executable to record with (else <c>MAINFRAME_FFMPEG</c>, <c>PATH</c>, common install folders)</description></item>
```

  - Add the properties:

```csharp
    /// <summary><c>--record</c>: the video to record to. Null when not recording from the command line.</summary>
    public string? RecordPath { get; init; }

    /// <summary><c>--record-scale</c>: <see cref="EngineOptions.ContentScale"/> for the recording; 0 leaves it alone.</summary>
    public float RecordScale { get; init; }

    /// <summary><c>--ffmpeg</c>: the encoder executable. See <see cref="FfmpegResolver"/>.</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>Recording with a fixed timestep: deterministic, any resolution, audio from the manual null device.</summary>
    public bool RecordOffline => RecordPath is not null && FixedFps > 0;
```

  - Add the `Parse` cases:

```csharp
                case "--record":
                    options = options with { RecordPath = Value(args, ref i) };
                    break;
                case "--record-scale":
                    options = options with { RecordScale = Scale(args, ref i) };
                    break;
                case "--ffmpeg":
                    options = options with { FfmpegPath = Value(args, ref i) };
                    break;
```

  - Add the helper beside `Seconds`:

```csharp
    private static float Scale(IReadOnlyList<string> args, ref int i)
    {
        var name = args[i];
        var text = Value(args, ref i);
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !(value > 0f) || value > 4f)
            throw new ArgumentException($"{name} must be a scale above 0 and at most 4 (got '{text}').");
        return value;
    }
```

  - Add to `Apply`, before `return options;`:

```csharp
        if (RecordPath is not null)
            options.EnableFrameCapture = true;
        if (RecordScale > 0f)
            options.ContentScale = RecordScale;
        if (RecordOffline)
            options.Audio.Device = AudioDeviceMode.NullManual;
```

  `EngineOptions` is a mutable struct parameter and `Audio` is a field, so `options.Audio.Device = …` assigns into the local copy that `Apply` returns.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~RecordingOptionsTests|FullyQualifiedName~GameHost"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine/Src/Project/GameHostOptions.cs Tests/MainframeEngine.Tests/Recording/RecordingOptionsTests.cs
git commit -m "Add --record, --record-scale and --ffmpeg host flags"
```

---

### Task 2: Frame and sample schedulers

**Files:**
- Create: `MainframeEngine/Src/Recording/FrameScheduler.cs`
- Create: `MainframeEngine/Src/Recording/SampleScheduler.cs`
- Test: `Tests/MainframeEngine.Tests/Recording/SchedulerTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed class FrameScheduler(int fps, bool offline)`:
    - `bool ShouldCapture(double time)`, called when a frame is about to be copied;
    - `int CopiesFor(double time)`, called when the frame's pixels arrive, returning 0 to drop;
    - `void Committed(int copies)` after a successful write;
    - `long WrittenFrames { get; }`.
  - `internal static class SampleScheduler`:
    - `long SamplesThrough(long frames, int sampleRate, int fps)`
    - `int SamplesForFrame(long frame, int sampleRate, int fps)`

- [ ] **Step 1: Write the failing tests**

```csharp
namespace MainframeEngine.Tests.Recording;

/// <summary>Constant-frame-rate output from irregular frames, and exact sample counts per video frame.</summary>
public sealed class SchedulerTests
{
    private static int[] Run(FrameScheduler s, IEnumerable<double> times, Func<int, bool>? accept = null)
    {
        var written = new List<int>();
        var n = 0;
        foreach (var t in times)
        {
            if (!s.ShouldCapture(t))
            {
                written.Add(-1); // never copied
                continue;
            }

            var copies = s.CopiesFor(t);
            if (copies > 0 && (accept?.Invoke(n++) ?? true))
                s.Committed(copies);
            written.Add(copies);
        }

        return [.. written];
    }

    [Fact]
    public void ThirtyHertzInputIsDoubledToSixty()
    {
        var times = Enumerable.Range(0, 4).Select(i => i / 30.0);
        Assert.Equal([1, 2, 2, 2], Run(new FrameScheduler(60, offline: false), times));
    }

    [Fact]
    public void OneTwentyHertzInputSkipsEveryOtherFrameWithoutCopying()
    {
        var times = Enumerable.Range(0, 6).Select(i => i / 120.0);
        Assert.Equal([1, -1, 1, -1, 1, -1], Run(new FrameScheduler(60, offline: false), times));
    }

    [Fact]
    public void ADroppedWriteIsMadeUpByTheNextFrame()
    {
        var s = new FrameScheduler(60, offline: false);
        var times = Enumerable.Range(0, 3).Select(i => i / 60.0);
        var result = Run(s, times, accept: n => n != 1); // the writer queue was full for the second frame
        Assert.Equal([1, 1, 2], result);
        Assert.Equal(3, s.WrittenFrames);
    }

    [Fact]
    public void OfflineWritesEveryFrameOnce()
    {
        var s = new FrameScheduler(60, offline: true);
        Assert.Equal([1, 1, 1], Run(s, [0, 0, 0])); // offline ignores the clock
        Assert.Equal(3, s.WrittenFrames);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(60)]
    [InlineData(144)]
    public void SamplesPerFrameSumExactlyToTheTotal(int fps)
    {
        long sum = 0;
        for (long f = 0; f < 10_000; f++)
        {
            var n = SampleScheduler.SamplesForFrame(f, 48_000, fps);
            Assert.InRange(n, 48_000 / fps, 48_000 / fps + 1);
            sum += n;
        }

        Assert.Equal(SampleScheduler.SamplesThrough(10_000, 48_000, fps), sum);
        Assert.Equal((10_000L * 48_000 + fps / 2) / fps, sum);
    }

    [Fact]
    public void SixtyFpsIsExactlyEightHundredSamplesPerFrame()
    {
        Assert.Equal(800, SampleScheduler.SamplesForFrame(12_345, 48_000, 60));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~SchedulerTests"`
Expected: a compile error, because `FrameScheduler` is not defined.

- [ ] **Step 3: Implement**

`FrameScheduler.cs`:

```csharp
namespace MainframeEngine;

/// <summary>
/// Decides how many times each rendered frame is written so a recording is constant frame rate.
/// Live: a frame stamped <c>t</c> covers output frames up to <c>floor(t·fps)</c>; slow frames repeat,
/// fast ones are skipped before they are even copied, and a write the encoder could not take is made
/// up by the next frame (length and sync hold, only smoothness suffers). Offline: one in, one out.
/// </summary>
internal sealed class FrameScheduler(int fps, bool offline)
{
    private long _promised;

    /// <summary>Output frames written so far.</summary>
    public long WrittenFrames { get; private set; }

    /// <summary>Called before a frame is copied off the GPU: false means it would be skipped anyway.</summary>
    public bool ShouldCapture(double time)
    {
        if (offline)
            return true;
        var target = (long)Math.Floor(time * fps) + 1;
        if (target <= _promised)
            return false;
        _promised = target;
        return true;
    }

    /// <summary>How many output frames the frame stamped <paramref name="time"/> fills (0: drop it).</summary>
    public int CopiesFor(double time) =>
        offline ? 1 : (int)Math.Max(0, (long)Math.Floor(time * fps) + 1 - WrittenFrames);

    /// <summary>The writer accepted the frame <paramref name="copies"/> times.</summary>
    public void Committed(int copies) => WrittenFrames += copies;
}
```

`SampleScheduler.cs`:

```csharp
namespace MainframeEngine;

/// <summary>
/// Exact audio sample counts per video frame: through frame <c>n</c> there are
/// <c>round(n · sampleRate / fps)</c> samples, so the total never drifts for rates that do not divide.
/// </summary>
internal static class SampleScheduler
{
    public static long SamplesThrough(long frames, int sampleRate, int fps) => (frames * sampleRate + fps / 2) / fps;

    public static int SamplesForFrame(long frame, int sampleRate, int fps) =>
        (int)(SamplesThrough(frame + 1, sampleRate, fps) - SamplesThrough(frame, sampleRate, fps));
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~SchedulerTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine/Src/Recording Tests/MainframeEngine.Tests/Recording/SchedulerTests.cs
git commit -m "Add constant-frame-rate and sample-count schedulers for recording"
```

---

### Task 3: Input activity and the capture timeline

**Files:**
- Create: `MainframeEngine/Src/Recording/InputActivity.cs`
- Create: `MainframeEngine/Src/Recording/CaptureTimeline.cs`
- Test: `Tests/MainframeEngine.Tests/Recording/CaptureTimelineTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed class InputActivity`:
    - `bool Observe(InputEvent e)`: true when it counts as player input;
    - `bool IsHeld { get; }`.
  - `internal sealed class CaptureTimeline`:
    - `void Input(double t)`, `void Update(double t, bool held, bool loading, bool paused)`;
    - `void Mark(double t, string label)`;
    - `int OpenKeep(double t, string label)` / `int OpenSkip(double t, string label)` / `void Close(int handle, double t)`;
    - `CaptureSpans Finish(double t)`.
  - `internal sealed record CaptureSpan(double Start, double End, string? Label = null)`
  - `internal sealed record CaptureMarker(double Time, string Label)`
  - `internal sealed record CaptureSpans(IReadOnlyList<CaptureSpan> Input, IReadOnlyList<CaptureSpan> Loading, IReadOnlyList<CaptureSpan> Paused, IReadOnlyList<CaptureSpan> Keep, IReadOnlyList<CaptureSpan> Skip, IReadOnlyList<CaptureMarker> Markers)`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Tests.Recording;

/// <summary>What counts as player input, and the spans the sidecar records.</summary>
public sealed class CaptureTimelineTests
{
    [Fact]
    public void KeyDownCountsAndHoldsUntilKeyUp()
    {
        var a = new InputActivity();
        Assert.True(a.Observe(new InputEventKey { Key = Key.W, Pressed = true }));
        Assert.True(a.IsHeld);
        Assert.True(a.Observe(new InputEventKey { Key = Key.W, Pressed = false }));
        Assert.False(a.IsHeld);
    }

    [Fact]
    public void SmallMouseMotionAndTinyStickDriftDoNotCount()
    {
        var a = new InputActivity();
        Assert.False(a.Observe(new InputEventMouseMotion { Relative = new Vector2(1f, 1f) }));
        Assert.True(a.Observe(new InputEventMouseMotion { Relative = new Vector2(3f, 0f) }));
        Assert.False(a.Observe(new InputEventGamepadAxis { Axis = GamepadAxis.LeftStick, Value = new Vector2(0.1f, 0.1f) }));
        Assert.True(a.Observe(new InputEventGamepadAxis { Axis = GamepadAxis.LeftStick, Value = new Vector2(0.5f, 0f) }));
        Assert.True(a.IsHeld); // a deflected stick is held until it returns
        a.Observe(new InputEventGamepadAxis { Axis = GamepadAxis.LeftStick, Value = Vector2.Zero });
        Assert.False(a.IsHeld);
    }

    [Fact]
    public void AnEventHoldsTheInputSpanOpenFor250Ms()
    {
        var t = new CaptureTimeline();
        t.Input(1.0);
        t.Update(1.2, held: false, loading: false, paused: false);
        t.Update(1.3, held: false, loading: false, paused: false);
        var spans = t.Finish(5.0);
        Assert.Equal([new CaptureSpan(1.0, 1.25)], spans.Input);
    }

    [Fact]
    public void HeldKeyKeepsSpanOpenWithoutEvents()
    {
        var t = new CaptureTimeline();
        t.Input(1.0);
        for (var s = 1.0; s <= 9.0; s += 0.5)
            t.Update(s, held: true, loading: false, paused: false);
        t.Update(9.5, held: false, loading: false, paused: false);
        t.Update(9.8, held: false, loading: false, paused: false);
        Assert.Equal([new CaptureSpan(1.0, 9.25)], t.Finish(20).Input); // tail runs 250 ms from the last held update (9.0)
    }

    [Fact]
    public void GapsUnder100MsMerge()
    {
        var t = new CaptureTimeline();
        t.Input(1.0);
        t.Update(1.3, held: false, loading: false, paused: false); // closes at 1.25
        t.Input(1.33);                                             // 80 ms later: same span
        t.Update(2.0, held: false, loading: false, paused: false);
        Assert.Equal([new CaptureSpan(1.0, 1.58)], t.Finish(3).Input);
    }

    [Fact]
    public void LoadingAndPausedFollowTheirFlags()
    {
        var t = new CaptureTimeline();
        t.Update(0.0, held: false, loading: true, paused: false);
        t.Update(2.0, held: false, loading: false, paused: false);
        t.Update(5.0, held: false, loading: false, paused: true);
        var spans = t.Finish(6.0);
        Assert.Equal([new CaptureSpan(0.0, 2.0)], spans.Loading);
        Assert.Equal([new CaptureSpan(5.0, 6.0)], spans.Paused);
    }

    [Fact]
    public void KeepSkipAndMarkersCarryLabelsAndOpenScopesCloseAtFinish()
    {
        var t = new CaptureTimeline();
        var keep = t.OpenKeep(1.0, "cutscene");
        t.Close(keep, 3.0);
        t.OpenSkip(4.0, "menu");
        t.Mark(2.0, "boss intro");
        var spans = t.Finish(6.0);
        Assert.Equal([new CaptureSpan(1.0, 3.0, "cutscene")], spans.Keep);
        Assert.Equal([new CaptureSpan(4.0, 6.0, "menu")], spans.Skip);
        Assert.Equal([new CaptureMarker(2.0, "boss intro")], spans.Markers);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~CaptureTimelineTests"`
Expected: a compile error, because `InputActivity` is not defined.

- [ ] **Step 3: Implement**

`InputActivity.cs`:

```csharp
using Silk.NET.Input;

namespace MainframeEngine;

/// <summary>
/// Classifies input events for the recording sidecar: key, mouse and gamepad buttons, mouse motion over
/// 2 px and a stick or trigger beyond 0.25 count as player input. Pressed buttons and deflected axes are
/// "held" until released, so walking with W held is never mistaken for idling. The recorder's own
/// threshold, independent of <see cref="InputAction.Deadzone"/>.
/// </summary>
internal sealed class InputActivity
{
    private const float MotionPixels = 2f;
    private const float AxisThreshold = 0.25f;

    private readonly HashSet<Key> _keys = [];
    private readonly HashSet<MouseButton> _mouse = [];
    private readonly HashSet<(int Device, ButtonName Button)> _pads = [];
    private readonly HashSet<(int Device, GamepadAxis Axis)> _axes = [];

    public bool IsHeld => _keys.Count + _mouse.Count + _pads.Count + _axes.Count > 0;

    /// <summary>Updates held state; true when <paramref name="e"/> is player input.</summary>
    public bool Observe(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey key:
                Track(_keys, key.Key, key.Pressed);
                return true;
            case InputEventMouseButton button:
                Track(_mouse, button.Button, button.Pressed);
                return true;
            case InputEventGamepadButton pad:
                Track(_pads, (pad.Device, pad.Button), pad.Pressed);
                return true;
            case InputEventMouseMotion motion:
                return motion.Relative.Length() > MotionPixels;
            case InputEventMouseWheel:
                return true;
            case InputEventGamepadAxis axis:
                var deflected = axis.Value.Length() > AxisThreshold;
                var wasDeflected = _axes.Contains((axis.Device, axis.Axis));
                Track(_axes, (axis.Device, axis.Axis), deflected);
                return deflected || wasDeflected;
            default:
                return false; // actions are synthesised from the events above; text duplicates keys
        }
    }

    private static void Track<T>(HashSet<T> set, T item, bool down)
    {
        if (down)
            set.Add(item);
        else
            set.Remove(item);
    }
}
```

`CaptureTimeline.cs`:

```csharp
namespace MainframeEngine;

/// <summary>A span of the recording's timeline in seconds. <see cref="Label"/> is set for Keep and Skip.</summary>
internal sealed record CaptureSpan(double Start, double End, string? Label = null);

/// <summary>A point the game marked, in seconds.</summary>
internal sealed record CaptureMarker(double Time, string Label);

/// <summary>Everything the sidecar lists, sorted by time.</summary>
internal sealed record CaptureSpans(
    IReadOnlyList<CaptureSpan> Input,
    IReadOnlyList<CaptureSpan> Loading,
    IReadOnlyList<CaptureSpan> Paused,
    IReadOnlyList<CaptureSpan> Keep,
    IReadOnlyList<CaptureSpan> Skip,
    IReadOnlyList<CaptureMarker> Markers);

/// <summary>
/// Builds the sidecar's spans from per-frame state and events, all on the recording clock. Input: an
/// event holds the span open 250 ms, a held control keeps it open, and a gap under 100 ms rejoins the
/// previous span. Loading and pause follow flags polled once per frame. Keep/Skip are explicit scopes.
/// </summary>
internal sealed class CaptureTimeline
{
    private const double InputTail = 0.25;
    private const double MergeGap = 0.1;

    private readonly List<CaptureSpan> _input = [];
    private readonly List<CaptureSpan> _loading = [];
    private readonly List<CaptureSpan> _paused = [];
    private readonly List<CaptureSpan> _keep = [];
    private readonly List<CaptureSpan> _skip = [];
    private readonly List<CaptureMarker> _markers = [];
    private readonly Dictionary<int, (bool Keep, double Start, string Label)> _scopes = [];

    private double? _inputStart;
    private double _inputUntil;
    private double? _loadingStart;
    private double? _pausedStart;
    private int _nextHandle;

    public void Input(double t)
    {
        if (_inputStart is null)
        {
            if (_input.Count > 0 && t - _input[^1].End < MergeGap)
            {
                _inputStart = _input[^1].Start;
                _input.RemoveAt(_input.Count - 1);
            }
            else
            {
                _inputStart = t;
            }
        }

        _inputUntil = Math.Max(_inputUntil, t + InputTail);
    }

    public void Update(double t, bool held, bool loading, bool paused)
    {
        if (held && _inputStart is not null)
            _inputUntil = Math.Max(_inputUntil, t + InputTail);
        else if (held)
            Input(t);
        if (_inputStart is { } start && !held && t >= _inputUntil)
        {
            _input.Add(new CaptureSpan(start, _inputUntil));
            _inputStart = null;
        }

        Flag(ref _loadingStart, loading, t, _loading);
        Flag(ref _pausedStart, paused, t, _paused);
    }

    public void Mark(double t, string label) => _markers.Add(new CaptureMarker(t, label));

    public int OpenKeep(double t, string label) => Open(keep: true, t, label);

    public int OpenSkip(double t, string label) => Open(keep: false, t, label);

    public void Close(int handle, double t)
    {
        if (!_scopes.Remove(handle, out var scope))
            return;
        (scope.Keep ? _keep : _skip).Add(new CaptureSpan(scope.Start, Math.Max(scope.Start, t), scope.Label));
    }

    /// <summary>Closes everything still open at <paramref name="t"/> (the end of the video) and returns the spans.</summary>
    public CaptureSpans Finish(double t)
    {
        if (_inputStart is { } start)
            _input.Add(new CaptureSpan(start, Math.Min(_inputUntil, t)));
        _inputStart = null;
        Flag(ref _loadingStart, false, t, _loading);
        Flag(ref _pausedStart, false, t, _paused);
        foreach (var handle in _scopes.Keys.ToArray())
            Close(handle, t);

        static List<CaptureSpan> Clip(IEnumerable<CaptureSpan> spans, double end) =>
            [.. spans.Select(s => s with { End = Math.Min(s.End, end) }).Where(s => s.End > s.Start).OrderBy(s => s.Start)];

        return new CaptureSpans(Clip(_input, t), Clip(_loading, t), Clip(_paused, t), Clip(_keep, t), Clip(_skip, t),
            [.. _markers.Where(m => m.Time <= t).OrderBy(m => m.Time)]);
    }

    private int Open(bool keep, double t, string label)
    {
        var handle = ++_nextHandle;
        _scopes[handle] = (keep, t, label);
        return handle;
    }

    private static void Flag(ref double? start, bool on, double t, List<CaptureSpan> into)
    {
        if (on && start is null)
            start = t;
        else if (!on && start is { } s)
        {
            if (t > s)
                into.Add(new CaptureSpan(s, t));
            start = null;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~CaptureTimelineTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine/Src/Recording Tests/MainframeEngine.Tests/Recording/CaptureTimelineTests.cs
git commit -m "Track player input, loading, pause and game scopes for the recording sidecar"
```

---

### Task 4: Sidecar writer and the shared fixture

**Files:**
- Create: `MainframeEngine/Src/Recording/CaptureSidecarWriter.cs`
- Create: `Tests/MainframeEngine.Tests/Recording/Fixtures/sample.mfcap.json` (byte-identical copy of `/Users/brogan/dev/SimpleVideoEditor/tests/Cutback.Core.Tests/Fixtures/sample.mfcap.json`)
- Modify: `Tests/MainframeEngine.Tests/MainframeEngine.Tests.csproj` (copy `Recording/Fixtures/**` to output)
- Test: `Tests/MainframeEngine.Tests/Recording/CaptureSidecarWriterTests.cs`

**Interfaces:**
- Consumes: `CaptureSpans`, `CaptureSpan`, `CaptureMarker` (Task 3).
- Produces:
  - `internal sealed record CaptureInfo(string Video, string EngineVersion, string Game, string Scene, bool Offline, int FrameRate, int Width, int Height, double DurationSeconds, long DroppedFrames)`
  - `internal static class CaptureSidecarWriter`:
    - `string ToJson(CaptureInfo info, CaptureSpans spans)`
    - `void Write(string videoPath, CaptureInfo info, CaptureSpans spans)` (atomic: temp file then move)
    - `string PathFor(string videoPath)`

- [ ] **Step 1: Copy the fixture.** If Phase 2 Task 3 has not landed yet, create the Cutback copy first from that plan.

```bash
mkdir -p Tests/MainframeEngine.Tests/Recording/Fixtures
cp /Users/brogan/dev/SimpleVideoEditor/tests/Cutback.Core.Tests/Fixtures/sample.mfcap.json Tests/MainframeEngine.Tests/Recording/Fixtures/
```

In the test csproj, beside the existing `Tests/Content/**` item:

```xml
    <None Include="Recording\Fixtures\**" CopyToOutputDirectory="PreserveNewest" LinkBase="Recording\Fixtures" />
```

- [ ] **Step 2: Write the failing tests**

```csharp
using System.Text.Json.Nodes;

namespace MainframeEngine.Tests.Recording;

/// <summary>The sidecar contract (spec §1.7): writing the fixture's data reproduces the fixture Cutback reads.</summary>
public sealed class CaptureSidecarWriterTests
{
    private static readonly CaptureInfo Info = new("forest-20261012-143005.mp4", "0.42.0", "Forest", "res://Scenes/Forest.scn",
        Offline: false, FrameRate: 60, Width: 1920, Height: 1080, DurationSeconds: 60.0, DroppedFrames: 0);

    private static readonly CaptureSpans Spans = new(
        Input: [new(0.0, 8.0), new(8.5, 14.0), new(22.0, 31.0), new(44.0, 52.0), new(53.0, 56.0)],
        Loading: [new(14.5, 19.0)],
        Paused: [new(56.0, 60.0)],
        Keep: [new(32.0, 35.0, "cutscene")],
        Skip: [],
        Markers: [new(26.0, "boss intro"), new(41.0, "chest opens")]);

    [Fact]
    public void WritingTheFixtureDataReproducesTheSharedFixture()
    {
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Recording", "Fixtures", "sample.mfcap.json")));
        var actual = JsonNode.Parse(CaptureSidecarWriter.ToJson(Info, Spans));
        Assert.True(JsonNode.DeepEquals(expected, actual), CaptureSidecarWriter.ToJson(Info, Spans));
    }

    [Fact]
    public void InputAndLoadingSpansHaveNoLabelKey()
    {
        var json = JsonNode.Parse(CaptureSidecarWriter.ToJson(Info, Spans))!;
        Assert.Null(json["input"]![0]!["label"]);
        Assert.Equal("cutscene", (string?)json["keep"]![0]!["label"]);
    }

    [Fact]
    public void OfflineModeIsWrittenAsOffline()
    {
        var json = JsonNode.Parse(CaptureSidecarWriter.ToJson(Info with { Offline = true }, Spans))!;
        Assert.Equal("offline", (string?)json["mode"]);
    }

    [Fact]
    public void WriteIsAtomicAndNamedAfterTheVideo()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var video = Path.Combine(dir.FullName, "clip.mp4");
            CaptureSidecarWriter.Write(video, Info, Spans);
            Assert.True(File.Exists(video + ".mfcap.json"));
            Assert.Single(Directory.GetFiles(dir.FullName)); // no temp file left behind
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~CaptureSidecarWriterTests"`
Expected: a compile error, because `CaptureInfo` is not defined.

- [ ] **Step 4: Implement** (`CaptureSidecarWriter.cs`):

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MainframeEngine;

/// <summary>What the sidecar says about the recording itself (spec §1.7).</summary>
internal sealed record CaptureInfo(
    string Video,
    string EngineVersion,
    string Game,
    string Scene,
    bool Offline,
    int FrameRate,
    int Width,
    int Height,
    double DurationSeconds,
    long DroppedFrames);

/// <summary>
/// Writes <c>&lt;video&gt;.mfcap.json</c>, version 1 of the Mainframe capture sidecar that Cutback (and
/// later the editor's Clips tab) reads to cut idle play, loading and pauses. The engine owns the format;
/// <c>Tests/…/Recording/Fixtures/sample.mfcap.json</c> is byte-identical to Cutback's copy, so a change
/// here fails both test suites until both readers agree.
/// </summary>
internal static class CaptureSidecarWriter
{
    public const string FormatName = "mainframe-capture";
    public const int Version = 1;
    public const string FileSuffix = ".mfcap.json";

    public static string PathFor(string videoPath) => videoPath + FileSuffix;

    public static string ToJson(CaptureInfo info, CaptureSpans spans)
    {
        var file = new SidecarFile(
            FormatName, Version, info.Video, info.EngineVersion, info.Game, info.Scene,
            info.Offline ? "offline" : "live", info.FrameRate, info.Width, info.Height, info.DurationSeconds, info.DroppedFrames,
            [.. spans.Input.Select(Span)], [.. spans.Loading.Select(Span)], [.. spans.Paused.Select(Span)],
            [.. spans.Keep.Select(Span)], [.. spans.Skip.Select(Span)],
            [.. spans.Markers.Select(m => new MarkerFile(m.Time, m.Label))]);
        return JsonSerializer.Serialize(file, SidecarJsonContext.Default.SidecarFile);
    }

    /// <summary>Writes next to <paramref name="videoPath"/> via a temp file and a move, so a reader never sees half a file.</summary>
    public static void Write(string videoPath, CaptureInfo info, CaptureSpans spans)
    {
        var path = PathFor(videoPath);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson(info, spans));
        File.Move(temp, path, overwrite: true);
    }

    private static SpanFile Span(CaptureSpan s) => new(s.Start, s.End, s.Label);

    internal sealed record SpanFile(
        double Start,
        double End,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label);

    internal sealed record MarkerFile(double Time, string Label);

    internal sealed record SidecarFile(
        string Format, int Version, string Video, string EngineVersion, string Game, string Scene, string Mode,
        int FrameRate, int Width, int Height, double DurationSeconds, long DroppedFrames,
        List<SpanFile> Input, List<SpanFile> Loading, List<SpanFile> Paused, List<SpanFile> Keep, List<SpanFile> Skip,
        List<MarkerFile> Markers);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CaptureSidecarWriter.SidecarFile))]
internal sealed partial class SidecarJsonContext : JsonSerializerContext
{
}
```

The comparison is `JsonNode.DeepEquals`, so whitespace and number formatting (`60` vs `60.0`) do not matter. `JsonNode` compares numbers by value.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~CaptureSidecarWriterTests"`
Expected: all PASS. If `DeepEquals` fails on `60` vs `60.0`, compare the two `GetValue<double>()` results in a custom walker rather than editing the fixture. The fixture is the contract.

- [ ] **Step 6: Commit**

```bash
git add MainframeEngine/Src/Recording Tests/MainframeEngine.Tests
git commit -m "Write the .mfcap.json recording sidecar against the shared fixture"
```

---

### Task 5: Finding ffmpeg, choosing an encoder, building arguments

**Files:**
- Create: `MainframeEngine/Src/Recording/FfmpegResolver.cs`
- Create: `MainframeEngine/Src/Recording/EncoderSelector.cs`
- Create: `MainframeEngine/Src/Recording/FfmpegArgs.cs`
- Test: `Tests/MainframeEngine.Tests/Recording/FfmpegTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class FfmpegNotFoundException(string message) : Exception(message)`.
  - `internal static class FfmpegResolver`:
    - `string Resolve(string? explicitPath, string? envPath, string? pathVariable, OSPlatformKind os, Func<string, bool> fileExists)` throws `FfmpegNotFoundException`;
    - `string ResolveForThisMachine(string? explicitPath)`.
  - `internal enum OSPlatformKind { Windows, MacOS, Linux }`.
  - `internal static class EncoderSelector`:
    - `IReadOnlyList<string> ParseEncoders(string ffmpegEncodersOutput)`
    - `IReadOnlyList<string> Candidates(OSPlatformKind os, IReadOnlyCollection<string> available)`
  - `internal enum PixelLayout { Bgra, Rgba }`.
  - `internal static class FfmpegArgs`:
    - `IReadOnlyList<string> Encode(string encoder, int width, int height, int fps, PixelLayout layout, string outputMkv)`
    - `IReadOnlyList<string> Mux(string videoMkv, string audioWav, string outputMp4)`
    - `IReadOnlyList<string> Probe(string encoder)`
    - `IReadOnlyList<string> ListEncoders()`
    - `long Bitrate(int width, int height, int fps)`

- [ ] **Step 1: Write the failing tests**

```csharp
namespace MainframeEngine.Tests.Recording;

/// <summary>ffmpeg lookup order, encoder preference per OS, and the exact command lines (ffmpeg itself never runs here).</summary>
public sealed class FfmpegTests
{
    [Fact]
    public void ExplicitPathWinsThenEnvThenPathThenKnownFolders()
    {
        var exists = new HashSet<string> { "/x/ffmpeg", "/env/ffmpeg", "/usr/bin/ffmpeg", "/opt/homebrew/bin/ffmpeg" };
        Assert.Equal("/x/ffmpeg", FfmpegResolver.Resolve("/x/ffmpeg", "/env/ffmpeg", "/usr/bin", OSPlatformKind.MacOS, exists.Contains));
        Assert.Equal("/env/ffmpeg", FfmpegResolver.Resolve(null, "/env/ffmpeg", "/usr/bin", OSPlatformKind.MacOS, exists.Contains));
        Assert.Equal("/usr/bin/ffmpeg", FfmpegResolver.Resolve(null, null, "/nope:/usr/bin", OSPlatformKind.MacOS, exists.Contains));
        Assert.Equal("/opt/homebrew/bin/ffmpeg", FfmpegResolver.Resolve(null, null, "/nope", OSPlatformKind.MacOS, exists.Contains));
    }

    [Fact]
    public void AnExplicitPathThatDoesNotExistIsAnError()
    {
        var ex = Assert.Throws<FfmpegNotFoundException>(() =>
            FfmpegResolver.Resolve("/missing/ffmpeg", null, "/usr/bin", OSPlatformKind.Linux, _ => false));
        Assert.Contains("/missing/ffmpeg", ex.Message);
    }

    [Theory]
    [InlineData(OSPlatformKind.MacOS, "brew install ffmpeg")]
    [InlineData(OSPlatformKind.Windows, "winget install Gyan.FFmpeg")]
    [InlineData(OSPlatformKind.Linux, "sudo apt install ffmpeg")]
    public void NotFoundNamesThePlatformInstallCommand(OSPlatformKind os, string command)
    {
        var ex = Assert.Throws<FfmpegNotFoundException>(() => FfmpegResolver.Resolve(null, null, "", os, _ => false));
        Assert.Contains(command, ex.Message);
    }

    [Fact]
    public void WindowsLooksForTheExeName()
    {
        Assert.Equal(@"C:\ff\ffmpeg.exe", FfmpegResolver.Resolve(null, null, @"C:\ff", OSPlatformKind.Windows, p => p == @"C:\ff\ffmpeg.exe"));
    }

    private const string EncodersOutput = """
        Encoders:
         V..... = Video
         ------
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D h264_videotoolbox    VideoToolbox H.264 Encoder (codec h264)
         A....D aac                  AAC (Advanced Audio Coding)
        """;

    [Fact]
    public void ParsesEncoderNamesFromTheListing()
    {
        Assert.Equal(["libx264", "h264_videotoolbox", "aac"], EncoderSelector.ParseEncoders(EncodersOutput));
    }

    [Fact]
    public void HardwareComesFirstThenSoftwareFallbacks()
    {
        var available = EncoderSelector.ParseEncoders(EncodersOutput).ToHashSet();
        Assert.Equal(["h264_videotoolbox", "libx264"], EncoderSelector.Candidates(OSPlatformKind.MacOS, available));
        Assert.Equal(["libx264"], EncoderSelector.Candidates(OSPlatformKind.Windows, available));
    }

    [Fact]
    public void WindowsOrderIsNvencQsvAmfMf()
    {
        var all = new HashSet<string> { "h264_mf", "h264_amf", "h264_qsv", "h264_nvenc", "libopenh264" };
        Assert.Equal(["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libopenh264"], EncoderSelector.Candidates(OSPlatformKind.Windows, all));
    }

    [Fact]
    public void BitrateIsAQuarterBitPerPixelPerFrame()
    {
        Assert.Equal(31_104_000, FfmpegArgs.Bitrate(1920, 1080, 60));
    }

    [Fact]
    public void EncodeReadsRawFramesFromStdinWithOneSecondGop()
    {
        var args = FfmpegArgs.Encode("h264_videotoolbox", 1920, 1080, 60, PixelLayout.Bgra, "/r/video.mkv");
        Assert.Equal(
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", "1920x1080", "-r", "60", "-i", "-",
            "-an", "-c:v", "h264_videotoolbox", "-b:v", "31104000", "-g", "60", "-pix_fmt", "yuv420p",
            "-progress", "pipe:1", "-nostats", "/r/video.mkv",
        ], args);
    }

    [Fact]
    public void Libx264UsesQualityModeNotBitrate()
    {
        var args = FfmpegArgs.Encode("libx264", 1280, 720, 30, PixelLayout.Rgba, "/v.mkv");
        Assert.Contains("rgba", args);
        Assert.Equal("ultrafast", args[args.ToList().IndexOf("-preset") + 1]);
        Assert.Equal("14", args[args.ToList().IndexOf("-crf") + 1]);
        Assert.DoesNotContain("-b:v", args);
    }

    [Fact]
    public void OddDimensionsAreCroppedToEven()
    {
        var args = FfmpegArgs.Encode("libx264", 1281, 721, 60, PixelLayout.Bgra, "/v.mkv").ToList();
        Assert.Equal("crop=1280:720:0:0", args[args.IndexOf("-vf") + 1]);
        Assert.Equal("1281x721", args[args.IndexOf("-s") + 1]); // input size is still the real one
    }

    [Fact]
    public void MuxCopiesVideoAndEncodesAac()
    {
        Assert.Equal(
        [
            "-hide_banner", "-loglevel", "error", "-y", "-i", "/r/video.mkv", "-i", "/r/audio.wav",
            "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "/r/out.mp4",
        ], FfmpegArgs.Mux("/r/video.mkv", "/r/audio.wav", "/r/out.mp4"));
    }

    [Fact]
    public void ProbeEncodesATenthOfASecondToNull()
    {
        Assert.Equal(
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=s=256x256:d=0.1", "-c:v", "h264_nvenc", "-f", "null", "-"],
            FfmpegArgs.Probe("h264_nvenc"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~FfmpegTests"`
Expected: a compile error, because `FfmpegResolver` is not defined.

- [ ] **Step 3: Implement**

`FfmpegResolver.cs`:

```csharp
using System.Runtime.InteropServices;

namespace MainframeEngine;

internal enum OSPlatformKind
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>Recording needs ffmpeg and none was found. The message names the platform's install command.</summary>
public sealed class FfmpegNotFoundException(string message) : Exception(message);

/// <summary>
/// Finds the user's ffmpeg: <c>--ffmpeg</c> → <c>MAINFRAME_FFMPEG</c> → <c>PATH</c> → common install folders.
/// The engine never ships or links ffmpeg (ADR 0190); recording runs it as a separate program.
/// </summary>
internal static class FfmpegResolver
{
    public const string EnvironmentVariable = "MAINFRAME_FFMPEG";

    public static string ResolveForThisMachine(string? explicitPath) =>
        Resolve(explicitPath, Environment.GetEnvironmentVariable(EnvironmentVariable), Environment.GetEnvironmentVariable("PATH"),
            CurrentOs, File.Exists);

    public static OSPlatformKind CurrentOs =>
        OperatingSystem.IsWindows() ? OSPlatformKind.Windows : OperatingSystem.IsMacOS() ? OSPlatformKind.MacOS : OSPlatformKind.Linux;

    public static string Resolve(string? explicitPath, string? envPath, string? pathVariable, OSPlatformKind os, Func<string, bool> fileExists)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return fileExists(explicitPath) ? explicitPath : throw new FfmpegNotFoundException($"ffmpeg was not found at {explicitPath}.");
        if (!string.IsNullOrWhiteSpace(envPath) && fileExists(envPath))
            return envPath;

        var exe = os == OSPlatformKind.Windows ? "ffmpeg.exe" : "ffmpeg";
        var separator = os == OSPlatformKind.Windows ? ';' : ':';
        var join = os == OSPlatformKind.Windows ? '\\' : '/';
        var folders = (pathVariable ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries).Concat(KnownFolders(os));
        foreach (var folder in folders)
        {
            var candidate = folder.TrimEnd('/', '\\') + join + exe;
            if (fileExists(candidate))
                return candidate;
        }

        throw new FfmpegNotFoundException("Recording needs ffmpeg, and it was not found. Install it with " + InstallHint(os) +
            ", or pass --ffmpeg <path> (or set " + EnvironmentVariable + ").");
    }

    private static IEnumerable<string> KnownFolders(OSPlatformKind os) => os switch
    {
        OSPlatformKind.MacOS => ["/opt/homebrew/bin", "/usr/local/bin"],
        OSPlatformKind.Windows =>
        [
            @"C:\ffmpeg\bin",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
        ],
        _ => ["/usr/bin", "/usr/local/bin", "/snap/bin"],
    };

    private static string InstallHint(OSPlatformKind os) => os switch
    {
        OSPlatformKind.MacOS => "`brew install ffmpeg`",
        OSPlatformKind.Windows => "`winget install Gyan.FFmpeg`",
        _ => "`sudo apt install ffmpeg` (or your distribution's package manager)",
    };
}
```

(`RuntimeInformation` is not needed; remove the `using` if the analyser flags it.)

`EncoderSelector.cs`:

```csharp
namespace MainframeEngine;

/// <summary>
/// Which H.264 encoder to record with: the platform's hardware encoder first (cheap enough for live
/// play), then software. Every candidate is probe-encoded before use (<see cref="FfmpegArgs.Probe"/>),
/// because ffmpeg lists hardware encoders even on machines without the hardware.
/// </summary>
internal static class EncoderSelector
{
    private static readonly string[] Software = ["libx264", "libopenh264"];

    public static IReadOnlyList<string> ParseEncoders(string output) =>
    [
        .. output.Split('\n')
            .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] is 'V' or 'A' or 'S' && parts[1] != "=")
            .Select(parts => parts[1]),
    ];

    public static IReadOnlyList<string> Candidates(OSPlatformKind os, IReadOnlyCollection<string> available)
    {
        string[] hardware = os switch
        {
            OSPlatformKind.MacOS => ["h264_videotoolbox"],
            OSPlatformKind.Windows => ["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf"],
            _ => ["h264_nvenc"],
        };
        return [.. hardware.Concat(Software).Where(available.Contains)];
    }
}
```

`FfmpegArgs.cs`:

```csharp
using System.Globalization;

namespace MainframeEngine;

internal enum PixelLayout
{
    Bgra,
    Rgba,
}

/// <summary>The recorder's ffmpeg command lines, as argument lists (never a shell string).</summary>
internal static class FfmpegArgs
{
    /// <summary>0.25 bits per pixel per frame: a high-quality intermediate, not a delivery file (≈31 Mbit/s at 1080p60).</summary>
    public static long Bitrate(int width, int height, int fps) => (long)width * height * fps / 4;

    public static IReadOnlyList<string> Encode(string encoder, int width, int height, int fps, PixelLayout layout, string outputMkv)
    {
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", layout == PixelLayout.Bgra ? "bgra" : "rgba",
            "-s", $"{width.ToString(inv)}x{height.ToString(inv)}", "-r", fps.ToString(inv), "-i", "-",
            "-an",
        };
        if (width % 2 != 0 || height % 2 != 0)
            args.AddRange(["-vf", $"crop={(width & ~1).ToString(inv)}:{(height & ~1).ToString(inv)}:0:0"]);
        args.AddRange(["-c:v", encoder]);
        if (encoder == "libx264")
            args.AddRange(["-preset", "ultrafast", "-crf", "14"]);
        else
            args.AddRange(["-b:v", Bitrate(width & ~1, height & ~1, fps).ToString(inv)]);
        args.AddRange(["-g", fps.ToString(inv), "-pix_fmt", "yuv420p", "-progress", "pipe:1", "-nostats", outputMkv]);
        return args;
    }

    public static IReadOnlyList<string> Mux(string videoMkv, string audioWav, string outputMp4) =>
    [
        "-hide_banner", "-loglevel", "error", "-y", "-i", videoMkv, "-i", audioWav,
        "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", outputMp4,
    ];

    public static IReadOnlyList<string> Probe(string encoder) =>
        ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=s=256x256:d=0.1", "-c:v", encoder, "-f", "null", "-"];

    public static IReadOnlyList<string> ListEncoders() => ["-hide_banner", "-encoders"];
}
```

Check `BitrateIsAQuarterBitPerPixelPerFrame`: 1920·1080·60/4 = 31 104 000 ✓. In `EncodeReadsRawFramesFromStdinWithOneSecondGop`, the dimensions are even, so there is no `-vf` ✓.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~FfmpegTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine/Src/Recording Tests/MainframeEngine.Tests/Recording/FfmpegTests.cs
git commit -m "Resolve ffmpeg, prefer hardware encoders and build recorder command lines"
```

---

### Task 6: Streaming WAV writer

**Files:**
- Create: `MainframeEngine/Src/Recording/StreamingWavWriter.cs`
- Test: `Tests/MainframeEngine.Tests/Recording/StreamingWavWriterTests.cs`

**Interfaces:**
- Produces `internal sealed class StreamingWavWriter(Stream stream, int channels, int sampleRate) : IDisposable`:
  - `void Write(ReadOnlySpan<float> interleaved)`
  - `void WriteSilence(long frames)`
  - `long Frames { get; }`
  - `void Finish(long targetFrames)`: pads with silence or truncates, then patches the header.
  - 32-bit IEEE float format.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Buffers.Binary;

namespace MainframeEngine.Tests.Recording;

/// <summary>An IEEE-float WAV written in pieces whose RIFF sizes are patched at the end.</summary>
public sealed class StreamingWavWriterTests
{
    private static (int Channels, int Rate, int DataBytes, float[] Samples) Read(byte[] wav)
    {
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal(3, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20))); // WAVE_FORMAT_IEEE_FLOAT
        var channels = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22));
        var rate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24));
        Assert.Equal("data"u8.ToArray(), wav[36..40]);
        var data = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40));
        var samples = new float[data / 4];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(wav.AsSpan(44 + i * 4));
        return (channels, rate, data, samples);
    }

    [Fact]
    public void PiecesAreConcatenatedAndTheHeaderIsPatched()
    {
        var ms = new MemoryStream();
        using (var w = new StreamingWavWriter(ms, 2, 48_000))
        {
            w.Write([0.1f, 0.2f]);
            w.Write([0.3f, 0.4f, 0.5f, 0.6f]);
            w.Finish(targetFrames: 3);
        }

        var (channels, rate, data, samples) = Read(ms.ToArray());
        Assert.Equal(2, channels);
        Assert.Equal(48_000, rate);
        Assert.Equal(24, data);
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f], samples);
    }

    [Fact]
    public void FinishPadsShortAudioWithSilence()
    {
        var ms = new MemoryStream();
        using (var w = new StreamingWavWriter(ms, 2, 48_000))
        {
            w.Write([1f, 1f]);
            w.Finish(targetFrames: 3);
        }

        Assert.Equal([1f, 1f, 0f, 0f, 0f, 0f], Read(ms.ToArray()).Samples);
    }

    [Fact]
    public void FinishTruncatesLongAudio()
    {
        var ms = new MemoryStream();
        using (var w = new StreamingWavWriter(ms, 2, 48_000))
        {
            w.Write([1f, 1f, 2f, 2f, 3f, 3f]);
            w.Finish(targetFrames: 2);
        }

        Assert.Equal([1f, 1f, 2f, 2f], Read(ms.ToArray()).Samples);
    }

    [Fact]
    public void WriteSilenceCountsFrames()
    {
        using var w = new StreamingWavWriter(new MemoryStream(), 2, 48_000);
        w.WriteSilence(100);
        Assert.Equal(100, w.Frames);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~StreamingWavWriterTests"`
Expected: a compile error, because `StreamingWavWriter` is not defined.

- [ ] **Step 3: Implement**

```csharp
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>
/// A 32-bit float WAV written as the recording runs (<see cref="MainframeEngine.Audio.WavWriter"/> needs the
/// whole buffer up front). The header is written with zero sizes and patched by <see cref="Finish"/>,
/// which also pads or truncates to the exact length the video needs, so A/V lengths always match.
/// </summary>
internal sealed class StreamingWavWriter : IDisposable
{
    private const int HeaderBytes = 44;
    private readonly Stream _stream;
    private readonly int _channels;
    private readonly int _sampleRate;

    public StreamingWavWriter(Stream stream, int channels, int sampleRate)
    {
        _stream = stream;
        _channels = channels;
        _sampleRate = sampleRate;
        WriteHeader(0);
    }

    /// <summary>Audio frames (one sample per channel) written so far.</summary>
    public long Frames { get; private set; }

    public void Write(ReadOnlySpan<float> interleaved)
    {
        if (BitConverter.IsLittleEndian)
        {
            _stream.Write(MemoryMarshal.AsBytes(interleaved));
        }
        else
        {
            Span<byte> one = stackalloc byte[4];
            foreach (var s in interleaved)
            {
                BinaryPrimitives.WriteSingleLittleEndian(one, s);
                _stream.Write(one);
            }
        }

        Frames += interleaved.Length / _channels;
    }

    public void WriteSilence(long frames)
    {
        Span<float> zeros = stackalloc float[256 * 2];
        zeros.Clear();
        var perChunk = zeros.Length / _channels;
        while (frames > 0)
        {
            var n = (int)Math.Min(frames, perChunk);
            Write(zeros[..(n * _channels)]);
            frames -= n;
        }
    }

    /// <summary>Makes the data exactly <paramref name="targetFrames"/> long and writes the real sizes.</summary>
    public void Finish(long targetFrames)
    {
        if (Frames < targetFrames)
            WriteSilence(targetFrames - Frames);
        else if (Frames > targetFrames)
        {
            _stream.SetLength(HeaderBytes + targetFrames * _channels * 4);
            Frames = targetFrames;
        }

        _stream.Flush();
        _stream.Position = 0;
        WriteHeader(Frames * _channels * 4);
        _stream.Seek(0, SeekOrigin.End);
        _stream.Flush();
    }

    public void Dispose() => _stream.Dispose();

    private void WriteHeader(long dataBytes)
    {
        Span<byte> h = stackalloc byte[HeaderBytes];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], (int)Math.Min(int.MaxValue, 36 + dataBytes));
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(h[20..], 3); // IEEE float
        BinaryPrimitives.WriteInt16LittleEndian(h[22..], (short)_channels);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], _sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], _sampleRate * _channels * 4);
        BinaryPrimitives.WriteInt16LittleEndian(h[32..], (short)(_channels * 4));
        BinaryPrimitives.WriteInt16LittleEndian(h[34..], 32);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], (int)Math.Min(int.MaxValue, dataBytes));
        _stream.Write(h);
    }
}
```

At 48 kHz stereo float, a WAV reaches 2 GB after about 3.1 hours. The sizes clamp at `int.MaxValue` and ffmpeg still reads the data to the end of the file. Task 12 records this as a known limit.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~StreamingWavWriterTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine/Src/Recording/StreamingWavWriter.cs Tests/MainframeEngine.Tests/Recording/StreamingWavWriterTests.cs
git commit -m "Add a streaming float WAV writer for recordings"
```

---

### Task 7: Live mix tap on the audio server

**Files:**
- Create: `MainframeEngine/Src/Audio/Graph/AudioCaptureTap.cs`
- Modify: `MainframeEngine/Src/Audio/Graph/AudioMixRoot.cs` (`GenerateAudio`, lines ~49–67)
- Modify: `MainframeEngine/Src/Audio/AudioServer.cs` (add `BeginCapture`/`EndCapture`)
- Test: `Tests/MainframeEngine.Tests/Recording/AudioCaptureTapTests.cs`

**Interfaces:**
- Consumes: `SpscRing<float>` (`EnqueueBatch`, `TryDequeue`).
- Produces:
  - `internal sealed class AudioCaptureTap(int capacitySamples)` (namespace `MainframeEngine.Audio`), whose `Write` runs on the audio thread:
    - `long FirstBlockTimestamp { get; }` (Stopwatch ticks, 0 until the first block);
    - `int Channels { get; }`;
    - `long OverflowSamples { get; }`;
    - `int Drain(Span<float> into)`, called on the writer thread.
  - `AudioServer`: `internal void BeginCapture(AudioCaptureTap tap)` and `internal void EndCapture()`.

- [ ] **Step 1: Write the failing tests**

```csharp
using MainframeEngine.Audio;

namespace MainframeEngine.Tests.Recording;

/// <summary>The recorder's copy of the master mix: exact sample counts, overflow accounting, detach.</summary>
public sealed class AudioCaptureTapTests
{
    [Fact]
    public void EveryRenderedSampleReachesTheTap()
    {
        using var server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        var tap = new AudioCaptureTap(1 << 16);
        server.BeginCapture(tap);

        server.RenderNullDevice(800);
        server.RenderNullDevice(800);

        var buffer = new float[1 << 16];
        Assert.Equal(1600 * 2, tap.Drain(buffer));
        Assert.Equal(2, tap.Channels);
        Assert.NotEqual(0, tap.FirstBlockTimestamp);
    }

    [Fact]
    public void AFullRingCountsOverflowInsteadOfBlocking()
    {
        using var server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        var tap = new AudioCaptureTap(1024);
        server.BeginCapture(tap);

        server.RenderNullDevice(2048);

        Assert.True(tap.OverflowSamples > 0);
    }

    [Fact]
    public void EndCaptureDetaches()
    {
        using var server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        var tap = new AudioCaptureTap(1 << 16);
        server.BeginCapture(tap);
        server.EndCapture();

        server.RenderNullDevice(800);

        Assert.Equal(0, tap.Drain(new float[4096]));
    }
}
```

If `AudioServer` is not `IDisposable`, follow the disposal pattern of the existing NullManual audio tests in `Tests/MainframeEngine.Tests/Audio/` instead of `using`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~AudioCaptureTapTests"`
Expected: a compile error, because `AudioCaptureTap` is not defined.

- [ ] **Step 3: Implement**

`AudioCaptureTap.cs`:

```csharp
using System.Diagnostics;

namespace MainframeEngine.Audio;

/// <summary>
/// Receives a copy of every master-mix block for the gameplay recorder (live mode). <see cref="Write"/>
/// runs on the audio thread: one <see cref="SpscRing{T}.EnqueueBatch"/>, no locks, no allocation; what
/// does not fit is counted, and the recorder writes silence for it so the length still matches.
/// </summary>
internal sealed class AudioCaptureTap(int capacitySamples)
{
    private readonly SpscRing<float> _ring = new(capacitySamples);
    private long _firstBlock;
    private long _overflow;
    private int _channels;

    /// <summary><see cref="Stopwatch"/> ticks when the first block arrived; 0 before that.</summary>
    public long FirstBlockTimestamp => Volatile.Read(ref _firstBlock);

    public int Channels => Volatile.Read(ref _channels);

    public long OverflowSamples => Interlocked.Read(ref _overflow);

    /// <summary>Audio thread.</summary>
    public void Write(ReadOnlySpan<float> buffer, int channels)
    {
        if (Volatile.Read(ref _firstBlock) == 0)
        {
            Volatile.Write(ref _channels, channels);
            Volatile.Write(ref _firstBlock, Stopwatch.GetTimestamp());
        }

        var written = _ring.EnqueueBatch(buffer);
        if (written < buffer.Length)
            Interlocked.Add(ref _overflow, buffer.Length - written);
    }

    /// <summary>Writer thread: moves up to <paramref name="into"/>.Length samples out of the ring.</summary>
    public int Drain(Span<float> into)
    {
        var n = 0;
        while (n < into.Length && _ring.TryDequeue(out var sample))
            into[n++] = sample;
        return n;
    }
}
```

`AudioMixRoot.cs`:
- Add a field: `private AudioCaptureTap? _captureTap;`
- Add a method: `internal void SetCaptureTap(AudioCaptureTap? tap) => Volatile.Write(ref _captureTap, tap);`
- In `GenerateAudio`, directly after `AfterMix(graph);` inside the `if`:

```csharp
                Volatile.Read(ref _captureTap)?.Write(buffer, channels); // gameplay recorder (live); unity gain after this point
```

`AudioServer.cs`: find the field that holds the `AudioMixRoot` (search `AudioMixRoot` in the file) and add:

```csharp
    /// <summary>Starts copying the master mix into <paramref name="tap"/> (gameplay recording, live mode).</summary>
    internal void BeginCapture(AudioCaptureTap tap) => _root.SetCaptureTap(tap);

    /// <summary>Stops the copy; blocks already in the tap stay there to be drained.</summary>
    internal void EndCapture() => _root.SetCaptureTap(null);
```

(Rename `_root` to the actual field name.)

- [ ] **Step 4: Run the audio tests**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~Audio"`
Expected: all PASS, both the new tap tests and the existing audio suite.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine/Src/Audio Tests/MainframeEngine.Tests/Recording/AudioCaptureTapTests.cs
git commit -m "Tap the master mix for gameplay recording without touching the audio thread's guarantees"
```

---

### Task 8: Renderer frame stream

**Files:**
- Create: `MainframeEngine/Src/Rendering/IFrameStream.cs`
- Modify: `MainframeEngine/Src/Rendering/Vulkan/VulkanRenderer.cs` (`BeginFrame` after the fence wait ~line 243; `EndFrame` ~lines 285–357; `RecordCapture` 392–453; `Dispose`)

**Interfaces:**
- Produces (namespace `MainframeEngine`, internal):

```csharp
internal interface IFrameStreamSink
{
    /// <summary>Main thread, in EndFrame before the copy is recorded. False skips copying this frame.</summary>
    bool ShouldCapture(out double stamp);

    /// <summary>Main thread, once the GPU has finished the frame. <paramref name="pixels"/> is valid only during the call.</summary>
    void OnFrame(ReadOnlySpan<byte> pixels, int width, int height, double stamp);

    /// <summary>The swapchain changed size; the stream has ended. Pending frames were delivered first.</summary>
    void OnStreamEnded(string reason);
}

internal interface IFrameStreamSource
{
    bool CanStream { get; }
    PixelLayout StreamLayout { get; }
    (int Width, int Height) StreamSize { get; }
    void BeginStream(IFrameStreamSink sink);
    /// <summary>Waits for and delivers any pending frames, then stops.</summary>
    void EndStream();
}
```

Vulkan has no unit-test harness in `MainframeEngine.Tests`, so this task is verified by the offline smoke run in Task 11, plus a temporary manual check described below.

- [ ] **Step 1: Interfaces.** Create `IFrameStream.cs` with the two interfaces above, each with a `<summary>`: "Streams the presented swapchain image back to the CPU every frame for the gameplay recorder: one host-visible buffer per frame in flight, read when BeginFrame's fence wait has already proven the copy finished, so recording adds a copy but never a stall."

- [ ] **Step 2: Factor the copy out of `RecordCapture`.**
  - Rename the body of `RecordCapture(CommandBuffer cb, uint imageIndex)` into `private void RecordSwapchainCopy(CommandBuffer cb, uint imageIndex, GpuBuffer target)`, using `target.Handle` where it used `_captureBuffer!.Handle`.
  - `RecordCapture` becomes:

```csharp
    private void RecordCapture(CommandBuffer cb, uint imageIndex)
    {
        _captureRequested = false;
        EnsureCaptureBuffer();
        RecordSwapchainCopy(cb, imageIndex, _captureBuffer!);
        _captureRecorded = true;
    }
```

  Then build and run the existing render tests (`dotnet test Tests/MainframeEngine.RenderTests`, which needs a display) to prove screenshots still work.

- [ ] **Step 3: Stream state and API.** Add to `VulkanRenderer`, beside the capture fields:

```csharp
    // gameplay recorder frame stream (IFrameStreamSource): one readback buffer per frame slot
    private IFrameStreamSink? _streamSink;
    private readonly GpuBuffer?[] _streamBuffers = new GpuBuffer?[MaxFramesInFlight];
    private readonly double?[] _streamPending = new double?[MaxFramesInFlight];
    private Extent2D _streamExtent;
```

  Make the class implement `IFrameStreamSource`:

```csharp
    public bool CanStream => _captureSupported;

    public PixelLayout StreamLayout =>
        _swapChainImageFormat is Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb ? PixelLayout.Bgra : PixelLayout.Rgba;

    public (int Width, int Height) StreamSize => ((int)_swapChainExtent.Width, (int)_swapChainExtent.Height);

    public void BeginStream(IFrameStreamSink sink)
    {
        if (!_captureSupported)
            throw new InvalidOperationException("Frame streaming needs EngineOptions.EnableFrameCapture and a capturable swapchain.");
        _streamSink = sink;
        _streamExtent = _swapChainExtent;
        var size = (ulong)_streamExtent.Width * _streamExtent.Height * 4;
        for (var i = 0; i < _streamBuffers.Length; i++)
        {
            if (_streamBuffers[i]?.Size != size)
            {
                _streamBuffers[i]?.Dispose();
                _streamBuffers[i] = GpuBuffer.Create(this, size, BufferUsageFlags.TransferDstBit, GpuMemoryUsage.Readback);
            }
        }
    }

    public void EndStream()
    {
        FlushStream();
        _streamSink = null;
    }

    // Waits for every frame that still holds a copy and hands it over, oldest first.
    private void FlushStream()
    {
        for (var k = 1; k <= MaxFramesInFlight; k++)
        {
            var slot = (_currentFrame + k) % MaxFramesInFlight;
            if (_streamPending[slot] is null)
                continue;
            _vk!.WaitForFences(_device, 1, in _inFlightFences[slot], true, ulong.MaxValue).Check("vkWaitForFences (frame stream flush)");
            DeliverStreamSlot(slot);
        }
    }

    private void DeliverStreamSlot(int slot)
    {
        if (_streamPending[slot] is not { } stamp || _streamSink is not { } sink)
            return;
        _streamPending[slot] = null;
        var buffer = _streamBuffers[slot]!;
        var pixels = new ReadOnlySpan<byte>((void*)buffer.MappedPointer, (int)buffer.Size);
        sink.OnFrame(pixels, (int)_streamExtent.Width, (int)_streamExtent.Height, stamp);
    }
```

- [ ] **Step 4: Hook BeginFrame and EndFrame.**
  - In `BeginFrame`, directly after `_uploads!.Release(completed);` (the slot's fence has just been waited on):

```csharp
        DeliverStreamSlot(_currentFrame);
```

  - In `EndFrame`, beside `if (_captureRequested) RecordCapture(cb, imageIndex);`:

```csharp
        if (_streamSink is { } streamSink && _swapChainExtent.Width == _streamExtent.Width && _swapChainExtent.Height == _streamExtent.Height
            && streamSink.ShouldCapture(out var stamp))
        {
            RecordSwapchainCopy(cb, imageIndex, _streamBuffers[_currentFrame]!);
            _streamPending[_currentFrame] = stamp;
        }
```

  - In `EndFrame`, immediately before the `RecreateSwapchain();` call in the present-result branch (~line 343), end the stream so the extent never changes under it:

```csharp
            if (_streamSink is { } ending)
            {
                FlushStream();
                _streamSink = null;
                ending.OnStreamEnded("window size changed");
            }
```

  - In `Dispose`, dispose each `_streamBuffers[i]`.

- [ ] **Step 5: Build**

Run: `just build`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 6: Temporary manual check (delete afterwards).**
  - In a scratch branch, add to `GameHost.OnLoad` (or the first `OnUpdate`) a sink that counts frames and logs the first pixel. Run Forest with `--frame-capture --max-frames 120`.
  - Confirm the log shows ~118 frames with a plausible non-black first pixel, and that resizing the window logs "window size changed".
  - Then discard the scratch change: `git checkout -- MainframeEngine/Src/Project/GameHost.cs`.

- [ ] **Step 7: Commit**

```bash
git add MainframeEngine/Src/Rendering
git commit -m "Stream presented frames to the CPU without a fence stall for gameplay recording"
```

---

### Task 9: ffmpeg recording backend

**Files:**
- Create: `MainframeEngine/Src/Recording/IRecordingBackend.cs`
- Create: `MainframeEngine/Src/Recording/FfmpegRecordingBackend.cs`
- Create: `MainframeEngine/Src/Recording/FfmpegProcess.cs`

**Interfaces:**
- Consumes: `FfmpegArgs`, `EncoderSelector`, `FfmpegResolver` (Task 5); `StreamingWavWriter` (Task 6); `CaptureSidecarWriter`, `CaptureInfo`, `CaptureSpans` (Task 4); `PixelLayout`.
- Produces:

```csharp
internal sealed record RecordingFormat(string OutputPath, int Width, int Height, int Fps, PixelLayout Layout, int SampleRate, int Channels, bool Offline);

internal interface IRecordingBackend : IDisposable
{
    void Begin(RecordingFormat format);
    /// <summary>Queues <paramref name="copies"/> copies of a frame. False when the encoder is behind (live: drop it).</summary>
    bool TryWriteVideo(ReadOnlySpan<byte> pixels, int copies, bool wait);
    void WriteAudio(ReadOnlySpan<float> interleaved);
    void WriteSilence(long frames);
    /// <summary>Encodes the rest, pads audio to <paramref name="videoFrames"/>, muxes, writes the sidecar, removes .partial.</summary>
    Task<RecordingResult> FinishAsync(long videoFrames, CaptureInfo info, CaptureSpans spans, CancellationToken ct);
}

public sealed record RecordingResult(string Path, double DurationSeconds, long DroppedFrames, string? Error);
```

Running ffmpeg is integration territory. This task's correctness rests on the pure builders already tested (Task 5) and on Task 11's smoke runs. Keep the class thin.

- [ ] **Step 1: `FfmpegProcess.cs`**, a small wrapper: start with an argument list, optional stdin, stdout lines to a callback, and a 4 KB stderr tail.

```csharp
using System.Diagnostics;
using System.Text;

namespace MainframeEngine;

/// <summary>One ffmpeg run: argument list (no shell), optional stdin, stdout lines to a callback, last 4 KB of stderr kept for errors.</summary>
internal sealed class FfmpegProcess : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();

    public FfmpegProcess(string ffmpeg, IEnumerable<string> args, bool stdin, Action<string>? onStdout = null)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = stdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            info.ArgumentList.Add(a);
        _process = new Process { StartInfo = info, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is { } line) onStdout?.Invoke(line); };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (_stderr)
            {
                _stderr.AppendLine(e.Data);
                if (_stderr.Length > 4096)
                    _stderr.Remove(0, _stderr.Length - 4096);
            }
        };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public Stream Input => _process.StandardInput.BaseStream;

    public bool HasExited => _process.HasExited;

    public string ErrorTail { get { lock (_stderr) return _stderr.ToString().Trim(); } }

    public async Task<int> WaitAsync(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct).ConfigureAwait(false);
        return _process.ExitCode;
    }

    /// <summary>Runs to completion and throws with ffmpeg's own error text on failure.</summary>
    public static async Task RunAsync(string ffmpeg, IEnumerable<string> args, CancellationToken ct)
    {
        using var p = new FfmpegProcess(ffmpeg, args, stdin: false);
        if (await p.WaitAsync(ct).ConfigureAwait(false) != 0)
            throw new IOException($"ffmpeg failed: {p.ErrorTail}");
    }

    public void Dispose()
    {
        if (!_process.HasExited)
            _process.Kill(entireProcessTree: true);
        _process.Dispose();
    }
}
```

- [ ] **Step 2: `FfmpegRecordingBackend.cs`**

```csharp
using System.Buffers;
using System.Collections.Concurrent;

namespace MainframeEngine;

/// <summary>
/// Records through the user's ffmpeg: raw frames into an encoder's stdin (writer thread, bounded queue of
/// 4), audio into a float WAV, both under <c>&lt;out&gt;.partial/</c> (MKV survives a crash), then a mux to MP4
/// and the sidecar. Encoder choice is probed once per process and cached.
/// </summary>
internal sealed class FfmpegRecordingBackend(string ffmpeg) : IRecordingBackend
{
    private static string? s_encoder;

    private readonly BlockingCollection<(byte[] Buffer, int Length, int Copies)> _frames = new(boundedCapacity: 4);
    private RecordingFormat? _format;
    private string _partial = "";
    private FfmpegProcess? _encoder;
    private Thread? _writer;
    private StreamingWavWriter? _wav;
    private volatile string? _writerError;

    public void Begin(RecordingFormat format)
    {
        _format = format;
        _partial = format.OutputPath + ".partial";
        Directory.CreateDirectory(_partial);
        var encoder = s_encoder ??= ChooseEncoder();
        _encoder = new FfmpegProcess(ffmpeg,
            FfmpegArgs.Encode(encoder, format.Width, format.Height, format.Fps, format.Layout, Path.Combine(_partial, "video.mkv")),
            stdin: true);
        _wav = new StreamingWavWriter(File.Create(Path.Combine(_partial, "audio.wav")), format.Channels, format.SampleRate);
        _writer = new Thread(WriteFrames) { Name = "Mainframe recorder video writer", IsBackground = true };
        _writer.Start();
        Log.Info($"[Recorder] Recording {format.Width}x{format.Height}@{format.Fps} with {encoder} to {format.OutputPath}");
    }

    public bool TryWriteVideo(ReadOnlySpan<byte> pixels, int copies, bool wait)
    {
        if (_writerError is { } error)
            throw new IOException(error);
        var buffer = ArrayPool<byte>.Shared.Rent(pixels.Length);
        pixels.CopyTo(buffer);
        var item = (buffer, pixels.Length, copies);
        if (wait ? _frames.TryAdd(item, Timeout.Infinite) : _frames.TryAdd(item))
            return true;
        ArrayPool<byte>.Shared.Return(buffer);
        return false;
    }

    public void WriteAudio(ReadOnlySpan<float> interleaved) => _wav!.Write(interleaved);

    public void WriteSilence(long frames) => _wav!.WriteSilence(frames);

    public async Task<RecordingResult> FinishAsync(long videoFrames, CaptureInfo info, CaptureSpans spans, CancellationToken ct)
    {
        var format = _format!;
        try
        {
            _frames.CompleteAdding();
            _writer!.Join();
            _encoder!.Input.Close();
            if (await _encoder.WaitAsync(ct).ConfigureAwait(false) != 0 || _writerError is not null)
                throw new IOException($"The encoder failed: {_writerError ?? _encoder.ErrorTail}");

            _wav!.Finish(SampleScheduler.SamplesThrough(videoFrames, format.SampleRate, format.Fps));
            _wav.Dispose();
            await FfmpegProcess.RunAsync(ffmpeg,
                FfmpegArgs.Mux(Path.Combine(_partial, "video.mkv"), Path.Combine(_partial, "audio.wav"), format.OutputPath), ct).ConfigureAwait(false);
            CaptureSidecarWriter.Write(format.OutputPath, info, spans);
            Directory.Delete(_partial, recursive: true);
            return new RecordingResult(format.OutputPath, info.DurationSeconds, info.DroppedFrames, null);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Error($"[Recorder] {e.Message} Partial files kept in {_partial}");
            return new RecordingResult(format.OutputPath, info.DurationSeconds, info.DroppedFrames,
                $"{e.Message} The partial recording is in {_partial}.");
        }
    }

    public void Dispose()
    {
        _frames.CompleteAdding();
        _encoder?.Dispose();
        _wav?.Dispose();
        _frames.Dispose();
    }

    private void WriteFrames()
    {
        try
        {
            foreach (var (buffer, length, copies) in _frames.GetConsumingEnumerable())
            {
                for (var i = 0; i < copies; i++)
                    _encoder!.Input.Write(buffer, 0, length);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (IOException e)
        {
            _writerError = $"{e.Message} {_encoder?.ErrorTail}";
        }
    }

    private string ChooseEncoder()
    {
        using var list = new FfmpegProcess(ffmpeg, FfmpegArgs.ListEncoders(), stdin: false, onStdout: Collect);
        list.WaitAsync(CancellationToken.None).GetAwaiter().GetResult();
        var os = FfmpegResolver.CurrentOs;
        foreach (var candidate in EncoderSelector.Candidates(os, EncoderSelector.ParseEncoders(_listing.ToString())))
        {
            try
            {
                FfmpegProcess.RunAsync(ffmpeg, FfmpegArgs.Probe(candidate), CancellationToken.None).GetAwaiter().GetResult();
                return candidate;
            }
            catch (IOException)
            {
                Log.Info($"[Recorder] {candidate} is listed but does not work here; trying the next encoder.");
            }
        }

        throw new IOException("This ffmpeg has no usable H.264 encoder (tried hardware encoders, libx264 and libopenh264).");
    }

    private readonly System.Text.StringBuilder _listing = new();

    private void Collect(string line)
    {
        lock (_listing)
            _listing.AppendLine(line);
    }
}
```

The `ChooseEncoder` probe blocks for up to about 1 s on the first recording of a process. That is acceptable at Start; Task 12 notes it.

- [ ] **Step 3: Build**

Run: `just build`
Expected: Build succeeded, 0 warnings. Fix any analyser findings in place, for example by moving `_listing` above its first use if the member-ordering rule fires.

- [ ] **Step 4: Commit**

```bash
git add MainframeEngine/Src/Recording
git commit -m "Record through an ffmpeg child process: encoder pipe, float WAV, mux and sidecar"
```

---

### Task 10: `GameRecorder` and wiring into the engine

**Files:**
- Create: `MainframeEngine/Src/Recording/GameRecorder.cs`
- Create: `MainframeEngine/Src/Recording/RecordingOptions.cs`
- Create: `MainframeEngine/Src/Scene/SceneTree.Recording.cs`
- Modify: `MainframeEngine/Src/Scene/SceneTree.cs` (`DispatchInput`, first line, ~460)
- Modify: `MainframeEngine/Src/Project/GameHost.cs` (`OnUpdate` ~370 before the `IsLoading` early return; `OnClose` ~459; constructor for creation)
- Test: `Tests/MainframeEngine.Tests/Recording/GameRecorderTests.cs`

**Interfaces:**
- Consumes everything above.
- Produces:
  - `public sealed record RecordingOptions(string Path, int Fps = 60)`
  - `public sealed class GameRecorder`, public API as in the spec §1.1:
    - `bool IsRecording`, `bool IsFinalising`, `TimeSpan Elapsed`;
    - `event Action? Started`, `event Action<RecordingResult>? Stopped`;
    - `void Start(RecordingOptions)`, `void Stop()`;
    - `void Mark(string)`, `IDisposable Keep(string)`, `IDisposable Skip(string)`.
  - Internal hooks:
    - `void ObserveInput(InputEvent e)`
    - `void OnUpdate(bool loading, bool paused)`
    - `void PollCompletion()`
    - `RecordingResult? StopAndWait(TimeSpan timeout)`
  - `SceneTree.Recorder : GameRecorder?` (public get, internal set).

`GameRecorder` takes its collaborators through an internal constructor, so tests can pass fakes:

```csharp
internal GameRecorder(
    Func<RecordingFormat, IRecordingBackend> backendFactory,
    IFrameStreamSource? frames,
    AudioServer? audio,
    bool offline,
    CaptureInfoSource info,          // game name, scene getter, engine version
    Func<double>? liveClock = null)  // seconds since Start; default Stopwatch
```

with `internal sealed record CaptureInfoSource(string Game, Func<string> Scene, string EngineVersion);`.

- [ ] **Step 1: Write the failing tests** (fake backend and fake frame source, no Vulkan, no ffmpeg):

```csharp
namespace MainframeEngine.Tests.Recording;

/// <summary>Start/Stop lifecycle, sidecar contents and failure handling, with a fake backend and frame source.</summary>
public sealed class GameRecorderTests
{
    private sealed class FakeBackend : IRecordingBackend
    {
        public RecordingFormat? Format;
        public int VideoFrames;
        public long AudioFrames;
        public CaptureSpans? Spans;
        public CaptureInfo? Info;
        public bool Full;
        public string? FailWith;

        public void Begin(RecordingFormat format) => Format = format;
        public bool TryWriteVideo(ReadOnlySpan<byte> pixels, int copies, bool wait) { if (Full && !wait) return false; VideoFrames += copies; return true; }
        public void WriteAudio(ReadOnlySpan<float> interleaved) => AudioFrames += interleaved.Length / 2;
        public void WriteSilence(long frames) => AudioFrames += frames;
        public Task<RecordingResult> FinishAsync(long videoFrames, CaptureInfo info, CaptureSpans spans, CancellationToken ct)
        {
            Info = info;
            Spans = spans;
            return Task.FromResult(new RecordingResult(Format!.OutputPath, info.DurationSeconds, info.DroppedFrames,
                FailWith is null ? null : FailWith + " The partial recording is in " + Format.OutputPath + ".partial."));
        }
        public void Dispose() { }
    }

    private sealed class FakeFrames : IFrameStreamSource
    {
        public IFrameStreamSink? Sink;
        public bool CanStream => true;
        public PixelLayout StreamLayout => PixelLayout.Bgra;
        public (int Width, int Height) StreamSize => (4, 2);
        public void BeginStream(IFrameStreamSink sink) => Sink = sink;
        public void EndStream() => Sink = null;

        /// <summary>One rendered frame: ask, then deliver immediately (the fake GPU is instant).</summary>
        public void Render()
        {
            if (Sink is { } s && s.ShouldCapture(out var stamp))
                s.OnFrame(new byte[4 * 2 * 4], 4, 2, stamp);
        }
    }

    private static (GameRecorder Recorder, FakeBackend Backend, FakeFrames Frames, Func<double, double> SetClock) Create(bool offline = false)
    {
        var backend = new FakeBackend();
        var frames = new FakeFrames();
        var now = 0.0;
        var recorder = new GameRecorder(_ => backend, frames, audio: null, offline,
            new CaptureInfoSource("Forest", () => "res://Scenes/Forest.scn", "0.42.0"), () => now);
        return (recorder, backend, frames, t => now = t);
    }

    [Fact]
    public void OfflineRecordingWritesOneFramePerUpdateAndExactAudio()
    {
        var (r, backend, frames, _) = Create(offline: true);
        r.Start(new RecordingOptions("/r/out.mp4"));
        for (var i = 0; i < 60; i++)
        {
            r.OnUpdate(loading: false, paused: false);
            frames.Render();
        }

        var result = r.StopAndWait(TimeSpan.FromSeconds(5))!;

        Assert.Null(result.Error);
        Assert.Equal(60, backend.VideoFrames);
        Assert.Equal(48_000, backend.AudioFrames);      // silence when there is no audio server, still exact
        Assert.Equal(1.0, backend.Info!.DurationSeconds);
        Assert.True(backend.Info.Offline);
    }

    [Fact]
    public void LiveFramesAreScheduledByTheClock()
    {
        var (r, backend, frames, setClock) = Create();
        r.Start(new RecordingOptions("/r/out.mp4"));
        foreach (var t in new[] { 0.0, 1 / 30.0, 2 / 30.0 })
        {
            setClock(t);
            r.OnUpdate(loading: false, paused: false);
            frames.Render();
        }

        r.StopAndWait(TimeSpan.FromSeconds(5));
        Assert.Equal(5, backend.VideoFrames); // 1 + 2 + 2
    }

    [Fact]
    public void MarksKeepsSkipsInputLoadingAndPauseReachTheSidecar()
    {
        var (r, backend, frames, setClock) = Create();
        r.Start(new RecordingOptions("/r/out.mp4"));
        setClock(0.0); r.OnUpdate(loading: true, paused: false); frames.Render();
        setClock(1.0); r.OnUpdate(loading: false, paused: false); frames.Render();
        r.ObserveInput(new InputEventKey { Key = Silk.NET.Input.Key.Space, Pressed = true });
        r.ObserveInput(new InputEventKey { Key = Silk.NET.Input.Key.Space, Pressed = false });
        r.Mark("boss intro");
        using (r.Keep("cutscene"))
        {
            setClock(2.0); r.OnUpdate(loading: false, paused: false); frames.Render();
        }

        setClock(3.0); r.OnUpdate(loading: false, paused: true); frames.Render();
        r.StopAndWait(TimeSpan.FromSeconds(5));

        var s = backend.Spans!;
        Assert.Equal([new CaptureSpan(0.0, 1.0)], s.Loading);
        Assert.Equal([new CaptureSpan(1.0, 1.25)], s.Input);
        Assert.Equal([new CaptureMarker(1.0, "boss intro")], s.Markers);
        Assert.Equal("cutscene", Assert.Single(s.Keep).Label);
        Assert.Single(s.Paused);
    }

    [Fact]
    public void StartWhileFinalisingThrows()
    {
        var (r, _, _, _) = Create();
        r.Start(new RecordingOptions("/r/a.mp4"));
        r.Stop();

        // Until PollCompletion (or StopAndWait) has raised Stopped, the previous recording still owns its files.
        Assert.Throws<InvalidOperationException>(() => r.Start(new RecordingOptions("/r/b.mp4")));
        r.StopAndWait(TimeSpan.FromSeconds(5));
        r.Start(new RecordingOptions("/r/b.mp4")); // and afterwards a new one may start
    }

    [Fact]
    public void StartWhileRecordingThrows()
    {
        var (r, _, _, _) = Create();
        r.Start(new RecordingOptions("/r/a.mp4"));
        Assert.Throws<InvalidOperationException>(() => r.Start(new RecordingOptions("/r/b.mp4")));
    }

    [Fact]
    public void StreamBreakStopsWithReason()
    {
        var (r, _, frames, _) = Create();
        RecordingResult? stopped = null;
        r.Stopped += result => stopped = result;
        r.Start(new RecordingOptions("/r/a.mp4"));
        frames.Render();

        frames.Sink!.OnStreamEnded("window size changed");
        r.StopAndWait(TimeSpan.FromSeconds(5));
        r.PollCompletion();

        Assert.False(r.IsRecording);
        Assert.Contains("window size changed", stopped!.Error);
    }

    [Fact]
    public void BackendFailureKeepsPartialPathInError()
    {
        var (r, backend, frames, _) = Create();
        backend.FailWith = "The encoder failed.";
        r.Start(new RecordingOptions("/r/a.mp4"));
        frames.Render();

        var result = r.StopAndWait(TimeSpan.FromSeconds(5))!;

        Assert.Contains(".partial", result.Error);
    }

    [Fact]
    public void StopDuringCloseFinalisesSynchronously()
    {
        var (r, backend, frames, _) = Create();
        r.Start(new RecordingOptions("/r/a.mp4"));
        frames.Render();

        var result = r.StopAndWait(TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.NotNull(backend.Spans);
        Assert.False(r.IsRecording);
        Assert.False(r.IsFinalising);
    }

    [Fact]
    public void MarkKeepAndSkipAreHarmlessWhenNotRecording()
    {
        var (r, _, _, _) = Create();
        r.Mark("x");
        r.Keep("y").Dispose();
        r.Skip("z").Dispose();
        Assert.False(r.IsRecording);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~GameRecorderTests"`
Expected: a compile error, because `GameRecorder` is not defined.

- [ ] **Step 3: Implement `RecordingOptions.cs` and `GameRecorder.cs`**

```csharp
namespace MainframeEngine;

/// <summary>Where to record and at what output frame rate (offline mode uses the host's <c>--fixed-fps</c>).</summary>
public sealed record RecordingOptions(string Path, int Fps = 60);
```

```csharp
using System.Diagnostics;
using MainframeEngine.Audio;

namespace MainframeEngine;

/// <summary>Game name, current scene and engine version for the sidecar.</summary>
internal sealed record CaptureInfoSource(string Game, Func<string> Scene, string EngineVersion);

/// <summary>
/// Records gameplay to a video and a <c>.mfcap.json</c> sidecar (spec Phase 1, ADR 0190). Live: real time,
/// frames scheduled to a constant rate, audio tapped from the master mix. Offline (<c>--fixed-fps</c>):
/// every update is one output frame and exactly <c>sampleRate/fps</c> samples from the manual null device.
/// Main thread only; <see cref="Stopped"/> is raised from <see cref="PollCompletion"/> on the main thread.
/// </summary>
public sealed class GameRecorder : IFrameStreamSink
{
    private const int DefaultSampleRate = 48_000;

    private readonly Func<RecordingFormat, IRecordingBackend> _backendFactory;
    private readonly IFrameStreamSource? _frames;
    private readonly AudioServer? _audio;
    private readonly bool _offline;
    private readonly CaptureInfoSource _info;
    private readonly Func<double>? _clockOverride;
    private readonly InputActivity _input = new();

    private IRecordingBackend? _backend;
    private FrameScheduler? _scheduler;
    private CaptureTimeline? _timeline;
    private AudioCaptureTap? _tap;
    private RecordingFormat? _format;
    private Stopwatch? _stopwatch;
    private long _startTimestamp;
    private long _offlineFrame;
    private long _audioFramesWritten;
    private long _dropped;
    private bool _audioStarted;
    private string? _endReason;
    private Task<RecordingResult>? _finishing;
    private readonly float[] _drain = new float[16_384];

    internal GameRecorder(Func<RecordingFormat, IRecordingBackend> backendFactory, IFrameStreamSource? frames, AudioServer? audio,
        bool offline, CaptureInfoSource info, Func<double>? liveClock = null)
    {
        _backendFactory = backendFactory;
        _frames = frames;
        _audio = audio;
        _offline = offline;
        _info = info;
        _clockOverride = liveClock;
    }

    public bool IsRecording => _backend is not null && _finishing is null;

    public bool IsFinalising => _finishing is { IsCompleted: false };

    public TimeSpan Elapsed => TimeSpan.FromSeconds(IsRecording ? Now() : 0);

    public event Action? Started;

    public event Action<RecordingResult>? Stopped;

    /// <exception cref="InvalidOperationException">Already recording or still finalising, or frames cannot be captured.</exception>
    /// <exception cref="FfmpegNotFoundException">No ffmpeg (from the real backend factory).</exception>
    public void Start(RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (IsRecording || _finishing is not null)
            throw new InvalidOperationException(IsFinalising ? "The previous recording is still being finalised." : "Already recording.");
        if (_frames is null || !_frames.CanStream)
            throw new InvalidOperationException("Recording needs frame capture (start the game with --record or --frame-capture).");

        var (w, h) = _frames.StreamSize;
        var rate = _audio?.SampleRate ?? DefaultSampleRate;
        _format = new RecordingFormat(Path.GetFullPath(options.Path), w, h, options.Fps, _frames.StreamLayout, rate, 2, _offline);
        _backend = _backendFactory(_format);
        _backend.Begin(_format);
        _scheduler = new FrameScheduler(options.Fps, _offline);
        _timeline = new CaptureTimeline();
        _offlineFrame = 0;
        _audioFramesWritten = 0;
        _dropped = 0;
        _audioStarted = false;
        _endReason = null;
        _stopwatch = Stopwatch.StartNew();
        _startTimestamp = Stopwatch.GetTimestamp(); // before the tap attaches, so the audio lead is never negative
        if (!_offline && _audio is not null)
        {
            _tap = new AudioCaptureTap(1 << 18); // ~2.7 s of 48 kHz stereo
            _audio.BeginCapture(_tap);
        }

        _frames.BeginStream(this);
        Started?.Invoke();
    }

    /// <summary>Ends the recording; finalising (encode tail, mux, sidecar) continues in the background.</summary>
    public void Stop()
    {
        if (!IsRecording)
            return;
        _frames!.EndStream();
        _audio?.EndCapture();
        if (_offline)
            RenderOfflineAudio(); // audio for the frames rendered since the last update
        else
            DrainLiveAudio();
        var duration = (double)_scheduler!.WrittenFrames / _format!.Fps;
        var spans = _timeline!.Finish(duration);
        var info = new CaptureInfo(Path.GetFileName(_format.OutputPath), _info.EngineVersion, _info.Game, _info.Scene(), _offline,
            _format.Fps, _format.Width, _format.Height, duration, _dropped);
        var backend = _backend!;
        var reason = _endReason;
        _finishing = Task.Run(async () =>
        {
            var result = await backend.FinishAsync(_scheduler.WrittenFrames, info, spans, CancellationToken.None).ConfigureAwait(false);
            backend.Dispose();
            return reason is null ? result : result with { Error = $"Recording stopped: {reason}." + (result.Error is null ? "" : " " + result.Error) };
        });
    }

    /// <summary>Stops and waits (window closing: the audio server and renderer must outlive the mux).</summary>
    internal RecordingResult? StopAndWait(TimeSpan timeout)
    {
        Stop();
        if (_finishing is null || !_finishing.Wait(timeout))
            return null;
        var result = _finishing.Result;
        Complete(result);
        return result;
    }

    /// <summary>Main thread, every update: raises <see cref="Stopped"/> once finalising has finished.</summary>
    internal void PollCompletion()
    {
        if (_finishing is { IsCompleted: true } done)
            Complete(done.Result);
    }

    public void Mark(string label)
    {
        if (IsRecording)
            _timeline!.Mark(Now(), label);
    }

    public IDisposable Keep(string label) => Scope(keep: true, label);

    public IDisposable Skip(string label) => Scope(keep: false, label);

    internal void ObserveInput(InputEvent e)
    {
        if (IsRecording && _input.Observe(e))
            _timeline!.Input(Now());
    }

    /// <summary>Main thread, once per update before the scene ticks.</summary>
    internal void OnUpdate(bool loading, bool paused)
    {
        PollCompletion();
        if (!IsRecording)
            return;
        if (_endReason is not null)
        {
            Stop();
            return;
        }

        var now = Now();
        _timeline!.Update(now, _input.IsHeld, loading, paused);
        if (_offline)
            RenderOfflineAudio();
        else
            DrainLiveAudio();
    }

    bool IFrameStreamSink.ShouldCapture(out double stamp)
    {
        stamp = _offline ? _offlineFrame++ / (double)_format!.Fps : Now();
        return _scheduler!.ShouldCapture(stamp);
    }

    void IFrameStreamSink.OnFrame(ReadOnlySpan<byte> pixels, int width, int height, double stamp)
    {
        if (_backend is null || _scheduler is null)
            return;
        var copies = _scheduler.CopiesFor(stamp);
        if (copies <= 0)
            return;
        if (_backend.TryWriteVideo(pixels, copies, wait: _offline))
            _scheduler.Committed(copies);
        else
            _dropped++;
    }

    void IFrameStreamSink.OnStreamEnded(string reason) => _endReason = reason;

    private double Now() => _offline
        ? _offlineFrame / (double)(_format?.Fps ?? 60)
        : _clockOverride?.Invoke() ?? _stopwatch?.Elapsed.TotalSeconds ?? 0;

    // Offline: through frame n the WAV holds exactly SamplesThrough(n) frames, rendered from the manual null device.
    private void RenderOfflineAudio()
    {
        var target = SampleScheduler.SamplesThrough(_offlineFrame, _format!.SampleRate, _format.Fps);
        var frames = (int)(target - _audioFramesWritten);
        if (frames <= 0)
            return;
        if (_audio is { } audio)
        {
            var buffer = new float[frames * 2];
            audio.RenderNullDevice(frames, buffer);
            _backend!.WriteAudio(buffer);
        }
        else
        {
            _backend!.WriteSilence(frames);
        }

        _audioFramesWritten = target;
    }

    // Live: align the first block to t0 (pad the gap with silence), then copy whatever the audio thread produced.
    private void DrainLiveAudio()
    {
        if (_tap is not { } tap || tap.FirstBlockTimestamp == 0)
            return;
        if (!_audioStarted)
        {
            var lead = Stopwatch.GetElapsedTime(_startTimestamp, tap.FirstBlockTimestamp);
            _backend!.WriteSilence((long)Math.Max(0, lead.TotalSeconds * _format!.SampleRate));
            _audioStarted = true;
        }

        int n;
        while ((n = tap.Drain(_drain)) > 0)
            _backend!.WriteAudio(_drain.AsSpan(0, n));
    }

    private void Complete(RecordingResult result)
    {
        _finishing = null;
        _backend = null;
        _tap = null;
        Log.Info(result.Error is null
            ? $"[Recorder] Saved {result.Path} ({result.DurationSeconds:0.0} s, {result.DroppedFrames} dropped frames)."
            : $"[Recorder] {result.Error}");
        Stopped?.Invoke(result);
    }

    private IDisposable Scope(bool keep, string label)
    {
        if (!IsRecording)
            return NoScope.Instance;
        var timeline = _timeline!;
        var handle = keep ? timeline.OpenKeep(Now(), label) : timeline.OpenSkip(Now(), label);
        return new ScopeHandle(() => { if (ReferenceEquals(timeline, _timeline)) timeline.Close(handle, Now()); });
    }

    private sealed class ScopeHandle(Action close) : IDisposable
    {
        private Action? _close = close;

        public void Dispose() => Interlocked.Exchange(ref _close, null)?.Invoke();
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }
}
```

`GameRecorder` implements the internal `IFrameStreamSink` explicitly. C# allows a public class to implement an internal interface; the members stay invisible to game code.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~GameRecorderTests"`
Expected: all PASS. Before changing an expected value, trace `MarksKeepsSkipsInputLoadingAndPauseReachTheSidecar` by hand:
- input at 1.0 opens a span with a tail to 1.25;
- the update at 2.0 is not held and 2.0 ≥ 1.25, so it closes at 1.25;
- loading runs `[0,1]`;
- the update at 3.0 opens paused, and `Finish` at duration = written frames / 60 closes it.

In the live fake, four renders at t = 0, 1, 2, 3 write 1 + 60 + 60 + 60 = 181 frames, so the duration is about 3.0167 s and paused is `[3.0, 3.0167]`, which is non-empty.

- [ ] **Step 5: Wire into the engine.**

`SceneTree.Recording.cs`:

```csharp
namespace MainframeEngine;

/// <summary>Gameplay recording: <see cref="Recorder"/> and the input hook.</summary>
public sealed partial class SceneTree
{
    /// <summary>The gameplay recorder, or null under <c>--headless</c> and in tools that never render.</summary>
    public GameRecorder? Recorder { get; internal set; }
}
```

`SceneTree.DispatchInput`: directly after `Input.ProcessEvent(inputEvent);` add:

```csharp
        Recorder?.ObserveInput(inputEvent); // before the UI can consume it: menus clicked while recording are still play
```

`GameHost`:
- **End of the constructor:** create the recorder once the renderer exists.
  - If `Renderer` is created later, in `OnLoad`, put this at the end of `GameHost.OnLoad` instead (search for `protected override void OnLoad`).
  - When `Renderer` is a `VulkanRenderer` it implements `IFrameStreamSource`, and the audio server is `Tree.Servers.Get<AudioServer>()`.

```csharp
        Tree.Recorder = new GameRecorder(
            _ => new FfmpegRecordingBackend(FfmpegResolver.ResolveForThisMachine(HostOptions.FfmpegPath)),
            Renderer as IFrameStreamSource,
            Tree.Servers.Get<AudioServer>(),
            HostOptions.RecordOffline,
            new CaptureInfoSource(Settings.Name, () => Tree.CurrentScene?.SceneFilePath ?? "", EngineInfo.Version));
        if (HostOptions.RecordPath is { } recordPath)
            Tree.Recorder.Start(new RecordingOptions(recordPath, HostOptions.FixedFps > 0 ? HostOptions.FixedFps : 60));
```

  An `FfmpegNotFoundException` from `Start` must not crash the game. Wrap the `Start` call in `try … catch (Exception e) when (e is FfmpegNotFoundException or IOException or InvalidOperationException)`, `Log.Error($"[Recorder] {e.Message}")`, and `Quit(ExitCode.Error)`. A `--record` run that cannot record should fail loudly, not play unrecorded.

- **`OnUpdate`:** directly after `Session.Update(gameTime);` and before `if (Tree.IsLoading)`:

```csharp
        Tree.Recorder?.OnUpdate(Tree.IsLoading, Tree.Paused);
```

- **`OnClose`:** before `Session.Shutdown(CurrentExitCode);`:

```csharp
        if (Tree.Recorder is { IsRecording: true } recorder &&
            recorder.StopAndWait(TimeSpan.FromMinutes(2)) is null)
            Log.Error("[Recorder] Finalising the recording took over two minutes; the partial files were kept.");
```

- **Dev overlay:** subscribe in the constructor. Use the actual dev-overlay visibility member; search `DevOverlayVisible` in `GameHost`/`Engine`.

```csharp
        Tree.Recorder.Started += () => SetDevOverlayVisible(false);
```

- [ ] **Step 6: Full gates**

Run: `just build && just test && just format-check`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add MainframeEngine Tests
git commit -m "Add GameRecorder and wire recording into GameHost, SceneTree input and shutdown"
```

---

### Task 11: Forest smoke runs and the HUD example

**Files:**
- Create: `build/forest-record.sh`
- Modify: the Forest HUD owner. `Examples/Forest/Forest/Src/Dev/FpsHud.cs` is the dev HUD; run `grep -n "Hud" Examples/Forest/Forest/Src/Menu/ForestWorld.cs` to find the gameplay HUD.

- [ ] **Step 1: Hide Forest's HUD while recording.** In each HUD node found above, add to its `_Ready`/`Ready` override (match the node's existing lifecycle method name):

```csharp
        if (Tree.Recorder is { } recorder)
        {
            recorder.Started += () => Visible = false;
            recorder.Stopped += _ => Visible = true;
            Visible = !recorder.IsRecording;
        }
```

- [ ] **Step 2: The smoke script**

```bash
#!/usr/bin/env bash
# Records the Forest twice and checks the results with ffprobe (needs ffmpeg/ffprobe on PATH, display awake, LFS art).
#   offline: 60 s flyover at 4K60, exactly 3600 frames and 60.000 s of audio
#   live:    10 s of autowalk at window size
# Usage: build/forest-record.sh [out-dir]
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:-$repo/artifacts/recordings}"
mkdir -p "$out"
dotnet build "$repo/Examples/Forest/Forest.slnx" -c Release -v q -nologo
bin="$repo/Examples/Forest/Forest.Desktop/bin/Release/net10.0"

(cd "$bin" && dotnet Forest.Desktop.dll --fixed-fps 60 --max-frames 3600 --record-scale 2 --no-log-file \
  --record "$out/offline.mp4" ++ --camera flyover --no-capture)
(cd "$bin" && dotnet Forest.Desktop.dll --max-frames 600 --no-log-file \
  --record "$out/live.mp4" ++ --autowalk --no-capture)

frames=$(ffprobe -v error -count_frames -select_streams v:0 -show_entries stream=nb_read_frames -of csv=p=0 "$out/offline.mp4")
size=$(ffprobe -v error -select_streams v:0 -show_entries stream=width,height -of csv=s=x:p=0 "$out/offline.mp4")
audio=$(ffprobe -v error -select_streams a:0 -show_entries stream=duration -of csv=p=0 "$out/offline.mp4")
echo "offline: $frames frames, $size, audio ${audio}s"
[[ "$frames" == "3600" ]] || { echo "expected 3600 frames"; exit 1; }
test -f "$out/offline.mp4.mfcap.json" && test -f "$out/live.mp4.mfcap.json"
grep -q '"mode": "offline"' "$out/offline.mp4.mfcap.json"
echo "ok: $out"
```

`chmod +x build/forest-record.sh`.

- [ ] **Step 3: Run it locally.** It needs a display; it is not for CI.

Run: `caffeinate -u -t 3; build/forest-record.sh`
Expected: `offline: 3600 frames, 3840x2160, audio 60.0…s` and `ok`. Open both MP4s and check them by eye:
- no HUD in either;
- the live one has sound;
- the offline flyover is smooth.

Then follow the spec's acceptance item 1. Record 2 minutes live, with a scene that calls `Recorder.Mark` and plays a beep with a white flash. Check in an editor that the flash and the beep line up at the start and the end, within one frame.

- [ ] **Step 4: Commit**

```bash
git add build/forest-record.sh Examples/Forest
git commit -m "Hide the Forest HUD while recording and add a recording smoke script"
```

---

### Task 12: Engine docs, ADR and spec amendments

**Files:**
- Create: `docs/design/recording.md`
- Create: `memory/decisions/0190-gameplay-recording-ffmpeg-child-process.md`
- Modify: `CLAUDE.md` (engine): add `--record` to the host flags list, and a line under subsystems pointing at `docs/design/recording.md`
- Modify: `/Users/brogan/dev/SimpleVideoEditor/docs/superpowers/specs/2026-10-12-mainframe-engine-integration-design.md` (the four amendments)

- [ ] **Step 1: `docs/design/recording.md`.** Follow the engine's subsystem-doc headings: `# Gameplay recording`, `## Purpose`, `## Key types`, `## Architecture` (with `### Threading`), `## Sidecar format`, `## Performance`, `## Known issues`, `## Related docs`. The content comes from this plan and spec §1:
  - **Key types:** a table of `GameRecorder`, `FrameScheduler`, `SampleScheduler`, `CaptureTimeline`, `InputActivity`, `CaptureSidecarWriter`, `FfmpegRecordingBackend`, `IFrameStreamSource` and `AudioCaptureTap`.
  - **Threading:**
    - main thread: the recorder, the frame stream and the timeline;
    - audio thread: `AudioCaptureTap.Write` only;
    - video writer thread: the encoder's stdin;
    - a thread-pool task: finalising.
  - **Sidecar format:** copy spec §1.7 verbatim, and say that `Tests/MainframeEngine.Tests/Recording/Fixtures/sample.mfcap.json` must stay byte-identical to Cutback's copy.
  - **Known issues:**
    - live audio runs on the device clock (drift is logged, not corrected);
    - the first recording in a process probes encoders for up to about 1 s;
    - WAV sizes clamp past about 3 hours;
    - a window resize ends the recording;
    - `StartRecording` mid-session (Phase 3) needs the game launched with `--frame-capture`.

- [ ] **Step 2: ADR 0190.** Use the engine's ADR format:

```markdown
# ADR 0190 — Gameplay recording via an ffmpeg child process

- **Date:** 2026-10-12
- **Status:** accepted
- **Milestone:** —
- **Docs:** docs/design/recording.md

## Context

Promo footage for games built on the engine needs clean, frame-accurate captures (live play and
deterministic offline renders) that a trimming tool can cut using what the engine knew: input,
loading, pause, markers. The engine is MIT and ships Spine and Steam redistributables, so it cannot
take on GPL code, and games must not inherit any encoder obligations.

## Decision

- **ffmpeg as a separate program, found on the user's machine.** Raw frames go to its stdin; audio
  goes to a float WAV; a second run muxes. The engine never links, bundles or P/Invokes an encoder.
  Rejected: linking libav* (LGPL obligations in every game, native build matrix); bundling ffmpeg
  (licence depends on the build; x264 builds are GPL); a named pipe for audio (per-OS plumbing for no gain).
- **Hardware H.264 first, probed before use**, then libx264/libopenh264, as a high-bitrate
  intermediate with a one-second GOP.
- **Frames from a per-frame-slot readback buffer read after BeginFrame's existing fence wait**, so
  recording adds a copy, never a stall.
- **A `.mfcap.json` sidecar** (format `mainframe-capture`, version 1) that the engine owns and
  Cutback reads; a shared fixture pins it in both repositories.

## Consequences

- Recording requires ffmpeg; without it `--record` fails with an install hint.
- Shipped games contain no encoder; `--record` is inert unless used.
- A future in-editor Clips tab reuses the same sidecar and the same process boundary.
```

- [ ] **Step 3: Spec amendments.** In the Cutback repo's spec, under §1.3, change "3 host-visible buffers" to "one host-visible buffer per frame in flight (2)". Correct the `BeginFrame` explanation to say the slot's own fence wait proves the copy finished. Under §1.4 and §1.5, add:
  - the `StreamingWavWriter`;
  - "the WAV is padded or truncated to the video's exact sample count at Stop, in both modes";
  - "odd dimensions are cropped by one pixel for yuv420p".

  Commit that file in the Cutback repo separately:

```bash
git -C /Users/brogan/dev/SimpleVideoEditor add docs/superpowers/specs/2026-10-12-mainframe-engine-integration-design.md
git -C /Users/brogan/dev/SimpleVideoEditor commit -m "Spec: record Phase 1 implementation amendments"
```

- [ ] **Step 4: Gates and commit (engine)**

Run: `just build && just test && just format-check`
Expected: all green.

```bash
git add docs/design/recording.md memory/decisions/0190-gameplay-recording-ffmpeg-child-process.md CLAUDE.md
git commit -m "Document gameplay recording and record ADR 0190"
```
