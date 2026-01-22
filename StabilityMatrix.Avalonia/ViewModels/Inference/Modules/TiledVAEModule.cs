using Injectio.Attributes;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Avalonia.Models.Inference;


namespace StabilityMatrix.Avalonia.ViewModels.Inference.Modules;

[ManagedService]
public class TiledVAEModule : ModuleBase
{
    private readonly TiledVAECardViewModel _card;

    public TiledVAEModule(IServiceManager<ViewModelBase> vmFactory)
        : base(vmFactory)
    {
        Title = "Tiled VAE Decode";

        // Jedna instanca kartice – UI i modul dijele istu
        _card = vmFactory.Get<TiledVAECardViewModel>();

        AddCards(_card);
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
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

                    TileSize = _card.TileSize,
                    Overlap = _card.Overlap,

                    TemporalSize = _card.UseCustomTemporalTiling
                        ? _card.TemporalSize
                        : 64,

                    TemporalOverlap = _card.UseCustomTemporalTiling
                        ? _card.TemporalOverlap
                        : 8
                }
            );

            builder.Connections.Primary = tiledDecode.Output;
        });
    }
}
