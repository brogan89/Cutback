namespace Cutback.App.Controls;

/// <summary>Where a boundary drag is in its life. One drag is one undo step, so the view model needs to know when it starts and ends.</summary>
public enum BoundaryDragPhase
{
    /// <summary>The pointer went down on the boundary. <see cref="BoundaryMove.Time"/> is its current position.</summary>
    Begin,

    /// <summary>The pointer moved. Applied live, unsnapped.</summary>
    Update,

    /// <summary>The pointer was released or the drag was interrupted. Snapped when released.</summary>
    End,
}

/// <summary>Parameter of the timeline's move-boundary command.</summary>
/// <param name="BoundaryIndex">Boundary between <c>Segments[BoundaryIndex - 1]</c> and <c>Segments[BoundaryIndex]</c>.</param>
/// <param name="Time">Requested position in seconds.</param>
/// <param name="Phase">Where in the drag this move sits.</param>
public sealed record BoundaryMove(int BoundaryIndex, double Time, BoundaryDragPhase Phase);
