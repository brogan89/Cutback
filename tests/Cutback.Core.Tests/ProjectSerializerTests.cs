using System.Text.Json;
using System.Text.Json.Nodes;
using Cutback.Core.Models;
using Cutback.Core.Projects;

namespace Cutback.Core.Tests;

public sealed class ProjectSerializerTests
{
    private static readonly SourceInfo Source = new(
        Path: "/abs/path/to/recording.mp4",
        Sha256: "0123abcd",
        DurationSeconds: 12.04,
        Width: 1920,
        Height: 1080,
        FrameRate: 30.0);

    private static CutbackProject SampleProject()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 3.21, enabled: true, SegmentOrigin.Auto),
            Segment.Create(3.21, 4.86, enabled: false, SegmentOrigin.Auto, "silence 1.65s"),
            Segment.Create(4.86, 12.04, enabled: true, SegmentOrigin.Manual),
        };
        var transcript = new[] { new Word("hello", 0.5, 0.9, 0.97) };
        var settings = new DetectionSettings(PaddingMs: 80, MinSilenceMs: 500, SilenceThresholdDb: -30.0, MinKeepMs: 150);
        return new CutbackProject(Source, segments, transcript, settings);
    }

    // ---- model --------------------------------------------------------------------------------

    [Fact]
    public void CreateNew_has_one_enabled_segment_and_default_settings()
    {
        var project = CutbackProject.CreateNew(Source);

        project.Source.Should().Be(Source);
        project.Segments.Should().ContainSingle();
        project.Segments[0].Start.Should().Be(0.0);
        project.Segments[0].End.Should().Be(Source.DurationSeconds);
        project.Segments[0].Enabled.Should().BeTrue();
        project.Transcript.Should().BeEmpty();
        project.Settings.Should().Be(DetectionSettings.Default);
    }

    [Fact]
    public void Default_settings_match_the_documented_schema()
    {
        DetectionSettings.Default.Should().Be(new DetectionSettings(
            PaddingMs: 60,
            MinSilenceMs: 400,
            SilenceThresholdDb: -34.0,
            MinKeepMs: 120));
    }

    [Fact]
    public void Constructing_a_project_with_an_invalid_partition_throws()
    {
        var bad = new[] { Segment.Create(0.0, 5.0, enabled: true, SegmentOrigin.Auto) }; // ends before 12.04

        var act = () => new CutbackProject(Source, bad, [], DetectionSettings.Default);

        act.Should().Throw<InvalidPartitionException>();
    }

    // ---- serialisation shape ------------------------------------------------------------------

    [Fact]
    public void Serialize_writes_the_current_version_and_camel_case_keys()
    {
        var json = ProjectSerializer.Serialize(SampleProject());

        var root = JsonNode.Parse(json)!.AsObject();
        root["version"]!.GetValue<int>().Should().Be(ProjectSerializer.CurrentVersion);
        root.Should().ContainKeys("source", "segments", "transcript", "settings");
        root["source"]!.AsObject().Should().ContainKeys("path", "sha256", "durationSeconds", "width", "height", "frameRate");
        root["settings"]!.AsObject().Should().ContainKeys("paddingMs", "minSilenceMs", "silenceThresholdDb", "minKeepMs");
    }

    [Fact]
    public void Serialize_writes_origin_as_a_lower_case_string()
    {
        var json = ProjectSerializer.Serialize(SampleProject());

        var segments = JsonNode.Parse(json)!["segments"]!.AsArray();
        segments[0]!["origin"]!.GetValue<string>().Should().Be("auto");
        segments[2]!["origin"]!.GetValue<string>().Should().Be("manual");
    }

    [Fact]
    public void Serialize_writes_a_null_reason_explicitly()
    {
        var json = ProjectSerializer.Serialize(SampleProject());

        var first = JsonNode.Parse(json)!["segments"]![0]!.AsObject();
        first.ContainsKey("reason").Should().BeTrue();
        first["reason"].Should().BeNull();
    }

    [Fact]
    public void Serialize_writes_only_schema_fields_for_a_segment()
    {
        var json = ProjectSerializer.Serialize(SampleProject());

        var first = JsonNode.Parse(json)!["segments"]![0]!.AsObject();
        first.Select(kv => kv.Key).Should().BeEquivalentTo("id", "start", "end", "enabled", "origin", "reason");
    }

    [Fact]
    public void Serialize_is_indented_for_diffability()
    {
        var json = ProjectSerializer.Serialize(SampleProject());

        json.Should().Contain("\n  \"version\"");
    }

    // ---- round trip ---------------------------------------------------------------------------

    [Fact]
    public void Round_trip_preserves_everything()
    {
        var original = SampleProject();

        var restored = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(original));

        restored.Source.Should().Be(original.Source);
        restored.Segments.Should().BeEquivalentTo(original.Segments, o => o.WithStrictOrdering());
        restored.Transcript.Should().BeEquivalentTo(original.Transcript, o => o.WithStrictOrdering());
        restored.Settings.Should().Be(original.Settings);
    }

    [Fact]
    public void Round_trip_of_a_single_segment_project_works()
    {
        var original = CutbackProject.CreateNew(Source);

        var restored = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(original));

        restored.Segments.Should().BeEquivalentTo(original.Segments);
    }

    [Fact]
    public async Task Save_and_load_round_trip_through_a_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cutback-test-{Guid.NewGuid():N}.cutback");
        try
        {
            var original = SampleProject();

            await ProjectSerializer.SaveAsync(original, path, CancellationToken.None);
            var restored = await ProjectSerializer.LoadAsync(path, CancellationToken.None);

            restored.Segments.Should().BeEquivalentTo(original.Segments, o => o.WithStrictOrdering());
            restored.Source.Should().Be(original.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Deserialize_accepts_the_schema_example_from_CLAUDE_md()
    {
        const string json = """
            {
              "version": 1,
              "source": {
                "path": "/abs/path/to/recording.mp4",
                "sha256": "abc",
                "durationSeconds": 12.04,
                "width": 1920, "height": 1080, "frameRate": 30.0
              },
              "segments": [
                { "id": "a", "start": 0.0,  "end": 3.21,  "enabled": true,  "origin": "auto",   "reason": null },
                { "id": "b", "start": 3.21, "end": 4.86,  "enabled": false, "origin": "auto",   "reason": "silence 1.65s" },
                { "id": "c", "start": 4.86, "end": 12.04, "enabled": true,  "origin": "manual", "reason": null }
              ],
              "transcript": [],
              "settings": {
                "paddingMs": 60,
                "minSilenceMs": 400,
                "silenceThresholdDb": -34.0,
                "minKeepMs": 120
              }
            }
            """;

        var project = ProjectSerializer.Deserialize(json);

        project.Segments.Should().HaveCount(3);
        project.Segments[1].Enabled.Should().BeFalse();
        project.Segments[1].Reason.Should().Be("silence 1.65s");
        project.Segments[2].Origin.Should().Be(SegmentOrigin.Manual);
        project.Settings.Should().Be(DetectionSettings.Default);
    }

    // ---- error handling -----------------------------------------------------------------------

    [Fact]
    public void Deserialize_rejects_a_file_from_a_newer_version()
    {
        var json = ProjectSerializer.Serialize(SampleProject())
            .Replace($"\"version\": {ProjectSerializer.CurrentVersion}", $"\"version\": {ProjectSerializer.CurrentVersion + 1}", StringComparison.Ordinal);

        var act = () => ProjectSerializer.Deserialize(json);

        act.Should().Throw<ProjectFormatException>().WithMessage("*newer*");
    }

    [Fact]
    public void Deserialize_rejects_a_missing_version()
    {
        var root = JsonNode.Parse(ProjectSerializer.Serialize(SampleProject()))!.AsObject();
        root.Remove("version");

        var act = () => ProjectSerializer.Deserialize(root.ToJsonString());

        act.Should().Throw<ProjectFormatException>().WithMessage("*version*");
    }

    [Fact]
    public void Deserialize_rejects_non_object_json()
    {
        var act = () => ProjectSerializer.Deserialize("[1, 2, 3]");

        act.Should().Throw<ProjectFormatException>();
    }

    [Fact]
    public void Deserialize_wraps_malformed_json_in_a_ProjectFormatException()
    {
        var act = () => ProjectSerializer.Deserialize("{ not json");

        act.Should().Throw<ProjectFormatException>().WithInnerException<JsonException>();
    }

    [Fact]
    public void Deserialize_surfaces_an_invalid_partition()
    {
        var root = JsonNode.Parse(ProjectSerializer.Serialize(SampleProject()))!.AsObject();
        root["segments"]!.AsArray().RemoveAt(1); // leaves a gap between 3.21 and 4.86

        var act = () => ProjectSerializer.Deserialize(root.ToJsonString());

        act.Should().Throw<InvalidPartitionException>();
    }

    [Fact]
    public void Deserialize_rejects_an_unknown_origin()
    {
        var root = JsonNode.Parse(ProjectSerializer.Serialize(SampleProject()))!.AsObject();
        root["segments"]![0]!["origin"] = "robot";

        var act = () => ProjectSerializer.Deserialize(root.ToJsonString());

        act.Should().Throw<ProjectFormatException>();
    }

    // ---- migration hook -----------------------------------------------------------------------

    [Fact]
    public void Migrator_applies_steps_in_order_and_bumps_the_version()
    {
        var log = new List<string>();
        var migrator = new ProjectMigrator(targetVersion: 3, steps: new Dictionary<int, Action<JsonObject>>
        {
            [1] = doc => { log.Add("1->2"); doc["fromOne"] = true; },
            [2] = doc => { log.Add("2->3"); doc["fromTwo"] = true; },
        });
        var doc = new JsonObject { ["version"] = 1 };

        migrator.Migrate(doc);

        log.Should().Equal("1->2", "2->3");
        doc["version"]!.GetValue<int>().Should().Be(3);
        doc["fromOne"]!.GetValue<bool>().Should().BeTrue();
        doc["fromTwo"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Migrator_leaves_a_current_document_untouched()
    {
        var called = false;
        var migrator = new ProjectMigrator(targetVersion: 2, steps: new Dictionary<int, Action<JsonObject>>
        {
            [1] = _ => called = true,
        });
        var doc = new JsonObject { ["version"] = 2 };

        migrator.Migrate(doc);

        called.Should().BeFalse();
        doc["version"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void Migrator_throws_when_a_step_is_missing()
    {
        var migrator = new ProjectMigrator(targetVersion: 3, steps: new Dictionary<int, Action<JsonObject>>
        {
            [1] = _ => { },
            // no step for 2 -> 3
        });
        var doc = new JsonObject { ["version"] = 1 };

        var act = () => migrator.Migrate(doc);

        act.Should().Throw<ProjectFormatException>().WithMessage("*2*3*");
    }

    [Fact]
    public void Migrator_rejects_a_version_below_one()
    {
        var migrator = new ProjectMigrator(targetVersion: 1, steps: new Dictionary<int, Action<JsonObject>>());
        var doc = new JsonObject { ["version"] = 0 };

        var act = () => migrator.Migrate(doc);

        act.Should().Throw<ProjectFormatException>();
    }

    [Fact]
    public void Default_migrator_applies_no_steps_to_a_current_version_document()
    {
        var doc = new JsonObject { ["version"] = ProjectSerializer.CurrentVersion };

        ProjectMigrator.Default.Migrate(doc);

        doc["version"]!.GetValue<int>().Should().Be(ProjectSerializer.CurrentVersion);
    }

    // ---- word anchors -------------------------------------------------------------------------

    [Fact]
    public void A_word_anchor_round_trips_and_is_null_when_absent()
    {
        var words = new[] { new Word("um", 1.0, 1.3, 0.9, Anchor: 1.12), new Word("so", 1.4, 1.6, 0.9) };
        var project = new CutbackProject(Source, [Segment.Create(0.0, 12.04, enabled: true, SegmentOrigin.Auto)], words, DetectionSettings.Default);

        var json = ProjectSerializer.Serialize(project);
        var loaded = ProjectSerializer.Deserialize(json);

        JsonNode.Parse(json)!["transcript"]![0]!["anchor"]!.GetValue<double>().Should().Be(1.12);
        loaded.Transcript[0].Anchor.Should().Be(1.12);
        loaded.Transcript[1].Anchor.Should().BeNull();
    }

    [Fact]
    public void A_transcript_word_without_an_anchor_field_loads_with_no_anchor()
    {
        const string json = """
            {
              "version": 2,
              "source": { "path": "/abs/path/to/recording.mp4", "sha256": "0123abcd", "durationSeconds": 12.04, "width": 1920, "height": 1080, "frameRate": 30.0 },
              "segments": [ { "id": "a", "start": 0.0, "end": 12.04, "enabled": true, "origin": "auto", "reason": null } ],
              "transcript": [ { "text": "um", "start": 1.0, "end": 1.3, "confidence": 0.9 } ],
              "settings": { "paddingMs": 60, "minSilenceMs": 400, "silenceThresholdDb": -34.0, "minKeepMs": 120 }
            }
            """;

        var project = ProjectSerializer.Deserialize(json);

        project.Transcript.Should().ContainSingle().Which.Anchor.Should().BeNull();
    }

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
}
