using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

/// <summary>
/// Comfy SaveAnimatedMP4 node za eksport videa sa MP4 kompresijom.
/// Koristi se za eksport animiranih slika kao MP4 video datoteka.
/// </summary>
[TypedNodeOptions(Name = "SaveAnimatedMP4")]
public record SaveAnimatedMP4 : ComfyTypedNodeBase
{
    /// <summary>
    /// Ulazne slike za eksport (Usually output from video generation nodes)
    /// </summary>
    public required object Images { get; init; }

    /// <summary>
    /// Prefiks imena datoteke (bez ekstenzije)
    /// </summary>
    public required string FilenamePrefix { get; init; }

    /// <summary>
    /// Broj frejmova po sekundi (1-120)
    /// </summary>
    public required double Fps { get; init; }

    /// <summary>
    /// Constant Rate Factor - kvaliteta kompresije (0-51)
    /// 0 = lossless, 51 = worst quality
    /// Preporučeno: 18-28 (default: 18)
    /// </summary>
    public required int Crf { get; init; }

    /// <summary>
    /// Video codec za kompresiju
    /// Opcije: "libx264" (H.264), "libx265" (H.265/HEVC)
    /// </summary>
    public required string Codec { get; init; }

    /// <summary>
    /// Video container format
    /// Opcije: "mp4", "mkv"
    /// </summary>
    public required string Container { get; init; }

    /// <summary>
    /// Bitrate u kbps (500-50000)
    /// Alternativa CRF kontroli.
    /// Obično se koristi CRF umjesto Bitrate-a.
    /// </summary>
    public required int Bitrate { get; init; }
}
