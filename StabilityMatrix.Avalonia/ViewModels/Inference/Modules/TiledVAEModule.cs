using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Inference;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Modules;

public class TiledVAEModule : ModuleBase
{
    private readonly TiledVAECardViewModel card;

    public TiledVAEModule(IServiceManager<ViewModelBase> vmFactory)
        : base(vmFactory)
    {
        Title = "Tiled VAE Decode";
        card = vmFactory.Get<TiledVAECardViewModel>();
        AddCards(card);

        IsEnabled = card.IsEnabled;
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        var builder = e.Builder;

        // ⭐ Typed node API — jedini API koji tvoj branch podržava
        var node = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.TiledVAEDecode
            {
                Samples = builder.Connections.Primary.AsT0(),
                Vae = builder.Connections.PrimaryVAE.AsT0(),
                TileSize = card.TileSize,
                Overlap = card.Overlap,
                TemporalSize = card.UseCustomTemporalTiling ? card.TemporalSize : 64,
                TemporalOverlap = card.UseCustomTemporalTiling ? card.TemporalOverlap : 8
            }
        );

        // ⭐ Postavi output node
        builder.Connections.Primary = node.Output;
    }
}
