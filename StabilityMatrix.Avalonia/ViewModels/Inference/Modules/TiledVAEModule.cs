using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Services;

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

        // Bind module activation to UI toggle
        IsEnabled = card.IsEnabled;
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        e.PreOutputActions.Add(args =>
        {
            var builder = args.Builder;

            // Only apply if primary is latent
            if (builder.Connections.Primary?.IsT0 != true)
                return;

            var latent = builder.Connections.Primary.AsT0;
            var vae = builder.Connections.GetDefaultVAE();

            var node = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.TiledVAEDecode
                {
                    Name = builder.Nodes.GetUniqueName("TiledVAEDecode"),
                    Samples = latent,
                    Vae = vae,
                    TileSize = card.TileSize,
                    Overlap = card.Overlap,
                    TemporalSize = card.UseCustomTemporalTiling ? card.TemporalSize : 64,
                    TemporalOverlap = card.UseCustomTemporalTiling ? card.TemporalOverlap : 8,
                }
            );

            builder.Connections.Primary = node.Output;
        });
    }
}
