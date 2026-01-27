public record SaveAnimatedMP4 : NamedComfyNode
{
    public new string Name { get; set; } = "SaveAnimatedMP4";

    public SaveAnimatedMP4() : base("SaveAnimatedMP4") { }

    public required object Images { get; init; }
    public required string FilenamePrefix { get; init; }
    public required double Fps { get; init; }
    public required int Crf { get; init; }
    public required string Codec { get; init; }
    public required string Container { get; init; }
}
