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
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "durationSeconds": 5 }""")]
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "mode": "sideways", "durationSeconds": 5 }""")]
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "mode": "live", "durationSeconds": 0 }""")]
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "mode": "live", "durationSeconds": 5, "input": [ { "start": 3, "end": 2 } ] }""")]
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "mode": "live", "durationSeconds": 5, "markers": [ { "time": -1, "label": "x" } ] }""")]
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "mode": "live", "durationSeconds": 5, "input": [ null ] }""")]
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "mode": "live", "durationSeconds": 5, "markers": [ null ] }""")]
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
