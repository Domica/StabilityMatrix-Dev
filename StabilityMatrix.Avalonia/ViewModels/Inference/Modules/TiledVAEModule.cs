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

        // UI i modul dijele istu instancu
        _card = vmFactory.Get<TiledVAECardViewModel>();
        AddCards(_card);
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        // Ako je modul isključen → ništa ne radi
        if (!_card.IsEnabled)
            return;

        // WAN-friendly decode override
        e.PreOutputActions.Add(args =>
        {
            var builder = args.Builder;

            // Primarni mora biti latent (T0)
            if (builder.Connections.Primary?.IsT0 != true)
                return;

            var latent = builder.Connections.Primary.AsT0;

            // WAN-friendly VAE dohvat
            var vae = builder.Connections.GetDefaultVAE();

            // Dodaj tiled decode node
            var tiledDecode = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.TiledVAEDecode
                {
                    Name = builder.Nodes.GetUniqueName("TiledVAEDecode"),
                    Samples = latent,
                    Vae = vae,

                    // Prostorni tiling
                    TileSize = _card.TileSize,
                    Overlap = _card.Overlap,

                    // Temporalni tiling
                    TemporalSize = _card.UseCustomTemporalTiling
                        ? _card.TemporalSize
                        : 64,

                    TemporalOverlap = _card.UseCustomTemporalTiling
                        ? _card.TemporalOverlap
                        : 8
                }
            );

            // Override primarnog outputa
            builder.Connections.Primary = tiledDecode.Output;
        });
    }
}
