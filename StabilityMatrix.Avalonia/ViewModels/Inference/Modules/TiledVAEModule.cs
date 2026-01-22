using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Attributes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Modules;

[Transient]
public class TiledVAEModule : ModuleBase
{
    private readonly TiledVAECardViewModel card;

    public TiledVAEModule(IServiceManager<ViewModelBase> vmFactory)
        : base(vmFactory)
    {
        Title = "Tiled VAE Decode";

        // Add card to module (UI)
        AddCards(vmFactory.Get<TiledVAECardViewModel>());

        // Use same instance as UI
        card = GetCard<TiledVAECardViewModel>();
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        // Modul isključen → ništa se ne radi
        if (!card.IsEnabled)
            return;

        var builder = e.Builder;

        var node = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.TiledVAEDecode
            {
                Name = "TiledVAEDecode",
                Samples = builder.Connections.Primary.AsT0,
                Vae = builder.Connections.PrimaryVAE,

                // Prostorni tiling
                TileSize = card.TileSize,
                Overlap = card.Overlap,

                // Temporalni tiling (default ili custom)
                TemporalSize = card.UseCustomTemporalTiling ? card.TemporalSize : 64,
                TemporalOverlap = card.UseCustomTemporalTiling ? card.TemporalOverlap : 8
            }
        );

        builder.Connections.Primary = node.Output;
    }
}
