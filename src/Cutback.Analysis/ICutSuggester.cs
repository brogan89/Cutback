using Cutback.Core.Models;

namespace Cutback.Analysis;

/// <summary>
/// Phase 3 seam: reasons over an already-timestamped <em>text</em> transcript and proposes cuts for
/// filler words, false starts, repeated phrases and tangents. The Claude implementation sends text
/// and indices only. It never sends audio or video, because the Anthropic API cannot transcribe.
/// </summary>
public interface ICutSuggester
{
    /// <summary>Proposes spans to remove. Every suggestion is reviewable before it is applied.</summary>
    Task<IReadOnlyList<CutSuggestion>> SuggestAsync(
        IReadOnlyList<Word> transcript,
        CancellationToken cancellationToken);
}

/// <summary>A proposed removal, shown to the user with its reason before it becomes a segment.</summary>
/// <param name="Start">Start of the span to remove, in seconds.</param>
/// <param name="End">End of the span to remove, in seconds.</param>
/// <param name="Reason">Why, e.g. "false start" or "filler: um".</param>
/// <param name="Confidence">In <c>[0, 1]</c>.</param>
public sealed record CutSuggestion(double Start, double End, string Reason, double Confidence);
