using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StabilityMatrix.Core.Models.FileInterfaces;

/// <summary>
/// Extension methods for identifying and categorizing Z-Image model components
/// </summary>
public static class ZImageModelExtensions
{
    /// <summary>
    /// Known Z-Image model file names and patterns
    /// </summary>
    private static readonly HashSet<string> KnownZImageFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        // Diffusion models
        "z_image_turbo_bf16.safetensors",
        "z_image_turbo_fp16.safetensors",
        "z_image_turbo.safetensors",
        
        // Text encoders
        "qwen_3_4b.safetensors",
        
        // VAE (when specifically in Z-Image context)
        "ae.safetensors"
    };

    /// <summary>
    /// Patterns that identify Z-Image related files
    /// </summary>
    private static readonly string[] ZImagePatterns =
    {
        "z_image",
        "zimage",
        "z-image"
    };

    /// <summary>
    /// Cached architecture detection results, keyed by absolute file path.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool>
        ArchitectureCache = new();

    /// <summary>
    /// Detects whether a safetensors file uses the Z-Image (Lumina2) architecture
    /// by inspecting the tensor names in the safetensors header.
    /// <para>
    /// Z-Image weights are keyed as <c>model.diffusion_model.layers.*</c> and carry the
    /// Z-Image specific <c>noise_refiner</c> / <c>context_refiner</c> / <c>t_embedder</c> /
    /// <c>cap_embedder</c> sub-modules. Wan models instead use
    /// <c>model.diffusion_model.blocks.*</c>. Checking the architecture (instead of only the
    /// file name) means renamed or fine-tuned Z-Image files are recognized as well.
    /// </para>
    /// </summary>
    public static bool IsZImageArchitecture(this string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        if (!filePath.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            return false;

        return ArchitectureCache.GetOrAdd(filePath, static path =>
        {
            try
            {
                if (!File.Exists(path))
                    return false;

                using var stream = File.OpenRead(path);

                // 8 bytes: little-endian uint64 with the length of the JSON header
                Span<byte> lengthBuffer = stackalloc byte[8];
                stream.ReadExactly(lengthBuffer);
                var jsonLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                    lengthBuffer
                );

                const ulong MAX_ALLOWED_JSON_LENGTH = 100 * 1024 * 1024;
                if (jsonLength is 0 or > MAX_ALLOWED_JSON_LENGTH)
                    return false;

                var jsonBytes = new byte[(int)jsonLength];
                stream.ReadExactly(jsonBytes);
                var header = System.Text.Encoding.UTF8.GetString(jsonBytes);

                // "layers" is the Z-Image/Lumina2 block name, "blocks" is used by Wan.
                return header.Contains("\"model.diffusion_model.layers.", StringComparison.Ordinal)
                    && header.Contains(
                        "\"model.diffusion_model.cap_embedder.",
                        StringComparison.Ordinal
                    );
            }
            catch
            {
                return false;
            }
        });
    }

    /// <summary>
    /// Checks if a file is a Z-Image model component
    /// </summary>
    public static bool IsZImageModel(this string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var fileName = Path.GetFileName(filePath);
        
        // Check against known files
        if (KnownZImageFiles.Contains(fileName))
            return true;

        // Check against patterns
        return ZImagePatterns.Any(pattern => 
            fileName.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the Z-Image component type
    /// </summary>
    public static ZImageComponentType? GetZImageComponentType(this string filePath)
    {
        if (!filePath.IsZImageModel())
            return null;

        var fileName = Path.GetFileName(filePath).ToLowerInvariant();
        var directory = Path.GetFileName(Path.GetDirectoryName(filePath))?.ToLowerInvariant();

        // Identify by directory first (most reliable)
        return directory switch
        {
            "diffusion_models" when fileName.Contains("z_image") => ZImageComponentType.DiffusionModel,
            "text_encoders" when fileName.Contains("qwen") => ZImageComponentType.TextEncoder,
            "vae" when fileName.Contains("ae") => ZImageComponentType.VAE,
            "loras" when fileName.Contains("z_image") => ZImageComponentType.Lora,
            _ => IdentifyByFileName(fileName)
        };
    }

    private static ZImageComponentType? IdentifyByFileName(string fileName)
    {
        if (fileName.Contains("z_image_turbo") && !fileName.Contains("lora"))
            return ZImageComponentType.DiffusionModel;
        
        if (fileName.Contains("qwen"))
            return ZImageComponentType.TextEncoder;
        
        if (fileName.StartsWith("ae."))
            return ZImageComponentType.VAE;
        
        if (fileName.Contains("z_image") && fileName.Contains("lora"))
            return ZImageComponentType.Lora;

        return null;
    }

    /// <summary>
    /// Gets user-friendly display name for Z-Image component
    /// </summary>
    public static string GetZImageDisplayName(this string filePath)
    {
        var componentType = filePath.GetZImageComponentType();
        if (!componentType.HasValue)
            return Path.GetFileNameWithoutExtension(filePath);

        var baseName = Path.GetFileNameWithoutExtension(filePath);
        return componentType.Value switch
        {
            ZImageComponentType.DiffusionModel => $"Z-Image Turbo (Main Model)",
            ZImageComponentType.TextEncoder => $"Qwen Text Encoder (Z-Image)",
            ZImageComponentType.VAE => $"Z-Image VAE",
            ZImageComponentType.Lora => baseName.Replace("_z_image", " (Z-Image LoRA)"),
            _ => baseName
        };
    }

    /// <summary>
    /// Checks if all required Z-Image components are present in a directory
    /// </summary>
    public static ZImageValidationResult ValidateZImageSetup(string modelsDirectory)
    {
        var result = new ZImageValidationResult();

        var diffusionPath = Path.Combine(modelsDirectory, "diffusion_models");
        var textEncoderPath = Path.Combine(modelsDirectory, "text_encoders");
        var vaePath = Path.Combine(modelsDirectory, "vae");

        // Check for diffusion model
        if (Directory.Exists(diffusionPath))
        {
            result.HasDiffusionModel = Directory.GetFiles(diffusionPath, "z_image_turbo*.safetensors").Any();
        }

        // Check for text encoder
        if (Directory.Exists(textEncoderPath))
        {
            result.HasTextEncoder = Directory.GetFiles(textEncoderPath, "qwen*.safetensors").Any();
        }

        // Check for VAE
        if (Directory.Exists(vaePath))
        {
            result.HasVAE = Directory.GetFiles(vaePath, "ae.safetensors").Any();
        }

        return result;
    }
}

/// <summary>
/// Z-Image model component types
/// </summary>
public enum ZImageComponentType
{
    DiffusionModel,
    TextEncoder,
    VAE,
    Lora
}

/// <summary>
/// Validation result for Z-Image setup
/// </summary>
public class ZImageValidationResult
{
    public bool HasDiffusionModel { get; set; }
    public bool HasTextEncoder { get; set; }
    public bool HasVAE { get; set; }
    
    public bool IsComplete => HasDiffusionModel && HasTextEncoder && HasVAE;
    
    public IEnumerable<string> MissingComponents
    {
        get
        {
            if (!HasDiffusionModel) yield return "Z-Image Turbo Diffusion Model";
            if (!HasTextEncoder) yield return "Qwen Text Encoder";
            if (!HasVAE) yield return "Z-Image VAE";
        }
    }
}
