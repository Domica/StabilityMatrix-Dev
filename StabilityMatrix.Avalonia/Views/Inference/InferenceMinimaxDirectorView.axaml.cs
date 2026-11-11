using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceMinimaxDirectorView>]
public partial class InferenceMinimaxDirectorView : DockUserControlBase
{
    public InferenceMinimaxDirectorView()
    {
        InitializeComponent();
    }
}
