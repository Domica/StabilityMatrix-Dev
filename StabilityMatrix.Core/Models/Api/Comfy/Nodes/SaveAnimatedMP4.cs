namespace StabilityMatrix.Core.Models.Api.Comfy.Nodes;

/// <summary>
/// Data model for MP4 video export.
/// This is a pure JSON‑serializable schema used by the ComfyUI prompt builder.
/// It contains no UI logic, no typed connections, and no base‑class behavior.
/// </summary>
public record SaveAnimatedMP4
{
    /// <summary>
    /// Input frames for the animation.
    /// The SMX Comfy generator will automatically assign the correct connection type.
    /// </summary>
    public required object Images { get; init; }

    /// <summary>
    /// Output filename prefix (without extension).
    /// </summary>
    public required string FilenamePrefix { get; init; }

    /// <summary>
    /// Output framerate (frames per second).
    /// Matches the behavior of other animation‑related nodes.
    /// </summary>
    public required double Fps { get; init; }

    /// <summary>
    /// CRF quality value (lower = higher quality).
    /// Typical range: 18–28.
    /// </summary>
    public required int Crf { get; init; }

    /// <summary>
    /// Video codec (e.g. libx264, libx265).
    /// Passed directly to the ComfyUI backend.
    /// </summary>
    public required string Codec { get; init; }

    /// <summary>
    /// Container format (e.g. mp4, mkv).
    /// Determines the output file extension.
    /// </summary>
    public required string Container { get; init; }
}
