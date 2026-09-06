using System.Text.Json.Serialization;

namespace Cutback.App.Services;

/// <summary>Per-user application settings. Not part of any project file.</summary>
/// <remarks>
/// Properties use <c>set</c>, not <c>init</c>, on purpose: the System.Text.Json source generator
/// ignores property initializers on init-only properties, so a key missing from an older
/// settings.json would come back as 0 or null instead of its default.
/// </remarks>
public sealed record AppSettings
{
    public const int DefaultUndoHistoryLimit = 100;
    public const int MinUndoHistoryLimit = 10;
    public const int MaxUndoHistoryLimit = 1000;

    /// <summary>How many edits can be undone. Each step is an array of references to immutable segments, so this is cheap.</summary>
    public int UndoHistoryLimit { get; set; } = DefaultUndoHistoryLimit;

    /// <summary>ffmpeg binary or directory the user chose, or the last auto-resolved one. Null until first resolution.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Most recent first. Capped by <see cref="AppSettingsStore.MaxRecentFiles"/>.</summary>
    public IReadOnlyList<string> RecentFiles { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}
