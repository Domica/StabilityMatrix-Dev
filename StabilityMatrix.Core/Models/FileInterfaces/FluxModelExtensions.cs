using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace StabilityMatrix.Core.Models.FileInterfaces;

/// <summary>
/// Extension methods for identifying and categorizing Flux 2 VAE model components
/// </summary>
public static class FluxModelExtensions
{
    /// <summary>
    /// Known Flux 2 VAE model file names
    /// </summary>
    private static readonly HashSet<string> KnownFluxVaeFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        // VAE
        "ae.safetensors",
    };

    /// <summary>
    /// Patterns that identify Flux 2 VAE related files
    /// </summary>
    private static readonly string[] FluxVaePatterns = { "flux_vae", "flux-vae", "flux2vae", "flux2-vae" };

    /// <summary>
    /// Tensor name patterns that indicate Flux 2 VAE architecture in safetensors headers.
    /// Flux VAE files contain encoder/decoder tensor structures.
    /// </summary>
    private static readonly string[] FluxVaeTensorPatterns = { "encoder.", "decoder." };

    /// <summary>
    /// Checks if a file is a Flux 2 VAE model component
    /// </summary>
    public static bool IsFlux2VaeModel(this string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var fileName = Path.GetFileName(filePath);

        // Check against known files
        if (KnownFluxVaeFiles.Contains(fileName))
            return true;

        // Check against patterns
        var lower = fileName.ToLowerInvariant();
        return FluxVaePatterns.Any(pattern => lower.Contains(pattern));
    }

    /// <summary>
    /// Checks if a safetensors file has Flux 2 VAE architecture by reading tensor header keys.
    /// Reads the safetensors header JSON and checks for encoder/decoder tensor name patterns.
    /// </summary>
    public static bool IsFlux2VaeArchitecture(this string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        if (!filePath.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            foreach (var tensorName in ReadSafetensorTensorNames(filePath))
            {
                if (
                    FluxVaeTensorPatterns.Any(pattern =>
                        tensorName.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads tensor names from a safetensors file header.
    /// Safetensors format: 8-byte u64 LE header length, then JSON header with tensor definitions.
    /// </summary>
    private static IEnumerable<string> ReadSafetensorTensorNames(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        // Read 8-byte header length (little-endian u64)
        var headerLenBytes = reader.ReadBytes(8);
        if (headerLenBytes.Length < 8)
            yield break;

        var headerLen = BinaryPrimitives.ReadUInt64LittleEndian(headerLenBytes);
        if (headerLen == 0 || headerLen > 100 * 1024 * 1024) // 100 MB max
            yield break;

        // Read JSON header
        var headerLenInt = (int)headerLen;
        var headerBytes = reader.ReadBytes(headerLenInt);
        if (headerBytes.Length < headerLenInt)
            yield break;

        using var doc = JsonDocument.Parse(headerBytes);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            // Skip __metadata__ - it's not a tensor
            if (property.Name == "__metadata__")
                continue;
            yield return property.Name;
        }
    }

    /// <summary>
    /// Gets the Flux 2 VAE component type
    /// </summary>
    public static FluxVaeComponentType? GetFluxVaeComponentType(this string filePath)
    {
        if (!filePath.IsFlux2VaeModel())
            return null;

        var fileName = Path.GetFileName(filePath).ToLowerInvariant();
        var directory = Path.GetFileName(Path.GetDirectoryName(filePath))?.ToLowerInvariant();

        // Identify by directory first (most reliable)
        return directory switch
        {
            "vae" => FluxVaeComponentType.VAE,
            _ => IdentifyByFileName(fileName),
        };
    }

    private static FluxVaeComponentType? IdentifyByFileName(string fileName)
    {
        if (fileName.StartsWith("ae."))
            return FluxVaeComponentType.VAE;

        if (fileName.Contains("flux") && (fileName.Contains("vae") || fileName.Contains("ae")))
            return FluxVaeComponentType.VAE;

        return null;
    }

    /// <summary>
    /// Gets user-friendly display name for Flux 2 VAE component
    /// </summary>
    public static string GetFluxVaeDisplayName(this string filePath)
    {
        var componentType = filePath.GetFluxVaeComponentType();
        if (!componentType.HasValue)
            return Path.GetFileNameWithoutExtension(filePath);

        var baseName = Path.GetFileNameWithoutExtension(filePath);
        return componentType.Value switch
        {
            FluxVaeComponentType.VAE => $"Flux 2 VAE ({baseName})",
            _ => baseName,
        };
    }

    /// <summary>
    /// Checks if all required Flux 2 VAE components are present in a directory
    /// </summary>
    public static FluxVaeValidationResult ValidateFluxVaeSetup(string modelsDirectory)
    {
        var result = new FluxVaeValidationResult();

        var vaePath = Path.Combine(modelsDirectory, "vae");

        // Check for VAE
        if (Directory.Exists(vaePath))
        {
            result.HasVAE = Directory
                .GetFiles(vaePath, "*.safetensors")
                .Any(f => f.IsFlux2VaeModel() || f.IsFlux2VaeArchitecture());
        }

        return result;
    }
}

/// <summary>
/// Flux 2 VAE model component types
/// </summary>
public enum FluxVaeComponentType
{
    VAE,
}

/// <summary>
/// Validation result for Flux 2 VAE setup
/// </summary>
public class FluxVaeValidationResult
{
    public bool HasVAE { get; set; }

    public bool IsComplete => HasVAE;

    public IEnumerable<string> MissingComponents
    {
        get
        {
            if (!HasVAE)
                yield return "Flux 2 VAE";
        }
    }
}
