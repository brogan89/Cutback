using System.Text.Json.Serialization;

namespace Cutback.App.Services;

/// <summary>Per-user application settings. Not part of any project file.</summary>
public sealed record AppSettings
{
    /// <summary>ffmpeg binary or directory the user chose, or the last auto-resolved one. Null until first resolution.</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>Most recent first. Capped by <see cref="AppSettingsStore.MaxRecentFiles"/>.</summary>
    public IReadOnlyList<string> RecentFiles { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}
