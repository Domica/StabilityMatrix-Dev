using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Video;

public class VideoOutputMethodJsonConverter : JsonConverter<VideoOutputMethod>
{
    public override VideoOutputMethod Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        try
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => Enum.Parse<VideoOutputMethod>(reader.GetString()!),
                JsonTokenType.Number => (VideoOutputMethod)reader.GetInt32(),
                _ => VideoOutputMethod.Default
            };
        }
        catch (ArgumentException)
        {
            // Ako vrijednost ne postoji, koristi Default (fallback)
            return VideoOutputMethod.Default;
        }
        catch (FormatException)
        {
            return VideoOutputMethod.Default;
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        VideoOutputMethod value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStringValue(value.ToString());
    }
}
