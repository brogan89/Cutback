namespace Cutback.Core.Models;

/// <summary>
/// A single transcribed word with its timing. Produced locally by an <c>ITranscriber</c> in
/// Phase 2. Only text and timestamps ever leave the machine, never audio.
/// </summary>
/// <param name="Text">The word as transcribed, including disfluencies such as "um".</param>
/// <param name="Start">Start time in seconds.</param>
/// <param name="End">End time in seconds.</param>
/// <param name="Confidence">Recogniser confidence in <c>[0, 1]</c>.</param>
public sealed record Word(string Text, double Start, double End, double Confidence);
