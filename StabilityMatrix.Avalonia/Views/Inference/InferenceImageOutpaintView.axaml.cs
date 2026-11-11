using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Core.Attributes;

namespace StabilityMatrix.Avalonia.Views.Inference;

[View(typeof(ViewModels.Inference.InferenceImageOutpaintViewModel))]
public partial class InferenceImageOutpaintView : UserControlBase  // ✅ UserControlBase
{
    public InferenceImageOutpaintView()
    {
        InitializeComponent();
    }
}
