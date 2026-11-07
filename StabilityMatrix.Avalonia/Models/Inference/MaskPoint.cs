namespace StabilityMatrix.Avalonia.Models.Inference;

/// <summary>
/// A click point in pixel coordinates of the original video frame.
/// </summary>
public record MaskPoint(double X, double Y);

/// <summary>
/// Raised by the frame point picker. Positive = left click (replace this), negative = right click (not this).
/// </summary>
public record MaskPointClick(double X, double Y, bool IsPositive);
