using System.Globalization;
using Avalonia.Media;

namespace StabilityMatrix.Avalonia.Models.Inference;

/// <summary>
/// One block in the Director's visual timeline. Block width is proportional to clip duration,
/// colour reflects the clip's run status, and skipped/resume blocks are dimmed.
/// </summary>
public sealed class MinimaxDirectorTimelineSegment
{
    private static readonly IBrush QueuedBrush = new SolidColorBrush(Color.Parse("#3C4A5A"));
    private static readonly IBrush RunningBrush = new SolidColorBrush(Color.Parse("#1D6FB8"));
    private static readonly IBrush DoneBrush = new SolidColorBrush(Color.Parse("#2E7D32"));
    private static readonly IBrush FailedBrush = new SolidColorBrush(Color.Parse("#B3261E"));
    private static readonly IBrush SkippedBrush = new SolidColorBrush(Color.Parse("#5A5A5A"));

    public required int Index { get; init; }

    public required string Title { get; init; }

    public required double Seconds { get; init; }

    public required int WindowCount { get; init; }

    public required string Status { get; init; }

    /// <summary>Precomputed pixel width so the XAML only needs a plain binding.</summary>
    public required double Width { get; init; }

    public bool IsResumeStart { get; init; }

    public bool IsSkipped { get; init; }

    public string IndexLabel => "#" + Index.ToString(CultureInfo.InvariantCulture);

    public string Detail =>
        $"{Seconds.ToString("0.#", CultureInfo.InvariantCulture)}s · "
        + $"{WindowCount.ToString(CultureInfo.InvariantCulture)}w";

    public string ToolTip =>
        $"Clip #{Index}: {Title}\n"
        + $"Duration: {Seconds.ToString("0.#", CultureInfo.InvariantCulture)}s · "
        + $"Windows: {WindowCount}\n"
        + $"Status: {Status}"
        + (IsResumeStart ? "\nRun starts here" : "")
        + (IsSkipped ? "\nSkipped by resume settings" : "");

    public IBrush Fill
    {
        get
        {
            if (IsSkipped)
            {
                return SkippedBrush;
            }

            if (Status.StartsWith("Done", StringComparison.OrdinalIgnoreCase)
                || Status.StartsWith("Kept", StringComparison.OrdinalIgnoreCase))
            {
                return DoneBrush;
            }

            if (Status.StartsWith("Running", StringComparison.OrdinalIgnoreCase))
            {
                return RunningBrush;
            }

            if (Status.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
                || Status.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                || Status.StartsWith("Cancelled", StringComparison.OrdinalIgnoreCase)
                || Status.Contains("no output", StringComparison.OrdinalIgnoreCase))
            {
                return FailedBrush;
            }

            return QueuedBrush;
        }
    }
}
