using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceWan2ImageToVideoView>]
public partial class InferenceWan2ImageToVideoView : DockUserControlBase
{
    public InferenceWanImageToVideoView()
    {
        InitializeComponent();
    }
}
