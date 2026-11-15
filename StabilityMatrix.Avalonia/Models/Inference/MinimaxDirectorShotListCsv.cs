using System.Globalization;
using System.Text;

namespace StabilityMatrix.Avalonia.Models.Inference;

/// <summary>A single row parsed from a Director shot-list CSV.</summary>
public sealed record MinimaxDirectorShotRow(
    string Label,
    string Prompt,
    double Seconds,
    long Seed,
    int? Width,
    int? Height,
    int? Fps,
    int? Steps,
    string ContinuityMode,
    IReadOnlyList<string> ReferenceImages
);

/// <summary>
/// CSV shot-list support for the Director screen. A shot list is the natural way to author a
/// multi-clip session in a spreadsheet: one row per clip, with an optional per-clip override set.
///
/// Recognised columns (case/space/underscore insensitive):
///   label | shot | title
///   prompt | description | text
///   seconds | duration | duration_s | length
///   seed
///   width | w
///   height | h
///   resolution (WxH, overrides width/height)
///   fps
///   steps
///   continuity | continuity_mode
///   ref_images | references | reference_images | image_refs  (paths separated by '|')
///
/// A file with no recognised header is treated as headerless: column 0 is the prompt,
/// column 1 (if present) is the label, column 2 (if present) is seconds.
/// </summary>
public static class MinimaxDirectorShotListCsv
{
    public static readonly string[] Columns =
    [
        "label", "prompt", "seconds", "seed", "width", "height",
        "fps", "steps", "continuity", "ref_images",
    ];

