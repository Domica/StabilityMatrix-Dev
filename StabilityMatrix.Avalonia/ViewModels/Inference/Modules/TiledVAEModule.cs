using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Attributes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Modules;

[Transient]   // ⭐ Model registration
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

        // ⭐ Typed node API
        var node = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.TiledVAEDecode
            {
                Name = "TiledVAEDecode",                     // required
                Samples = builder.Connections.Primary.AsT0,  // OneOf → AsT0 property
                Vae = builder.Connections.PrimaryVAE,        // već je VAENodeConnection
                TileSize = card.TileSize,
                Overlap = card.Overlap,
                TemporalSize = card.UseCustomTemporalTiling ? card.TemporalSize : 64,
                TemporalOverlap = card.UseCustomTemporalTiling ? card.TemporalOverlap : 8
            }
        );

        // ⭐ Output node
        builder.Connections.Primary = node.Output;
    }
}
