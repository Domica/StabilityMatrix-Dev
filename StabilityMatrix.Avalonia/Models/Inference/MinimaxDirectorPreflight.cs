using System.Text.Json.Serialization;
using Avalonia.Media;

namespace StabilityMatrix.Avalonia.Models.Inference;

/// <summary>How serious a preflight finding is. Only <see cref="Error"/> blocks a run.</summary>
public enum PreflightSeverity
{
    Ok = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// One line in the Director preflight report. Run before a long session so a missing model,
/// a broken reference-image path or an unusable workflow is caught while it is still cheap to fix.
/// </summary>
public sealed class MinimaxDirectorPreflightCheck
{
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#2E7D32"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#B26A00"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#B3261E"));

    public required string Label { get; init; }

    public required string Detail { get; init; }

    public PreflightSeverity Severity { get; init; } = PreflightSeverity.Ok;

    [JsonIgnore]
    public string Glyph => Severity switch
    {
        PreflightSeverity.Ok => "OK",
        PreflightSeverity.Warning => "!",
        _ => "X",
    };

    [JsonIgnore]
    public IBrush BadgeBrush => Severity switch
    {
        PreflightSeverity.Ok => OkBrush,
        PreflightSeverity.Warning => WarningBrush,
        _ => ErrorBrush,
    };
}
