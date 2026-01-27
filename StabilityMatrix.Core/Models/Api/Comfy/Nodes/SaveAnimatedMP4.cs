namespace StabilityMatrix.Core.Models.Api.Comfy.Nodes;

/// <summary>
/// Data model for MP4 video export.
/// Mirrors SaveAnimatedWEBP but exposes MP4‑specific parameters.
/// </summary>
public record SaveAnimatedMP4 : NamedComfyNode
{
    /// <summary>
    /// Node name used by UI and workflow builder.
    /// Must match the ComfyUI class_type.
    /// </summary>
    public static string Name => "SaveAnimatedMP4";

    /// <summary>
    /// Input frames for the animation.
    /// SMX generator will assign the correct connection type.
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
