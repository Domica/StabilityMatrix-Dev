using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Services;

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

    public override void ApplyStep(InferenceStepContext context)
    {
        if (!IsEnabled)
            return;

        var node = context.AddNode("TiledVAEDecode", "VAEDecodeTiled");

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

        node.Set("samples", context.LatentNodeName);
        node.Set("vae", context.VaeNodeName);

        context.OutputNodeNames.Add(node.Name);
    }
}
