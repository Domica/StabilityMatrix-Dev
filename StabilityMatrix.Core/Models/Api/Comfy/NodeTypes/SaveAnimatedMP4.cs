using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

/// <summary>
/// Comfy SaveAnimatedMP4 node for exporting video with standard MP4 settings.
/// 
/// NOTE: This is the BASIC SaveAnimatedMP4 node from ComfyUI which only accepts:
/// - images: IMAGE
/// - fps: FLOAT
/// - filename_prefix: STRING
/// 
/// For advanced MP4 export with CRF, Codec, Container, and Bitrate control,
/// use SaveAnimatedMP4Advanced (custom node) instead.
/// 
/// Location: StabilityMatrix.Core/Models/Api/Comfy/NodeTypes/SaveAnimatedMP4Advanced.cs
/// </summary>
[TypedNodeOptions(Name = "SaveAnimatedMP4")]
public record SaveAnimatedMP4 : ComfyTypedNodeBase
{
    /// <summary>
    /// Input images for export.
    /// Must be ImageNodeConnection to properly reference the image source node.
    /// </summary>
    public required ImageNodeConnection Images { get; init; }

    /// <summary>
    /// Frames per second (1-120)
    /// Typical values: 24 (cinema), 30 (broadcast), 60 (high fps)
    /// </summary>
    public required double Fps { get; init; }

    /// <summary>
    /// Output filename prefix (without extension)
    /// Example: "InferenceVideo" → "InferenceVideo_00001.mp4"
    /// </summary>
    public required string FilenamePrefix { get; init; }
}
