using Injectio.Attributes;
using Microsoft.Extensions.Logging;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Modules;

[ManagedService]
public class WanMemoryCleanupModule : ModuleBase
{
    private readonly ILogger<WanMemoryCleanupModule> _log;

    public WanMemoryCleanupModule(IServiceManager<ViewModelBase> vmFactory, ILogger<WanMemoryCleanupModule> logger)
        : base(vmFactory)
    {
        Title = "WAN Memory Cleanup";
        _log = logger;
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        if (!IsEnabled)
            return;

        e.PreOutputActions.Add(args =>
        {
            var builder = args.Builder;

            _log.LogDebug("Injecting WANMemoryCleanupNode");

            var cleanupNode = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.NamedComfyNode
                {
                    Name = builder.Nodes.GetUniqueName("WANMemoryCleanup"),
                    ClassType = "WANMemoryCleanupNode",
                    Inputs = new Dictionary<string, object?>
                    {
                        ["anything"] = null,
                        ["offload_wan_models"] = true,
                        ["offload_cache"] = true
                    }
                }
            );

            // Cleanup node has no outputs — nothing to connect
        });
    }
}
