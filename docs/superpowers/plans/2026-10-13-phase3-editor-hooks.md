# Phase 3 — Editor Hooks for Gameplay Recording Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** From the Mainframe Editor, record gameplay with one click or key. "Play and Record" launches the game with `--record`; while the game runs, the same button starts or stops a recording over the editor link. The toolbar shows `● REC 01:23`. When a recording finishes, a dialog offers **Open in Cutback** and **Show in Folder**.

**Architecture:**
- **Protocol:** editor link v4 adds `StartRecording`/`StopRecording` commands and a game→editor `Recording` message. The message is queued, not latest-wins, so a "finished" event is never lost.
- **Game side:** `GameSession` turns the commands into `GameRecorder` calls and reports the recorder's `Started`/`Stopped` as `Recording` messages.
- **Editor side:**
  - `PlayService` tracks recording state per instance and raises `RecordingFinished`.
  - `PlayController` adds Play and Record and the toggle, and always passes `--frame-capture` so a recording can start mid-session.
  - A small `RecordingNotifier` shows the result dialog and launches Cutback through `CutbackLocator`.

**Tech Stack:** .NET 10, the engine's editor (RmlUi panels, `EditorCommands`, `PlayService`/`PlayController`, `MessageDialog`, `EditorSettings`), and xunit.v3 with plain `Assert`.

**Spec:** `/Users/brogan/dev/SimpleVideoEditor/docs/superpowers/specs/2026-10-12-mainframe-engine-integration-design.md`, "Phase 3 — Editor hooks (engine)"

