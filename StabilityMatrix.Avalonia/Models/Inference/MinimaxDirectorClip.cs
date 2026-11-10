using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StabilityMatrix.Avalonia.Models.Inference;

/// <summary>
/// A single clip in a MiniMax Director session. Mirrors VENO Director's ClipPlan:
/// each clip has its own prompt/duration/seed and may override global generation settings.
/// </summary>
public partial class MinimaxDirectorClip : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private string prompt = "";

    [ObservableProperty]
    private double seconds = 6;

    /// <summary>-1 means "derive a random seed from the session seed".</summary>
    [ObservableProperty]
    private long seed = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private string label = "";

    [ObservableProperty]
    private int? widthOverride;

    [ObservableProperty]
    private int? heightOverride;

    [ObservableProperty]
    private int? fpsOverride;

    [ObservableProperty]
    private int? stepsOverride;

    /// <summary>last_frame | none | manual</summary>
    [ObservableProperty]
    private string continuityMode = "last_frame";

    /// <summary>
    /// Local reference images. These are fed into the H3 workflow's ref_images slots
    /// (character / scene references). With ContinuityMode "manual" they are the only
    /// conditioning; with "last_frame" they are combined with the previous clip's last frame.
    /// With "none" they are ignored (fully independent clip).
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<string> referenceImagePaths = new();

    [ObservableProperty]
    [property: JsonIgnore]
    private string status = "Queued";

    [ObservableProperty]
    [property: JsonIgnore]
    private bool isRunning;

    [JsonIgnore]
    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Label)
            ? (string.IsNullOrWhiteSpace(Prompt) ? "(empty prompt)" : Prompt.Trim().Replace('\n', ' '))
            : Label;

    public MinimaxDirectorClip Clone() =>
        new()
        {
            Prompt = Prompt,
            Seconds = Seconds,
            Seed = Seed,
            Label = Label,
            WidthOverride = WidthOverride,
            HeightOverride = HeightOverride,
            FpsOverride = FpsOverride,
            StepsOverride = StepsOverride,
            ContinuityMode = ContinuityMode,
            ReferenceImagePaths = new ObservableCollection<string>(ReferenceImagePaths),
        };
}
