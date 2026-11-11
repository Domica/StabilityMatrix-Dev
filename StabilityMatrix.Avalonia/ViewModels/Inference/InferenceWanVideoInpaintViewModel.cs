using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Models.FileInterfaces;
using System.Text.Json;
using Avalonia.Controls.Notifications;
using Avalonia.Media.Imaging;
using System.Text.Json.Serialization;
using Injectio.Attributes;
using SkiaSharp;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Controls;
using StabilityMatrix.Avalonia.Views.Inference;
using System.Threading.Tasks;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Services;
using StabilityMatrix.Avalonia.Extensions;
using StabilityMatrix.Avalonia.ViewModels.Settings;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using System.Security.Cryptography;
using System.Text;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceWanVideoInpaintView), IsPersistent = true)]
[RegisterScoped<InferenceWanVideoInpaintViewModel>, ManagedService]
public partial class InferenceWanVideoInpaintViewModel : InferenceWanTextToVideoViewModel, IParametersLoadableState
{
    [JsonPropertyName("MaskImage")]
    public SelectImageCardViewModel MaskImageCardViewModel { get; }

    [JsonPropertyName("ReferenceImage")]
    public SelectImageCardViewModel ReferenceImageCardViewModel { get; }

    [ObservableProperty]
    [JsonPropertyName("VideoInput")]
    private string? videoInputPath;

    /// <summary>
    /// When enabled the mask is created inside Stability Matrix: click points on the first frame and
    /// SAM2 tracks the object through the video. When disabled the mask image card is used as a static mask.
    /// </summary>
    [ObservableProperty]
    [JsonPropertyName("UseSam2Tracking")]
    [NotifyPropertyChangedFor(nameof(EffectiveMotionPreservation), nameof(MotionPreservationLabel))]
    private bool useSam2Tracking = true;

    [ObservableProperty]
    [JsonPropertyName("Sam2Model")]
    private string sam2ModelName = "sam2.1_hiera_small.safetensors";

    /// <summary>
    /// Kijai's DownloadAndLoadSAM2Model rewrites "sam2.1_*" filenames to "*-fp16.safetensors"
    /// for fp16/bf16. fp32 uses the exact filename, which matches the checkpoint shipped with
    /// Stability Matrix, so it is the safe default.
    /// </summary>
    [ObservableProperty]
    [JsonPropertyName("Sam2Precision")]
    private string sam2Precision = "fp32";

    [JsonIgnore]
    public IReadOnlyList<string> Sam2Precisions { get; } = new[] { "fp32", "fp16", "bf16" };

    /// <summary>
    /// Pixels the mask is grown by (covers hair/edges/shadows the tracker misses).
    /// Defaults to 0 because growing a SAM2 mask blurs the tracked outline, and growing a
    /// non-binary image (the old manual-mode behaviour) made the mask cover the whole frame.
    /// </summary>
    [ObservableProperty]
    [JsonPropertyName("MaskGrow")]
    private int maskGrow = 0;

    /// <summary>
    /// How much of the ORIGINAL frame is kept inside the masked region of control_video.
    /// VACE's reactive channel (control_video * mask + 0.5) is the only place where the model
    /// can see the original face motion. A fully blanked mask leaves only the static
    /// reference_image and the prompt, so the generated face freezes. Blending the original
    /// frames back in restores lip/head motion at the cost of a weaker identity change.
    /// 0 = fully blanked (previous behaviour, frozen face); 1 = original face reproduced.
    /// DEFAULT IS 0: for face swap, any original-face pixels in the reactive channel give VACE
    /// a strong identity prior and it reproduces the original person instead of the reference
    /// image. Set > 0 only if you explicitly want to trade identity strength for motion.
    /// </summary>
    [ObservableProperty]
    [JsonPropertyName("MotionPreservation")]
    [NotifyPropertyChangedFor(nameof(EffectiveMotionPreservation), nameof(MotionPreservationLabel))]
    private double motionPreservation = 0.0d;

    /// <summary>
    /// Master switch for temporal splitting. When enabled the input video is processed in short
    /// sequential chunks (one ComfyUI prompt at a time, so peak VRAM stays low) and the resulting
    /// MP4s are joined with ffmpeg into a single video with the same frame count / FPS / duration
    /// as the input.
    /// </summary>
    [ObservableProperty]
    [JsonPropertyName("EnableChunkedProcessing")]
    [NotifyPropertyChangedFor(nameof(EffectiveMotionPreservation), nameof(MotionPreservationLabel))]
    private bool enableChunkedProcessing = false;

    /// <summary>
    /// Derive the frames-per-chunk from the free VRAM reported by ComfyUI's /system_stats.
    /// Falls back to <see cref="ChunkFrames"/> when the endpoint is unavailable.
    /// </summary>
    [ObservableProperty]
    [JsonPropertyName("AutoChunkSize")]
    private bool autoChunkSize = true;

    /// <summary>
    /// Frames per chunk when <see cref="AutoChunkSize"/> is off. Snapped down to Wan VACE's
    /// 4n+1 rule (5, 9, 17, 25, 49, 81 ...).
    /// </summary>
    [ObservableProperty]
    [JsonPropertyName("ChunkFrames")]
    private int chunkFrames = 25;

    /// <summary>
    /// Frames repeated between neighbouring chunks to hide temporal seams. The repeated frames
    /// are discarded before the final concat. 0 = exact, fastest, no re-encode.
    /// </summary>
    [ObservableProperty]
    [JsonPropertyName("ChunkOverlapFrames")]
    private int chunkOverlapFrames = 0;

    /// <summary>
    /// Automatically choose the "keep original motion" strength instead of using the expert
    /// slider. SAM2 erases the whole face on every frame and therefore needs a stronger motion
    /// signal than the static manual mask (which already leaks some original motion).
    /// </summary>
    [ObservableProperty]
    [JsonPropertyName("EnableAutoPreserveMovement")]
    [NotifyPropertyChangedFor(nameof(EffectiveMotionPreservation), nameof(MotionPreservationLabel))]
    private bool enableAutoPreserveMovement = false;

    /// <summary>
    /// Split-base + SAM2 only: each chunk currently re-seeds the SAME click coordinates on its
    /// own first frame. After a scene cut those coordinates land on a different person (or on
    /// the background), which is why the "wrong" face was replaced. When enabled, ffmpeg scene
    /// detection marks every chunk that is not fully inside the scene that was clicked, and
    /// those chunks are generated with an empty mask so the original footage is left alone.
    /// </summary>
    [ObservableProperty]
    [JsonPropertyName("ProtectAgainstSceneCuts")]
    private bool protectAgainstSceneCuts = true;

    /// <summary>
    /// ffmpeg's "gt(scene, X)" cut sensitivity. 0.20 was chosen after measuring the test clip:
    /// its three real cuts score 0.245 / 0.271 / 0.372 while every other frame scores &lt; 0.02,
    /// so 0.20 catches all cuts with a wide margin without reacting to normal motion.
    /// </summary>
    [ObservableProperty]
    [JsonPropertyName("SceneCutThreshold")]
    private double sceneCutThreshold = 0.20d;

    /// <summary>Value actually sent to the graph (auto heuristic or the expert slider).</summary>
    [JsonIgnore]
    public double EffectiveMotionPreservation
    {
        get
        {
            if (!EnableAutoPreserveMovement)
                return System.Math.Clamp(MotionPreservation, 0.0d, 1.0d);

            // Face swap (reference image loaded): AUTO must return 0. Even 15% of the original
            // face in the reactive channel gives VACE enough of an identity prior to reproduce
            // the original person instead of adopting the reference image. Motion preservation
            // is only useful for object removal (no reference image) where some original motion
            // is better than a frozen result.
            if (ReferenceImageCardViewModel.ImageSource is not null)
                return 0.0d;

            // Object removal / no reference image: a small motion signal prevents the
            // regenerated region from freezing. SAM2 erases the whole area so it needs a
            // slightly stronger signal than the static manual mask.
            var value = UseSam2Tracking ? 0.15d : 0.10d;
            if (EnableChunkedProcessing)
            {
                // A slightly stronger motion signal reduces identity drift at chunk seams.
                value += 0.03d;
            }

            return System.Math.Clamp(value, 0.0d, 0.30d);
        }
    }

    /// <summary>Readable label for the UI, invariant-culture formatted.</summary>
    [JsonIgnore]
    public string MotionPreservationLabel =>
        $"Effective: {EffectiveMotionPreservation.ToString("0.00", CultureInfo.InvariantCulture)}"
        + (EnableAutoPreserveMovement ? " (auto)" : " (expert slider)");

    /// <summary>Last computed split plan, shown in the UI by the "Check plan" button.</summary>
    [ObservableProperty]
    [JsonIgnore]
    private string chunkPlanSummary = "Split base is off - the whole clip is generated in one prompt.";

    [ObservableProperty]
    [JsonIgnore]
    private bool isLoadingFrame;

    private Bitmap? framePreview;

    /// <summary>
    /// First frame of the input video, shown in the point picker.
    /// </summary>
    [JsonIgnore]
    public Bitmap? FramePreview
    {
        get => framePreview;
        private set
        {
            if (SetProperty(ref framePreview, value))
                OnPropertyChanged(nameof(IsFramePreviewMissing));
        }
    }

    /// <summary>
    /// Drives the "No frame loaded" placeholder. The frame preview is only populated when
    /// ComfyUI is connected; a saved project can restore the video path without a frame.
    /// </summary>
    [JsonIgnore]
    public bool IsFramePreviewMissing => framePreview is null;

    /// <summary>Live feedback: auto (SAM2) mode needs at least one green point to run.</summary>
    [JsonIgnore]
    public string PointStatusText =>
        $"Points: {PositivePoints.Count} green / {NegativePoints.Count} red";

    /// <summary>
    /// SAM2 mask previews (first / middle / last frame). Kept separate from FramePreview so
    /// previewing a mask does not replace the video frame used for left/right clicks.
    /// </summary>
    [JsonIgnore]
    public ObservableCollection<Bitmap> MaskPreviewFrames { get; } = new();

    private IReadOnlyList<MaskPoint> positivePoints = Array.Empty<MaskPoint>();

    /// <summary>
    /// Points (frame pixel coordinates) that mark what should be replaced.
    /// </summary>
    [JsonPropertyName("PositivePoints")]
    public IReadOnlyList<MaskPoint> PositivePoints
    {
        get => positivePoints;
        set
        {
            if (SetProperty(ref positivePoints, value ?? Array.Empty<MaskPoint>()))
                OnPropertyChanged(nameof(PointStatusText));
        }
    }

    private IReadOnlyList<MaskPoint> negativePoints = Array.Empty<MaskPoint>();

    /// <summary>
    /// Points (frame pixel coordinates) that mark what must NOT be replaced.
    /// </summary>
    [JsonPropertyName("NegativePoints")]
    public IReadOnlyList<MaskPoint> NegativePoints
    {
        get => negativePoints;
        set
        {
            if (SetProperty(ref negativePoints, value ?? Array.Empty<MaskPoint>()))
                OnPropertyChanged(nameof(PointStatusText));
        }
    }

    /// <summary>
    /// Available SAM2 checkpoints (auto-downloaded by the SAM2 custom node on first use).
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> Sam2ModelNames { get; } =
        new[]
        {
            "sam2.1_hiera_tiny.safetensors",
            "sam2.1_hiera_small.safetensors",
            "sam2.1_hiera_base_plus.safetensors",
            "sam2.1_hiera_large.safetensors",
        };

    /// <summary>
    /// Command to browse and select an input video file.
    /// </summary>
    public IRelayCommand BrowseVideoCommand { get; }

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ISettingsManager settingsManager;
    private readonly INotificationService notifications;

    // Order the points were placed in, so Undo removes the newest one (true = positive)
    private readonly Stack<bool> pointHistory = new();

    public InferenceWanVideoInpaintViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
        this.settingsManager = settingsManager;
        notifications = notificationService;

        // Override sampler settings for VACE inpainting
        SamplerCardViewModel.Width = 720;
        SamplerCardViewModel.Height = 720;
        SamplerCardViewModel.Length = 81;
        SamplerCardViewModel.DenoiseStrength = 1.0d;
        SamplerCardViewModel.IsLengthEnabled = true;
        SamplerCardViewModel.IsCfgScaleEnabled = true;

        // Manual (static) mask, only used when SAM2 tracking is turned off
        MaskImageCardViewModel = vmFactory.Get<SelectImageCardViewModel>();
        MaskImageCardViewModel.IsMaskEditorEnabled = true;

        ReferenceImageCardViewModel = vmFactory.Get<SelectImageCardViewModel>();
        // The reference image is only used as VACE conditioning. A mask drawn here is never
        // uploaded nor connected to the graph, so hide the editor to avoid confusion.
        ReferenceImageCardViewModel.IsMaskEditorEnabled = false;

        StackCardViewModel.AddCards(
            ModelCardViewModel,
            SamplerCardViewModel,
            SeedCardViewModel,
            BatchSizeCardViewModel,
            VideoOutputSettingsCardViewModel
        );

        BrowseVideoCommand = new RelayCommand(async () => await BrowseVideoAsync());
    }

    /// <summary>
    /// Left click on the frame = positive point, right click = negative point.
    /// </summary>
    [RelayCommand]
    private void AddPoint(MaskPointClick? click)
    {
        if (click is null)
            return;

        var point = new MaskPoint(Math.Round(click.X, 1), Math.Round(click.Y, 1));

        if (click.IsPositive)
            PositivePoints = PositivePoints.Append(point).ToList();
        else
            NegativePoints = NegativePoints.Append(point).ToList();

        pointHistory.Push(click.IsPositive);
    }

    [RelayCommand]
    private void UndoPoint()
    {
        // History is empty after loading a saved project, so fall back to "last negative, else last positive"
        var removePositive = pointHistory.Count > 0 ? pointHistory.Pop() : NegativePoints.Count == 0;

        if (removePositive && PositivePoints.Count > 0)
            PositivePoints = PositivePoints.Take(PositivePoints.Count - 1).ToList();
        else if (NegativePoints.Count > 0)
            NegativePoints = NegativePoints.Take(NegativePoints.Count - 1).ToList();
    }

    [RelayCommand]
    private void ClearPoints()
    {
        PositivePoints = Array.Empty<MaskPoint>();
        NegativePoints = Array.Empty<MaskPoint>();
        pointHistory.Clear();
    }

    // Cached mask prepared from the manual mask card. BuildPrompt and GetInputImages() must
    // use the exact same ImageSource so the uploaded hash filename matches the node input.
    private ImageSource? preparedManualMask;
    private string? preparedManualMaskKey;
    private int preparedManualMaskReplacePixels;
    private int preparedManualMaskTotalPixels;

    // Current chunk being built. GenerateImageImpl sets these immediately before each
    // BuildPrompt call; generation is strictly sequential (await RunGeneration) so no locking
    // is needed. A value of 0 / 1 means "legacy single-prompt behaviour".
    private int currentChunkStartFrame;
    private int currentChunkLength;
    private int currentChunkIndex;
    private int currentChunkCount = 1;
    private int totalInputFrames;
    private double effectiveFps;
    private string currentRunId = "";
    private bool videoUploadedForRun;
    private bool currentChunkSkipMask;

    /// <summary>
    /// Preview of the binary mask that manual mode will actually upload. Lets the user verify
    /// the white "replace" area before spending two minutes on a generation.
    /// </summary>
    [ObservableProperty]
    [JsonIgnore]
    private Bitmap? manualMaskPreview;

    /// <summary>
    /// The filename Kijai's node actually looks for. For ANY non-fp32 precision the node
    /// appends "-fp16" (also for bf16), so staging has to use that exact name.
    /// </summary>
    private string EffectiveSam2ModelFileName
    {
        get
        {
            var name = Sam2ModelName;
            if (Sam2Precision == "fp32" || !name.Contains("2.1", StringComparison.Ordinal))
                return name;

            var dot = name.LastIndexOf('.');
            return dot < 0 ? name : $"{name[..dot]}-fp16{name[dot..]}";
        }
    }

    /// <summary>
    /// Kijai's SAM2 loader resolves models against ComfyUI/models/sam2 and ignores
    /// extra_model_paths.yaml. Stability Matrix keeps shared models in LibraryDir/Models/sam2,
    /// so stage the checkpoint once into the package's own models/sam2 folder.
    /// </summary>
    private void EnsureSam2ModelAvailable()
    {
        try
        {
            if (ClientManager.Client?.LocalServerPath is not { } comfyRoot)
            {
                Logger.Warn("Sam2: ComfyUI local path unknown, cannot verify the checkpoint");
                return;
            }

            var fileName = EffectiveSam2ModelFileName;
            var targetDir = System.IO.Path.Combine(comfyRoot.FullPath, "models", "sam2");
            var target = System.IO.Path.Combine(targetDir, fileName);
            if (System.IO.File.Exists(target))
                return;

            var sharedDir = System.IO.Path.Combine(settingsManager.ModelsDirectory, "sam2");
            var source = System.IO.Path.Combine(sharedDir, fileName);
            if (!System.IO.File.Exists(source))
            {
                // fp16/bf16 look for *-fp16.safetensors; the shared checkpoint uses the plain name
                source = System.IO.Path.Combine(sharedDir, Sam2ModelName);
            }

            if (!System.IO.File.Exists(source))
            {
                Logger.Warn(
                    $"Sam2: checkpoint not found in {sharedDir} ({fileName}); ComfyUI will try to download it"
                );
                return;
            }

            System.IO.Directory.CreateDirectory(targetDir);
            System.IO.File.Copy(source, target, false);
            Logger.Info($"Sam2: staged checkpoint from {source} to {target}");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Sam2: could not stage the checkpoint");
        }
    }

    /// <summary>
    /// Builds the binary control mask the app is supposed to prepare itself in manual mode:
    /// white painted pixels = 1 (replace), everything else = 0 (keep).
    /// </summary>
    private ImageSource? GetOrCreateManualMask(bool forceRefresh = false)
    {
        var card = MaskImageCardViewModel;
        var editor = card.MaskEditorViewModel;
        var canvas = editor?.PaintCanvasViewModel;
        var path = card.ImageSource?.LocalFile?.FullPath ?? string.Empty;
        var size = card.CurrentBitmapSize;

        // CurrentBitmapSize is set by the view and is often empty until the mask editor is
        // opened. Recover the real frame size from any source we have.
        if (size.IsEmpty && canvas?.BackgroundImage is { } background)
            size = new System.Drawing.Size(background.Width, background.Height);

        if (size.IsEmpty && !string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
        {
            using var probe = SKBitmap.Decode(path);
            if (probe is not null)
                size = new System.Drawing.Size(probe.Width, probe.Height);
        }

        if (size.IsEmpty && canvas is not null && canvas.CanvasSize != System.Drawing.Size.Empty)
            size = canvas.CanvasSize;

        // Hash the full editor state: repainting the same number of strokes must invalidate
        // the cached mask, otherwise BuildPrompt uploads the previous mask.
        string editorStateKey;
        try
        {
            editorStateKey = canvas?.SaveStateToJsonObject().ToJsonString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "VideoInpaint: could not serialise mask editor state for cache key");
            editorStateKey =
                $"{canvas?.Paths.Count}|{canvas?.TemporaryPaths.Count}|{canvas?.PaintBrushColor}";
        }

        var key = string.Join(
            "|",
            path,
            size.Width,
            size.Height,
            editor?.IsMaskEnabled,
            canvas?.HasDrawnContent,
            editorStateKey
        );

        if (!forceRefresh && preparedManualMask is not null && preparedManualMaskKey == key)
            return preparedManualMask;

        preparedManualMaskKey = key;
        preparedManualMask = BuildManualMask(
            canvas,
            size,
            path,
            out preparedManualMaskReplacePixels,
            out preparedManualMaskTotalPixels
        );
        return preparedManualMask;
    }

    private static ImageSource? BuildManualMask(
        PaintCanvasViewModel? canvas,
        System.Drawing.Size size,
        string imagePath,
        out int replacePixels,
        out int totalPixels
    )
    {
        replacePixels = 0;
        totalPixels = 0;

        // PaintCanvasViewModel.CanvasSize is only set while the editor dialog is open.
        // Without this the render returns null or a mask with the wrong dimensions.
        if (canvas is not null && !size.IsEmpty)
            canvas.CanvasSize = size;

        // Preferred: anything painted or loaded into the mask editor. This also covers a
        // black/white mask image loaded with "Load Mask", which lives in the Images layer
        // and never appears in Paths.
        if (canvas is not null && canvas.HasDrawnContent && !size.IsEmpty)
        {
            using var skImage = canvas.RenderToBinaryMaskImage(out replacePixels);
            if (skImage is not null)
            {
                totalPixels = size.Width * size.Height;
                Logger.Info(
                    $"VideoInpaint: manual mask from editor {size.Width}x{size.Height}, "
                        + $"white (replace) pixels = {replacePixels} / {totalPixels}"
                );
                return new ImageSource(skImage.ToAvaloniaBitmap());
            }
        }

        // Fallback: a genuine black/white mask PNG loaded directly into the card.
        if (!string.IsNullOrEmpty(imagePath) && System.IO.File.Exists(imagePath))
            return BinarizeMaskFile(imagePath, out replacePixels, out totalPixels);

        return null;
    }

    private static ImageSource? BinarizeMaskFile(
        string path,
        out int whitePixels,
        out int totalPixels
    )
    {
        whitePixels = 0;
        totalPixels = 0;

        using var source = SKBitmap.Decode(path);
        if (source is null)
            return null;

        totalPixels = source.Width * source.Height;
        using var target = new SKBitmap(
            source.Width,
            source.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul
        );

        var nearBlackOrWhite = 0;
        var minX = source.Width;
        var minY = source.Height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var c = source.GetPixel(x, y);
                // Only (near) pure white counts as "replace". A bright area of an ordinary
                // photo almost never has all three channels >= 235.
                var isWhite =
                    c.Red >= 235
                    && c.Green >= 235
                    && c.Blue >= 235
                    && c.Alpha > 0;
                var isBlack = c.Red <= 24 && c.Green <= 24 && c.Blue <= 24;
                if (isWhite || isBlack)
                    nearBlackOrWhite++;
                if (isWhite)
                {
                    whitePixels++;
                    if (x < minX)
                        minX = x;
                    if (y < minY)
                        minY = y;
                    if (x > maxX)
                        maxX = x;
                    if (y > maxY)
                        maxY = y;
                }
                var v = isWhite ? (byte)255 : (byte)0;
                target.SetPixel(x, y, new SKColor(v, v, v, 255));
            }
        }

        // A plain photo is not a mask, but a photo with a solid WHITE area painted over the
        // part to replace is a legitimate manual workflow. Tell them apart by asking whether
        // the white pixels form a compact blob - scattered specular highlights do not.
        var bboxArea =
            maxX < minX || maxY < minY ? 0 : (maxX - minX + 1) * (maxY - minY + 1);
        var bboxFill = bboxArea > 0 ? (double)whitePixels / bboxArea : 0;
        var hasSolidWhiteArea =
            whitePixels >= System.Math.Max(256, totalPixels / 2000) && bboxFill >= 0.3;
        var isBlackWhiteMask = nearBlackOrWhite >= totalPixels * 0.95;

        Logger.Info(
            $"VideoInpaint: binarised manual mask {source.Width}x{source.Height}, "
                + $"white (replace) pixels = {whitePixels} / {totalPixels}, "
                + $"near black-or-white = {nearBlackOrWhite}, "
                + $"white bbox = {minX},{minY}-{maxX},{maxY} (fill {bboxFill:0.00})"
        );

        // Otherwise the old code fed a few hundred bright highlight pixels into VACE and
        // produced a silent no-op generation instead of a clear error.
        if (totalPixels == 0 || (!isBlackWhiteMask && !hasSolidWhiteArea))
        {
            Logger.Warn(
                "VideoInpaint: card image is neither a black/white mask nor a photo with a solid white marking - refusing it"
            );
            whitePixels = 0;
            return null;
        }

        using var image = SKImage.FromBitmap(target);
        return new ImageSource(image.ToAvaloniaBitmap());
    }

    /// <summary>
    /// Picks the image produced by the requested output node.
    /// Recent ComfyUI versions also expose the input MP4 from LoadVideo as an "images" UI
    /// output; decoding that as a bitmap fails with an ftyp header.
    /// </summary>
    private static ComfyImage? SelectOutputImage(
        Dictionary<string, List<ComfyImage>?> outputs,
        string preferredNodeName,
        bool allowFallback = true
    )
    {
        if (outputs.TryGetValue(preferredNodeName, out var preferred) && preferred is { Count: > 0 })
            return preferred[0];

        if (!allowFallback)
            return null;

        return outputs
            .Where(kvp => !kvp.Key.Contains("LoadVideo", StringComparison.OrdinalIgnoreCase))
            .SelectMany(kvp => kvp.Value ?? new List<ComfyImage>())
            .FirstOrDefault(img =>
                !string.Equals(img.Type, "input", StringComparison.OrdinalIgnoreCase)
                && !img.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                && !img.FileName.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
                && !img.FileName.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
            );
    }

    /// <summary>
    /// Opens the mask editor for the loaded frame with masking enabled. The layer button on
    /// the image card is easy to miss and the editor's Enabled toggle is off by default.
    /// </summary>
    [RelayCommand]
    private async Task EditManualMaskAsync()
    {
        var card = MaskImageCardViewModel;
        if (card.ImageSource is null)
        {
            notifications.Show(
                "Load a frame first",
                "Load the first frame (or a black/white mask) into the mask card before editing it.",
                NotificationType.Warning
            );
            return;
        }

        if (card.MaskEditorViewModel is { } editor)
            editor.IsMaskEnabled = true;

        await card.OpenEditMaskDialogCommand.ExecuteAsync(null);

        preparedManualMask = null;
        preparedManualMaskKey = null;
        PreviewManualMask();
    }

    /// <summary>
    /// Renders the binary mask manual mode will actually upload, so an empty or wrong mask
    /// is visible before a two-minute generation is queued.
    /// </summary>
    [RelayCommand]
    private void PreviewManualMask()
    {
        preparedManualMask = null;
        preparedManualMaskKey = null;
        try
        {
            var mask = GetOrCreateManualMask(forceRefresh: true);
            if (mask?.Bitmap is not { } bitmap)
            {
                ManualMaskPreview = null;
                notifications.Show(
                    "No manual mask",
                    "Click 'Edit mask', paint the area to replace in WHITE and press Save. "
                        + "A normal photo is not a mask.",
                    NotificationType.Warning
                );
                return;
            }

            ManualMaskPreview = bitmap;
            Logger.Info(
                $"VideoInpaint: manual mask preview, white (replace) pixels = "
                    + $"{preparedManualMaskReplacePixels} / {preparedManualMaskTotalPixels}"
            );

            if (
                preparedManualMaskTotalPixels > 0
                && preparedManualMaskReplacePixels
                    < System.Math.Max(256, preparedManualMaskTotalPixels / 2000)
            )
            {
                notifications.Show(
                    "Manual mask is almost empty",
                    $"Only {preparedManualMaskReplacePixels} of {preparedManualMaskTotalPixels} pixels are white. "
                        + "Paint a larger WHITE area in 'Edit mask' and preview again.",
                    NotificationType.Warning
                );
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "VideoInpaint: could not preview the manual mask");
            ManualMaskPreview = null;
            notifications.Show("Manual mask", ex.Message, NotificationType.Warning);
        }
    }

    /// <summary>
    /// Runs SAM2 tracking to generate and preview the mask for the first, middle, and last frame.
    /// </summary>
    [RelayCommand]
    private async Task PreviewMaskAsync()
    {
        if (!UseSam2Tracking)
        {
            notifications.Show("Auto mask is off", "Enable Auto mask to preview SAM2 mask", NotificationType.Warning);
            return;
        }

        if (PositivePoints.Count == 0)
        {
            notifications.Show(
                "No green points",
                FramePreview is null
                    ? "Click 'Load frame' first, then LEFT-click 3-8 points inside the face/head on the frame preview."
                    : "LEFT-click 3-8 points inside the face/head on the frame preview (green dots); right click marks areas to exclude.",
                NotificationType.Warning
            );
            return;
        }

        if (string.IsNullOrEmpty(VideoInputPath) || !System.IO.File.Exists(VideoInputPath))
        {
            notifications.Show("No video", "Select an input video first", NotificationType.Warning);
            return;
        }

        if (!await CheckClientConnectedWithPrompt() || !ClientManager.IsConnected)
            return;

        if (ClientManager.Client is not { } client)
            return;

        try
        {
            IsLoadingFrame = true;
            EnsureSam2ModelAvailable();

            var sanitizedVideoName = GetComfyVideoFileName(VideoInputPath);
            await client.UploadFileAsync(
                VideoInputPath,
                System.IO.Path.Combine("input", sanitizedVideoName)
            );

            var frameCount = SamplerCardViewModel.Length;
            var framesToPreview = new[] { 0, frameCount / 2, frameCount - 1 }.Distinct().ToArray();

            foreach (var oldMask in MaskPreviewFrames)
                oldMask.Dispose();
            MaskPreviewFrames.Clear();

            // ONE SAM2 run over the whole clip. The old code passed a single frame to
            // Sam2VideoSegmentationAddPoints, so num_frames was 1 and the "middle" / "last"
            // previews were really the same single-frame segmentation repeated three times.
            var builder = new ComfyNodeBuilder();

            var loadVideo = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.LoadVideo { Name = "PreviewLoadVideo", Video = sanitizedVideoName }
            );
            var components = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.GetVideoComponents
                {
                    Name = "PreviewComponents",
                    Video = loadVideo.Output,
                }
            );
            var clipFrames = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.ImageFromBatch
                {
                    Name = "PreviewClipFrames",
                    Image = components.Output,
                    BatchIndex = 0,
                    Length = frameCount,
                }
            );

            var sam2Model = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.DownloadAndLoadSam2Model
                {
                    Name = builder.Nodes.GetUniqueName("Sam2Loader"),
                    Model = Sam2ModelName,
                    Segmentor = "video",
                    Device = "cuda",
                    Precision = Sam2Precision,
                }
            );

            var addPoints = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.Sam2VideoSegmentationAddPoints
                {
                    Name = builder.Nodes.GetUniqueName("Sam2AddPoints"),
                    Sam2Model = sam2Model.Output,
                    CoordinatesPositive = ToCoordinateJson(PositivePoints),
                    CoordinatesNegative =
                        NegativePoints.Count > 0 ? ToCoordinateJson(NegativePoints) : null,
                    FrameIndex = 0,
                    ObjectIndex = 0,
                    Image = clipFrames.Output,
                }
            );

            var sam2Segmentation = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.Sam2VideoSegmentation
                {
                    Name = builder.Nodes.GetUniqueName("Sam2Track"),
                    Sam2Model = addPoints.Output1,
                    InferenceState = addPoints.Output2,
                    KeepModelLoaded = false,
                }
            );

            // SAM2 returns a MASK; PreviewImage needs an IMAGE
            var maskAsImage = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.MaskToImage
                {
                    Name = builder.Nodes.GetUniqueName("MaskToImage"),
                    Mask = sam2Segmentation.Output,
                }
            );

            foreach (var frameIndex in framesToPreview)
            {
                var frameMask = builder.Nodes.AddTypedNode(
                    new ComfyNodeBuilder.ImageFromBatch
                    {
                        Name = $"PreviewMaskFrame_{frameIndex}",
                        Image = maskAsImage.Output,
                        BatchIndex = frameIndex,
                        Length = 1,
                    }
                );
                var preview = builder.Nodes.AddTypedNode(
                    new ComfyNodeBuilder.PreviewImage
                    {
                        Name = $"PreviewMask_{frameIndex}",
                        Images = frameMask.Output,
                    }
                );
                builder.Connections.OutputNodes.Add(preview);
            }

            var promptTask = await client.QueuePromptAsync(builder.ToNodeDictionary());
            await promptTask.Task;

            var outputs = await client.GetImagesForExecutedPromptAsync(promptTask.Id);

            foreach (var frameIndex in framesToPreview)
            {
                var image = SelectOutputImage(outputs, $"PreviewMask_{frameIndex}", false);
                if (image is null)
                {
                    Logger.Warn($"Mask preview: no usable image output for frame {frameIndex}");
                    continue;
                }

                await using var stream = await client.GetImageStreamAsync(image);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                memory.Position = 0;

                MaskPreviewFrames.Add(new Bitmap(memory));
            }

            if (MaskPreviewFrames.Count > 0)
            {
                notifications.Show(
                    "Mask preview",
                    $"Generated mask preview for {MaskPreviewFrames.Count} frame(s)",
                    NotificationType.Information
                );
            }
            else
            {
                notifications.Show(
                    "Mask preview",
                    "ComfyUI did not return any mask preview",
                    NotificationType.Warning
                );
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to preview mask");
            notifications.Show("Could not preview mask", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoadingFrame = false;
        }
    }

    /// <summary>
    /// Extracts the first frame of the input video through ComfyUI (no extra tools needed)
    /// and shows it in the point picker.
    /// </summary>
    [RelayCommand]
    private async Task LoadFrameAsync()
    {
        await LoadPreviewFrameAsync(promptForConnection: true);
    }

    private async Task LoadPreviewFrameAsync(bool promptForConnection)
    {
        if (string.IsNullOrEmpty(VideoInputPath) || !System.IO.File.Exists(VideoInputPath))
        {
            notifications.Show("No video", "Select an input video first", NotificationType.Warning);
            return;
        }

        if (promptForConnection)
        {
            if (!await CheckClientConnectedWithPrompt())
                return;
        }
        else if (!ClientManager.IsConnected)
        {
            return;
        }

        if (ClientManager.Client is not { } client)
            return;

        try
        {
            IsLoadingFrame = true;

            // Deterministic name: re-loading the frame must not re-upload/re-copy the video
            var sanitizedVideoName = GetComfyVideoFileName(VideoInputPath);
            await client.UploadFileAsync(VideoInputPath, $"input/{sanitizedVideoName}");

            var builder = new ComfyNodeBuilder();

            var loadVideo = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.LoadVideo { Name = "PreviewLoadVideo", Video = sanitizedVideoName }
            );
            var components = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.GetVideoComponents { Name = "PreviewComponents", Video = loadVideo.Output }
            );
            var firstFrame = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.ImageFromBatch
                {
                    Name = "PreviewFirstFrame",
                    Image = components.Output,
                    BatchIndex = 0,
                    Length = 1,
                }
            );
            var preview = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.PreviewImage { Name = "PreviewFrame", Images = firstFrame.Output }
            );
            builder.Connections.OutputNodes.Add(preview);

            var promptTask = await client.QueuePromptAsync(builder.ToNodeDictionary());
            await promptTask.Task;

            var outputs = await client.GetImagesForExecutedPromptAsync(promptTask.Id);
            // LoadVideo can expose the input MP4 as a UI "images" output. Pick the real
            // PreviewFrame node first, otherwise decoding MP4 bytes as a bitmap fails.
            var frame = SelectOutputImage(outputs, "PreviewFrame");

            if (frame is null)
            {
                notifications.Show("No frame", "ComfyUI did not return a frame preview", NotificationType.Warning);
                return;
            }

            await using var stream = await client.GetImageStreamAsync(frame);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);

            var header = Convert.ToHexString(memory.ToArray().Take(8).ToArray());
            Logger.Info(
                $"Preview frame: file={frame.FileName}, subfolder={frame.SubFolder}, type={frame.Type}, bytes={memory.Length}, header={header}"
            );

            if (memory.Length == 0)
            {
                notifications.Show(
                    "Could not load frame",
                    $"ComfyUI returned an empty file ({frame.FileName}, type {frame.Type})",
                    NotificationType.Error
                );
                return;
            }

            memory.Position = 0;

            try
            {
                var oldFrame = FramePreview;
                FramePreview = new Bitmap(memory);
                oldFrame?.Dispose();
            }
            catch (Exception decodeEx)
            {
                // A real PNG starts with 89504E47; anything else is an error page or another format
                throw new InvalidOperationException(
                    $"{decodeEx.Message} (file {frame.FileName}, type {frame.Type}, {memory.Length} bytes, header {header})",
                    decodeEx
                );
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load preview frame");
            notifications.Show("Could not load frame", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoadingFrame = false;
        }
    }

    private string GetComfyVideoFileName(string videoPath)
    {
        var info = new System.IO.FileInfo(videoPath);
        var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
        return $"sm_inpaint_{hash}{info.Extension.ToLowerInvariant()}";
    }

    private static string ToCoordinateJson(IEnumerable<MaskPoint> points)
    {
        // Kijai's Sam2VideoSegmentationAddPoints expects pixel coordinates of the ORIGINAL frames,
        // e.g. [{"x":412.5,"y":133.0}]. SAM2 divides them by the video width/height itself,
        // so normalized 0-1 values would all land in the top-left corner.
        return JsonSerializer.Serialize(points.Select(p => new { x = p.X, y = p.Y }));
    }

    private async Task BrowseVideoAsync()
    {
        try
        {
            var provider = App.StorageProvider;
            if (provider is null)
                return;

            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Input Video",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Video Files")
                    {
                        Patterns = new[] { "*.mp4", "*.mov", "*.avi", "*.mkv", "*.webm" }
                    }
                }
            });

            if (files.Count > 0 && files[0] is { } file)
            {
                VideoInputPath = file.Path.LocalPath;
                Logger.Info($"Input video selected: {VideoInputPath}");

                // Points belong to the previous video's frame
                ClearPoints();
                FramePreview?.Dispose();
                FramePreview = null;

                foreach (var oldMask in MaskPreviewFrames)
                    oldMask.Dispose();
                MaskPreviewFrames.Clear();

                // Show the first frame right away if ComfyUI is already connected (only needed for SAM2 points)
                if (UseSam2Tracking)
                    await LoadPreviewFrameAsync(promptForConnection: false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to select input video");
        }
    }

    // ---------------------------------------------------------------------------
    // Split base (temporal chunking) - planning, probing and ffmpeg join
    // ---------------------------------------------------------------------------

    private sealed record VideoChunk(int StartFrame, int Length)
    {
        /// <summary>
        /// True when this chunk is not fully inside the scene that carries the SAM2 click
        /// points. Such a chunk is generated with an empty mask (no replacement at all).
        /// </summary>
        public bool SkipMask { get; init; }
    }

    private sealed class ChunkPlan
    {
        public List<VideoChunk> Chunks { get; } = new();
        public int TotalFrames { get; set; }

        /// <summary>0 = use the FPS from VideoOutputSettingsCardViewModel (legacy behaviour).</summary>
        public double Fps { get; set; }

        public int Overlap { get; set; }
        public string Summary { get; set; } = "";

        /// <summary>Frame indices at which ffmpeg detected a scene cut.</summary>
        public List<int> SceneCutFrames { get; } = new();
    }

    /// <summary>
    /// Wan VACE declares length with step=4 (min 1), so only 4n+1 values are valid
    /// (5, 9, 17, 25, 49, 81 ...). Always snap DOWN so we never request more frames
    /// than the source can supply.
    /// </summary>
    private static int SnapChunkLength(int frames)
    {
        if (frames <= 5)
            return 5;
        return ((frames - 1) / 4) * 4 + 1;
    }

    private static string? JsonText(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();

    private static double ParseFrameRate(JsonElement stream, string property)
    {
        if (!stream.TryGetProperty(property, out var value))
            return 0;

        var text = JsonText(value);
        if (string.IsNullOrEmpty(text))
            return 0;

        var parts = text.Split('/');
        if (
            parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            && den > 0
        )
            return num / den;

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var direct)
            ? direct
            : 0;
    }

    /// <summary>Find ffmpeg / ffprobe on PATH, falling back to ComfyUI's bundled imageio_ffmpeg.</summary>
    private string ResolveFfmpegTool(string exeName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException) { }
        }

        if (string.Equals(exeName, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            var comfyRoot = ClientManager.Client?.LocalServerPath?.FullPath;
            if (!string.IsNullOrEmpty(comfyRoot))
            {
                var binDir = Path.Combine(
                    comfyRoot,
                    "venv",
                    "Lib",
                    "site-packages",
                    "imageio_ffmpeg",
                    "binaries"
                );
                if (Directory.Exists(binDir))
                {
                    var bundled = Directory.GetFiles(binDir, "ffmpeg*.exe").FirstOrDefault();
                    if (bundled is not null)
                        return bundled;
                }
            }
        }

        // Last resort: let Process.Start resolve it through PATH.
        return exeName;
    }

    /// <summary>Probe the input video's frame count and FPS so the joined output has the same duration.</summary>
    private async Task<(int FrameCount, double Fps)> ProbeVideoAsync(string videoPath)
    {
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            return (0, 0);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolveFfmpegTool("ffprobe.exe"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            foreach (
                var arg in new[]
                {
                    "-v",
                    "error",
                    "-select_streams",
                    "v:0",
                    "-show_entries",
                    "stream=nb_frames,r_frame_rate,avg_frame_rate,duration",
                    "-show_entries",
                    "format=duration",
                    "-of",
                    "json",
                    videoPath,
                }
            )
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return (0, 0);

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                Logger.Warn($"VideoInpaint: ffprobe failed ({process.ExitCode}): {stderr}");
                return (0, 0);
            }

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            var fps = 0d;
            var frames = 0;
            var duration = 0d;

            if (root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
            {
                var stream = streams[0];
                fps = ParseFrameRate(stream, "r_frame_rate");
                if (fps <= 0)
                    fps = ParseFrameRate(stream, "avg_frame_rate");

                if (
                    stream.TryGetProperty("nb_frames", out var nbFrames)
                    && int.TryParse(
                        JsonText(nbFrames),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var n
                    )
                )
                    frames = n;

                if (
                    duration <= 0
                    && stream.TryGetProperty("duration", out var streamDuration)
                    && double.TryParse(
                        JsonText(streamDuration),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var sd
                    )
                )
                    duration = sd;
            }

            if (
                duration <= 0
                && root.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var fmtDuration)
                && double.TryParse(
                    JsonText(fmtDuration),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var fd
                )
            )
                duration = fd;

            if (frames <= 0 && duration > 0 && fps > 0)
                frames = (int)System.Math.Round(duration * fps);

            Logger.Info(
                $"VideoInpaint: probed video frames={frames}, fps={fps}, duration={duration:0.###}s"
            );
            return (frames, fps);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "VideoInpaint: ffprobe unavailable, falling back to the sampler length");
            return (0, 0);
        }
    }

    /// <summary>
    /// Query ComfyUI's /system_stats for free VRAM and derive a conservative frames-per-chunk.
    /// ~3 frames per free GB, clamped to 9..81 and snapped to 4n+1.
    /// </summary>
    private async Task<int> DetectAutoChunkFramesAsync()
    {
        try
        {
            var client = ClientManager.Client;
            if (client is not null)
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                var baseUri = new Uri(client.BaseAddress.ToString().TrimEnd('/') + "/");
                using var response = await http.GetAsync(new Uri(baseUri, "system_stats"));
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (
                    doc.RootElement.TryGetProperty("devices", out var devices)
                    && devices.ValueKind == JsonValueKind.Array
                    && devices.GetArrayLength() > 0
                    && devices[0].TryGetProperty("vram_free", out var free)
                    && free.TryGetDouble(out var freeBytes)
                )
                {
                    var gb = freeBytes / (1024d * 1024d * 1024d);
                    var snapped = SnapChunkLength(
                        Math.Clamp((int)System.Math.Round(gb * 3.0d), 9, 81)
                    );
                    Logger.Info(
                        $"VideoInpaint: auto chunk size from {gb:0.0} GB free VRAM -> {snapped} frames"
                    );
                    return snapped;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "VideoInpaint: /system_stats unavailable, using the manual chunk size");
        }

        return SnapChunkLength(Math.Clamp(ChunkFrames, 5, 161));
    }

    /// <summary>
    /// Detect hard scene cuts with ffmpeg's "scene" filter and return the frame index of the
    /// first frame of every new scene. The user's SAM2 clicks belong to frame 0, i.e. to the
    /// first scene; any chunk that reaches a cut must not re-seed those clicks. A detection
    /// failure returns an empty list (protection simply does nothing) and never aborts a run.
    /// </summary>
    private async Task<List<int>> DetectSceneCutFramesAsync(
        string videoPath,
        double fps,
        CancellationToken cancellationToken
    )
    {
        var cuts = new List<int>();
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath) || fps <= 0)
            return cuts;

        try
        {
            var threshold = System.Math.Clamp(SceneCutThreshold, 0.05d, 0.9d).ToString(
                "0.###",
                CultureInfo.InvariantCulture
            );

            var psi = new ProcessStartInfo
            {
                FileName = ResolveFfmpegTool("ffmpeg.exe"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (
                var arg in new[]
                {
                    "-hide_banner",
                    "-nostats",
                    "-i",
                    videoPath,
                    "-vf",
                    $"select='gt(scene,{threshold})',showinfo",
                    "-an",
                    "-f",
                    "null",
                    "-",
                }
            )
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return cuts;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            timeout.CancelAfter(TimeSpan.FromSeconds(180));

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token);
            var stderr = await stderrTask;
            await stdoutTask;

            // showinfo logs "pts_time:3.166667" for every selected (= cut) frame. select
            // keeps the original PTS, so this converts directly to a source frame index.
            foreach (
                System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                    stderr,
                    @"pts_time:\s*([0-9]+(?:\.[0-9]+)?)"
                )
            )
            {
                if (
                    double.TryParse(
                        match.Groups[1].Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var seconds
                    )
                )
                {
                    var frame = (int)System.Math.Round(seconds * fps);
                    if (frame > 0 && !cuts.Contains(frame))
                        cuts.Add(frame);
                }
            }

            cuts.Sort();
            Logger.Info(
                $"VideoInpaint: scene-cut detection (threshold {threshold}) found "
                    + $"{cuts.Count} cut(s) at frames [{string.Join(", ", cuts)}]"
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "VideoInpaint: scene-cut detection failed; protection disabled");
            cuts.Clear();
        }

        return cuts;
    }

    /// <summary>Build the list of temporal chunks for this run.</summary>
    private async Task<ChunkPlan> BuildChunkPlanAsync(CancellationToken cancellationToken)
    {
        var plan = new ChunkPlan();
        var samplerLength = SamplerCardViewModel.Length;

        if (!EnableChunkedProcessing)
        {
            plan.Chunks.Add(new VideoChunk(0, samplerLength));
            plan.TotalFrames = samplerLength;
            plan.Fps = 0; // use the FPS from the video output card (unchanged legacy behaviour)
            plan.Summary = "Split base OFF - the whole clip is generated in one prompt.";
            return plan;
        }

        var probe = await ProbeVideoAsync(VideoInputPath ?? string.Empty);
        var totalFrames = probe.FrameCount > 0 ? probe.FrameCount : samplerLength;
        var fps = probe.Fps > 0 ? probe.Fps : VideoOutputSettingsCardViewModel.Fps;

        var maxFrames = AutoChunkSize
            ? await DetectAutoChunkFramesAsync()
            : SnapChunkLength(Math.Clamp(ChunkFrames, 5, 161));

        if (totalFrames <= maxFrames)
        {
            Logger.Info(
                $"VideoInpaint: video ({totalFrames} frames) fits in one chunk ({maxFrames}); "
                    + "splitting is not needed for this run"
            );
            plan.Chunks.Add(new VideoChunk(0, samplerLength));
            plan.TotalFrames = samplerLength;
            plan.Fps = 0;
            plan.Summary =
                $"Split base not needed: {totalFrames} frames <= {maxFrames} per chunk. One prompt.";
            return plan;
        }

        var overlap = Math.Clamp(ChunkOverlapFrames, 0, maxFrames - 8);
        var step = maxFrames - overlap;
        if (step <= 0)
        {
            overlap = 0;
            step = maxFrames;
        }

        for (var start = 0; start < totalFrames; start += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Every chunk keeps the same 4n+1 length, so WanVaceToVideo is always valid.
            // The last chunk may extend past the end; the join trims to totalFrames.
            plan.Chunks.Add(new VideoChunk(start, maxFrames));
            if (plan.Chunks.Count >= 256)
                break;
        }

        plan.TotalFrames = totalFrames;
        plan.Fps = fps;
        plan.Overlap = overlap;

        // Scene-cut protection. In split-base + SAM2 mode the same click coordinates are
        // re-applied to every chunk's first frame. After a cut they land on a completely
        // different person/background, so those chunks must NOT be re-seeded. They are
        // generated with an empty mask instead, which leaves the original footage intact.
        var protectCuts = ProtectAgainstSceneCuts && UseSam2Tracking;
        if (protectCuts)
        {
            plan.SceneCutFrames.AddRange(
                await DetectSceneCutFramesAsync(VideoInputPath ?? string.Empty, fps, cancellationToken)
            );
        }

        var firstCut = plan.SceneCutFrames.Count > 0 ? plan.SceneCutFrames[0] : int.MaxValue;
        var protectedChunks = 0;
        for (var i = 0; i < plan.Chunks.Count; i++)
        {
            var chunk = plan.Chunks[i];
            // "afterCut" covers chunks fully inside a later scene. A chunk that merely
            // straddles a cut near its END is still mostly the annotated scene and is worth
            // keeping (only 1-2 frames at the cut can be affected); one whose cut is in the
            // first half is mostly a different scene and must be left untouched.
            var afterCut = chunk.StartFrame >= firstCut;
            var cutEarlyInChunk = plan.SceneCutFrames.Any(c =>
                c > chunk.StartFrame && c < chunk.StartFrame + chunk.Length / 2
            );

            if (protectCuts && (afterCut || cutEarlyInChunk))
            {
                plan.Chunks[i] = chunk with { SkipMask = true };
                protectedChunks++;
            }
        }

        plan.Summary =
            $"Split base: {plan.Chunks.Count} chunks x {maxFrames} frames "
            + $"(overlap {overlap}), input {totalFrames} frames @ "
            + $"{fps.ToString("0.###", CultureInfo.InvariantCulture)} fps -> joined output "
            + $"{totalFrames} frames, same duration as the input.";

        if (protectCuts)
        {
            plan.Summary +=
                $" Scene cuts at frames [{string.Join(", ", plan.SceneCutFrames)}]: "
                + $"{protectedChunks}/{plan.Chunks.Count} chunks kept unchanged "
                + "(no re-seeding of SAM2 points on a different scene).";
        }

        return plan;
    }

    /// <summary>Show the chunk plan without generating anything.</summary>
    [RelayCommand]
    private async Task PreviewChunkPlan()
    {
        try
        {
            var plan = await BuildChunkPlanAsync(CancellationToken.None);
            ChunkPlanSummary = plan.Summary;
            notifications.Show("Split base plan", plan.Summary, NotificationType.Information);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "VideoInpaint: could not build the chunk plan preview");
            notifications.Show("Split base plan", ex.Message, NotificationType.Warning);
        }
    }

    /// <summary>Run ffmpeg, drain both pipes and return the exit code plus stderr.</summary>
    private static async Task<(int ExitCode, string Stderr)> RunFfmpegAsync(
        string ffmpeg,
        string arguments,
        CancellationToken cancellationToken
    )
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException("Could not start ffmpeg");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;
        await stdoutTask;
        return (process.ExitCode, stderr);
    }

    /// <summary>
    /// The generated chunks are silent. Best-effort mux of the ORIGINAL audio track so the
    /// joined video matches the input's soundtrack as well as its duration. Any failure leaves
    /// the silent joined video untouched.
    /// </summary>
    private async Task RestoreOriginalAudioAsync(
        string ffmpeg,
        string finalPath,
        string outputDirPath,
        string runId,
        int batchIndex,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrEmpty(VideoInputPath) || !File.Exists(VideoInputPath))
            return;

        var withAudio = Path.Combine(
            outputDirPath,
            $"wan_vace_joined_{runId}_b{batchIndex}_audio.mp4"
        );

        try
        {
            // Do NOT use -shortest here: the original audio is commonly a few frames shorter
            // than the video, and -shortest silently dropped the LAST generated frame
            // (147 -> 146). Without it the video stream is copied in full and only the audio
            // track is allowed to end early.
            var args =
                $"-hide_banner -loglevel error -y -i \"{finalPath.Replace("\\", "/")}\" "
                + $"-i \"{VideoInputPath.Replace("\\", "/")}\" "
                + "-map 0:v:0 -map 1:a? -c:v copy -c:a aac -movflags +faststart "
                + $"\"{withAudio.Replace("\\", "/")}\"";

            var (exitCode, stderr) = await RunFfmpegAsync(ffmpeg, args, cancellationToken);
            if (exitCode != 0 || !File.Exists(withAudio) || new FileInfo(withAudio).Length == 0)
            {
                Logger.Warn($"VideoInpaint: audio mux skipped (ffmpeg {exitCode}): {stderr}");
                return;
            }

            File.Delete(finalPath);
            File.Move(withAudio, finalPath);
            Logger.Info("VideoInpaint: restored the original audio track on the joined video");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "VideoInpaint: could not restore audio, keeping the silent join");
        }
        finally
        {
            try
            {
                if (File.Exists(withAudio))
                    File.Delete(withAudio);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    /// <summary>Join the per-chunk MP4s into one video with exactly the input's frame count.</summary>
    private async Task JoinChunksAsync(
        IReadOnlyList<string> chunkFiles,
        ChunkPlan plan,
        string runId,
        int batchIndex,
        string outputDirPath,
        CancellationToken cancellationToken
    )
    {
        var ffmpeg = ResolveFfmpegTool("ffmpeg.exe");
        // Save the joined video in the SAME folder as the chunk outputs (ComfyUI's output dir,
        // which is typically Images/Text2Img or a symlink to it). Saving to Images/Inference
        // made the joined video invisible in the gallery, which shows the ComfyUI output folder.
        var outputDir = string.IsNullOrEmpty(outputDirPath)
            ? settingsManager.ImagesInferenceDirectory.FullPath
            : outputDirPath;
        Directory.CreateDirectory(outputDir);
        var finalPath = Path.Combine(
            outputDir,
            $"wan_vace_joined_{runId}_b{batchIndex}.mp4"
        );
        var fpsText = (plan.Fps > 0 ? plan.Fps : VideoOutputSettingsCardViewModel.Fps).ToString(
            "0.######",
            CultureInfo.InvariantCulture
        );

        string arguments;
        string? concatList = null;

        if (plan.Overlap == 0)
        {
            // All chunks share codec/parameters, so a stream-copy concat is lossless and fast.
            concatList = Path.Combine(Path.GetTempPath(), $"sm_chunks_{runId}_{batchIndex}.txt");
            // The ffmpeg concat demuxer treats a backslash as an escape character, so Windows
            // paths must use forward slashes. Single quotes protect spaces in folder names.
            await File.WriteAllLinesAsync(
                concatList,
                chunkFiles.Select(f =>
                    $"file '{f.Replace("\\", "/").Replace("'", "'\\''")}'"
                ),
                cancellationToken
            );
            arguments =
                $"-hide_banner -loglevel error -y -f concat -safe 0 "
                + $"-i \"{concatList.Replace("\\", "/")}\" "
                + $"-c:v copy -an -frames:v {plan.TotalFrames} "
                + $"\"{finalPath.Replace("\\", "/")}\"";
        }
        else
        {
            // Drop the repeated leading frames of every chunk after the first, then concat.
            var inputs = string.Join(
                " ",
                chunkFiles.Select(f => $"-i \"{f.Replace("\\", "/")}\"")
            );
            var filter = new StringBuilder();
            var labels = new StringBuilder();
            for (var i = 0; i < chunkFiles.Count; i++)
            {
                var trim = i == 0 ? 0 : plan.Overlap;
                filter.Append($"[{i}:v]trim=start_frame={trim},setpts=PTS-STARTPTS[v{i}];");
                labels.Append($"[v{i}]");
            }

            filter.Append($"{labels}concat=n={chunkFiles.Count}:v=1:a=0[outv]");
            arguments =
                $"-hide_banner -loglevel error -y {inputs} -filter_complex \"{filter}\" "
                + $"-map \"[outv]\" -c:v libx264 -crf 18 -pix_fmt yuv420p -r {fpsText} "
                + $"-frames:v {plan.TotalFrames} -an \"{finalPath.Replace("\\", "/")}\"";
        }

        Logger.Info(
            $"VideoInpaint: joining {chunkFiles.Count} chunks "
                + $"({totalInputFrames} source frames, overlap {plan.Overlap}) -> {finalPath}"
        );

        try
        {
            var (exitCode, stderr) = await RunFfmpegAsync(ffmpeg, arguments, cancellationToken);
            if (exitCode != 0)
                throw new InvalidOperationException($"ffmpeg exited with {exitCode}: {stderr}");

            await RestoreOriginalAudioAsync(
                ffmpeg,
                finalPath,
                outputDir,
                runId,
                batchIndex,
                cancellationToken
            );

            // Verify the final file really has the input's frame count. This catches both
            // concat rounding and an audio mux that drops the last frame.
            var finalProbe = await ProbeVideoAsync(finalPath);
            var finalFrames = finalProbe.FrameCount;
            if (finalFrames > 0 && finalFrames != plan.TotalFrames)
            {
                Logger.Warn(
                    $"VideoInpaint: joined video has {finalFrames} frames but the input has "
                        + $"{plan.TotalFrames}"
                );
            }

            var frameNote =
                finalFrames > 0
                    ? $"{finalFrames} frames"
                    : "frame count unavailable (ffprobe)";

            var filePath = new FilePath(finalPath);
            ImageGalleryCardViewModel.ImageSources.Add(
                new ImageSource(filePath) { Label = "Joined video" }
            );
            EventManager.Instance.OnImageFileAdded(filePath);
            notifications.Show(
                "Chunks joined",
                $"{chunkFiles.Count} chunks joined into one {frameNote} video "
                    + $"(input: {plan.TotalFrames} frames, same duration).",
                NotificationType.Success
            );
            Logger.Info(
                $"VideoInpaint: join verified -> {finalPath}, {frameNote}, "
                    + $"expected {plan.TotalFrames}"
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VideoInpaint: ffmpeg join failed");
            notifications.Show(
                "Chunk join failed",
                "The individual chunk videos were kept in the gallery. " + ex.Message,
                NotificationType.Warning
            );
        }
        finally
        {
            if (concatList is not null)
            {
                try
                {
                    File.Delete(concatList);
                }
                catch
                {
                    // Best effort cleanup.
                }
            }
        }
    }

    /// <inheritdoc />
    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        var applyArgs = args.ToModuleApplyStepEventArgs();
        var builder = args.Builder;

        // Split base: GenerateImageImpl sets these immediately before each BuildPrompt call.
        // Legacy single-prompt runs keep offset=0, length=sampler Length, fps=card FPS.
        var clipOffset = currentChunkStartFrame;
        var clipLength = currentChunkLength > 0 ? currentChunkLength : SamplerCardViewModel.Length;
        var outputFps = effectiveFps > 0 ? effectiveFps : VideoOutputSettingsCardViewModel.Fps;
        var chunkSuffix =
            currentChunkCount > 1 ? $"_{currentRunId}_c{currentChunkIndex:D3}" : "";

        Logger.Info(
            $"VideoInpaint: BuildPrompt chunk {currentChunkIndex + 1}/{System.Math.Max(1, currentChunkCount)}, "
                + $"offset={clipOffset}, length={clipLength}, fps={outputFps}"
        );

        builder.Connections.Seed = args.SeedOverride switch
        {
            { } seed => Convert.ToUInt64(seed),
            _ => Convert.ToUInt64(SeedCardViewModel.Seed),
        };

        // Load models (VACE model, CLIP, VAE)
        ModelCardViewModel.ApplyStep(applyArgs);

        // Setup empty latent source for VACE
        builder.SetupEmptyLatentSource(
            SamplerCardViewModel.Width,
            SamplerCardViewModel.Height,
            BatchSizeCardViewModel.BatchSize,
            BatchSizeCardViewModel.IsBatchIndexEnabled ? BatchSizeCardViewModel.BatchIndex : null,
            clipLength,
            LatentType.Hunyuan
        );

        BatchSizeCardViewModel.ApplyStep(applyArgs);
        PromptCardViewModel.ApplyStep(applyArgs);

        // Load input video
        if (string.IsNullOrEmpty(VideoInputPath))
            throw new ValidationException("Input video is required for Video Inpaint");

        if (!System.IO.File.Exists(VideoInputPath))
            throw new ValidationException($"Input video file not found: {VideoInputPath}");

        Logger.Info($"VideoInpaint: Loading input video from {VideoInputPath}");

        // Deterministic name derived from path + size + mtime. In split-base mode every chunk
        // references the SAME uploaded video, so the file is transferred only once instead of
        // re-uploading the whole MP4 under a new GUID for each chunk.
        var sanitizedVideoName = GetComfyVideoFileName(VideoInputPath);

        // Copy to ComfyUI's input/ directory so LoadVideo can find it.
        // Only the FIRST prompt of a run needs to transfer the file; every later chunk keeps
        // referencing the same deterministic name. Without this, a 200-frame video would be
        // copied once per chunk.
        var destinationPath = $"input/{sanitizedVideoName}";
        if (
            !videoUploadedForRun
            && !args.FilesToTransfer.Any(x => x.SourcePath == VideoInputPath)
        )
        {
            args.FilesToTransfer.Add((VideoInputPath, destinationPath));
            videoUploadedForRun = true;
        }

        var loadVideo = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LoadVideo
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LoadVideo)),
                Video = sanitizedVideoName,
            }
        );

        // VIDEO -> IMAGE frames (WanVaceToVideo needs IMAGE, not VIDEO)
        var videoComponents = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.GetVideoComponents
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.GetVideoComponents)),
                Video = loadVideo.Output,
            }
        );

        // Only <clipLength> frames starting at <clipOffset> are used, so only track/encode
        // those. In split-base mode each chunk is an independent prompt with the same graph.
        var clipFrames = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.ImageFromBatch
            {
                Name = builder.Nodes.GetUniqueName("ClipFrames"),
                Image = videoComponents.Output,
                BatchIndex = clipOffset,
                Length = clipLength,
            }
        );

        // One mask per frame: white = replace, black = keep
        ImageMaskConnection controlMasks;

        if (currentChunkSkipMask)
        {
            // Scene-cut protection: this chunk is not fully inside the scene that carries the
            // SAM2 click points. Re-seeding the same coordinates here is what replaced the
            // WRONG face after a cut (or selected the background). An all-black mask means
            // "nothing to replace", so VACE reconstructs the original footage while keeping
            // the chunk's resolution/codec identical to the edited chunks for a clean concat.
            // The mask MUST have one frame per clip frame: WanVaceToVideo pads missing mask
            // frames with 1.0, i.e. "replace everything".
            var noEditMask = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.SolidMask
                {
                    Name = builder.Nodes.GetUniqueName("NoEditMask"),
                    Value = 0.0d,
                    Width = 8,
                    Height = 8,
                }
            );
            var noEditAsImage = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.MaskToImage
                {
                    Name = builder.Nodes.GetUniqueName("NoEditMaskToImage"),
                    Mask = noEditMask.Output,
                }
            );
            var noEditRepeated = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.RepeatImageBatch
                {
                    Name = builder.Nodes.GetUniqueName("NoEditMaskRepeat"),
                    Image = noEditAsImage.Output,
                    Amount = clipLength,
                }
            );
            var noEditBatch = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.ImageToMask
                {
                    Name = builder.Nodes.GetUniqueName("NoEditMaskBatch"),
                    Image = noEditRepeated.Output,
                    Channel = "red",
                }
            );
            controlMasks = noEditBatch.Output;

            Logger.Info(
                $"VideoInpaint: chunk {currentChunkIndex + 1} is outside the annotated scene - "
                    + "using an empty mask (original frames kept)"
            );
        }
        else if (UseSam2Tracking)
        {
            // Kijai's loader only looks in ComfyUI/models/sam2, while Stability Matrix keeps
            // shared checkpoints in Models/sam2 - stage it before the prompt is queued.
            EnsureSam2ModelAvailable();
            Logger.Info(
                $"VideoInpaint: SAM2 model={Sam2ModelName}, precision={Sam2Precision}, "
                    + $"file={EffectiveSam2ModelFileName}"
            );

            if (PositivePoints.Count == 0)
            {
                // Be explicit about the two possible causes: no frame was loaded, or the user
                // simply has not clicked any positive points yet.
                var hint =
                    FramePreview is null
                        ? "No frame is loaded - click 'Load frame' first, then LEFT-click 3-8 points inside the face/head on the frame preview. "
                        : "LEFT-click 3-8 points inside the face/head on the frame preview (green dots). ";
                throw new ValidationException(
                    hint
                        + "Right click marks areas to exclude. Auto (SAM2) mode needs at least one green point; "
                        + "scroll down in the Video & Mask pane if the frame is not visible."
                );
            }

            var sam2Model = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.DownloadAndLoadSam2Model
                {
                    Name = builder.Nodes.GetUniqueName("Sam2Loader"),
                    Model = Sam2ModelName,
                    Segmentor = "video",
                    Device = "cuda",
                    Precision = Sam2Precision,
                }
            );

            var addPoints = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.Sam2VideoSegmentationAddPoints
                {
                    Name = builder.Nodes.GetUniqueName("Sam2AddPoints"),
                    Sam2Model = sam2Model.Output,
                    CoordinatesPositive = ToCoordinateJson(PositivePoints),
                    CoordinatesNegative = NegativePoints.Count > 0 ? ToCoordinateJson(NegativePoints) : null,
                    // The points are placed on the first frame of the clip, tracking runs forward from it
                    FrameIndex = 0,
                    ObjectIndex = 0,
                    Image = clipFrames.Output,
                }
            );

            var sam2Segmentation = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.Sam2VideoSegmentation
                {
                    Name = builder.Nodes.GetUniqueName("Sam2Track"),
                    Sam2Model = addPoints.Output1,
                    InferenceState = addPoints.Output2,
                    KeepModelLoaded = false,
                }
            );

            controlMasks = sam2Segmentation.Output;
        }
        else
        {
            // The app prepares the mask itself. White painted pixels (or the white areas of a
            // black/white mask image) become 1 = replace, everything else 0 = keep.
            // Never fall back to the raw card image: that is a photo, and LoadImageMask
            // would feed its red channel into VACE as a soft, full-frame mask.
            if (GetOrCreateManualMask() is not { } maskSource)
            {
                throw new ValidationException(
                    "Manual mask is empty. Click 'Edit mask', paint the area to replace in WHITE and press Save, "
                        + "or load a black/white mask PNG into the mask card (white = replace, black = keep). "
                        + "A normal photo is not a mask."
                );
            }

            if (
                preparedManualMaskTotalPixels > 0
                && preparedManualMaskReplacePixels
                    < System.Math.Max(256, preparedManualMaskTotalPixels / 2000)
            )
            {
                throw new ValidationException(
                    $"Manual mask covers only {preparedManualMaskReplacePixels} of "
                        + $"{preparedManualMaskTotalPixels} pixels "
                        + $"({100.0 * preparedManualMaskReplacePixels / preparedManualMaskTotalPixels:0.00}%). "
                        + "Paint a larger WHITE area in 'Edit mask' and check it with 'Preview manual mask'."
                );
            }

            var maskUploadName = maskSource.GetHashGuidFileNameCached("Inference");
            Logger.Info(
                $"VideoInpaint: prepared manual mask -> {maskUploadName}, "
                    + $"white (replace) pixels = {preparedManualMaskReplacePixels} / {preparedManualMaskTotalPixels}"
            );

            var maskLoad = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.LoadImageMask
                {
                    Name = builder.Nodes.GetUniqueName("LoadMaskImage"),
                    Image = maskUploadName,
                    Channel = "red",
                }
            );

            // WanVaceToVideo pads missing mask frames with 1.0 (= replace everything),
            // so the single mask has to be repeated for every frame
            var maskAsImage = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.MaskToImage
                {
                    Name = builder.Nodes.GetUniqueName("MaskToImage"),
                    Mask = maskLoad.Output,
                }
            );
            var maskRepeated = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.RepeatImageBatch
                {
                    Name = builder.Nodes.GetUniqueName("RepeatMask"),
                    Image = maskAsImage.Output,
                    Amount = clipLength,
                }
            );
            var maskBatch = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.ImageToMask
                {
                    Name = builder.Nodes.GetUniqueName("MaskBatch"),
                    Image = maskRepeated.Output,
                    Channel = "red",
                }
            );

            controlMasks = maskBatch.Output;
        }

        // Negative values erode the mask, which keeps more of the original motion around
        // the outline; positive values expand the erased area and freeze even more.
        if (MaskGrow != 0)
        {
            var grownMask = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.GrowMask
                {
                    Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.GrowMask)),
                    Mask = controlMasks,
                    Expand = MaskGrow,
                    TaperedCorners = true,
                }
            );
            controlMasks = grownMask.Output;
        }

        // CRITICAL: VACE keeps the ORIGINAL pixels inside the masked region in its
        // "reactive" channel (reactive = control_video * mask + 0.5). If the original face
        // is left there, the model simply reproduces it - this is why neither SAM2 nor
        // manual mode changed anything. The official VACE inpainting workflow blanks that
        // region with ImageCompositeMasked before WanVaceToVideo, so the model has to
        // generate new content from reference_image + prompt.
        var invertedControlMask = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.InvertMask
            {
                Name = builder.Nodes.GetUniqueName("InvertControlMask"),
                Mask = controlMasks,
            }
        );
        var blankControlSource = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.MaskToImage
            {
                Name = builder.Nodes.GetUniqueName("BlankControlSource"),
                Mask = invertedControlMask.Output,
            }
        );
        var compositedControlVideo = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.ImageCompositeMasked
            {
                Name = builder.Nodes.GetUniqueName("BlankControlVideo"),
                Destination = clipFrames.Output,
                Source = blankControlSource.Output,
                X = 0,
                Y = 0,
                ResizeSource = true,
                Mask = controlMasks,
            }
        );
        Logger.Info("VideoInpaint: blanked masked region in control_video (ImageCompositeMasked)");

        // Motion preservation. VACE builds two control channels from control_video:
        //   inactive = (control_video - 0.5) * (1 - mask) + 0.5
        //   reactive = (control_video - 0.5) * mask        + 0.5
        // After blanking, reactive contains NO original pixels in the masked area, so the
        // model can only copy the static reference_image and the mouth/expressions freeze.
        // Blending the original clip back in with a constant mask feeds that motion back into
        // reactive while keeping the mask itself binary, so VACE still knows what to regenerate.
        //   0 -> fully blanked (frozen face, strongest identity change)
        //   1 -> original clip (exact original motion, no identity change)
        var controlVideoForVace = compositedControlVideo.Output;
        // Protected chunks keep the original frames as-is: feed the unmodified clip back in.
        var motionPreservation = currentChunkSkipMask
            ? 1.0d
            : System.Math.Clamp(EffectiveMotionPreservation, 0.0d, 1.0d);
        if (motionPreservation > 0.0d)
        {
            // composite() interpolates any mask to the frame size, so a tiny constant mask
            // becomes a uniform blend factor without needing the frame dimensions.
            var blendMask = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.SolidMask
                {
                    Name = builder.Nodes.GetUniqueName("MotionBlendMask"),
                    Value = motionPreservation,
                    Width = 8,
                    Height = 8,
                }
            );
            var motionBlend = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.ImageCompositeMasked
                {
                    Name = builder.Nodes.GetUniqueName("BlendOriginalMotion"),
                    Destination = controlVideoForVace,
                    Source = clipFrames.Output,
                    X = 0,
                    Y = 0,
                    ResizeSource = true,
                    Mask = blendMask.Output,
                }
            );
            controlVideoForVace = motionBlend.Output;
        }
        Logger.Info(
            $"VideoInpaint: motion preservation = {motionPreservation:0.00} "
                + $"({motionPreservation * 100:0}% of the original face motion kept in reactive)"
        );

        // Reference image (optional): what the masked area should turn into.
        // Scene-cut protected chunks have no masked area at all: feeding the new identity
        // here would only encourage VACE to redraw an unrelated person's face, so remove it.
        ImageNodeConnection? referenceImage = null;
        if (!currentChunkSkipMask && ReferenceImageCardViewModel.ImageSource is { } refImage)
        {
            var refImageLoad = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.LoadImage
                {
                    Name = builder.Nodes.GetUniqueName("LoadReferenceImage"),
                    Image = refImage.GetHashGuidFileNameCached("Inference"),
                }
            );
            referenceImage = refImageLoad.Output1;
        }

        // WanVaceToVideo node - the core VACE inpainting node.
        // It fills the masked area of the control video with neutral gray internally.
        var baseConditioning = builder.Connections.GetRefinerOrBaseConditioning();
        
        // DEBUG: Log reference image and mask connections
        var refImgStatus = referenceImage != null ? "SET" : "NULL";
        var maskStatus = controlMasks != null ? "SET" : "NULL";
        var clipStatus = clipFrames.Output != null ? "SET" : "NULL";
        var refImgSrcStatus = ReferenceImageCardViewModel.ImageSource != null ? "SET" : "NULL";
        var maskImgSrcStatus = MaskImageCardViewModel.ImageSource != null ? "SET" : "NULL";
        
        Logger.Info($"VideoInpaint: referenceImage connection = {refImgStatus}");
        Logger.Info($"VideoInpaint: controlMasks connection = {maskStatus}");
        Logger.Info($"VideoInpaint: clipFrames.Output = {clipStatus}");
        Logger.Info($"VideoInpaint: UseSam2Tracking = {UseSam2Tracking}");
        Logger.Info($"VideoInpaint: ReferenceImageCardViewModel.ImageSource = {refImgSrcStatus}");
        Logger.Info($"VideoInpaint: MaskImageCardViewModel.ImageSource = {maskImgSrcStatus}");
        Logger.Info($"VideoInpaint: EffectiveMotionPreservation = {EffectiveMotionPreservation:0.00}");
        
        var wanVaceToVideo = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.WanVaceToVideo
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.WanVaceToVideo)),
                Positive = baseConditioning.Positive,
                Negative = baseConditioning.Negative,
                Vae = builder.Connections.GetDefaultVAE(),
                ControlVideo = controlVideoForVace,
                ControlMasks = controlMasks,
                ReferenceImage = referenceImage,
                Width = SamplerCardViewModel.Width,
                Height = SamplerCardViewModel.Height,
                Length = clipLength,
                BatchSize = BatchSizeCardViewModel.BatchSize,
                Strength = 1.0d,
            }
        );
        
        var wanOut1 = wanVaceToVideo.Output1 != null ? "SET" : "NULL";
        var wanOut2 = wanVaceToVideo.Output2 != null ? "SET" : "NULL";
        var wanOut3 = wanVaceToVideo.Output3 != null ? "SET" : "NULL";
        var wanOut4 = wanVaceToVideo.Output4 != null ? "SET" : "NULL";
        Logger.Info($"VideoInpaint: WanVaceToVideo created, outputs: {wanOut1}, {wanOut2}, {wanOut3}, {wanOut4}");

        // KSampler
        var kSampler = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.KSampler
            {
                Name = "Sampler",
                Model = builder.Connections.Base.Model,
                Seed = builder.Connections.Seed,
                SamplerName = SamplerCardViewModel.SelectedSampler?.Name ?? "uni_pc",
                Scheduler = SamplerCardViewModel.SelectedScheduler?.Name ?? "simple",
                Steps = SamplerCardViewModel.Steps,
                Cfg = SamplerCardViewModel.CfgScale,
                Positive = wanVaceToVideo.Output1,
                Negative = wanVaceToVideo.Output2,
                LatentImage = wanVaceToVideo.Output3,
                Denoise = SamplerCardViewModel.DenoiseStrength
            }
        );

        // Remove the latent frames that belong to the reference image
        var trimLatent = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.TrimVideoLatent
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.TrimVideoLatent)),
                Samples = kSampler.Output,
                TrimAmount = wanVaceToVideo.Output4,
            }
        );

        // Tiled VAEDecode (uses TiledVAEModule settings if enabled)
        var tiledVaeDecode = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.TiledVAEDecode
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.TiledVAEDecode)),
                Samples = trimLatent.Output,
                Vae = builder.Connections.GetDefaultVAE(),
                TileSize = 256,
                Overlap = 64,
                TemporalSize = 32,
                TemporalOverlap = 8
            }
        );

        // SaveAnimatedMP4Advanced for video output (uses the available Advanced node)
        var saveVideo = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.SaveAnimatedMP4Advanced
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.SaveAnimatedMP4Advanced)),
                Images = tiledVaeDecode.Output,
                // NOTE: SaveAnimatedMP4Advanced ignores this FilenamePrefix input and instead
                // names files from a timestamp + model + seed (see the node's own code). Chunks
                // still get unique names because they encode strictly sequentially, so mtime
                // order == chunk order - which RecoverChunkVideos relies on. We keep setting the
                // prefix so the node would honor it if that behaviour ever changes.
                FilenamePrefix = $"wan_vace_inpaint{chunkSuffix}",
                // In split-base mode the probed input FPS is used so the joined MP4 keeps
                // exactly the same duration as the source video.
                Fps = outputFps,
                Crf = 18,
                Codec = "libx264",
                Container = "mp4",
                Bitrate = VideoOutputSettingsCardViewModel.Bitrate,
                // Hidden inputs for metadata
                Cfg = SamplerCardViewModel.CfgScale,
                Steps = SamplerCardViewModel.Steps,
                SamplerName = SamplerCardViewModel.SelectedSampler?.Name ?? "uni_pc",
                SchedulerName = SamplerCardViewModel.SelectedScheduler?.Name ?? "simple",
                ModelName = ModelCardViewModel.SelectedModel?.FileName,
                VaeName = ModelCardViewModel.SelectedVae?.FileName
            }
        );

        builder.Connections.OutputNodes.Add(saveVideo);

        applyArgs.InvokeAllPreOutputActions();

        // Do NOT call VideoOutputSettingsCardViewModel.ApplyStep here. This view model already
        // owns its output node above, and Connections.Primary is still the EMPTY latent from
        // SetupEmptyLatentSource (SamplerCardViewModel.ApplyStep is not used in this workflow).
        // Calling ApplyStep decoded that empty latent and produced a second, flat grey
        // "InferenceVideo" MP4 which the gallery collected as a result alongside the real one.
        // FPS / CRF / codec / bitrate from the card are applied directly to saveVideo instead.
    }

    /// <summary>
    /// Snapshot the MP4s already present in ComfyUI's output folder.
    /// SaveAnimatedMP4Advanced writes its MP4 straight to disk and emits NO ComfyImage, so
    /// RunGeneration adds nothing to the gallery - diffing the gallery always reported
    /// "chunk produced 0 video(s)" and the final concat was skipped. The output folder is
    /// therefore the primary source of truth for chunk discovery.
    /// </summary>
    private static Dictionary<string, (long Ticks, long Length)> SnapshotComfyOutputVideos(
        string? outputDir
    )
    {
        var snapshot = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(outputDir) || !Directory.Exists(outputDir))
            return snapshot;

        try
        {
            foreach (
                var file in Directory.EnumerateFiles(
                    outputDir,
                    "*.mp4",
                    SearchOption.TopDirectoryOnly
                )
            )
            {
                try
                {
                    var info = new FileInfo(file);
                    snapshot[file] = (info.LastWriteTimeUtc.Ticks, info.Length);
                }
                catch
                {
                    // A file being written or removed by another process - ignore it.
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "VideoInpaint: could not snapshot ComfyUI's output folder");
        }

        return snapshot;
    }

    /// <summary>
    /// Find the MP4 produced by the chunk that just finished. Polls briefly because the
    /// websocket "execution complete" event can arrive a moment before the encoder flushes.
    /// </summary>
    private static async Task<string?> DiscoverChunkVideoAsync(
        string? outputDir,
        IReadOnlyDictionary<string, (long Ticks, long Length)> before,
        DateTime startedUtc,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrEmpty(outputDir) || !Directory.Exists(outputDir))
            return null;

        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = new List<(string Path, DateTime Modified)>();

            try
            {
                foreach (
                    var file in Directory.EnumerateFiles(
                        outputDir,
                        "*.mp4",
                        SearchOption.TopDirectoryOnly
                    )
                )
                {
                    FileInfo info;
                    try
                    {
                        info = new FileInfo(file);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!info.Exists || info.Length < 1024 || info.LastWriteTimeUtc < startedUtc)
                        continue;

                    if (
                        before.TryGetValue(file, out var old)
                        && old.Ticks == info.LastWriteTimeUtc.Ticks
                        && old.Length == info.Length
                    )
                        continue; // unchanged pre-existing file

                    candidates.Add((file, info.LastWriteTimeUtc));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "VideoInpaint: error while scanning for the chunk MP4");
            }

            if (candidates.Count > 0)
            {
                // If more than one MP4 appeared during this chunk, take the newest one.
                var newest = candidates.OrderByDescending(c => c.Modified).First().Path;
                try
                {
                    var size = new FileInfo(newest).Length;
                    await Task.Delay(200, cancellationToken);
                    // Only accept the file once the encoder has stopped growing it.
                    if (size == new FileInfo(newest).Length)
                        return newest;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    return newest;
                }

                continue;
            }

            await Task.Delay(300, cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// Last-resort recovery for the chunk join. When per-chunk discovery (folder snapshot +
    /// poll) misses a file, re-scan ComfyUI's output folder for every MP4 created since this
    /// batch started and order them by mtime. Because chunks run strictly sequentially, mtime
    /// order IS chunk order. If exactly <paramref name="expectedCount"/> files are found, the
    /// caller can proceed with the concat; otherwise an empty list is returned and the join is
    /// skipped (as before) so we never feed an incomplete/wrong set to ffmpeg.
    /// </summary>
    private static List<string> RecoverChunkVideos(
        string? outputDir,
        DateTime batchStartedUtc,
        int expectedCount
    )
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(outputDir) || !Directory.Exists(outputDir) || expectedCount <= 1)
            return result;

        try
        {
            var found = new List<string>();
            foreach (
                var file in Directory.EnumerateFiles(
                    outputDir,
                    "*.mp4",
                    SearchOption.TopDirectoryOnly
                )
            )
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Exists && info.Length >= 1024 && info.LastWriteTimeUtc >= batchStartedUtc)
                        found.Add(file);
                }
                catch
                {
                    // Ignore files locked by another process.
                }
            }

            found.Sort(
                (a, b) =>
                    new FileInfo(a).LastWriteTimeUtc.CompareTo(new FileInfo(b).LastWriteTimeUtc)
            );

            if (found.Count == expectedCount)
            {
                result.AddRange(found);
                Logger.Info($"VideoInpaint: fallback recovered all {expectedCount} chunk videos by mtime");
            }
            else
            {
                Logger.Warn(
                    $"VideoInpaint: fallback found {found.Count} MP4(s) since batch start but "
                        + $"expected {expectedCount}; not using recovery (join skipped)"
                );
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "VideoInpaint: fallback chunk recovery failed");
            result.Clear();
        }

        return result;
    }

    /// <inheritdoc />
    protected override async Task GenerateImageImpl(
        GenerateOverrides overrides,
        CancellationToken cancellationToken
    )
    {
        if (!await CheckClientConnectedWithPrompt() || !ClientManager.IsConnected)
            return;

        if (!await ModelCardViewModel.ValidateModel())
            return;

        var seedCard = StackCardViewModel.GetCard<SeedCardViewModel>();
        if (overrides is not { UseCurrentSeed: true } && seedCard.IsRandomizeEnabled)
            seedCard.GenerateNewSeed();

        Logger.Info($"VideoInpaint: GenerateImageImpl called with VideoInputPath={VideoInputPath}, MaskImage={MaskImageCardViewModel.ImageSource?.LocalFile?.FullPath}");

        var client = ClientManager.Client;
        var batches = BatchSizeCardViewModel.BatchCount;
        var runId = Guid.NewGuid().ToString("N")[..8];

        // Probe the input and compute the temporal chunk plan. In split-base mode the FULL
        // source length is used (not the sampler card's Length, which would truncate it).
        var plan = await BuildChunkPlanAsync(cancellationToken);
        ChunkPlanSummary = plan.Summary;
        Logger.Info($"VideoInpaint: {plan.Summary}");

        // Face-swap guard. Without a Reference Image VACE has no target identity to paint into
        // the masked region, so it repaints the face with generic/neutral content - it looks
        // like "something happened to the face" but the face is never actually swapped. This is
        // the single most common cause of that symptom, so surface it before the (slow) run.
        if (ReferenceImageCardViewModel.ImageSource is null)
        {
            Logger.Warn(
                "VideoInpaint: no Reference Image loaded - the masked region will be repainted "
                    + "without a target identity, so the face will NOT be swapped. Load the target "
                    + "face in the Reference Image card (object removal is fine without it)."
            );
            notifications.Show(
                "No reference image",
                "No Reference Image is loaded. VACE will repaint the masked area without a "
                    + "target identity, so the face will not actually be swapped. Load the target "
                    + "face in the Reference Image card for a face swap. (Object removal works "
                    + "without it.)",
                NotificationType.Warning
            );
        }
        else
        {
            Logger.Info(
                $"VideoInpaint: Reference Image loaded. Face swap mode active. "
                    + $"EffectiveMotionPreservation = {EffectiveMotionPreservation:0.00} "
                    + "(0 = fully blanked original face, VACE must use the reference identity)."
            );
        }

        // Publish the plan to BuildPrompt. Generation is strictly sequential (await), so these
        // fields need no locking.
        currentRunId = runId;
        currentChunkCount = plan.Chunks.Count;
        effectiveFps = plan.Fps;
        totalInputFrames = plan.TotalFrames;
        videoUploadedForRun = false;

        for (var batchIndex = 0; batchIndex < batches; batchIndex++)
        {
            var seed = seedCard.Seed + batchIndex;
            var chunkFiles = new List<string>();
            // ComfyUI output folder — same dir for all chunks in this batch. SaveAnimatedMP4Advanced
            // writes here, and JoinChunksAsync will also write the joined video here so it shows up
            // in the gallery (which monitors the ComfyUI output folder).
            var comfyOutputDir = client.OutputImagesDir?.FullPath;
            // Everything below this instant that ComfyUI writes to its output folder (MP4s) is
            // part of THIS batch's chunks; used by RecoverChunkVideos to re-scan if discovery
            // misses a file.
            var batchStartedUtc = DateTime.UtcNow;

            for (var chunkIndex = 0; chunkIndex < plan.Chunks.Count; chunkIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = plan.Chunks[chunkIndex];
                currentChunkIndex = chunkIndex;
                currentChunkStartFrame = chunk.StartFrame;
                currentChunkLength = chunk.Length;
                currentChunkSkipMask = chunk.SkipMask;

                Logger.Info(
                    $"VideoInpaint: chunk {chunkIndex + 1}/{plan.Chunks.Count} "
                        + $"(frames {chunk.StartFrame}..{chunk.StartFrame + chunk.Length - 1}), "
                        + $"batch {batchIndex + 1}/{batches}"
                        + (chunk.SkipMask ? ", scene-cut protected (no mask)" : "")
                );

                var buildPromptArgs = new BuildPromptEventArgs
                {
                    Overrides = overrides,
                    SeedOverride = seed,
                };
                BuildPrompt(buildPromptArgs);

                var inferenceProject = InferenceProjectDocument.FromLoadable(this);
                if (inferenceProject.State?["Seed"]?["Seed"] is not null)
                    inferenceProject = inferenceProject.WithState(x => x["Seed"]["Seed"] = seed);

                var generationArgs = new ImageGenerationEventArgs
                {
                    Client = client,
                    Nodes = buildPromptArgs.Builder.ToNodeDictionary(),
                    OutputNodeNames = buildPromptArgs.Builder.Connections.OutputNodeNames.ToArray(),
                    Parameters = SaveStateToParameters(new GenerationParameters()) with
                    {
                        Seed = Convert.ToUInt64(seed),
                    },
                    Project = inferenceProject,
                    FilesToTransfer = buildPromptArgs.FilesToTransfer,
                    BatchIndex = batchIndex,
                    ClearOutputImages = batchIndex == 0 && chunkIndex == 0,
                };

                // PRIMARY: snapshot ComfyUI's output folder. SaveAnimatedMP4Advanced writes
                // its MP4 straight to disk and emits no ComfyImage, so a gallery diff alone
                // can never find the chunk file.
                // (comfyOutputDir is declared at batch scope — same for all chunks.)
                var beforeVideos = SnapshotComfyOutputVideos(comfyOutputDir);
                var chunkStartedUtc = DateTime.UtcNow.AddSeconds(-2);

                // FALLBACK: some output nodes DO emit a ComfyImage; keep the gallery diff too.
                var knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in ImageGalleryCardViewModel.ImageSources)
                {
                    var path = source.LocalFile?.FullPath;
                    if (!string.IsNullOrEmpty(path))
                        knownFiles.Add(path);
                }

                await RunGeneration(generationArgs, cancellationToken);

                if (
                    generationArgs.Extra != null
                    && generationArgs.Extra.TryGetValue("vram_freed_mb", out var freedObj)
                )
                {
                    if (freedObj is float freed)
                        Logger.Info($"MemoryCleanup: VRAM Freed = {freed:F2} MB");
                    else
                        Logger.Warn("MemoryCleanup: vram_freed_mb returned but not a float");
                }

                var produced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in ImageGalleryCardViewModel.ImageSources)
                {
                    var path = source.LocalFile?.FullPath;
                    if (
                        string.IsNullOrEmpty(path)
                        || knownFiles.Contains(path)
                        || !path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                    )
                        continue;

                    produced.Add(path);
                }

                var discovered = await DiscoverChunkVideoAsync(
                    comfyOutputDir,
                    beforeVideos,
                    chunkStartedUtc,
                    cancellationToken
                );
                if (!string.IsNullOrEmpty(discovered))
                    produced.Add(discovered);

                string? chunkVideo = null;
                if (produced.Count == 1)
                {
                    chunkVideo = produced.First();
                }
                else if (produced.Count > 1)
                {
                    chunkVideo = produced
                        .OrderByDescending(p =>
                        {
                            try
                            {
                                return File.GetLastWriteTimeUtc(p);
                            }
                            catch
                            {
                                return DateTime.MinValue;
                            }
                        })
                        .First();
                }

                if (string.IsNullOrEmpty(chunkVideo))
                {
                    Logger.Warn(
                        $"VideoInpaint: chunk {chunkIndex + 1} produced NO MP4 "
                            + $"(output folder: {comfyOutputDir ?? "unknown"})"
                    );
                }
                else
                {
                    chunkFiles.Add(chunkVideo);

                    // Make the individual chunk visible in the gallery too. Files found on
                    // disk are not added by RunGeneration, so do it here (deduplicated).
                    if (!knownFiles.Contains(chunkVideo))
                    {
                        var filePath = new FilePath(chunkVideo);
                        ImageGalleryCardViewModel.ImageSources.Add(
                            new ImageSource(filePath)
                            {
                                Label = $"Chunk {chunkIndex + 1}/{plan.Chunks.Count}",
                            }
                        );
                        EventManager.Instance.OnImageFileAdded(filePath);
                    }

                    Logger.Info($"VideoInpaint: chunk {chunkIndex + 1} -> {chunkVideo}");
                }
            }

            if (plan.Chunks.Count <= 1)
                continue;

            if (chunkFiles.Count == plan.Chunks.Count)
            {
                await JoinChunksAsync(
                    chunkFiles,
                    plan,
                    runId,
                    batchIndex,
                    comfyOutputDir,
                    cancellationToken
                );
            }
            else
            {
                // Primary per-chunk discovery missed one or more files. Before giving up on the
                // join, try the last-resort mtime scan of the output folder.
                var recovered = RecoverChunkVideos(
                    client.OutputImagesDir?.FullPath,
                    batchStartedUtc,
                    plan.Chunks.Count
                );
                if (recovered.Count == plan.Chunks.Count)
                {
                    Logger.Warn(
                        $"VideoInpaint: primary discovery found {chunkFiles.Count} chunk(s); "
                            + "recovered the full set via folder scan and joining"
                    );
                    await JoinChunksAsync(
                        recovered,
                        plan,
                        runId,
                        batchIndex,
                        comfyOutputDir,
                        cancellationToken
                    );
                }
                else
                {
                    Logger.Warn(
                        $"VideoInpaint: expected {plan.Chunks.Count} chunk videos but found "
                            + $"{chunkFiles.Count}; skipping the join, individual chunks are kept "
                            + $"(scanned folder: {client.OutputImagesDir?.FullPath ?? "unknown"})"
                    );
                    notifications.Show(
                        "Chunk join skipped",
                        $"Expected {plan.Chunks.Count} chunk videos, found {chunkFiles.Count}. "
                            + "The individual chunks are kept in the gallery. "
                            + $"ComfyUI output folder: {client.OutputImagesDir?.FullPath ?? "unknown"}",
                        NotificationType.Warning
                    );
                }
            }
        }
    }

    /// <inheritdoc />
    protected override IEnumerable<ImageSource> GetInputImages()
    {
        // BuildPrompt and the uploader must use the exact SAME prepared mask instance:
        // LoadImageMask references it by its hash-based filename.
        if (!UseSam2Tracking && GetOrCreateManualMask() is { } preparedMask)
        {
            yield return preparedMask;
        }

        if (ReferenceImageCardViewModel.ImageSource is { } refImage)
            yield return refImage;
    }
}