**Repository and branch:** every path below is relative to the engine worktree created for this phase: `/Users/brogan/mainframe/mainframe-engine/.claude/worktrees/editor-recording`, branch `feature/editor-recording`. The branch is cut from `feature/gameplay-recorder` (Mainframe-Games/mainframe-engine#62), so this PR stacks on Phase 1.

## Global Constraints

- **Editor link protocol:**
  - `EditorLinkProtocol.Version` becomes **4**.
  - New command kinds: `StartRecording = 7` (Argument = output path) and `StopRecording = 8`.
  - New message type: `Recording = 8` (game→editor).
  - An editor and game with different versions already warn (`PlayService.cs` ~463). A v3 editor drops a v4 game that sends `Recording`, which is why the version is bumped.
- **Recording message wire format:** `u8 state, str path, f32 elapsedSeconds, i64 droppedFrames, i64 duplicatedFrames, str error`. `state` is `Started=0 | Stopped=1 | Failed=2`.
- **Recordings folder:** `{project user data}/recordings/{label}-{yyyyMMdd-HHmmss}.mp4`. Here `{project user data}` is `ProjectService.UserDataDirectoryOf(settings)` and `{label}` is the scene's file name without extension, or "game".
- **Launch arguments:** editor play launches always pass `--frame-capture`, as a host flag before `--editor-port`. Recording mid-session needs a swapchain with `TRANSFER_SRC`.
- **Command and shortcut:** `play.record`, bound to **F10**. F10 is free; Shift+F5 is `play.another` and F9 is the RmlUi debugger. The Run menu label is "Play and Record" when idle, "Start Recording" while playing, and "Stop Recording" while recording.
- **Toolbar:** `#record` sits in the play group, uses the `point-filled` icon (already in the atlas) and takes a red `recording` class while active. The status reads `● REC m:ss` while recording and `Saving recording…` while finalising.
- **Finished dialog:** uses the existing modal `MessageDialog`, since the editor has no toast system. On success the buttons are "Open in Cutback", "Show in Folder" and "Close". On failure they are "Show in Folder" (only if `.partial` exists) and "Close".
- **`CutbackLocator` lookup order:**
  1. `EditorSettings.CutbackPath`
  2. `PATH` entries for `cutback`, `Cutback` and `Cutback.exe`
  3. `/Applications/Cutback.app/Contents/MacOS/Cutback`
  4. `%LOCALAPPDATA%\Programs\Cutback\Cutback.exe`
  5. `~/.local/bin/cutback`

  If none is found, a dialog links `https://github.com/brogan89/Cutback/releases` and points at Editor Settings.
- **Engine conventions:**
  - file-scoped namespaces; `sealed` by default; dense `<summary>` docs that explain why;
  - tests in plain xunit `Assert` with PascalCase sentence names;
  - `InternalsVisibleTo` covers `MainframeEngine.Tests` and `MainframeEngine.Editor.Tests`.
- **Gates per task:** `just build`, `just test` (or the focused filter), `just format-check`. Before the final review, also the Release `dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false` and `just test-render`.
- **Never push** (the user decides); work only in the Phase 3 worktree.

## Spec amendments made by this plan

Task 7 records these in the spec:
1. **A modal dialog, not a notification.** The editor has no toast system, so "Open in Cutback / Show in Folder" lives in a `MessageDialog`.
2. **F10, not Shift+F5.** Shift+F5 is already "Run Another Instance". This also answers spec open question 4.
3. **Every editor play launch passes `--frame-capture`,** so Record works mid-session. The cost is a `TRANSFER_SRC` usage flag on the swapchain.
4. **The record icon is the atlas's existing `point-filled`, tinted red,** which avoids an icon-atlas regeneration.

## Review Focus

1. **The recording finishes while the editor is busy or the dialog is already open.** Expected: one dialog per finished recording, queued by `MessageDialog`. Test: Task 6, `TwoFinishedRecordingsQueueTwoDialogs`.
2. **The game quits (F8/Stop) while recording.** Expected: the game finalises in `OnClose`, sends `Recording: Stopped` before `Goodbye`, and the editor shows the dialog. Test: Task 2, `StopCommandWhileRecordingReportsStoppedBeforeQuit`, plus Task 3's harness test.
3. **Record is pressed with multiple instances running.** Expected: recording starts on the first running instance only, and the toggle stops whichever instance is recording. Test: Task 5, `ToggleRecordingTargetsTheFirstRunningInstance`.
4. **The game has no ffmpeg, or StartRecording fails.** Expected: a `Recording: Failed` message with the readable error, and the editor shows it. Test: Task 2, `StartRecordingFailureIsReported`.
5. **The Cutback path setting points at a file that doesn't exist.** Expected: fall through to the other locations, then the "not found" dialog. Test: Task 4, `AMissingSettingFallsThroughToPath`.

---

### Task 1: Editor link protocol v4

**Files:**
- Modify: `MainframeEngine/Src/EditorLink/EditorLinkProtocol.cs`
- Test: `Tests/MainframeEngine.Tests/Project/EditorLinkTests.cs`

**Interfaces:**
- Produces:
  - `EditorCommandKind.StartRecording = 7` and `EditorCommandKind.StopRecording = 8`
  - `EditorLinkMessageType.Recording = 8`
  - `public enum EditorLinkRecordingState : byte { Started = 0, Stopped = 1, Failed = 2 }`
  - `public readonly record struct EditorLinkRecording(EditorLinkRecordingState State, string Path, float ElapsedSeconds, long DroppedFrames, long DuplicatedFrames, string Error)`
  - `EditorLinkMessage.Recording` (init property)
  - `EditorLinkProtocol.WriteRecording(ArrayBufferWriter<byte>, in EditorLinkRecording)`
  - `EditorLinkProtocol.Version == 4`

- [ ] **Step 1: Write the failing tests** (append to `EditorLinkTests`, reusing its `RoundTrip` helper)

```csharp
[Fact]
public void RecordingMessagesRoundTrip()
{
    var started = new EditorLinkRecording(EditorLinkRecordingState.Started, "/u/recordings/forest-20261013-101500.mp4", 0f, 0, 0, "");
    var stopped = new EditorLinkRecording(EditorLinkRecordingState.Stopped, "/u/r.mp4", 83.25f, 2, 11, "");
    var failed = new EditorLinkRecording(EditorLinkRecordingState.Failed, "/u/r.mp4", 0f, 0, 0, "ffmpeg was not found.");
    foreach (var recording in new[] { started, stopped, failed })
    {
        var message = RoundTrip(b => EditorLinkProtocol.WriteRecording(b, recording));
        Assert.Equal(EditorLinkMessageType.Recording, message.Type);
        Assert.Equal(recording, message.Recording);
    }
}

[Fact]
public void RecordingCommandsRoundTrip()
{
    var start = new EditorLinkCommand(EditorCommandKind.StartRecording, "/u/recordings/a.mp4");
    var stop = new EditorLinkCommand(EditorCommandKind.StopRecording);
    Assert.Equal(start, RoundTrip(b => EditorLinkProtocol.WriteCommand(b, start)).Command);
    Assert.Equal(stop, RoundTrip(b => EditorLinkProtocol.WriteCommand(b, stop)).Command);
}

[Fact]
public void AnUnknownRecordingStateIsRejected()
{
    var buffer = new ArrayBufferWriter<byte>();
    EditorLinkProtocol.WriteRecording(buffer, new EditorLinkRecording(EditorLinkRecordingState.Failed, "p", 0f, 0, 0, "e"));
    var body = buffer.WrittenSpan[EditorLinkProtocol.HeaderLength..].ToArray();
    body[1] = 9; // state byte
    Assert.False(EditorLinkProtocol.TryDecode(body, out _));
}

[Fact]
public void ProtocolVersionIsFour()
{
    Assert.Equal(4, EditorLinkProtocol.Version);
}
```

Also change the existing unknown-command rejection test (~line 163) so it uses a kind above `StopRecording`. The bytes `[Command, 99, …]` already are, so confirm it still passes unchanged.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~EditorLinkTests"`
Expected: a compile error, because `EditorLinkRecording` is not defined.

- [ ] **Step 3: Implement.** In `EditorLinkProtocol.cs`:
  - Enum additions: `StartRecording = 7, StopRecording = 8` in `EditorCommandKind`, each with a `<summary>` ("Start recording to Argument (an .mp4 path); the game answers with a Recording message", "Stop the current recording; the game answers Recording Stopped once finalised"). `Recording = 8` in `EditorLinkMessageType` ("game → editor: a recording started, stopped or failed").
  - New types next to `EditorLinkStatus`:

```csharp
/// <summary>What happened to a gameplay recording (<see cref="EditorLinkMessageType.Recording"/>).</summary>
public enum EditorLinkRecordingState : byte
{
    Started = 0,
    Stopped = 1,
    Failed = 2,
}

/// <summary>
/// A gameplay recording event, game → editor. Unlike status and tree snapshots these are queued, not
/// latest-wins, so the editor always learns that a recording finished.
/// </summary>
public readonly record struct EditorLinkRecording(
    EditorLinkRecordingState State,
    string Path,
    float ElapsedSeconds,
    long DroppedFrames,
    long DuplicatedFrames,
    string Error);
```

  - `EditorLinkMessage`: add `public EditorLinkRecording Recording { get; init; }`.
  - Wire doc comment: add `<item>Recording: u8 state, str path, f32 elapsed s, i64 dropped, i64 duplicated, str error</item>`.
  - `Version = 4;` with the comment `// 2: Tree / RequestTree · 3: Welcome · 4: StartRecording / StopRecording / Recording`.
  - Writer:

```csharp
public static void WriteRecording(ArrayBufferWriter<byte> output, in EditorLinkRecording recording)
{
    var w = new FrameWriter(output, EditorLinkMessageType.Recording);
    w.Byte((byte)recording.State);
    w.String(recording.Path);
    w.Single(recording.ElapsedSeconds);
    w.Int64(recording.DroppedFrames);
    w.Int64(recording.DuplicatedFrames);
    w.String(recording.Error);
    w.Finish();
}
```

  - In `TryDecode`, add a case in the existing style:

```csharp
case EditorLinkMessageType.Recording:
{
    if (!r.Byte(out var state) || state > (byte)EditorLinkRecordingState.Failed
        || !r.String(out var path) || !r.Single(out var elapsed)
        || !r.Int64(out var dropped) || !r.Int64(out var duplicated) || !r.String(out var error))
        return false;
    message = new EditorLinkMessage
    {
        Type = type,
        Recording = new EditorLinkRecording((EditorLinkRecordingState)state, path, elapsed, dropped, duplicated, error),
    };
    break;
}
```

  - Raise the command range check's upper bound from `RequestTree` to `StopRecording`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~EditorLink"`, then `just build` (the editor references the protocol too).
Expected: all PASS, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine/Src/EditorLink/EditorLinkProtocol.cs Tests/MainframeEngine.Tests/Project/EditorLinkTests.cs
git commit -m "Editor link v4: StartRecording/StopRecording commands and a Recording message"
```

---

### Task 2: Game side — commands to the recorder, recorder events to the editor

**Files:**
- Modify: `MainframeEngine/Src/EditorLink/EditorLinkClient.cs` (queued `SendRecording`)
- Modify: `MainframeEngine/Src/Project/GameSession.cs` (`Apply` cases; `AttachRecorder`)
- Modify: `MainframeEngine/Src/Project/GameHost.cs` (`CreateRecorder` calls `Session.AttachRecorder`)
- Create: `Tests/MainframeEngine.Tests/Recording/RecorderFakes.cs` (moves the private `FakeBackend`/`FakeFrames` out of `GameRecorderTests` as `internal sealed` classes, plus a `RecorderFakes.Create(...)` factory with the same signature as `GameRecorderTests.Create`)
- Modify: `Tests/MainframeEngine.Tests/Recording/GameRecorderTests.cs` (uses the shared fakes, with no behaviour change)
- Test: `Tests/MainframeEngine.Tests/Project/GameSessionRecordingTests.cs`

**Interfaces:**
- Consumes: Task 1 types; `GameRecorder` (`Start(RecordingOptions)`, `Stop()`, `IsRecording`, `IsFinalising`, `Elapsed`, `event Started`, `event Stopped(RecordingResult)`); `RecordingResult` (`Path`, `DurationSeconds`, `DroppedFrames`, `DuplicatedFrames`, `Error`).
- Produces:
  - `EditorLinkClient.SendRecording(in EditorLinkRecording)` (queued: a `Queue<EditorLinkRecording>` under `_statusGate`, all written in `Pump` after the status/tree slots)
  - `GameSession.AttachRecorder(GameRecorder recorder)`
  - Static `GameSession.ToLink(RecordingResult)` → `EditorLinkRecording` (Stopped, or Failed when `Error` is not null)

- [ ] **Step 1: Extract the fakes.** Move `FakeBackend` and `FakeFrames` (and the `Create` helper) from `GameRecorderTests.cs` into `RecorderFakes.cs` as `internal sealed` types in namespace `MainframeEngine.Tests.Recording`, then point `GameRecorderTests` at them.
  Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~GameRecorderTests"`
  Expected: all PASS, with no expectation changes.

- [ ] **Step 2: Write the failing tests** (`GameSessionRecordingTests.cs`). Each test builds a `SceneTree`, a `GameSession(tree, settings)` and a recorder from `RecorderFakes.Create(...)`, then calls `session.AttachRecorder(recorder)`. To capture what the session would send, give `GameSession` an internal test seam: `internal Action<EditorLinkRecording>? RecordingSink` (the default sends through `Link?.SendRecording`), set in the tests.

```csharp
using MainframeEngine.Tests.Recording;

namespace MainframeEngine.Tests.Project;

/// <summary>Editor-link recording commands drive GameRecorder, and its events go back to the editor in order.</summary>
public sealed class GameSessionRecordingTests
{
    private static ProjectSettings Settings() => new() { Name = "T" };

    private static (GameSession Session, GameRecorder Recorder, FakeFrames Frames, List<EditorLinkRecording> Sent) Create()
    {
        var tree = new SceneTree();
        var session = new GameSession(tree, Settings());
        var (recorder, _, frames, _) = RecorderFakes.Create();
        var sent = new List<EditorLinkRecording>();
        session.RecordingSink = sent.Add;
        session.AttachRecorder(recorder);
        session.Start();
        return (session, recorder, frames, sent);
    }

    [Fact]
    public void StartRecordingCommandStartsTheRecorderAndReportsStarted()
    {
        var (session, recorder, _, sent) = Create();
        using var _ = session;

        session.Apply(new EditorLinkCommand(EditorCommandKind.StartRecording, "/r/a.mp4"));

        Assert.True(recorder.IsRecording);
        var started = Assert.Single(sent);
        Assert.Equal(EditorLinkRecordingState.Started, started.State);
        Assert.EndsWith("a.mp4", started.Path);
    }

    [Fact]
    public void StopRecordingCommandReportsStoppedOnceFinalised()
    {
        var (session, recorder, frames, sent) = Create();
        using var _ = session;
        session.Apply(new EditorLinkCommand(EditorCommandKind.StartRecording, "/r/a.mp4"));
        frames.Render();

        session.Apply(new EditorLinkCommand(EditorCommandKind.StopRecording));
        recorder.StopAndWait(TimeSpan.FromSeconds(5)); // raises Stopped on this thread

        Assert.Equal([EditorLinkRecordingState.Started, EditorLinkRecordingState.Stopped], sent.Select(s => s.State));
        Assert.Equal("", sent[1].Error);
    }

    [Fact]
    public void StartRecordingFailureIsReported()
    {
        var (session, recorder, _, sent) = Create();
        using var _ = session;
        session.Apply(new EditorLinkCommand(EditorCommandKind.StartRecording, "/r/a.mp4"));

        session.Apply(new EditorLinkCommand(EditorCommandKind.StartRecording, "/r/b.mp4")); // already recording → throws inside

        Assert.Equal(EditorLinkRecordingState.Failed, sent[^1].State);
        Assert.Contains("Already recording", sent[^1].Error);
        Assert.True(recorder.IsRecording); // the first recording is unaffected
    }

    [Fact]
    public void StartRecordingWithoutARecorderReportsFailure()
    {
        var tree = new SceneTree();
        using var session = new GameSession(tree, Settings());
        var sent = new List<EditorLinkRecording>();
        session.RecordingSink = sent.Add;
        session.Start();

        session.Apply(new EditorLinkCommand(EditorCommandKind.StartRecording, "/r/a.mp4"));

        Assert.Equal(EditorLinkRecordingState.Failed, Assert.Single(sent).State);
    }

    [Fact]
    public void StopCommandWhileRecordingReportsStoppedBeforeQuit()
    {
        var (session, recorder, frames, sent) = Create();
        using var _ = session;
        var quitAfterStopped = false;
        session.QuitRequested += _ => quitAfterStopped = sent.Any(s => s.State == EditorLinkRecordingState.Stopped);
        session.Apply(new EditorLinkCommand(EditorCommandKind.StartRecording, "/r/a.mp4"));
        frames.Render();

        session.Apply(new EditorLinkCommand(EditorCommandKind.Stop));

        // GameHost.OnClose finalises (StopAndWait) before Session.Shutdown; model that here.
        recorder.StopAndWait(TimeSpan.FromSeconds(5));
        Assert.Contains(sent, s => s.State == EditorLinkRecordingState.Stopped);
        Assert.False(quitAfterStopped); // Quit is requested first; the Stopped report follows in OnClose, before Goodbye
    }

    [Fact]
    public void ToLinkMapsAnErrorToFailed()
    {
        var ok = GameSession.ToLink(new RecordingResult { Path = "/r/a.mp4", DurationSeconds = 2, DroppedFrames = 1, DuplicatedFrames = 3 });
        var bad = GameSession.ToLink(new RecordingResult { Path = "/r/a.mp4", Error = "boom" });
        Assert.Equal(new EditorLinkRecording(EditorLinkRecordingState.Stopped, "/r/a.mp4", 2f, 1, 3, ""), ok);
        Assert.Equal(EditorLinkRecordingState.Failed, bad.State);
        Assert.Equal("boom", bad.Error);
    }
}
```

Adapt the names to the real `RecorderFakes.Create` tuple, and to whether `GameSession` is `IDisposable`. If it isn't, follow `GameSessionTests`' disposal pattern.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~GameSessionRecordingTests"`
Expected: a compile error, because `AttachRecorder` and `RecordingSink` are not defined.

- [ ] **Step 4: Implement**

`EditorLinkClient`:

```csharp
    private readonly Queue<EditorLinkRecording> _recordings = new(); // guarded by _statusGate

    /// <summary>Queues a recording event; unlike status and tree these are never coalesced.</summary>
    public void SendRecording(in EditorLinkRecording recording)
    {
        lock (_statusGate)
        {
            if (_recordings.Count < 64)
                _recordings.Enqueue(recording);
        }

        _signal.Set();
    }
```

In `Pump`, inside the existing `lock (_statusGate)` block after the tree:

```csharp
            while (_recordings.TryDequeue(out var recording))
                EditorLinkProtocol.WriteRecording(buffer, recording);
```

`GameSession`:

```csharp
    private GameRecorder? _recorder;

    /// <summary>Test seam: where recording events go (default: the editor link).</summary>
    internal Action<EditorLinkRecording>? RecordingSink { get; set; }

    /// <summary>Lets the editor start and stop <paramref name="recorder"/> and hear about it (GameHost calls this once the recorder exists).</summary>
    public void AttachRecorder(GameRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        _recorder = recorder;
        recorder.Started += () => SendRecording(new EditorLinkRecording(EditorLinkRecordingState.Started, CurrentRecordingPath(), 0f, 0, 0, ""));
        recorder.Stopped += result => SendRecording(ToLink(result));
    }

    internal static EditorLinkRecording ToLink(RecordingResult result) => new(
        result.Error is null ? EditorLinkRecordingState.Stopped : EditorLinkRecordingState.Failed,
        result.Path, (float)result.DurationSeconds, result.DroppedFrames, result.DuplicatedFrames, result.Error ?? "");

    private void SendRecording(in EditorLinkRecording recording)
    {
        if (RecordingSink is { } sink)
            sink(recording);
        else
            Link?.SendRecording(recording);
    }
```

`Started` carries no path, so record the path passed to `Start` in a `_recordingPath` field set by the `StartRecording` case. `CurrentRecordingPath()` returns it, or `""` for a `--record` run started by GameHost. Better: if `GameRecorder` exposes the current output path, use that. If it doesn't, add `public string? OutputPath { get; }` to `GameRecorder` (set in `Start`, from the full path in `_format`). That is the cleanest option and is covered by the `Started` test.

In `Apply`'s switch:

```csharp
            case EditorCommandKind.StartRecording:
                StartRecording(command.Argument);
                break;
            case EditorCommandKind.StopRecording:
                _recorder?.Stop();
                break;
```

```csharp
    private void StartRecording(string path)
    {
        if (_recorder is not { } recorder)
        {
            SendRecording(new EditorLinkRecording(EditorLinkRecordingState.Failed, path, 0f, 0, 0,
                "This game has no recorder (it is headless or was not started by GameHost)."));
            return;
        }

        try
        {
            recorder.Start(new RecordingOptions { Path = path, Fps = Options.FixedFps > 0 ? Options.FixedFps : 60 });
        }
        catch (Exception e) when (e is FfmpegNotFoundException or IOException or InvalidOperationException
                                      or UnauthorizedAccessException or ArgumentException)
        {
            Log.Error($"[Recorder] {e.Message}");
            SendRecording(new EditorLinkRecording(EditorLinkRecordingState.Failed, path, 0f, 0, 0, e.Message));
        }
    }
```

`GameHost.CreateRecorder` (~line 344): after the recorder is assigned to `Tree.Recorder`, call `Session.AttachRecorder(recorder);`.

- [ ] **Step 5: Run the tests**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~GameSessionRecordingTests|FullyQualifiedName~GameRecorderTests|FullyQualifiedName~GameSession"`, then `just build`.
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add MainframeEngine Tests/MainframeEngine.Tests
git commit -m "Let the editor start and stop gameplay recording over the link"
```

---

### Task 3: Editor `PlayService` — recording state per instance, always launch with `--frame-capture`

**Files:**
- Modify: `MainframeEngine.Editor/Src/Play/PlayInstance.cs`
- Modify: `MainframeEngine.Editor/Src/Play/PlayService.cs`
- Modify: `Tests/MainframeEngine.Editor.Tests/Play/PlayFakes.cs` (`FakeGame` answers recording commands)
- Test: `Tests/MainframeEngine.Editor.Tests/Play/PlayServiceTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces:
  - `PlayInstance`:
    - `public PlayRecordingState Recording { get; internal set; }` where `public enum PlayRecordingState { None, Recording, Saving }`
    - `public string? RecordingPath { get; internal set; }`
    - `public DateTime? RecordingStartedUtc { get; internal set; }`
  - `PlayService`:
    - `public bool StartRecording(PlayInstance instance, string path)`
    - `public bool StopRecording(PlayInstance instance)`
    - `public event Action<PlayInstance, EditorLinkRecording>? RecordingFinished` (Stopped or Failed)
    - `public bool AnyRecording`

- [ ] **Step 1: Teach `FakeGame` to answer.** In `PlayFakes.cs`, in `FakeGame.Run()`'s command switch:
  - `StartRecording` → `client.SendRecording(new(Started, cmd.Argument, 0, 0, 0, ""))`
  - `StopRecording` → `client.SendRecording(new(Stopped, lastPath, 1.5f, 0, 2, ""))`

  Add a `FakeGame.FailRecording` flag that makes `StartRecording` reply `Failed` with `"no ffmpeg"`.

- [ ] **Step 2: Write the failing tests** (in `PlayServiceTests`, using `PlayHarness`)

```csharp
[Fact]
public void LaunchAlwaysPassesFrameCaptureBeforeTheEditorPort()
{
    using var h = new PlayHarness();
    var game = h.LaunchRunning("Game", scene: null, extra: ["--hidden", "++", "--host"]);
    Assert.Equal(["--hidden", "--frame-capture", "--editor-port", h.PortText, "++", "--host"], h.Launcher.LastArguments);
}

[Fact]
public void StartAndStopRecordingTrackStateAndRaiseFinished()
{
    using var h = new PlayHarness();
    var game = h.LaunchRunning("Game");
    var instance = h.Service.Instances.Single();
    var finished = new List<EditorLinkRecording>();
    h.Service.RecordingFinished += (_, r) => finished.Add(r);

    Assert.True(h.Service.StartRecording(instance, "/u/recordings/a.mp4"));
    h.Pump(() => instance.Recording == PlayRecordingState.Recording, "recording started");
    Assert.Equal("/u/recordings/a.mp4", instance.RecordingPath);
    Assert.True(h.Service.AnyRecording);

    Assert.True(h.Service.StopRecording(instance));
    Assert.Equal(PlayRecordingState.Saving, instance.Recording); // until the game reports Stopped
    h.Pump(() => finished.Count == 1, "recording finished");
    Assert.Equal(PlayRecordingState.None, instance.Recording);
    Assert.Equal(EditorLinkRecordingState.Stopped, finished[0].State);
}

[Fact]
public void AFailedStartIsReportedAsFinishedWithTheError()
{
    using var h = new PlayHarness();
    var game = h.LaunchRunning("Game");
    game.FailRecording = true;
    var instance = h.Service.Instances.Single();
    EditorLinkRecording? finished = null;
    h.Service.RecordingFinished += (_, r) => finished = r;

    h.Service.StartRecording(instance, "/u/a.mp4");
    h.Pump(() => finished is not null, "failure reported");

    Assert.Equal(EditorLinkRecordingState.Failed, finished!.Value.State);
    Assert.Equal("no ffmpeg", finished.Value.Error);
    Assert.Equal(PlayRecordingState.None, instance.Recording);
}
```

Adapt `PlayHarness` member names (`PortText`, `Launcher.LastArguments`, `LaunchRunning` overloads) to the real harness (`PlayServiceTests.cs:7-61`). Then update the existing argument-order tests (around lines 135, 361 and 378) to include `--frame-capture` just before `--editor-port`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~PlayServiceTests"`
Expected: a compile error, because `StartRecording` is not defined.

- [ ] **Step 4: Implement**
  - `PlayInstance`: add the enum and the three properties. Document the enum as "Saving: the game was asked to stop and is finalising".
  - `PlayService.Launch`: add `arguments.Add("--frame-capture");` immediately before `arguments.Add("--editor-port");`, with the comment `// recording can start mid-session (the swapchain needs TRANSFER_SRC)`.
  - Public API:

```csharp
    /// <summary>Asks <paramref name="instance"/> to start recording to <paramref name="path"/>.</summary>
    public bool StartRecording(PlayInstance instance, string path) =>
        !_disposed && instance.Recording == PlayRecordingState.None && SendTo(instance, new EditorLinkCommand(EditorCommandKind.StartRecording, path));

    /// <summary>Asks <paramref name="instance"/> to stop recording; <see cref="RecordingFinished"/> follows once it has saved.</summary>
    public bool StopRecording(PlayInstance instance)
    {
        if (_disposed || instance.Recording != PlayRecordingState.Recording || !SendTo(instance, new EditorLinkCommand(EditorCommandKind.StopRecording)))
            return false;
        instance.Recording = PlayRecordingState.Saving;
        _changed = true;
        return true;
    }

    public bool AnyRecording => _instances.Any(i => i.Recording != PlayRecordingState.None);

    /// <summary>A recording stopped (saved) or failed. Raised on the editor thread from <see cref="Update"/>.</summary>
    public event Action<PlayInstance, EditorLinkRecording>? RecordingFinished;
```

  - `DrainLink` case:

```csharp
                case EditorLinkMessageType.Recording:
                    if (_byGameId.TryGetValue(message.GameId, out var recorder))
                        OnRecording(recorder, message.Recording);
                    break;
```

```csharp
    private void OnRecording(PlayInstance instance, in EditorLinkRecording recording)
    {
        switch (recording.State)
        {
            case EditorLinkRecordingState.Started:
                instance.Recording = PlayRecordingState.Recording;
                instance.RecordingPath = recording.Path;
                instance.RecordingStartedUtc = DateTime.UtcNow;
                break;
            default:
                instance.Recording = PlayRecordingState.None;
                instance.RecordingStartedUtc = null;
                RecordingFinished?.Invoke(instance, recording);
                break;
        }

        _changed = true;
    }
```

  - Ticking status: in `Update()`, while `AnyRecording`, set `_changed = true` once per wall-clock second (track `_lastRecordingTick` as a whole-second counter) so the toolbar's `● REC m:ss` refreshes without a per-frame panel refresh.
  - When an instance exits or disconnects while recording, set `Recording = None`. If it was `Saving` or `Recording`, raise `RecordingFinished` with `Failed` and `"The game exited before the recording was saved."`. Do this only if no `Stopped`/`Failed` arrived, so the editor never waits forever.

- [ ] **Step 5: Run the tests**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Play"`, then `just build`.
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add MainframeEngine.Editor Tests/MainframeEngine.Editor.Tests
git commit -m "Track gameplay recording per play instance and launch games with frame capture"
```

---

### Task 4: `RecordingPaths`, `CutbackLocator` and the Cutback path setting

**Files:**
- Create: `MainframeEngine.Editor/Src/Play/RecordingPaths.cs`
- Create: `MainframeEngine.Editor/Src/Play/CutbackLocator.cs`
- Modify: `MainframeEngine.Editor/Src/Settings/EditorSettings.cs` (+ `EditorSettingsData`, `Load`, `Save`, `Clone`)
- Modify: `MainframeEngine.Editor/Content/Editor/editor_settings.rml` and `MainframeEngine.Editor/Src/UI/EditorSettingsDialog.cs`
- Test: `Tests/MainframeEngine.Editor.Tests/Play/RecordingPathsTests.cs`, `CutbackLocatorTests.cs`, and the existing settings round-trip test file (`ViewResolutionTests.cs` pattern)

**Interfaces:**
- Produces:
  - `public static class RecordingPaths`:
    - `string Directory(string userDataDirectory)` returns `<dir>/recordings`
    - `string NewPath(string userDataDirectory, string? scenePath, DateTime now, Func<string,bool> exists)`
  - `public static class CutbackLocator`:
    - `string? Find(string? settingPath, string? pathVariable, OsKind os, string home, string? localAppData, Func<string,bool> fileExists)`
    - `string? FindForThisMachine(string? settingPath)`
    - `const string ReleasesUrl = "https://github.com/brogan89/Cutback/releases"`
  - `public enum OsKind { MacOS, Windows, Linux }` (editor-local; do not reuse the engine's internal recorder enum)
  - `EditorSettings.CutbackPath : string` (default `""`)

- [ ] **Step 1: Write the failing tests**

```csharp
namespace MainframeEngine.Editor.Tests.Play;

public sealed class RecordingPathsTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 10, 15, 0);

    [Fact]
    public void NamesTheFileAfterTheSceneAndTime()
    {
        Assert.Equal(Path.Combine("/u", "recordings", "forest-20261013-101500.mp4"),
            RecordingPaths.NewPath("/u", "Content/Scenes/forest.mscene", Now, _ => false));
    }

    [Fact]
    public void FallsBackToGameWithoutAScene()
    {
        Assert.EndsWith("game-20261013-101500.mp4", RecordingPaths.NewPath("/u", null, Now, _ => false));
    }

    [Fact]
    public void AddsASuffixWhenTheNameIsTaken()
    {
        var taken = new HashSet<string> { Path.Combine("/u", "recordings", "game-20261013-101500.mp4") };
        Assert.EndsWith("game-20261013-101500-2.mp4", RecordingPaths.NewPath("/u", null, Now, taken.Contains));
    }

    [Fact]
    public void UnsafeSceneNameCharactersAreReplaced()
    {
        Assert.EndsWith("my_scene-20261013-101500.mp4", RecordingPaths.NewPath("/u", "Content/my scene.mscene", Now, _ => false));
    }
}
```

```csharp
namespace MainframeEngine.Editor.Tests.Play;

