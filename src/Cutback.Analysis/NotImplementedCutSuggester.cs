using Cutback.Core.Models;

namespace Cutback.Analysis;

/// <summary>Placeholder until Phase 3. Exists so the composition root has something to register.</summary>
public sealed class NotImplementedCutSuggester : ICutSuggester
{
    public Task<IReadOnlyList<CutSuggestion>> SuggestAsync(IReadOnlyList<Word> transcript, CancellationToken cancellationToken)
        => throw new NotImplementedException("Claude cut suggestions are not part of this phase. See the Roadmap in CLAUDE.md.");
}
