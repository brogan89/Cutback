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