public sealed class CutbackLocatorTests
{
    [Fact]
    public void TheSettingWinsWhenItExists()
    {
        Assert.Equal("/opt/cb", CutbackLocator.Find("/opt/cb", "/usr/bin", OsKind.Linux, "/home/u", null, p => p is "/opt/cb" or "/usr/bin/cutback"));
    }

    [Fact]
    public void AMissingSettingFallsThroughToPath()
    {
        Assert.Equal("/usr/bin/cutback", CutbackLocator.Find("/nope", "/usr/bin", OsKind.Linux, "/home/u", null, p => p == "/usr/bin/cutback"));
    }

    [Fact]
    public void MacFindsTheAppBundle()
    {
        Assert.Equal("/Applications/Cutback.app/Contents/MacOS/Cutback",
            CutbackLocator.Find(null, "", OsKind.MacOS, "/Users/u", null, p => p == "/Applications/Cutback.app/Contents/MacOS/Cutback"));
    }

    [Fact]
    public void WindowsFindsTheLocalProgramsInstall()
    {
        Assert.Equal(@"C:\Users\u\AppData\Local\Programs\Cutback\Cutback.exe",
            CutbackLocator.Find(null, "", OsKind.Windows, @"C:\Users\u", @"C:\Users\u\AppData\Local", p => p == @"C:\Users\u\AppData\Local\Programs\Cutback\Cutback.exe"));
    }

