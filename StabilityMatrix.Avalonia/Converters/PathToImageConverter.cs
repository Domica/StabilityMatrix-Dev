using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace StabilityMatrix.Avalonia.Converters;

/// <summary>
/// Loads a local image file (by absolute path) into an <see cref="IImage"/> for preview
/// thumbnails. Decodes to a small width so a few reference images do not blow up host RAM.
/// Results are cached by path.
/// </summary>
    [Localizable(false)]
    public class PathToImageConverter : IValueConverter
    {
        private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        if (Cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            using var stream = File.OpenRead(path);
            // Decode to a modest width; the Image control scales it to the thumbnail size.
            var bitmap = Bitmap.DecodeToWidth(stream, 240);
            Cache[path] = bitmap;
            return bitmap;
        }
        catch
        {
            Cache[path] = null;
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException($"{nameof(PathToImageConverter)} does not support ConvertBack.");
    }
}
