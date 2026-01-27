namespace StabilityMatrix.Core.Models.Api.Comfy.Nodes;

/// <summary>
/// Data model for MP4 video export.
/// Pure JSON‑serializable schema used by the ComfyUI prompt builder.
/// Contains no UI logic or typed behavior.
/// </summary>
public record SaveAnimatedMP4
{
    /// <summary>
    /// Input frames for the animation.
    /// </summary>
    public required object Images { get; init; }

    /// <summary>
    /// Output filename prefix (without extension).
    /// </summary>
    public required string FilenamePrefix { get; init; }

    /// <summary>
    /// Output framerate (frames per second).
    /// </summary>
    public required double Fps { get; init; }

    /// <summary>
    /// CRF quality value (lower = higher quality).
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