    [Fact]
    public void LinuxFindsTheUserBin()
    {
        Assert.Equal("/home/u/.local/bin/cutback", CutbackLocator.Find(null, "", OsKind.Linux, "/home/u", null, p => p == "/home/u/.local/bin/cutback"));
    }

    [Fact]
    public void NothingFoundIsNull()
    {
        Assert.Null(CutbackLocator.Find(null, "/usr/bin", OsKind.Linux, "/home/u", null, _ => false));
    }
}
```

Settings: add a test in the existing `EditorSettings` round-trip style. It sets `CutbackPath = "/opt/cb"`, saves to a temp file, loads it back and checks the value, and checks that `Clone()` copies it.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~RecordingPathsTests|FullyQualifiedName~CutbackLocatorTests|FullyQualifiedName~ViewResolutionTests"`
Expected: compile errors.

- [ ] **Step 3: Implement**

`RecordingPaths.cs`:

```csharp
using System.Globalization;

namespace MainframeEngine.Editor;

/// <summary>Where the editor's Record button saves: <c>{project user data}/recordings/{scene}-{yyyyMMdd-HHmmss}.mp4</c>.</summary>
public static class RecordingPaths
{
    public static string Directory(string userDataDirectory) => Path.Combine(userDataDirectory, "recordings");

    public static string NewPath(string userDataDirectory, string? scenePath, DateTime now, Func<string, bool> exists)
    {
        var label = string.IsNullOrEmpty(scenePath) ? "game" : Safe(Path.GetFileNameWithoutExtension(scenePath));
        var stem = $"{label}-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        var path = Path.Combine(Directory(userDataDirectory), stem + ".mp4");
        for (var n = 2; exists(path); n++)
            path = Path.Combine(Directory(userDataDirectory), $"{stem}-{n.ToString(CultureInfo.InvariantCulture)}.mp4");
        return path;
    }

    private static string Safe(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return chars.Length == 0 ? "game" : new string(chars);
    }
}
```

