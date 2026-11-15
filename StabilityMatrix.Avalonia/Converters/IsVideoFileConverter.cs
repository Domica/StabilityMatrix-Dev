using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace StabilityMatrix.Avalonia.Converters;

/// <summary>
/// Returns true when the bound string (a file path) has a video extension
/// (.mp4/.webm/.mkv/.mov/.avi), so the gallery can show a video badge/overlay.
/// </summary>
public class IsVideoFileConverter : IValueConverter
{
    private static readonly string[] VideoExtensions = [".mp4", ".webm", ".mkv", ".mov", ".avi"];

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    )
    {
        if (value is string path && !string.IsNullOrEmpty(path))
        {
            var extension = System.IO.Path.GetExtension(path);
            return Array.Exists(
                VideoExtensions,
                ext => ext.Equals(extension, StringComparison.OrdinalIgnoreCase)
            );
        }

        return false;
    }

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
