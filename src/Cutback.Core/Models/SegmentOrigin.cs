using System.Text.Json.Serialization;

namespace Cutback.Core.Models;

/// <summary>
/// Who decided a segment's boundaries and state. Re-running detection replaces <see cref="Auto"/>
/// segments and must never touch <see cref="Manual"/> ones.
/// </summary>
public enum SegmentOrigin
{
    [JsonStringEnumMemberName("auto")]
    Auto,

    [JsonStringEnumMemberName("manual")]
    Manual,

    [JsonStringEnumMemberName("claude")]
    Claude,
}
