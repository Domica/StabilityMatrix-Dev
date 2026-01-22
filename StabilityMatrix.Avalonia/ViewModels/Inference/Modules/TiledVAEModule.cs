using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Modules;

public class TiledVAEModule : ModuleBase
{
    private readonly TiledVAECardViewModel card;

    public TiledVAEModule(ViewModelFactory vmFactory)
        : base(vmFactory)
    {
        Title = "Tiled VAE Decode";
        card = vmFactory.Get<TiledVAECardViewModel>();
        AddCards(card);

        IsEnabled = card.IsEnabled;
    }

    public override void ApplyStep(BuildContext ctx)
    {
        if (!IsEnabled)
            return;

        var builder = ctx.Builder;

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

        node.Set("samples", builder.Connections.LatentNodeName);
        node.Set("vae", builder.Connections.VaeNodeName);

        builder.Connections.OutputNodeNames.Add(node.Name);
    }
}
