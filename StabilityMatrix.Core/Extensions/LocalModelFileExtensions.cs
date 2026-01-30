using StabilityMatrix.Core.Models.Database;
using StabilityMatrix.Core.Models.Packages;
using StabilityMatrix.Core.Models.FileInterfaces;

namespace StabilityMatrix.Core.Extensions;

/// <summary>
/// Extension methods for LocalModelFile to support Z-Image models
/// </summary>
public static class LocalModelFileExtensions
{
    /// <summary>
    /// Enriches model metadata with Z-Image specific information
    /// </summary>
    public static void EnrichZImageMetadata(this LocalModelFile model, string rootModelDirectory)
    {
        var fullPath = model.GetFullPath(rootModelDirectory);
        
        if (!fullPath.IsZImageModel())
            return;

        var componentType = fullPath.GetZImageComponentType();
        if (!componentType.HasValue)
            return;

        // Note: LocalModelFile is a record - we can only modify mutable properties
        // Z-Image identification happens at runtime via extension methods
    }

    /// <summary>
    /// Gets compatible VAE for this Z-Image model
    /// </summary>
    public static string? GetCompatibleZImageVAE(this LocalModelFile model, string rootModelDirectory)
    {
        var fullPath = model.GetFullPath(rootModelDirectory);
        
        if (!fullPath.IsZImageModel())
            return null;

        var componentType = fullPath.GetZImageComponentType();
        if (componentType != ZImageComponentType.DiffusionModel)
            return null;

        var vaePath = Path.Combine(rootModelDirectory, "vae", "ae.safetensors");
        return File.Exists(vaePath) ? vaePath : null;
    }

    /// <summary>
    /// Gets user-friendly display information for Z-Image models
    /// </summary>
    public static string GetZImageDisplayInfo(this LocalModelFile model, string rootModelDirectory)
    {
        var fullPath = model.GetFullPath(rootModelDirectory);
        
        if (!fullPath.IsZImageModel())
            return string.Empty;

        var componentType = fullPath.GetZImageComponentType();
        if (!componentType.HasValue)
            return string.Empty;

        return componentType.Value switch
        {
            ZImageComponentType.DiffusionModel => "Z-Image Turbo Diffusion Model",
            ZImageComponentType.TextEncoder => "Qwen Text Encoder (Required for Z-Image)",
            ZImageComponentType.VAE => "Z-Image VAE Autoencoder",
            ZImageComponentType.Lora => "Z-Image LoRA Style Adapter",
            _ => string.Empty
        };
    }

    /// <summary>
    /// Checks if this model is part of Z-Image system
    /// </summary>
    public static bool IsZImageComponent(this LocalModelFile model, string rootModelDirectory)
    {
        var fullPath = model.GetFullPath(rootModelDirectory);
        return fullPath.IsZImageModel();
    }

    /// <summary>
    /// Gets the Z-Image component type for this model
    /// </summary>
    public static ZImageComponentType? GetComponentType(this LocalModelFile model, string rootModelDirectory)
    {
        var fullPath = model.GetFullPath(rootModelDirectory);
        return fullPath.GetZImageComponentType();
    }
}
