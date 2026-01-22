using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.Models.Inference;

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

        // ⭐ STARI API — ovo tvoj branch koristi
        var node = builder.Nodes.AddNode("TiledVAEDecode", "VAEDecodeTiled");

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

        node.Set("samples", builder.Connections.Primary);
        node.Set("vae", builder.Connections.PrimaryVAE);

        builder.Connections.Primary = node.Name;
    }
}
