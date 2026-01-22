using Injectio.Attributes;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Services;   // ✔ OVO POSTOJI

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Modules;

[ManagedService]
public class TiledVAEModule : ModuleBase
{
    private static readonly LoggingService Log = LoggingService.Create<TiledVAEModule>();

    private readonly TiledVAECardViewModel _card;

    public TiledVAEModule(IServiceManager<ViewModelBase> vmFactory)
        : base(vmFactory)
    {
        Title = "Tiled VAE Decode";

        _card = vmFactory.Get<TiledVAECardViewModel>();
        AddCards(_card);
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        Log.Warn($"TiledVAE DEBUG: OnApplyStep called. IsEnabled={_card.IsEnabled}");

        if (!_card.IsEnabled)
        {
            Log.Warn("TiledVAE DEBUG: Early exit – card disabled.");
            return;
        }

        e.PreOutputActions.Add(args =>
        {
            var builder = args.Builder;

            Log.Warn($"TiledVAE DEBUG: PreOutputAction. Primary null={builder.Connections.Primary is null}, IsT0={builder.Connections.Primary?.IsT0}");

            if (builder.Connections.Primary?.IsT0 != true)
            {
                Log.Warn("TiledVAE DEBUG: Early exit – Primary is not T0 latent.");
                return;
            }

            var latent = builder.Connections.Primary.AsT0;
            var vae = builder.Connections.GetDefaultVAE();

            Log.Warn($"TiledVAE DEBUG: Adding TiledVAEDecode node. TileSize={_card.TileSize}, Overlap={_card.Overlap}");

            var tiledDecode = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.TiledVAEDecode
                {
                    Name = builder.Nodes.GetUniqueName("TiledVAEDecode"),
                    Samples = latent,
                    Vae = vae,
                    TileSize = _card.TileSize,
                    Overlap = _card.Overlap,
                    TemporalSize = _card.UseCustomTemporalTiling ? _card.TemporalSize : 64,
                    TemporalOverlap = _card.UseCustomTemporalTiling ? _card.TemporalOverlap : 8
                }
            );

            builder.Connections.Primary = tiledDecode.Output;
        });
    }
}