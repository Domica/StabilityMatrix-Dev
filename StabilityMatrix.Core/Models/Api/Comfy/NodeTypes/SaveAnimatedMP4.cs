using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

/// <summary>
/// Typed MP4 animation export node.
/// Inherits from ComfyTypedNodeBase so it can produce a NamedComfyNode
/// with ClassType and Inputs populated automatically.
/// </summary>
[TypedNodeOptions(Name = "SaveAnimatedMP4")]
public record SaveAnimatedMP4 : ComfyTypedNodeBase
{
    public required object Images { get; init; }
    public required string FilenamePrefix { get; init; }
    public required double Fps { get; init; }
    public required int Crf { get; init; }
    public required string Codec { get; init; }
    public required string Container { get; init; }
}
