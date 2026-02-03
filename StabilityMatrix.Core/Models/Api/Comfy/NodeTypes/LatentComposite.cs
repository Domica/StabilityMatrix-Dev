using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes.Connections;

namespace StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

public record LatentComposite : ComfyNode
{
    public LatentComposite()
    {
        ClassType = "LatentComposite";
    }

    public ComfyNodeInput<LatentNodeConnection> Original => Input<LatentNodeConnection>("original");
    public ComfyNodeInput<LatentNodeConnection> Generated => Input<LatentNodeConnection>("generated");
    public ComfyNodeInput<MaskNodeConnection> Mask => Input<MaskNodeConnection>("mask");

    public ComfyNodeOutput<LatentNodeConnection> Output => Output<LatentNodeConnection>(0);
}
