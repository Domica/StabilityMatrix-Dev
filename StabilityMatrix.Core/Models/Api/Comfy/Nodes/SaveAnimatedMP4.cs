using StabilityMatrix.Core.Models.Api.Comfy;

namespace StabilityMatrix.Core.Models.Api.Comfy.Nodes;

/// <summary>
/// Comfy node wrapper for exporting an animation as MP4.
/// This mirrors the structure of SaveAnimatedWEBP but exposes MP4‑specific parameters.
/// </summary>
public sealed class SaveAnimatedMP4 : ComfyNode
{
    /// <summary>
    /// The Comfy node type identifier.
    /// IMPORTANT:
    /// Replace this with the actual node type used by your Comfy backend.
    /// Examples:
    ///   "FFMPEG_Video"
    ///   "SaveAnimatedMP4"
    ///   "save_video"
    /// </summary>
    public override string Type => "SaveAnimatedMP4";

    /// <summary>
    /// Display title for debugging or UI purposes.
    /// </summary>
    public override string Title => "Save Animated MP4";

    /// <summary>
    /// Input frames (images) from the video pipeline.
    /// This matches the "images" input of SaveAnimatedWEBP.
    /// </summary>
    public required ComfyValue Images { get; init; }

    /// <summary>
    /// Output video framerate.
    /// </summary>
    public double Fps { get; init; } = 16.0d;

    /// <summary>
    /// CRF quality value (lower = higher quality, higher bitrate).
    /// Typical range: 18–28.
    /// </summary>
    public int Crf { get; init; } = 18;

    /// <summary>
    /// Video codec to use.
    /// Common values:
    ///   "libx264"
    ///   "libx265"
    /// </summary>
    public string Codec { get; init; } = "libx264";

    /// <summary>
    /// Output container format.
    /// Usually "mp4".
    /// </summary>
    public string Container { get; init; } = "mp4";

    /// <summary>
    /// Filename prefix for the exported video.
    /// </summary>
    public string FilenamePrefix { get; init; } = "InferenceVideo";

    /// <summary>
    /// Builds the dictionary payload sent to the Comfy backend.
    /// Keys must match the input names expected by the actual Comfy node.
    /// </summary>
    public override IDictionary<string, object?> ToComfyNode()
    {
        var dict = base.ToComfyNode();

        dict["images"] = Images;
        dict["fps"] = Fps;
        dict["crf"] = Crf;
        dict["codec"] = Codec;
        dict["container"] = Container;
        dict["filename_prefix"] = FilenamePrefix;

        return dict;
    }
}
