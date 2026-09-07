namespace Cutback.App.Controls;

/// <summary>
/// A run of transcript words the user acted on, as inclusive indices. <paramref name="Anchor"/> is
/// the word under the press point; its state decides whether the run is cut or restored.
/// </summary>
public sealed record WordRange(int First, int Last, int Anchor);
