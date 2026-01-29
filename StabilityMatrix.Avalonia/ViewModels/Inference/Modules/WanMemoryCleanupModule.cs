using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Modules;

public class WanMemoryCleanupModule : ModuleBase
{
    public WanMemoryCleanupModule(IServiceManager<ViewModelBase> vmFactory)
        : base(vmFactory)
    {
        Title = "WAN Memory Cleanup";
        IsEnabled = false;
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        if (!IsEnabled)
            return;

        var builder = e.Builder;

        // Generate unique node name
        var name = builder.GetUniqueName("WANMemoryCleanup");

        var cleanupNode = new NamedComfyNode(name)
        {
            ClassType = "WANMemoryCleanupNode",
            Inputs = new Dictionary<string, object?>
            {
                ["anything"] = null,
                ["offload_wan_models"] = true,
                ["offload_cache"] = true
            }
        };

        builder.Nodes.Add(name, cleanupNode);
    }
}
