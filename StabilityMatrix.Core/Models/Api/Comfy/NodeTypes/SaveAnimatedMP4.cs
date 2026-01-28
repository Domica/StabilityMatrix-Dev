using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

/// <summary>
/// Comfy SaveAnimatedMP4 node for exporting video with MP4 compression.
/// Used to export animated images as MP4 video files.
/// 
/// Inputs:
/// - images: ImageNodeConnection - images for export (required!)
/// - filename_prefix: string - filename prefix
/// - fps: float - frames per second (1-120)
/// - crf: int - Constant Rate Factor (0-51)
/// - codec: string - video codec ("libx264" or "libx265")
/// - container: string - container format ("mp4" or "mkv")
/// - bitrate: int - bitrate in kbps (500-50000)
/// </summary>
[TypedNodeOptions(Name = "SaveAnimatedMP4")]
public record SaveAnimatedMP4 : ComfyTypedNodeBase
{
    /// <summary>
    /// Input images for export - MUST be ImageNodeConnection!
    /// 
    /// IMPORTANT: This is ImageNodeConnection, not object!
    /// This ensures the correct Comfy API reference is generated:
    /// [["VAEDecode_1", 0]] instead of {}
    /// </summary>
    public required ImageNodeConnection Images { get; init; }

    /// <summary>
    /// Filename prefix (without extension)
    /// Example: "InferenceVideo" → "InferenceVideo_00001.mp4"
    /// </summary>
    public required string FilenamePrefix { get; init; }

    /// <summary>
    /// Frames per second (1-120)
    /// Typical values: 24 (cinema), 30 (broadcast), 60 (high fps)
    /// </summary>
    public required double Fps { get; init; }

    /// <summary>
    /// Constant Rate Factor - compression quality (0-51)
    /// 
    /// Values:
    /// - 0 = lossless (largest file, not recommended)
    /// - 18 = default (good quality)
    /// - 23 = standard quality
    /// - 28 = smaller file
    /// - 51 = lowest quality (smallest file)
    /// 
    /// Rule: Lower values = better quality + larger file
    /// </summary>
    public required int Crf { get; init; }

    /// <summary>
    /// Video codec for compression
    /// 
    /// Options:
    /// - "libx264" (H.264) - standard, compatible with most players
    /// - "libx265" (H.265/HEVC) - better compression, slower encoding
    /// </summary>
    public required string Codec { get; init; }

    /// <summary>
    /// Video container format
    /// 
    /// Options:
    /// - "mp4" - standard video format, compatible with most players
    /// - "mkv" - Matroska format, better for archiving
    /// </summary>
    public required string Container { get; init; }

    /// <summary>
    /// Bitrate in kilobits per second (500-50000)
    /// 
    /// Alternative to CRF control - can be used instead of CRF.
    /// Usually CRF is preferred as it gives better results.
    /// 
    /// Typical values:
    /// - 500-2000: web streaming (low quality)
    /// - 2000-5000: streaming (quality/size balance)
    /// - 5000-10000: high quality
    /// - 10000+: archiving/master
    /// 
    /// Note: CRF usually takes priority over bitrate in most H.264 encoders
    /// </summary>
    public required int Bitrate { get; init; }
}
