using StabilityMatrix.Core.Models.Database;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Models.Packages;

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
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Checks if this model is part of Z-Image system
    /// </summary>
    public static bool IsZImageComponent(this LocalModelFile model, string rootModelDirectory)
    {
        var fullPath = model.GetFullPath(rootModelDirectory);
        return fullPath.IsZImageModel() || fullPath.IsZImageArchitecture();
    }

    /// <summary>
    /// Gets the Z-Image component type for this model
    /// </summary>
    public static ZImageComponentType? GetComponentType(this LocalModelFile model, string rootModelDirectory)
    {
        var fullPath = model.GetFullPath(rootModelDirectory);
        return fullPath.GetZImageComponentType();
    }

    /// <summary>
    /// Enriches model metadata with Flux 2 VAE specific information
    /// </summary>
    public static void EnrichFluxVaeMetadata(this LocalModelFile model, string rootModelDirectory)
    {
        var fullPath = model.GetFullPath(rootModelDirectory);

        if (!fullPath.IsFlux2VaeModel())
            return;

        var componentType = fullPath.GetFluxVaeComponentType();
        if (!componentType.HasValue)
            return;

        // Note: LocalModelFile is a record - we can only modify mutable properties
        // Flux 2 VAE identification happens at runtime via extension methods
    }

    /// <summary>
    /// Gets compatible Flux 2 VAE for this model
    /// </summary>
    public static string? GetCompatibleFluxVae(this LocalModelFile model, string rootModelDirectory)
    {
        var fullPath = model.GetFullPath(rootModelDirectory);

        if (!fullPath.IsFlux2VaeModel())
            return null;

        var componentType = fullPath.GetFluxVaeComponentType();
        if (componentType != FluxVaeComponentType.VAE)
            return null;

        var vaePath = Path.Combine(rootModelDirectory, "vae", "ae.safetensors");
        return File.Exists(vaePath) ? vaePath : null;
    }

    /// <summary>
    /// Gets user-friendly display information for Flux 2 VAE models
    /// </summary>
    public static string GetFluxVaeDisplayInfo(this LocalModelFile model, string rootModelDirectory)
    {
        var fullPath = model.GetFullPath(rootModelDirectory);

        if (!fullPath.IsFlux2VaeModel())
            return string.Empty;

        var componentType = fullPath.GetFluxVaeComponentType();
        if (!componentType.HasValue)
            return string.Empty;

        return componentType.Value switch
        {
            FluxVaeComponentType.VAE => "Flux 2 VAE Autoencoder",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Checks if this model is part of Flux 2 VAE system
    /// </summary>
    public static bool IsFluxVaeComponent(this LocalModelFile model, string rootModelDirectory)
    {
        var fullPath = model.GetFullPath(rootModelDirectory);
        return fullPath.IsFlux2VaeModel();
    }

    /// <summary>
    /// Gets the Flux 2 VAE component type for this model
    /// </summary>
    public static FluxVaeComponentType? GetFluxVaeComponentType(
        this LocalModelFile model,
        string rootModelDirectory
    )
    {
        var fullPath = model.GetFullPath(rootModelDirectory);
        return fullPath.GetFluxVaeComponentType();
    }
}
