namespace Cutback.Analysis;

/// <summary>A recogniser token with its timing in seconds. Engine-neutral so word assembly can be tested without a model.</summary>
public readonly record struct TokenTiming(string Text, double Start, double End, float Probability);
