using Injectio.Attributes;
using Microsoft.Extensions.Logging;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Modules;

[ManagedService]
public class TiledVAEModule : ModuleBase
{
    private readonly ILogger<TiledVAEModule> _log;
    private readonly TiledVAECardViewModel _card;

    public TiledVAEModule(IServiceManager<ViewModelBase> vmFactory, ILogger<TiledVAEModule> logger)
        : base(vmFactory)
    {
        Title = "Tiled VAE Decode";
        _log = logger;
        _card = vmFactory.Get<TiledVAECardViewModel>();
        AddCards(_card);
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        if (!_card.IsEnabled)
        {
            return;
        }

        e.PreOutputActions.Add(args =>
        {
            var builder = args.Builder;

            if (builder.Connections.Primary?.IsT0 != true)
            {
                return;
            }

            var latent = builder.Connections.Primary.AsT0;
            var vae = builder.Connections.GetDefaultVAE();

            // Determine which tiling values to use
            var tileSize = _card.IsCustomTilingEnabled ? _card.CustomTileSize : _card.TileSize;
            var overlap = _card.IsCustomTilingEnabled ? _card.CustomOverlap : _card.Overlap;
            
            var temporalSize = _card.UseCustomTemporalTiling
                ? (_card.IsCustomTilingEnabled ? _card.CustomTemporalSize : _card.TemporalSize)
                : 64;
            
            var temporalOverlap = _card.UseCustomTemporalTiling
                ? (_card.IsCustomTilingEnabled ? _card.CustomTemporalOverlap : _card.TemporalOverlap)
                : 8;

            _log.LogDebug(
                "Adding TiledVAEDecode node. TileSize={TileSize}, Overlap={Overlap}, TemporalSize={TemporalSize}, TemporalOverlap={TemporalOverlap}",
                tileSize,
                overlap,
                temporalSize,
                temporalOverlap
            );

            var tiledDecode = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.TiledVAEDecode
                {
                    Name = builder.Nodes.GetUniqueName("TiledVAEDecode"),
                    Samples = latent,
                    Vae = vae,
                    TileSize = tileSize,
                    Overlap = overlap,
                    TemporalSize = temporalSize,
                    TemporalOverlap = temporalOverlap
                }
            );

            builder.Connections.Primary = tiledDecode.Output;
        });
    }
}
