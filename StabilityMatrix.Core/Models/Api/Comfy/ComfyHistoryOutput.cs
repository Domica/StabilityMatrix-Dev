using System.Text.Json.Serialization;

namespace StabilityMatrix.Core.Models.Api.Comfy;

public class ComfyHistoryOutput
{
    [JsonPropertyName("images")]
    public List<ComfyImage>? Images { get; set; }


    [JsonPropertyName("text")]
    public List<string>? Text { get; set; }}
