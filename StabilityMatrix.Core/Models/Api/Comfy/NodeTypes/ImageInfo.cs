using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

public record ImageInfo : ComfyNode
{
    public ImageInfo()
    {
        ClassType = "ImageInfo";
    }

    public ComfyNodeInput<ImageNodeConnection> Image
        => Input<ImageNodeConnection>("image");

    public ComfyNodeOutput<IntNodeConnection> Width
        => Output<IntNodeConnection>(0);

    public ComfyNodeOutput<IntNodeConnection> Height
        => Output<IntNodeConnection>(1);
}
