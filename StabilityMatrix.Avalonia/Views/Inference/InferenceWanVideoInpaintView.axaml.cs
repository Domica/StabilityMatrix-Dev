using Avalonia.Controls;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceWanVideoInpaintView>]
public partial class InferenceWanVideoInpaintView : DockUserControlBase
{
    public InferenceWanVideoInpaintView()
    {
        InitializeComponent();
    }
}
