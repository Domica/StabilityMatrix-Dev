using StabilityMatrix.Core.Attributes;

namespace StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

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
