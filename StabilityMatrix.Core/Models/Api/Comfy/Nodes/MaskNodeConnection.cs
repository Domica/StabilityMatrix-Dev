using StabilityMatrix.Core.Models.Api.Comfy;

namespace StabilityMatrix.Core.Models.Api.Comfy.Nodes;

public record MaskNodeConnection : ComfyNodeConnection
{
    public override string Type => "MASK";
}
