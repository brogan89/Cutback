namespace Cutback.App.Controls;

/// <summary>Command parameter for a boundary drag: which boundary and where it should go.</summary>
/// <param name="BoundaryIndex">Index into the segment list's boundaries, 1..Count-1. See <c>SegmentList.MoveBoundary</c>.</param>
/// <param name="Time">Requested time in seconds, already snapped.</param>
public sealed record BoundaryMove(int BoundaryIndex, double Time);
