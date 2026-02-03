using StabilityMatrix.Core.Models.Api.Comfy;

namespace StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

public class ImageInfo : ComfyNode
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
