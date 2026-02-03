using StabilityMatrix.Core.Models.Api.Comfy;

namespace StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

public class MaskFromExpand : ComfyNode
{
    public MaskFromExpand()
    {
        ClassType = "MaskFromExpand";
    }

    public ComfyNodeInput<IntNodeConnection> Width
        => Input<IntNodeConnection>("width");

    public ComfyNodeInput<IntNodeConnection> Height
        => Input<IntNodeConnection>("height");

    public ComfyNodeInput<IntNodeConnection> Left
        => Input<IntNodeConnection>("left");

    public ComfyNodeInput<IntNodeConnection> Right
        => Input<IntNodeConnection>("right");

    public ComfyNodeInput<IntNodeConnection> Top
        => Input<IntNodeConnection>("top");

    public ComfyNodeInput<IntNodeConnection> Bottom
        => Input<IntNodeConnection>("bottom");

    public ComfyNodeOutput<MaskNodeConnection> Mask
        => Output<MaskNodeConnection>(0);
}
