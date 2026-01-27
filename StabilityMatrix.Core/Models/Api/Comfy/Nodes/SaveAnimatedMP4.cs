using System.Diagnostics.CodeAnalysis;

namespace StabilityMatrix.Core.Models.Api.Comfy.Nodes;

/// <summary>
/// UI‑visible MP4 animation export node.
/// Mirrors the structure of other NamedComfyNode‑based output nodes.
/// </summary>
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
public class SaveAnimatedMP4 : NamedComfyNode
{
    /// <summary>
    /// Node display name used by the UI and workflow builder.
    /// Overrides the base Name property to provide a fixed identifier.
    /// </summary>
    public new string Name { get; set; } = "SaveAnimatedMP4";

    /// <summary>
    /// Required constructor — NamedComfyNode enforces explicit naming.
    /// Ensures the node is registered with the correct class_type.
    /// </summary>namespace StabilityMatrix.Core.Models.Api.Comfy.Nodes;

/// <summary>
/// Data model for MP4 video export.
/// Pure JSON‑serializable schema used by the ComfyUI prompt builder.
/// Contains no UI logic or typed behavior.
/// </summary>
public record SaveAnimatedMP4
{
    /// <summary>
    /// Input frames for the animation.
    /// </summary>
    public required object Images { get; init; }

    /// <summary>
    /// Output filename prefix (without extension).
    /// </summary>
    public required string FilenamePrefix { get; init; }

    /// <summary>
    /// Output framerate (frames per second).
    /// </summary>
    public required double Fps { get; init; }

    /// <summary>
    /// CRF quality value (lower = higher quality).
    /// </summary>
    public required int Crf { get; init; }

    /// <summary>
    /// Video codec (e.g. libx264, libx265).
    /// </summary>
    public required string Codec { get; init; }

    /// <summary>
    /// Container format (e.g. mp4, mkv).
    /// Determines the output file extension.
    /// </summary>
    public required string Container { get; init; }
}

    public SaveAnimatedMP4() : base("SaveAnimatedMP4")
    {
    }

    /// <summary>
    /// Input frames for the animation.
    /// The SMX Comfy generator will automatically assign the correct connection type.
    /// </summary>
    public required object Images { get; init; }

    /// <summary>
    /// Output filename prefix (without extension).
    /// </summary>
    public required string FilenamePrefix { get; init; }

    /// <summary>
    /// Output framerate (frames per second).
    /// </summary>
    public required double Fps { get; init; }

    /// <summary>
    /// CRF quality value (lower = higher quality).
    /// Typical range: 18–28.
    /// </summary>
    public required int Crf { get; init; }

    /// <summary>
    /// Video codec (e.g. libx264, libx265).
    /// </summary>
    public required string Codec { get; init; }

    /// <summary>
    /// Container format (e.g. mp4, mkv).
    /// Determines the output file extension.
    /// </summary>
    public required string Container { get; init; }
}
