using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

/// <summary>
/// Typed wrapper for MP4 animation export.
/// Provides class_type, input mapping and typed node behavior.
/// </summary>
[ComfyNode("SaveAnimatedMP4")]
public record SaveAnimatedMP4
    : ComfyTypedNodeBase<global::StabilityMatrix.Core.Models.Api.Comfy.Nodes.SaveAnimatedMP4>
{
    public SaveAnimatedMP4()
    {
        ClassType = "SaveAnimatedMP4";

        Inputs = new()
        {
            ["images"] = default!,
            ["filename_prefix"] = default!,
            ["fps"] = default!,
            ["crf"] = default!,
            ["codec"] = default!,
            ["container"] = default!
        };
    }
}
