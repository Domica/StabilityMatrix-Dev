using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Video;

/// <summary>
/// Enum equality converter for XAML binding with enum values.
/// 
/// Usage:
/// <code>
/// <![CDATA[
/// <StackPanel IsVisible="{Binding Format, 
///     Converter={StaticResource EnumEqualsConverter}, 
///     ConverterParameter=WebP}">
/// ]]>
/// </code>
/// 
/// Result: True if Format == VideoFormat.WebP
/// </summary>
public class EnumEqualsConverter : IValueConverter
{
    /// <summary>
    /// Convert enum value to boolean by comparing with parameter.
    /// Returns true if values are equal.
    /// </summary>
    /// <param name="value">Enum value (e.g. VideoFormat.WebP)</param>
    /// <param name="targetType">Conversion target type (usually bool)</param>
    /// <param name="parameter">Value to compare with (e.g. "WebP")</param>
    /// <param name="culture">Culture for conversion</param>
    /// <returns>True if values are equal, otherwise False</returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Null check - both values must be non-null
        if (value == null || parameter == null)
            return false;

        // ✅ OPTIMIZATION: Direct comparison without ToString()
        // This is faster because enum has built-in Equals implementation
        if (value.Equals(parameter))
            return true;

        // Fallback: String comparison if parameter is string
        // This is useful if ConverterParameter is string instead of enum value
        if (value is Enum enumValue && parameter is string strParam)
        {
            return enumValue.ToString() == strParam;
        }

        return false;
    }

    /// <summary>
    /// Convert boolean value back to enum value.
    /// Enables TwoWay binding.
    /// </summary>
    /// <param name="value">Boolean value (true/false)</param>
    /// <param name="targetType">Enum type for conversion</param>
    /// <param name="parameter">Enum value to return if true</param>
    /// <param name="culture">Culture for conversion</param>
    /// <returns>Enum value if true, otherwise BindingOperations.DoNothing</returns>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // If value is not true, don't do anything
        if (!(value is bool boolValue) || !boolValue || parameter == null)
            return BindingOperations.DoNothing;

        // If target type is enum, try to parse parameter
        if (targetType.IsEnum)
        {
            try
            {
                // If parameter is already an enum value, just return it
                if (parameter.GetType() == targetType)
                {
                    return parameter;
                }

                // Otherwise try to parse string
                return Enum.Parse(targetType, parameter.ToString()!);
            }
            catch (ArgumentException)
            {
                // If parsing fails, return BindingOperations.DoNothing
                return BindingOperations.DoNothing;
            }
        }

        return BindingOperations.DoNothing;
    }
}
