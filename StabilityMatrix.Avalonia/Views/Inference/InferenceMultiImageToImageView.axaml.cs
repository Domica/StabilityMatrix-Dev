using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceMultiImageToImageView>]
public partial class InferenceMultiImageToImageView : DockUserControlBase
{
    public InferenceMultiImageToImageView()
    {
        InitializeComponent();
    }
}
