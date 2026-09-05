using System.Text.Json.Serialization;
using Cutback.Core.Models;

namespace Cutback.Core.Projects;

/// <summary>
/// The on-disk shape of a <c>.cutback</c> file. Kept separate from <see cref="CutbackProject"/> so
/// the schema version is a persistence concern, not a model concern.
/// </summary>
internal sealed record ProjectFile(
    int Version,
    SourceInfo Source,
    List<Segment> Segments,
    List<Word> Transcript,
    DetectionSettings Settings);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    Converters = [typeof(JsonStringEnumConverter<SegmentOrigin>)])]
[JsonSerializable(typeof(ProjectFile))]
internal sealed partial class ProjectJsonContext : JsonSerializerContext
{
}
