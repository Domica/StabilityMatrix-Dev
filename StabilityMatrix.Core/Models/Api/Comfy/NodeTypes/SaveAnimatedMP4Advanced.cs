[TypedNodeOptions(Name = "SaveAnimatedMP4Advanced")]
public record SaveAnimatedMP4Advanced : ComfyTypedNodeBase
{
    public required ImageNodeConnection Images { get; init; }
    public required double Fps { get; init; }
    public required string FilenamePrefix { get; init; }
    public required int Crf { get; init; }
    public required string Codec { get; init; }
    public required string Container { get; init; }
    public required int Bitrate { get; init; }
}
