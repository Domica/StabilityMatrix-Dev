using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Core.Models.Api.Comfy.Nodes;

/// <summary>
/// Typed-node wrapper for MP4 video export.
/// Mirrors SaveAnimatedWEBP but exposes MP4-specific parameters.
/// </summary>
[ComfyNode("SaveAnimatedMP4")]
public record SaveAnimatedMP4 : ComfyTypedNodeBase<ImageNodeConnection>
{
    /// <summary>
    /// Input frames (images) from the video pipeline.
    /// </summary>
    public required List<LatentNodeConnection> Images { get; init; }

    /// <summary>
    /// Output filename prefix (without extension).
    /// </summary>
    public required string FilenamePrefix { get; init; }

    /// <summary>
    /// Output framerate.
    /// </summary>
    public required int Fps { get; init; }

    /// <summary>
    /// CRF quality value (lower is higher quality).
    /// Typical range: 18–28.
    /// </summary>
    public required int Crf { get; init; }

    /// <summary>
    /// Codec name (e.g. libx264, libx265).
    /// </summary>
    public required string Codec { get; init; }

    /// <summary>
    /// Container format (e.g. mp4, mkv).
    /// </summary>
    public required string Container { get; init; }
}