`CutbackLocator.cs`:

```csharp
namespace MainframeEngine.Editor;

public enum OsKind
{
    MacOS,
    Windows,
    Linux,
}

/// <summary>
/// Finds the Cutback app (the video trimmer that reads Mainframe recordings): the Editor Settings path,
/// then PATH, then the usual install location per OS. Null when it is not installed.
/// </summary>
public static class CutbackLocator
{
    public const string ReleasesUrl = "https://github.com/brogan89/Cutback/releases";

    public static string? FindForThisMachine(string? settingPath) => Find(
        settingPath, Environment.GetEnvironmentVariable("PATH"),
        OperatingSystem.IsWindows() ? OsKind.Windows : OperatingSystem.IsMacOS() ? OsKind.MacOS : OsKind.Linux,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        File.Exists);

    public static string? Find(string? settingPath, string? pathVariable, OsKind os, string home, string? localAppData, Func<string, bool> fileExists)
    {
        if (!string.IsNullOrWhiteSpace(settingPath) && fileExists(settingPath))
            return settingPath;

        var separator = os == OsKind.Windows ? ';' : ':';
        var join = os == OsKind.Windows ? '\\' : '/';
        string[] names = os == OsKind.Windows ? ["Cutback.exe", "cutback.exe"] : ["cutback", "Cutback"];
        foreach (var folder in (pathVariable ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                var candidate = folder.Trim('"').TrimEnd('/', '\\') + join + name;
                if (fileExists(candidate))
                    return candidate;
            }
        }

        string? known = os switch
        {
            OsKind.MacOS => "/Applications/Cutback.app/Contents/MacOS/Cutback",
            OsKind.Windows when localAppData is not null => localAppData.TrimEnd('\\') + @"\Programs\Cutback\Cutback.exe",
            OsKind.Linux => home.TrimEnd('/') + "/.local/bin/cutback",
            _ => null,
        };
        return known is not null && fileExists(known) ? known : null;
    }
}
```

