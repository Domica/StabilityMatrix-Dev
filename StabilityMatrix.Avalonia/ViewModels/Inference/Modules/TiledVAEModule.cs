using Injectio.Attributes;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Modules;

[ManagedService]
public class TiledVAEModule : ModuleBase
{
    public TiledVAEModule(IServiceManager<ViewModelBase> vmFactory)
        : base(vmFactory)
    {
        Title = "Tiled VAE Decode";
        AddCards(vmFactory.Get<TiledVAECardViewModel>());
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        var card = GetCard<TiledVAECardViewModel>();

        e.PreOutputActions.Add(args =>
        {
            var builder = args.Builder;

            if (builder.Connections.Primary?.IsT0 != true)
                return;

            var latent = builder.Connections.Primary.AsT0;
            var vae = builder.Connections.GetDefaultVAE();

            var tiledDecode = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.TiledVAEDecode
                {
                    Name = builder.Nodes.GetUniqueName("TiledVAEDecode"),
                    Samples = latent,
                    Vae = vae,
                    TileSize = card.TileSize,
                    Overlap = card.Overlap,
                    TemporalSize = card.UseCustomTemporalTiling
                        ? card.TemporalSize
                        : 64,
                    TemporalOverlap = card.UseCustomTemporalTiling
                        ? card.TemporalOverlap
                        : 8
                }
            );

            builder.Connections.Primary = tiledDecode.Output;
        });
    }
}
