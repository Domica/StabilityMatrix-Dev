using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Data;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Video;

/// <summary>
/// Enum equality converter for XAML bindings using enum values.
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
    /// Converts an enum value to boolean by comparing it with the parameter.
    /// Returns true if the values are equal.
    /// </summary>
    /// <param name="value">Enum value (e.g. VideoFormat.WebP)</param>
    /// <param name="targetType">Target type (usually bool)</param>
    /// <param name="parameter">Comparison value (e.g. "WebP")</param>
    /// <param name="culture">Culture info</param>
    /// <returns>True if values match, otherwise false</returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Null check – both value and parameter must be non-null
        if (value is null || parameter is null)
            return false;

        // Fast path: direct enum comparison (no ToString allocation)
        if (value.Equals(parameter))
            return true;

        // Fallback: parameter provided as string (e.g. "WebP")
        if (value is Enum enumValue && parameter is string strParam)
            return enumValue.ToString() == strParam;

        return false;
    }

    /// <summary>
    /// Converts a boolean value back to an enum value.
    /// Enables TwoWay binding scenarios.
    /// </summary>
    /// <param name="value">Boolean value (true/false)</param>
    /// <param name="targetType">Target enum type</param>
    /// <param name="parameter">Enum value to return when true</param>
    /// <param name="culture">Culture info</param>
    /// <returns>Enum value when true, otherwise Binding.DoNothing</returns>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Only act on true; otherwise leave binding unchanged
        if (value is not bool boolValue || !boolValue || parameter is null)
            return BindingOperations.DoNothing;

        // Target must be an enum
        if (!targetType.IsEnum)
            return BindingOperations.DoNothing;

        try
        {
            // Parameter already matches target enum type
            if (parameter.GetType() == targetType)
                return parameter;

            // Parse enum from string parameter
            return Enum.Parse(targetType, parameter.ToString()!, ignoreCase: true);
        }
        catch
        {
            // Parsing failed – do not update binding
            return BindingOperations.DoNothing;
        }
    }
}
