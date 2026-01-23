using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(TiledVAECard))]
[ManagedService]
public partial class TiledVAECardViewModel : LoadableViewModelBase
{
    public const string ModuleKey = "TiledVAE";

    [ObservableProperty]
    private bool isEnabled = true;

    // Default tiling settings
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

    // Custom tiling toggle and settings
    [ObservableProperty]
    private bool isCustomTilingEnabled = false;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required]
    [Range(64, 4096)]
    private int customTileSize = 512;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required]
    [Range(0, 4096)]
    private int customOverlap = 224;

    // Temporal tiling toggle and settings
    [ObservableProperty]
    private bool useCustomTemporalTiling = true;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required]
    [Range(8, 4096)]
    private int temporalSize = 64;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required]
    [Range(4, 4096)]
    private int temporalOverlap = 8;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required]
    [Range(8, 4096)]
    private int customTemporalSize = 96;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required]
    [Range(4, 4096)]
    private int customTemporalOverlap = 24;
}
