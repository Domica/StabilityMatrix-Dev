using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(WanMemoryCleanupCard))]
[ManagedService]
[RegisterTransient<WanMemoryCleanupCardViewModel>]
public partial class WanMemoryCleanupCardViewModel : LoadableViewModelBase
{
    public const string ModuleKey = "WanMemoryCleanup";
}
