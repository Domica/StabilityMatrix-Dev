using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using StabilityMatrix.Avalonia.Models.Inference;

namespace StabilityMatrix.Avalonia.Controls;

/// <summary>
/// Shows a video frame and lets the user place SAM2 prompt points:
/// left click = positive point (replace this), right click = negative point (not this).
/// Points are reported in pixel coordinates of the original frame.
/// </summary>
public class FramePointPicker : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty = AvaloniaProperty.Register<
        FramePointPicker,
        Bitmap?
    >(nameof(Source));

    public static readonly StyledProperty<IReadOnlyList<MaskPoint>?> PositivePointsProperty =
        AvaloniaProperty.Register<FramePointPicker, IReadOnlyList<MaskPoint>?>(nameof(PositivePoints));

    public static readonly StyledProperty<IReadOnlyList<MaskPoint>?> NegativePointsProperty =
        AvaloniaProperty.Register<FramePointPicker, IReadOnlyList<MaskPoint>?>(nameof(NegativePoints));

    public static readonly StyledProperty<ICommand?> PointClickedCommandProperty = AvaloniaProperty.Register<
        FramePointPicker,
        ICommand?
    >(nameof(PointClickedCommand));

    static FramePointPicker()
    {
        AffectsRender<FramePointPicker>(SourceProperty, PositivePointsProperty, NegativePointsProperty);
        AffectsMeasure<FramePointPicker>(SourceProperty);
    }

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public IReadOnlyList<MaskPoint>? PositivePoints
    {
        get => GetValue(PositivePointsProperty);
        set => SetValue(PositivePointsProperty, value);
    }

    public IReadOnlyList<MaskPoint>? NegativePoints
    {
        get => GetValue(NegativePointsProperty);
        set => SetValue(NegativePointsProperty, value);
    }

    /// <summary>Command invoked with a <see cref="MaskPointClick"/> parameter.</summary>
    public ICommand? PointClickedCommand
    {
        get => GetValue(PointClickedCommandProperty);
        set => SetValue(PointClickedCommandProperty, value);
    }

    /// <summary>Destination rectangle of the bitmap inside this control (uniform fit, centered).</summary>
    private bool TryGetImageRect(out Rect rect, out double scale)
    {
        rect = default;
        scale = 1;

        if (Source is not { } bitmap || bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0)
            return false;

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return false;

        scale = Math.Min(bounds.Width / bitmap.PixelSize.Width, bounds.Height / bitmap.PixelSize.Height);
        var width = bitmap.PixelSize.Width * scale;
        var height = bitmap.PixelSize.Height * scale;
        rect = new Rect((bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height);
        return true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Source is not { } bitmap)
            return new Size(0, 0);

        var width = double.IsInfinity(availableSize.Width) ? bitmap.PixelSize.Width : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? bitmap.PixelSize.Height : availableSize.Height;
        var scale = Math.Min(width / bitmap.PixelSize.Width, height / bitmap.PixelSize.Height);
        return new Size(bitmap.PixelSize.Width * scale, bitmap.PixelSize.Height * scale);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Source is not { } bitmap || !TryGetImageRect(out var rect, out var scale))
            return;

        context.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height), rect);

        DrawPoints(context, PositivePoints, Brushes.LimeGreen, rect, scale);
        DrawPoints(context, NegativePoints, Brushes.Red, rect, scale);
    }

    private static void DrawPoints(
        DrawingContext context,
        IReadOnlyList<MaskPoint>? points,
        IBrush fill,
        Rect imageRect,
        double scale
    )
    {
        if (points is null)
            return;

        var outline = new Pen(Brushes.White, 2);
        foreach (var point in points)
        {
            var center = new Point(imageRect.X + point.X * scale, imageRect.Y + point.Y * scale);
            context.DrawEllipse(fill, outline, center, 7, 7);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!TryGetImageRect(out var rect, out var scale))
            return;

        var current = e.GetCurrentPoint(this);
        var isPositive = current.Properties.IsLeftButtonPressed;
        var isNegative = current.Properties.IsRightButtonPressed;
        if (!isPositive && !isNegative)
            return;

        var position = current.Position;
        if (!rect.Contains(position))
            return;

        var x = (position.X - rect.X) / scale;
        var y = (position.Y - rect.Y) / scale;

        var click = new MaskPointClick(x, y, isPositive);
        if (PointClickedCommand is { } command && command.CanExecute(click))
        {
            command.Execute(click);
        }

        e.Handled = true;
    }
}
