using Injectio.Attributes;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Modules;

[ManagedService]
[RegisterTransient<WanMemoryCleanupModule>]
public class WanMemoryCleanupModule : ModuleBase
{
    public WanMemoryCleanupModule(IServiceManager<ViewModelBase> vmFactory)
        : base(vmFactory)
    {
        Title = "WAN Memory Cleanup";
        AddCards(vmFactory.Get<WanMemoryCleanupCardViewModel>());
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        var builder = e.Builder;

        var cleanupNode = new NamedComfyNode(builder.Nodes.GetUniqueName("WANMemoryCleanup"))
        {
            ClassType = "WANMemoryCleanupNode",
            Inputs = new Dictionary<string, object?>
            {
                ["anything"] = null,
                ["offload_wan_models"] = true,
                ["offload_cache"] = true
            }
        };

        builder.Nodes.Add(cleanupNode.Name, cleanupNode);
    }
}