`EditorSettings`: add `CutbackPath` in the five places, following `CodeEditorCommand`. That covers the property, `EditorSettingsData` (nullable), `Load` (`?? ""`), `Save` and `Clone`.

`editor_settings.rml`: add a row after the code-editor row:

```html
<div class="dialog-row"><span class="caption">Cutback</span>
    <input type="text" class="text mono" id="es-cutback" data-value="cutback" placeholder="Found automatically"/></div>
<div class="hint">The Cutback app that trims recordings (Run › Play and Record). Leave empty to find it on PATH or in the usual install folder.</div>
```

`EditorSettingsDialog.OnReady`:

```csharp
.Bind("cutback", this, static d => d._working.CutbackPath, static (d, v) => d._working.CutbackPath = v ?? "")
```

- [ ] **Step 4: Run the tests and build**

Run: the same filter as Step 2, then `just build`.
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine.Editor Tests/MainframeEngine.Editor.Tests
git commit -m "Name recordings, find the Cutback app and add its path to Editor Settings"
```

---

### Task 5: Play and Record — command, F10, Run menu, toolbar button and REC status

**Files:**
- Modify: `MainframeEngine.Editor/Src/Play/PlayController.cs`
- Modify: `MainframeEngine.Editor/Src/EditorCommands.cs` (Run switch, Run menu, help text ~827–837)
- Modify: `MainframeEngine.Editor/Src/EditorWorkspace.Projects.cs` (`ProjectShortcutFor`: F10 maps to `play.record`)
- Modify: `MainframeEngine.Editor/Content/Editor/toolbar.rml` (button and styles)
- Modify: `MainframeEngine.Editor/Src/UI/Panels.cs` (`RefreshPlay`)
- Test: `Tests/MainframeEngine.Editor.Tests/PlayShortcutTests.cs` and `Tests/MainframeEngine.Editor.Tests/Play/PlayRecordingControllerTests.cs`

**Interfaces:**
- Consumes: Task 3 (`StartRecording`/`StopRecording`/`AnyRecording`, `PlayInstance.Recording`/`RecordingStartedUtc`); Task 4 (`RecordingPaths`).
- Produces:
  - `PlayController.PlayAndRecord()`
  - `PlayController.ToggleRecording()`, which plays and records when no game is running, starts on the first running instance when none is recording, and stops the recording instance otherwise
  - `PlayController.RecordingLabel`, returning "Play and Record", "Start Recording" or "Stop Recording"
  - static `PlayController.RecordStatus(PlayRecordingState, DateTime? startedUtc, DateTime nowUtc) : string`

- [ ] **Step 1: Write the failing tests**

```csharp
// PlayShortcutTests: add
[InlineData(Key.F10, EditorModifiers.None, "play.record")]
```

```csharp
namespace MainframeEngine.Editor.Tests.Play;

public sealed class PlayRecordingControllerTests
{
    [Theory]
    [InlineData(PlayRecordingState.None, 0, "")]
    [InlineData(PlayRecordingState.Recording, 83, "● REC 1:23")]
    [InlineData(PlayRecordingState.Recording, 3725, "● REC 62:05")]
    [InlineData(PlayRecordingState.Saving, 10, "Saving recording…")]
    public void RecordStatusFormatsMinutesAndSeconds(PlayRecordingState state, int seconds, string expected)
    {
        var start = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, PlayController.RecordStatus(state, start, start.AddSeconds(seconds)));
    }
}
```

Then add `PlayHarness`-level tests in `PlayServiceTests` (or a new file using the same harness). If `PlayController` can't be built around `PlayHarness`, find how existing controller-level tests build an `EditorWorkspace` (`SessionFixture.cs`) and use that.
- `PlayAndRecordLaunchesWithRecordArgs`: after `PlayAndRecord()`, the launcher's arguments contain `--record`, followed by a path under `<userdata>/recordings/` ending in `.mp4`, all before `++`.
- `ToggleRecordingTargetsTheFirstRunningInstance`: with two instances running, the first toggle sends StartRecording only to the first. The second toggle sends StopRecording to that same instance.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~PlayShortcutTests|FullyQualifiedName~PlayRecording"`
Expected: FAIL or compile error.

