using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

/// <summary>
/// Typed wrapper for the MP4 animation export node.
/// This class connects the plain data model to the Comfy node registry
/// by providing the class_type and input key metadata via the ComfyNode attribute.
/// </summary>
[ComfyNode(
    ClassType = "SaveAnimatedMP4",
    Inputs = new[]
    {
        "images",
        "filename_prefix",
        "fps",
        "crf",
        "codec",
        "container"
    }
)]
public record SaveAnimatedMP4
    : global::StabilityMatrix.Core.Models.Api.Comfy.Nodes.SaveAnimatedMP4;
