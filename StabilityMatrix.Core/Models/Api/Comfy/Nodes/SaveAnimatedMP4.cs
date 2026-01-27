namespace StabilityMatrix.Core.Models.Api.Comfy.Nodes;

/// <summary>
/// Data model for MP4 video export.
/// Mirrors SaveAnimatedWEBP but exposes MP4‑specific parameters.
/// This class is a pure data container (no typed connections).
/// </summary>
public record SaveAnimatedMP4
{
    /// <summary>
    /// Input frames for the animation.
    /// SMX generator will assign the correct connection type.
    /// </summary>
    public required List<object> Images { get; init; }

    /// <summary>
    /// Output filename prefix (without extension).
    /// </summary>
    public required string FilenamePrefix { get; init; }

    /// <summary>
    /// Output framerate (frames per second).
    /// </summary>
    public required int Fps { get; init; }

    /// <summary>
    /// CRF quality value (lower = higher quality).
    /// Typical range: 18–28.
    /// </summary>
    public required int Crf { get; init; }

    /// <summary>
    /// Video codec (e.g. libx264, libx265).
    /// </summary>
    public required string Codec { get; init; }

    /// <summary>
    /// Container format (e.g. mp4, mkv).
    /// </summary>
    public required string Container { get; init; }
}
