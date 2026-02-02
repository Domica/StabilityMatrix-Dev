using Avalonia.Controls;
using StabilityMatrix.Core.Attributes;

namespace StabilityMatrix.Avalonia.Views.Inference;

[View(typeof(ViewModels.Inference.InferenceImageOutpaintViewModel))]
public partial class InferenceImageOutpaintView : UserControl
{
    public InferenceImageOutpaintView()
    {
        InitializeComponent();
    }
}
