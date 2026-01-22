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

        // Jedna instanca kartice – dijele je UI i modul
        _card = vmFactory.Get<TiledVAECardViewModel>();

        // Dodaj karticu u UI
        AddCards(_card);
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        // Originalna dev logika: pre-output akcija koja zamjenjuje standardni VAE decode tiled verzijom
        e.PreOutputActions.Add(args =>
        {
            var builder = args.Builder;

            // Primarni mora biti latent (T0), inače ne radimo ništa
            if (builder.Connections.Primary?.IsT0 != true)
                return;

            var latent = builder.Connections.Primary.AsT0;
            var vae = builder.Connections.GetDefaultVAE();

            // Tiled VAE decode node
            var tiledDecode = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.TiledVAEDecode
                {
                    Name = builder.Nodes.GetUniqueName("TiledVAEDecode"),
                    Samples = latent,
                    Vae = vae,

                    // Prostorni tiling
                    TileSize = _card.TileSize,
                    Overlap = _card.Overlap,

                    // Temporalni tiling – koristi custom vrijednosti samo ako je uključeno
                    TemporalSize = _card.UseCustomTemporalTiling
                        ? _card.TemporalSize
                        : 64,

                    TemporalOverlap = _card.UseCustomTemporalTiling
                        ? _card.TemporalOverlap
                        : 8
                }
            );

            // Primarni connection sada pokazuje na dekodiranu sliku
            builder.Connections.Primary = tiledDecode.Output;
        });
    }
}
