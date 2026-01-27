using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

/// <summary>
/// Typed wrapper for the MP4 animation export node.
/// This class connects the plain data model to the Comfy node registry.
/// The ComfyNode attribute provides the class_type used by ComfyUI.
/// </summary>
[ComfyNode("SaveAnimatedMP4")]
public record SaveAnimatedMP4
    : global::StabilityMatrix.Core.Models.Api.Comfy.Nodes.SaveAnimatedMP4;
