using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Modules;

public class WanMemoryCleanupModule : ModuleBase
{
    private bool _enabled;

    public WanMemoryCleanupModule(IServiceManager<ViewModelBase> vmFactory)
        : base(vmFactory)
    {
        Title = "WAN Memory Cleanup";
        IsEnabled = true;
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    protected override void OnApplyStep(ModuleApplyStepEventArgs e)
    {
        if (!Enabled)
            return;

        var builder = e.Builder;

        var cleanupNode = new ComfyNode
        {
            Type = "WANMemoryCleanupNode",
            Id = builder.GenerateNodeId(),
            Inputs = new Dictionary<string, object>
            {
                { "anything", null },
                { "offload_wan_models", true },
                { "offload_cache", true }
            }
        };

        if (builder.Nodes.ContainsKey("WANInferenceNode"))
        {
            builder.InsertNodeAfter("WANInferenceNode", cleanupNode);
        }
        else
        {
            builder.Nodes.Add(cleanupNode.Id, cleanupNode);
        }
    }
}
