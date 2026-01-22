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
    private readonly TiledVAECardViewModel _card;

    public TiledVAEModule(IServiceManager<ViewModelBase> vmFactory)
        : base(vmFactory)
    {
        Title = "Tiled VAE Decode";

        // Jedna instanca kartice — UI i modul dijele iste vrijednosti
        _card = vmFactory.Get<TiledVAECardViewModel>();
        AddCards(_card);
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        var card = _card;

        e.PreOutputActions.Add(args =>
        {
            var builder = args.Builder;

            // Primarni output mora biti latent
            if (builder.Connections.Primary?.IsT0 != true)
                return;

            var latent = builder.Connections.Primary.AsT0;
            var vae = builder.Connections.GetDefaultVAE();

            // Tiled decode node
            var tiledDecode = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.TiledVAEDecode
                {
                    Name = builder.Nodes.GetUniqueName("TiledVAEDecode"),
                    Samples = latent,
                    Vae = vae,

                    // Custom spatial tiling
                    TileSize = card.TileSize,
                    Overlap = card.Overlap,

                    // Custom temporal tiling (ispravno)
                    TemporalSize = card.UseCustomTemporalTiling
                        ? card.TemporalSize
                        : 64,

                    TemporalOverlap = card.UseCustomTemporalTiling
                        ? card.TemporalOverlap
                        : 8
                }
            );

            // Preusmjeri primary na tiled decode
            builder.Connections.Primary = tiledDecode.Output;
        });
    }
}
