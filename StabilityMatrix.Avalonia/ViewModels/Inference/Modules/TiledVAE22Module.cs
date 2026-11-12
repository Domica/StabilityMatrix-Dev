using Injectio.Attributes;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Modules;

/// <summary>
/// Wan 2.2 (TI2V 5B) specific tiled VAE decode.
/// <para>
/// Uses ComfyUI's core <c>VAEDecodeTiled</c>, which is safe here because the official
/// <c>Wan2_2_VAE_bf16.safetensors</c> is detected by ComfyUI as a 3D Wan VAE
/// (<c>decoder.middle.0.residual.0.gamma</c> + Wan 2.2 layout markers), giving
/// latent_dim=3, 48 channels, /16 spatial and 4x temporal compression. The generic
/// addon uses Wan 2.1 defaults that do not produce useful temporal chunks on Wan 2.2.
/// </para>
/// <para>
/// Requires the matching Wan 2.2 VAE. The converted <c>wan22_test_simplevae.safetensors</c>
/// (2D / 16-channel) and <c>wan22_test_wan16x_vae.safetensors</c> are NOT detected as Wan VAEs
/// and will fall through ComfyUI's generic decode path, which can collapse a 5D latent to its
/// first frame or fail with a conv2d shape error.
/// </para>
/// </summary>
[ManagedService]
[RegisterTransient<TiledVAE22Module>]
public class TiledVAE22Module : ModuleBase
{
    public TiledVAE22Module(IServiceManager<ViewModelBase> vmFactory)
        : base(vmFactory)
    {
        Title = "Tiled VAE Decode (Wan 2.2)";
        AddCards(vmFactory.Get<TiledVAE22CardViewModel>());
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        var card = GetCard<TiledVAE22CardViewModel>();

        e.PreOutputActions.Add(args =>
        {
            var builder = args.Builder;

            // Only apply if primary is still in latent space (i.e. nothing decoded it yet)
            if (builder.Connections.Primary?.IsT0 != true)
                return;

            var latent = builder.Connections.Primary.AsT0;
            var vae = builder.Connections.GetDefaultVAE();

            LatentNodeConnection samples = latent;

            if (card.OffloadModelBeforeDecode)
            {
                // WANMemoryCleanupNode takes an "anything" passthrough input and returns it as
                // output slot 0. Wiring that output into the decode both guarantees the cleanup
                // actually executes (ComfyUI prunes nodes whose outputs are unused) and forces
                // it to run immediately before the VAE is loaded. The "*" type is compatible
                // with LATENT in both directions.
                var cleanupNode = new NamedComfyNode(builder.Nodes.GetUniqueName("Wan22_TiledVae_Cleanup"))
                {
                    ClassType = "WANMemoryCleanupNode",
                    Inputs = new Dictionary<string, object?>
                    {
                        ["anything"] = latent.Data,
                        ["offload_wan_models"] = true,
                        ["offload_cache"] = true,
                    }
                };

                builder.Nodes.Add(cleanupNode.Name, cleanupNode);

                samples = new LatentNodeConnection { Data = [cleanupNode.Name, 0] };
            }

            var tiledDecode = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.TiledVAEDecode
                {
                    Name = builder.Nodes.GetUniqueName("Wan22_TiledVAEDecode"),
                    Samples = samples,
                    Vae = vae,
                    TileSize = card.TileSize,
                    Overlap = card.Overlap,
                    TemporalSize = card.TemporalSize,
                    TemporalOverlap = card.TemporalOverlap
                }
            );

            // Hand decoded frames to the video output card instead of letting it add a
            // (non-tiled) VAEDecode of its own.
            builder.Connections.Primary = tiledDecode.Output;
        });
    }
}
