using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceVideoUpscaleView>]
public partial class InferenceVideoUpscaleView : DockUserControlBase
{
    public InferenceVideoUpscaleView()
    {
        InitializeComponent();
    }
}
