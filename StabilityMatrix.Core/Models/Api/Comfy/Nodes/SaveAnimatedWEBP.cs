using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Core.Models.Api.Comfy.Nodes;

public record SaveAnimatedWEBP : NamedComfyNode
{
    public new string Name { get; set; } = "SaveAnimatedWEBP";

    public SaveAnimatedWEBP() : base("SaveAnimatedWEBP") { }

    public required object Images { get; init; }
    public required string FilenamePrefix { get; init; }
    public required double Fps { get; init; }
    public required bool Lossless { get; init; }
    public required int Quality { get; init; }
    public required string Method { get; init; }
}
