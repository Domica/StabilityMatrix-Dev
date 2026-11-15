using System;
using System.Globalization;
using Avalonia.Data.Converters;
using StabilityMatrix.Core.Models.Database;

namespace StabilityMatrix.Avalonia.Converters;

/// <summary>
/// Resolves the display URI of an <see cref="LocalImageFile"/>.
/// <para>
/// For videos the generated poster-frame thumbnail is used when it is available,
/// because the image loaders cannot decode video containers. Falls back to the
/// file itself otherwise.
/// </para>
/// </summary>
public class ImageFileUriConverter : IValueConverter
{
    private static readonly FileUriConverter FileUriConverter = new();

    /// <inheritdoc />
    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    )
    {
        if (targetType != typeof(Uri))
        {
            return null;
        }

        if (value is LocalImageFile file)
        {
            var path = file.ThumbnailPath ?? file.AbsolutePath;
            return FileUriConverter.Convert(path, targetType, parameter, culture);
        }

        return FileUriConverter.Convert(value, targetType, parameter, culture);
    }

    /// <inheritdoc />
    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
