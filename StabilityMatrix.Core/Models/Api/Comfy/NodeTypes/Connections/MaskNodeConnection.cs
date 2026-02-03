using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Core.Models.Api.Comfy.NodeTypes.Connections;

public class MaskNodeConnection : ComfyNodeConnection
{
    public override string Type => "MASK";
}
