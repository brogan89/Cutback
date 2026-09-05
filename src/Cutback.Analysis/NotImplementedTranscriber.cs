using Cutback.Core.Models;

namespace Cutback.Analysis;

/// <summary>Placeholder until Phase 2. Exists so the composition root has something to register.</summary>
public sealed class NotImplementedTranscriber : ITranscriber
{
    public Task<IReadOnlyList<Word>> TranscribeAsync(string mediaPath, IProgress<double>? progress, CancellationToken cancellationToken)
        => throw new NotImplementedException("Speech recognition is not part of this phase. See the Roadmap in CLAUDE.md.");
}
