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
        card = vmFactory.Get<TiledVAECardViewModel>();
        AddCards(card);
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        // ✔ UI switch finally works
        if (!card.IsEnabled)
            return;

        var builder = e.Builder;

        var node = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.TiledVAEDecode
            {
                Name = "TiledVAEDecode",
                Samples = builder.Connections.Primary.AsT0,
                Vae = builder.Connections.PrimaryVAE,
                TileSize = card.TileSize,
                Overlap = card.Overlap,
                TemporalSize = card.UseCustomTemporalTiling ? card.TemporalSize : 64,
                TemporalOverlap = card.UseCustomTemporalTiling ? card.TemporalOverlap : 8
            }
        );

        builder.Connections.Primary = node.Output;
    }
}
