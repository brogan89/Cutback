namespace Cutback.App.Controls;

/// <summary>
/// A range the user drew on the timeline to become a new section. <paramref name="Anchor"/> is
/// where the drag started; the view model uses the segment under it to decide the new section's
/// state, so dragging over kept footage makes a cut and dragging inside a cut restores that range.
/// </summary>
/// <param name="Start">Start in seconds, already snapped and clamped.</param>
/// <param name="End">End in seconds, already snapped and clamped. Greater than <paramref name="Start"/>.</param>
/// <param name="Anchor">Time under the pointer when the drag began.</param>
public sealed record SectionRange(double Start, double End, double Anchor);
