using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

/// <summary>
/// Settings for the Wan 2.2 (TI2V 5B) tiled VAE decode addon.
/// <para>
/// Wan 2.2's VAE is a 48-channel, /16 spatial, 4x temporal VAE. ComfyUI's core
/// <c>VAEDecodeTiled</c> divides <c>temporal_size</c> by the temporal compression (4), so the
/// Wan 2.1-style default of 64 produces a tile_t of 16 latent frames - larger than an entire
/// short clip, which means temporal tiling never actually kicks in and VRAM still spikes.
/// A default of 16 gives tile_t = 4 latent frames (~13 output frames) and forces real chunks.
/// </para>
/// </summary>
[View(typeof(TiledVAE22Card))]
[ManagedService]
[RegisterTransient<TiledVAE22CardViewModel>]
public partial class TiledVAE22CardViewModel : LoadableViewModelBase
{
    public const string ModuleKey = "TiledVAE22";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required]
    [Range(64, 4096)]
    private int tileSize = 512;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required]
    [Range(0, 4096)]
    private int overlap = 64;

    /// <summary>
    /// Output frames per temporal tile. Divided by 4 for Wan 2.2, so 16 = 4 latent frames.
    /// Lower this (min 8) if decoding still runs out of VRAM.
    /// </summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required]
    [Range(8, 4096)]
    private int temporalSize = 16;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required]
    [Range(4, 4096)]
    private int temporalOverlap = 8;

    /// <summary>
    /// Chain a WANMemoryCleanupNode between the sampler and the decode. Because the node's
    /// <c>anything</c> passthrough output is consumed by the decode, it is guaranteed to execute
    /// immediately before the VAE loads - offloading the diffusion model from VRAM first.
    /// </summary>
    [ObservableProperty]
    private bool offloadModelBeforeDecode = true;
}
