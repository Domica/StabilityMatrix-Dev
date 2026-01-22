using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Services;

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

        IsEnabled = card.IsEnabled;
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        var builder = e.Builder;

        var node = builder.AddNode("TiledVAEDecode", "VAEDecodeTiled");

        node.Set("tile_size", card.TileSize);
        node.Set("overlap", card.Overlap);

        if (card.UseCustomTemporalTiling)
        {
            node.Set("temporal_size", card.TemporalSize);
            node.Set("temporal_overlap", card.TemporalOverlap);
        }
        else
        {
            node.Set("temporal_size", 64);
            node.Set("temporal_overlap", 8);
        }

        node.Set("samples", e.Connections.LatentNodeName);
        node.Set("vae", e.Connections.VaeNodeName);

        e.Connections.OutputNodeNames.Add(node.Name);
    }
}