    // Keys are already normalised (lowercase, no separators) - see NormalizeKey.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["label"] = "label",
        ["shot"] = "label",
        ["title"] = "label",
        ["name"] = "label",
        ["prompt"] = "prompt",
        ["description"] = "prompt",
        ["text"] = "prompt",
        ["seconds"] = "seconds",
        ["duration"] = "seconds",
        ["durations"] = "seconds",
        ["durationsec"] = "seconds",
        ["length"] = "seconds",
        ["seed"] = "seed",
        ["width"] = "width",
        ["w"] = "width",
        ["height"] = "height",
        ["h"] = "height",
        ["resolution"] = "resolution",
        ["fps"] = "fps",
        ["steps"] = "steps",
        ["continuity"] = "continuity",
        ["continuitymode"] = "continuity",
        ["refimages"] = "ref_images",
        ["references"] = "ref_images",
        ["referenceimages"] = "ref_images",
        ["imagerefs"] = "ref_images",
    };

    private static string NormalizeKey(string raw) =>
        new(raw.Trim().TrimStart('\uFEFF').ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private static string? MapColumn(string raw)
    {
        var key = NormalizeKey(raw);
        return Aliases.TryGetValue(key, out var canonical) ? canonical : null;
    }

    /// <summary>Picks the delimiter with the most occurrences outside quotes on the first line.</summary>
    private static char DetectDelimiter(string text)
    {
        var firstLine = text.Split('\n')[0];
        var best = ',';
        var bestCount = -1;

        foreach (var candidate in new[] { ',', ';', '\t' })
        {
            var inQuotes = false;
            var count = 0;
            foreach (var ch in firstLine)
            {
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (ch == candidate && !inQuotes)
                {
                    count++;
                }
            }

            if (count > bestCount)
            {
                bestCount = count;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// RFC 4180-ish parser: handles quoted fields, escaped double quotes, embedded newlines,
    /// CRLF/LF line endings, a UTF-8 BOM, and comma / semicolon / tab delimiters.
    /// </summary>
    private static List<List<string>> ParseRows(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"' when field.Length == 0:
                    inQuotes = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    if (row.Any(x => !string.IsNullOrWhiteSpace(x)))
                    {
                        rows.Add(row);
                    }
                    row = new List<string>();
                    break;
                default:
                    if (ch == delimiter)
                    {
                        row.Add(field.ToString());
                        field.Clear();
                    }
                    else
                    {
                        field.Append(ch);
                    }
                    break;
            }
        }

        row.Add(field.ToString());
        if (row.Any(x => !string.IsNullOrWhiteSpace(x)))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static string Get(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> map, string column)
    {
        if (!map.TryGetValue(column, out var index) || index < 0 || index >= row.Count)
        {
            return "";
        }

        return row[index].Trim();
    }

    private static double ParseDouble(string raw, double fallback, double min, double max)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && !double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            return fallback;
        }

        return double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }

    private static long ParseLong(string raw, long fallback) =>
        long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static int? ParseNullableInt(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>Parses shot-list text into rows. Blank/invalid rows are ignored.</summary>
    public static IReadOnlyList<MinimaxDirectorShotRow> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<MinimaxDirectorShotRow>();
        }

        var clean = text.TrimStart('\uFEFF');
        var delimiter = DetectDelimiter(clean);
        var rows = ParseRows(clean, delimiter);
        if (rows.Count == 0)
        {
            return Array.Empty<MinimaxDirectorShotRow>();
        }

        var header = rows[0];
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++)
        {
            if (MapColumn(header[i]) is { } canonical && !map.ContainsKey(canonical))
            {
                map[canonical] = i;
            }
        }

        var hasHeader = map.ContainsKey("prompt") || map.ContainsKey("label");
        var dataRows = hasHeader ? rows.Skip(1).ToList() : rows;
        var promptIndex = hasHeader ? map.GetValueOrDefault("prompt", -1) : 0;
        var labelIndex = hasHeader ? map.GetValueOrDefault("label", -1) : 1;
        var secondsIndex = hasHeader ? map.GetValueOrDefault("seconds", -1) : 2;

        var result = new List<MinimaxDirectorShotRow>();
        foreach (var row in dataRows)
        {
            var prompt = promptIndex >= 0 && promptIndex < row.Count
                ? row[promptIndex].Trim()
                : "";
            if (string.IsNullOrWhiteSpace(prompt))
            {
                continue;
            }

            var label = hasHeader
                ? Get(row, map, "label")
                : (labelIndex >= 0 && labelIndex < row.Count ? row[labelIndex].Trim() : "");

            var secondsRaw = hasHeader
                ? Get(row, map, "seconds")
                : (secondsIndex >= 0 && secondsIndex < row.Count ? row[secondsIndex].Trim() : "");

            var width = hasHeader ? ParseNullableInt(Get(row, map, "width")) : null;
            var height = hasHeader ? ParseNullableInt(Get(row, map, "height")) : null;

            if (hasHeader)
            {
                var resolution = Get(row, map, "resolution");
                var parts = resolution.Split(
                    new[] { 'x', 'X', '*', ':' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                );
                if (parts.Length == 2
                    && int.TryParse(parts[0], out var rw)
                    && int.TryParse(parts[1], out var rh))
                {
                    width = rw;
                    height = rh;
                }
            }

            var continuity = hasHeader ? Get(row, map, "continuity") : "";
            if (!string.IsNullOrWhiteSpace(continuity))
            {
                continuity = continuity.ToLowerInvariant() switch
                {
                    "inherit" or "last" or "last_frame" or "lastframe" => "inherit",
                    "none" or "independent" or "off" => "none",
                    "manual" or "reference" or "ref" => "manual",
                    _ => "inherit",
                };
            }
            else
            {
                continuity = "inherit";
            }

            var refsRaw = hasHeader ? Get(row, map, "ref_images") : "";
            var references = string.IsNullOrWhiteSpace(refsRaw)
                ? Array.Empty<string>()
                : refsRaw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToArray();

            result.Add(new MinimaxDirectorShotRow(
                Label: label,
                Prompt: prompt,
                Seconds: ParseDouble(secondsRaw, 6, 0.5, 600),
                Seed: hasHeader ? ParseLong(Get(row, map, "seed"), -1) : -1,
                Width: width,
                Height: height,
                Fps: hasHeader ? ParseNullableInt(Get(row, map, "fps")) : null,
                Steps: hasHeader ? ParseNullableInt(Get(row, map, "steps")) : null,
                ContinuityMode: continuity,
                ReferenceImages: references
            ));
        }

        return result;
    }

    private static string Escape(string value)
    {
        var needsQuotes = value.Contains(',')
                          || value.Contains('"')
                          || value.Contains('\r')
                          || value.Contains('\n');
        var escaped = value.Replace("\"", "\"\"");
        return needsQuotes ? $"\"{escaped}\"" : escaped;
    }

    /// <summary>Exports the current queue as a round-trippable shot-list CSV.</summary>
    public static string Serialize(IEnumerable<MinimaxDirectorClip> clips)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", Columns));

        foreach (var clip in clips)
        {
            var fields = new[]
            {
                clip.Label,
                clip.Prompt,
                clip.Seconds.ToString("0.#", CultureInfo.InvariantCulture),
                clip.Seed.ToString(CultureInfo.InvariantCulture),
                clip.WidthOverride?.ToString(CultureInfo.InvariantCulture) ?? "",
                clip.HeightOverride?.ToString(CultureInfo.InvariantCulture) ?? "",
                clip.FpsOverride?.ToString(CultureInfo.InvariantCulture) ?? "",
                clip.StepsOverride?.ToString(CultureInfo.InvariantCulture) ?? "",
                clip.ContinuityMode,
                string.Join("|", clip.ReferenceImagePaths),
            };

            builder.AppendLine(string.Join(",", fields.Select(Escape)));
        }

        return builder.ToString();
    }
}