- [ ] **Step 3: Implement**
  - **`PlayController`:**
    - `PlayAndRecord()` computes `RecordingPaths.NewPath(_workspace.Project.UserDataDirectory!, scene: null, DateTime.Now, File.Exists)`. If the project has no user data directory, it shows a `MessageRequest` "Open a project first" and returns. It creates the recordings directory, then calls `PlayMain(["--record", path])`.
    - `ToggleRecording()`:
      - not playing → `PlayAndRecord()`;
      - else, if any instance's `Recording == Recording` → `Service.StopRecording(that)`;
      - else → `Service.StartRecording(firstRunning, RecordingPaths.NewPath(..., firstRunning.CurrentScene, ...))`.
    - `RecordingLabel` reads `!IsPlaying ? "Play and Record" : Service.AnyRecording ? "Stop Recording" : "Start Recording"`.
    - `RecordStatus(...)` is pure: `Recording` → `$"● REC {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}"`, `Saving` → `"Saving recording…"`, otherwise `""`.
  - **`EditorCommands.Run`:** add `case "play.record": _workspace.Play.ToggleRecording(); return true;`.
  - **Run menu:** add after "Play Scene":

```csharp
new MenuItem(_workspace.Play.RecordingLabel, "play.record", "F10", _workspace.Project.DesktopProject is not null && !_workspace.Play.IsBuilding, Icon: "point-filled"),
```

  - **Help text:** add `· F10 play and record` to the play-keys line.
  - **`ProjectShortcutFor`:** `Key.F10 when !shift => "play.record"`. Place it with the F5–F8 arms; no Cmd/Alt variant.
  - **`toolbar.rml`:** after `#play-scene`, add:

```html
<button id="record" class="tool-button" data-command="play.record" data-tooltip="Play and Record (F10) — record gameplay for promo footage; press again to stop"><span class="icon icon-point-filled"></span></button>
```

    and in the inline styles:

```css
#record .icon { image-color: #f87171; }
#record.disabled .icon { image-color: #4b5263; }
#record.recording { background-color: #3f1d1d; border-color: #ef4444; }
#record.recording .icon { image-color: #ffffff; }
```

  - **`Panels.RefreshPlay`:** set `#record` disabled when `!canPlay && !play.IsPlaying`, and `recording` when `play.Service.AnyRecording`. The status becomes the build text when building, otherwise `RecordStatus` of the first recording instance (or `""`). Keep the existing change-only `SetText`.
  - **Tooltip:** when recording, change it to "Stop Recording (F10)", only when the text changes.

- [ ] **Step 4: Run the tests and build**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests`, then `just build`.
Expected: all PASS. One existing test checks that every icon the editor references exists in the atlas; `point-filled` is already listed, so it passes.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine.Editor Tests/MainframeEngine.Editor.Tests
git commit -m "Add Play and Record (F10) with a toolbar record button and REC status"
```

---

### Task 6: Recording finished dialog — Open in Cutback, Show in Folder

**Files:**
- Create: `MainframeEngine.Editor/Src/Play/RecordingNotifier.cs`
- Modify: `MainframeEngine.Editor/Src/EditorWorkspace.cs` (create the notifier and wire it to `Play.Service.RecordingFinished`; add `Options.LaunchCutback` and reuse `Options.OpenFolder` / a new `Options.Reveal` as test seams)
- Test: `Tests/MainframeEngine.Editor.Tests/Play/RecordingNotifierTests.cs`

**Interfaces:**
- Consumes:
  - `PlayService.RecordingFinished(PlayInstance, EditorLinkRecording)`
  - `CutbackLocator`
  - `EditorSettings.CutbackPath`
  - `MessageDialog` through `_workspace.Message.Show(MessageRequest)`
  - `OsShell.Reveal(path)`, `OsShell.OpenUrl(Uri)`
- Produces: `internal sealed class RecordingNotifier`. Its constructor is `(Action<MessageRequest> show, Func<string?> cutbackSetting, Func<string?, string?> findCutback, Func<string, string, bool> launchCutback, Action<string> reveal, Action<Uri> openUrl)` and it has `void OnFinished(EditorLinkRecording recording)`. Pure seams, so it is testable without RmlUi.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace MainframeEngine.Editor.Tests.Play;

public sealed class RecordingNotifierTests
{
    private sealed class Harness
    {
        public readonly List<MessageRequest> Shown = [];
        public readonly List<(string App, string File)> Launched = [];
        public readonly List<string> Revealed = [];
        public readonly List<Uri> Opened = [];
        public string? Cutback = "/Applications/Cutback.app/Contents/MacOS/Cutback";

        public RecordingNotifier Create() => new(Shown.Add, () => null, _ => Cutback,
            (app, file) => { Launched.Add((app, file)); return true; }, Revealed.Add, Opened.Add);
    }

    private static EditorLinkRecording Saved(string path = "/u/recordings/a.mp4") =>
        new(EditorLinkRecordingState.Stopped, path, 83.5f, 0, 2, "");

    [Fact]
    public void ASavedRecordingOffersCutbackAndTheFolder()
    {
        var h = new Harness();
        h.Create().OnFinished(Saved());

        var dialog = Assert.Single(h.Shown);
        Assert.Equal("Recording saved", dialog.Title);
        Assert.Equal(["Open in Cutback", "Show in Folder", "Close"], dialog.Buttons);
        Assert.Contains("a.mp4", dialog.Message);
        Assert.Contains("1:23", dialog.Message);
    }

    [Fact]
    public void OpenInCutbackLaunchesItWithTheRecording()
    {
        var h = new Harness();
        h.Create().OnFinished(Saved());

        h.Shown[0].Callback!(0, "");

        Assert.Equal([("/Applications/Cutback.app/Contents/MacOS/Cutback", "/u/recordings/a.mp4")], h.Launched);
    }

    [Fact]
    public void ShowInFolderRevealsTheFile()
    {
        var h = new Harness();
        h.Create().OnFinished(Saved());

        h.Shown[0].Callback!(1, "");

        Assert.Equal(["/u/recordings/a.mp4"], h.Revealed);
    }

    [Fact]
    public void MissingCutbackExplainsWhereToGetIt()
    {
        var h = new Harness { Cutback = null };
        h.Create().OnFinished(Saved());

        h.Shown[0].Callback!(0, "");

        var help = h.Shown[1];
        Assert.Equal("Cutback is not installed", help.Title);
        Assert.Contains("Editor Settings", help.Message);
        help.Callback!(0, ""); // "Download"
        Assert.Equal([new Uri(CutbackLocator.ReleasesUrl)], h.Opened);
    }

    [Fact]
    public void AFailedRecordingShowsTheError()
    {
        var h = new Harness();
        h.Create().OnFinished(new EditorLinkRecording(EditorLinkRecordingState.Failed, "/u/a.mp4", 0, 0, 0,
            "The encoder failed. The partial recording is in /u/a.mp4.partial."));

        var dialog = Assert.Single(h.Shown);
        Assert.Equal("Recording failed", dialog.Title);
        Assert.Contains("encoder failed", dialog.Message);
        Assert.Equal(["Show in Folder", "Close"], dialog.Buttons);
    }

