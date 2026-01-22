using Injectio.Attributes;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Modules;

[ManagedService]
[RegisterTransient<TiledVAEModule>]
public class TiledVAEModule : ModuleBase
{
    private readonly TiledVAECardViewModel _card;

    public TiledVAEModule(IServiceManager<ViewModelBase> vmFactory)
        : base(vmFactory)
    {
        Title = "Tiled VAE Decode";

        // 1) Kreiraj instancu kartice (UI + modul dijele istu)
        _card = vmFactory.Get<TiledVAECardViewModel>();

        // 2) Učitaj prethodno spremljeno stanje (ako postoji)
        _card.LoadState();

        // 3) Tek onda dodaj karticu u UI
        AddCards(_card);
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        // Register a pre-output action that replaces standard VAE decode with tiled decode
        e.PreOutputActions.Add(args =>
        {
            var builder = args.Builder;

            // Only apply if primary is in latent space
            if (builder.Connections.Primary?.IsT0 != true)
                return;

            var latent = builder.Connections.Primary.AsT0;
            var vae = builder.Connections.GetDefaultVAE();

            // Use tiled VAE decode instead of standard decode
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

            // Update primary connection to the decoded image
            builder.Connections.Primary = tiledDecode.Output;
        });
    }
}
