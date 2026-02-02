using System.Text.Json.Serialization;

namespace StabilityMatrix.Core.Models.Api.Comfy;

public record ComfyOutpainter
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }
    
    [JsonPropertyName("type")]
    public string Type { get; set; } = "outpainting";
}