    [Fact]
    public void TwoFinishedRecordingsQueueTwoDialogs()
    {
        var h = new Harness();
        var notifier = h.Create();
        notifier.OnFinished(Saved("/u/a.mp4"));
        notifier.OnFinished(Saved("/u/b.mp4"));
        Assert.Equal(2, h.Shown.Count); // MessageDialog itself queues requests that arrive while one is open
    }
}
```

`AFailedRecordingShowsTheError` expects "Show in Folder" only when the message mentions a `.partial` path. Implement it that way: offer the button when the error contains ".partial", and reveal the `.partial` directory. Add one more test with an error that has no `.partial` path, expecting just `["Close"]`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~RecordingNotifierTests"`
Expected: a compile error.

- [ ] **Step 3: Implement** (`RecordingNotifier.cs`)

```csharp
using System.Globalization;

namespace MainframeEngine.Editor;

/// <summary>
/// Tells the user a gameplay recording finished and offers the next step: open it in Cutback (the trimmer that
/// reads the .mfcap.json sidecar) or show it in the file browser. Failures show the recorder's own message.
/// </summary>
internal sealed class RecordingNotifier(
    Action<MessageRequest> show,
    Func<string?> cutbackSetting,
    Func<string?, string?> findCutback,
    Func<string, string, bool> launchCutback,
    Action<string> reveal,
    Action<Uri> openUrl)
{
    public void OnFinished(EditorLinkRecording recording)
    {
        if (recording.State == EditorLinkRecordingState.Failed)
        {
            ShowFailure(recording);
            return;
        }

        var path = recording.Path;
        var elapsed = TimeSpan.FromSeconds(recording.ElapsedSeconds);
        show(new MessageRequest
        {
            Title = "Recording saved",
            Message = string.Create(CultureInfo.InvariantCulture,
                $"{Path.GetFileName(path)} ({(int)elapsed.TotalMinutes}:{elapsed.Seconds:00})\n{Path.GetDirectoryName(path)}"),
            Buttons = ["Open in Cutback", "Show in Folder", "Close"],
            DefaultButton = 0,
            CancelButton = 2,
            Callback = (button, _) =>
            {
                if (button == 0)
                    OpenInCutback(path);
                else if (button == 1)
                    reveal(path);
            },
        });
    }

    private void OpenInCutback(string path)
    {
        if (findCutback(cutbackSetting()) is { } app && launchCutback(app, path))
            return;

        show(new MessageRequest
        {
            Title = "Cutback is not installed",
            Message = "Cutback trims recordings using what the game recorded. Download it, or set its path in Editor Settings.",
            Buttons = ["Download", "Close"],
            DefaultButton = 0,
            CancelButton = 1,
            Callback = (button, _) =>
            {
                if (button == 0)
                    openUrl(new Uri(CutbackLocator.ReleasesUrl));
            },
        });
    }

    private void ShowFailure(EditorLinkRecording recording)
    {
        var partial = recording.Path + ".partial";
        var hasPartial = recording.Error.Contains(".partial", StringComparison.Ordinal);
        show(new MessageRequest
        {
            Title = "Recording failed",
            Message = recording.Error.Length > 0 ? recording.Error : "The recording could not be saved.",
            Buttons = hasPartial ? ["Show in Folder", "Close"] : ["Close"],
            DefaultButton = 0,
            CancelButton = hasPartial ? 1 : 0,
            Callback = (button, _) =>
            {
                if (hasPartial && button == 0)
                    reveal(partial);
            },
        });
    }
}
```

`EditorWorkspace` wiring, where `Play` is created:

```csharp
var notifier = new RecordingNotifier(
    Message.Show,
    () => Settings.CutbackPath,
    CutbackLocator.FindForThisMachine,
    Options.LaunchCutback ?? LaunchCutback,
    Options.Reveal ?? OsShell.Reveal,
    OsShell.OpenUrl);
Play.Service.RecordingFinished += (_, recording) => notifier.OnFinished(recording);
```

```csharp
/// <summary>Starts Cutback on <paramref name="file"/> as a separate process (it is a GPL app; the editor never links it).</summary>
private static bool LaunchCutback(string app, string file)
{
    try
    {
        var info = ProcessRunner.CreateStartInfo(app, [file], redirectOutput: false);
        using var process = Process.Start(info);
        return process is not null;
    }
    catch (Exception e) when (e is Win32Exception or InvalidOperationException)
    {
        Log.Warning($"[Recorder] Could not start Cutback ({app}): {e.Message}");
        return false;
    }
}
```

Add `public Func<string, string, bool>? LaunchCutback { get; init; }` and `public Action<string>? Reveal { get; init; }` to `EditorWorkspace.Options`, beside `OpenFolder`, following its style.

- [ ] **Step 4: Run the tests and build**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests`, then `just build`.
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine.Editor Tests/MainframeEngine.Editor.Tests
git commit -m "Offer Open in Cutback and Show in Folder when a recording finishes"
```

---

### Task 7: Docs, spec amendments and a QA script

**Files:**
- Modify: `docs/design/recording.md` (new section "Recording from the editor")
- Modify: `docs/design/editor.md` (shortcuts table and keys list: F10)
- Modify: the editor-link protocol section wherever it is documented (grep `RequestTree` in `docs/design/`)
- Modify: `memory/context/progress-log.md` ("Resume here" entry for `feature/editor-recording`)
- Create: `Tests/QA/record.qa`
- Modify: `/Users/brogan/dev/SimpleVideoEditor/docs/superpowers/specs/2026-10-12-mainframe-engine-integration-design.md` (Phase 3 amendments; separate commit in that repo, which stays on its current branch)

- [ ] **Step 1: Engine docs.**
  - `recording.md`: describe the editor flow (F10 / Run › Play and Record / `#record`), the protocol v4 messages, `--frame-capture` on every editor launch, the recordings folder, `RecordingNotifier` and `CutbackLocator` order. Remove the Phase 1 known issue "StartRecording mid-session needs --frame-capture" or rewrite it as "the editor always passes it".
  - `editor.md`: add F10 to the shortcut table and keys list.
  - Protocol doc: add the v4 commands and message.
  - Progress log: branch, worktree, stacked on #62, gates, what's next (Phase 4).

- [ ] **Step 2: QA script** (`Tests/QA/record.qa`), modelled on `Tests/QA/project-workflow.qa`. It opens the Demo project, runs `command play.record`, `wait-for playing 120`, `wait 3`, then `command play.record` again (stops). It waits for the save, then captures. This is an end-to-end script for a person (or the controller) to run with a display and ffmpeg. It is not run in CI.

- [ ] **Step 3: Spec amendments** (Cutback repo, spec file only). In "Phase 3 — Editor hooks (engine)", record the four amendments from this plan's header, set the shortcut to F10 and resolve open question 4. Add the `RecordingNotifier` dialog wording and the `--frame-capture` launch flag.

- [ ] **Step 4: Gates and commits**

Run: `just build && just test && just format-check`.

```bash
git add docs memory Tests/QA/record.qa
git commit -m "Document recording from the editor"
git -C /Users/brogan/dev/SimpleVideoEditor add docs/superpowers/specs/2026-10-12-mainframe-engine-integration-design.md
git -C /Users/brogan/dev/SimpleVideoEditor commit -m "Spec: record Phase 3 implementation amendments"
```
