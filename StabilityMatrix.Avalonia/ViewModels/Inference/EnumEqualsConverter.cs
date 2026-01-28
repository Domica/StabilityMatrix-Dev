using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Data;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Video;

/// <summary>
/// Enum equality converter za XAML binding sa enum vrijednostima.
/// 
/// Korištenje:
/// <code>
/// <![CDATA[
/// <StackPanel IsVisible="{Binding Format, 
///     Converter={StaticResource EnumEqualsConverter}, 
///     ConverterParameter=WebP}">
/// ]]>
/// </code>
/// 
/// Rezultat: True ako je Format == VideoFormat.WebP
/// </summary>
public class EnumEqualsConverter : IValueConverter
{
    /// <summary>
    /// Konvertuj enum vrijednost u boolean poređenjem sa parametrom.
    /// Vraća true ako su vrijednosti jednake.
    /// </summary>
    /// <param name="value">Enum vrijednost (npr. VideoFormat.WebP)</param>
    /// <param name="targetType">Tip konverzije (obično bool)</param>
    /// <param name="parameter">Vrijednost za poređenje (npr. "WebP")</param>
    /// <param name="culture">Kultura za konverziju</param>
    /// <returns>True ako su vrijednosti jednake, inače False</returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Null check - obje vrijednosti moraju biti non-null
        if (value == null || parameter == null)
            return false;

        // ✅ OPTIMIZACIJA: Direktno poređenje bez ToString()
        // To je brže jer enum ima ugrađenu Equals implementaciju
        if (value.Equals(parameter))
            return true;

        // Fallback: String poređenje ako je parameter string
        // Ovo je korisno ako je ConverterParameter string umjesto enum vrijednosti
        if (value is Enum enumValue && parameter is string strParam)
        {
            return enumValue.ToString() == strParam;
        }

        return false;
    }

    /// <summary>
    /// Konvertuj boolean vrijednost nazad u enum vrijednost.
    /// Omogućava TwoWay binding.
    /// </summary>
    /// <param name="value">Boolean vrijednost (true/false)</param>
    /// <param name="targetType">Enum tip za konverziju</param>
    /// <param name="parameter">Enum vrijednost za vraćanje ako je true</param>
    /// <param name="culture">Kultura za konverziju</param>
    /// <returns>Enum vrijednost ako je true, inače Binding.DoNothing</returns>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Ako vrijednost nije true, nemoj ništa raditi
        if (!(value is bool boolValue) || !boolValue || parameter == null)
            return Binding.DoNothing;

        // Ako je target type enum, pokušaj parsirati parameter
        if (targetType.IsEnum)
        {
            try
            {
                // Ako je parameter već enum vrijednost, samo ga vrati
                if (parameter.GetType() == targetType)
                {
                    return parameter;
                }

                // Inače pokušaj parsirati string
                return Enum.Parse(targetType, parameter.ToString()!);
            }
            catch (ArgumentException)
            {
                // Ako parsiranje ne uspije, vrati Binding.DoNothing
                return Binding.DoNothing;
            }
        }

        return Binding.DoNothing;
    }
}
