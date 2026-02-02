using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(Avalonia.Controls.Inference.OutpaintCard))]
[ManagedService]
[Transient]
public partial class OutpaintCardViewModel : LoadableViewModelBase
{
    public const string ModuleKey = "Outpaint";

    [ObservableProperty]
    private int expandLeft;

    [ObservableProperty]
    private int expandRight;

    [ObservableProperty]
    private int expandTop;

    [ObservableProperty]
    private int expandBottom;

    [ObservableProperty]
    private int feathering = 40;

    [ObservableProperty]
    private OutpaintDirection selectedDirection = OutpaintDirection.Custom;

    public OutpaintCardViewModel()
    {
    }

    [RelayCommand]
    private void SetDirectionPreset(string direction)
    {
        if (!Enum.TryParse<OutpaintDirection>(direction, out var dir))
            return;

        SelectedDirection = dir;

        // Reset all
        ExpandLeft = 0;
        ExpandRight = 0;
        ExpandTop = 0;
        ExpandBottom = 0;

        // Set based on direction
        const int defaultExpansion = 256;
        const int allExpansion = 128;

        switch (dir)
        {
            case OutpaintDirection.Left:
                ExpandLeft = defaultExpansion;
                break;
            case OutpaintDirection.Right:
                ExpandRight = defaultExpansion;
                break;
            case OutpaintDirection.Top:
                ExpandTop = defaultExpansion;
                break;
            case OutpaintDirection.Bottom:
                ExpandBottom = defaultExpansion;
                break;
            case OutpaintDirection.All:
                ExpandLeft = ExpandRight = ExpandTop = ExpandBottom = allExpansion;
                break;
            case OutpaintDirection.Horizontal:
                ExpandLeft = ExpandRight = defaultExpansion;
                break;
            case OutpaintDirection.Vertical:
                ExpandTop = ExpandBottom = defaultExpansion;
                break;
        }
    }

    [RelayCommand]
    private void Reset()
    {
        ExpandLeft = 0;
        ExpandRight = 0;
        ExpandTop = 0;
        ExpandBottom = 0;
        Feathering = 40;
        SelectedDirection = OutpaintDirection.Custom;
    }
}

public enum OutpaintDirection
{
    Custom,
    Left,
    Right,
    Top,
    Bottom,
    All,
    Horizontal,
    Vertical
}
