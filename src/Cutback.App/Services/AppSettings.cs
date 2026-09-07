using System.Text.Json.Serialization;
using Cutback.Core.Detection;
using Cutback.Core.Models;

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

    // Bounds for the silence detection fields. The Preferences NumericUpDowns mirror them.
    public const double MinSilenceThresholdDb = -80;
    public const double MaxSilenceThresholdDb = 0;
    public const int MinMinSilenceMs = 50;
    public const int MaxMinSilenceMs = 5000;
    public const int MinPaddingMs = 0;
    public const int MaxPaddingMs = 500;
    public const int MinMinKeepMs = 0;
    public const int MaxMinKeepMs = 2000;

    /// <summary>How many edits can be undone. Each step is an array of references to immutable segments, so this is cheap.</summary>
    public int UndoHistoryLimit { get; set; } = DefaultUndoHistoryLimit;

    /// <summary>ffmpeg binary or directory the user chose, or the last auto-resolved one. Null until first resolution.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Most recent first. Capped by <see cref="AppSettingsStore.MaxRecentFiles"/>.</summary>
    public IReadOnlyList<string> RecentFiles { get; set; } = [];

    /// <summary>Key of the Whisper model used for transcription, e.g. <c>base.en</c>. See <c>WhisperModelInfo</c>.</summary>
    public string WhisperModel { get; set; } = "base.en";

    /// <summary>Words that "Remove filler words" cuts. Matched after <see cref="FillerDetector.Normalize"/>.</summary>
    public IReadOnlyList<string> FillerWords { get; set; } = FillerDetector.DefaultWords;

    // Silence detection. Per user, not per project: Detect silence and Remove filler words read
    // these at call time, and the project file only records the values the last run used.
    // Flat properties rather than a nested record so a key missing from an old file keeps its default.

    /// <summary>Level below which audio counts as silence. See <see cref="DetectionSettings.SilenceThresholdDb"/>.</summary>
    public double SilenceThresholdDb { get; set; } = DetectionSettings.Default.SilenceThresholdDb;

    /// <summary>Silences shorter than this are not cut. See <see cref="DetectionSettings.MinSilenceMs"/>.</summary>
    public int MinSilenceMs { get; set; } = DetectionSettings.Default.MinSilenceMs;

    /// <summary>Extra kept audio on each side of a cut. See <see cref="DetectionSettings.PaddingMs"/>.</summary>
    public int PaddingMs { get; set; } = DetectionSettings.Default.PaddingMs;

    /// <summary>Kept fragments shorter than this are merged into the neighbouring cut. See <see cref="DetectionSettings.MinKeepMs"/>.</summary>
    public int MinKeepMs { get; set; } = DetectionSettings.Default.MinKeepMs;

    /// <summary>The four detection values as the record the planners take. Derived, not persisted.</summary>
    [JsonIgnore]
    public DetectionSettings Detection => new(PaddingMs, MinSilenceMs, SilenceThresholdDb, MinKeepMs);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}
