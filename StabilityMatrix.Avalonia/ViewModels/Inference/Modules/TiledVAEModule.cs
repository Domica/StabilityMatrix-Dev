using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Attributes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Modules;

[Transient]
public class TiledVAEModule : ModuleBase
{
    private readonly TiledVAECardViewModel _card;

    public TiledVAEModule(IServiceManager<ViewModelBase> vmFactory)
        : base(vmFactory)
    {
        Title = "Tiled VAE Decode";

        // Instanciraj karticu jednom i koristi je za UI i modul
        _card = vmFactory.Get<TiledVAECardViewModel>();
        AddCards(_card);
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        // Modul isključen → ništa se ne radi
        if (!_card.IsEnabled)
            return;

        var builder = e.Builder;

        var node = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.TiledVAEDecode
            {
                Name = "TiledVAEDecode",
                Samples = builder.Connections.Primary.AsT0,
                Vae = builder.Connections.PrimaryVAE,

                // Prostorni tiling
                TileSize = _card.TileSize,
                Overlap = _card.Overlap,

                // Temporalni tiling (default ili custom)
                TemporalSize = _card.UseCustomTemporalTiling ? _card.TemporalSize : 64,
                TemporalOverlap = _card.UseCustomTemporalTiling ? _card.TemporalOverlap : 8
            }
        );

        builder.Connections.Primary = node.Output;
    }
}
