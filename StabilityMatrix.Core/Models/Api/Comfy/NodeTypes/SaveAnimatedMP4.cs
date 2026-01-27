using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

/// <summary>
/// Typed MP4 animation export node.
/// This class is used by the UI and workflow builder.
/// It inherits from ComfyTypedNodeBase so it can produce a NamedComfyNode
/// with ClassType and Inputs populated automatically.
/// </summary>
[TypedNodeOptions(Name = "SaveAnimatedMP4")]
public record SaveAnimatedMP4 : ComfyTypedNodeBase
{
    /// <summary>
    /// Unique node name used by ComfyUI.
    /// Required by ComfyTypedNodeBase.
    /// </summary>
    public override required string Name { get; init; }

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
