using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

[TypedNodeOptions(Name = "SaveAnimatedWEBP")]
public record SaveAnimatedWEBP : ComfyTypedNodeBase
{
    public required object Images { get; init; }
    public required string FilenamePrefix { get; init; }
    public required double Fps { get; init; }
    public required bool Lossless { get; init; }
    public required int Quality { get; init; }
    public required string Method { get; init; }
}
