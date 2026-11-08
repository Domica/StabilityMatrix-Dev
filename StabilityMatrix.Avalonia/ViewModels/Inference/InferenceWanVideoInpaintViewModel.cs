using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Avalonia.Controls.Notifications;
using Avalonia.Media.Imaging;
using System.Text.Json.Serialization;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
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
    private bool useSam2Tracking = true;

    [ObservableProperty]
    [JsonPropertyName("Sam2Model")]
    private string sam2ModelName = "sam2.1_hiera_small.safetensors";

    /// <summary>
    /// Pixels the mask is grown by (covers hair/edges/shadows the tracker misses).
    /// </summary>
    [ObservableProperty]
    [JsonPropertyName("MaskGrow")]
    private int maskGrow = 8;

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
        private set => SetProperty(ref framePreview, value);
    }

    private IReadOnlyList<MaskPoint> positivePoints = Array.Empty<MaskPoint>();

    /// <summary>
    /// Points (frame pixel coordinates) that mark what should be replaced.
    /// </summary>
    [JsonPropertyName("PositivePoints")]
    public IReadOnlyList<MaskPoint> PositivePoints
    {
        get => positivePoints;
        set => SetProperty(ref positivePoints, value ?? Array.Empty<MaskPoint>());
    }

    private IReadOnlyList<MaskPoint> negativePoints = Array.Empty<MaskPoint>();

    /// <summary>
    /// Points (frame pixel coordinates) that mark what must NOT be replaced.
    /// </summary>
    [JsonPropertyName("NegativePoints")]
    public IReadOnlyList<MaskPoint> NegativePoints
    {
        get => negativePoints;
        set => SetProperty(ref negativePoints, value ?? Array.Empty<MaskPoint>());
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
        ReferenceImageCardViewModel.IsMaskEditorEnabled = true;

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
            notifications.Show("No points", "Click on the frame to mark what to replace (left click) or keep (right click)", NotificationType.Warning);
            return;
        }

        if (!await CheckClientConnectedWithPrompt() || !ClientManager.IsConnected)
            return;

        if (ClientManager.Client is not { } client)
            return;

        try
        {
            IsLoadingFrame = true;

            var sanitizedVideoName = GetComfyVideoFileName(VideoInputPath);
            await client.UploadFileAsync(VideoInputPath, System.IO.Path.Combine("input", sanitizedVideoName));

            var builder = new ComfyNodeBuilder();

            var loadVideo = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.LoadVideo { Name = "PreviewLoadVideo", Video = sanitizedVideoName }
            );
            var components = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.GetVideoComponents { Name = "PreviewComponents", Video = loadVideo.Output }
            );

            // Get first, middle, and last frame
            var frameCount = SamplerCardViewModel.Length;
            var middleIndex = frameCount / 2;
            var lastIndex = frameCount - 1;

            var framesToPreview = new[] { 0, middleIndex, lastIndex }.Distinct().ToArray();

            var maskFrames = new List<Bitmap>();

            foreach (var frameIndex in framesToPreview)
            {
                var firstFrame = builder.Nodes.AddTypedNode(
                    new ComfyNodeBuilder.ImageFromBatch
                    {
                        Name = $"PreviewFrame_{frameIndex}",
                        Image = components.Output,
                        BatchIndex = frameIndex,
                        Length = 1,
                    }
                );

                var sam2Model = builder.Nodes.AddTypedNode(
                    new ComfyNodeBuilder.DownloadAndLoadSam2Model
                    {
                        Name = builder.Nodes.GetUniqueName("Sam2Loader"),
                        Model = Sam2ModelName,
                        Segmentor = "video",
                        Device = "cuda",
                        Precision = "fp16",
                    }
                );

                var addPoints = builder.Nodes.AddTypedNode(
                    new ComfyNodeBuilder.Sam2VideoSegmentationAddPoints
                    {
                        Name = builder.Nodes.GetUniqueName("Sam2AddPoints"),
                        Sam2Model = sam2Model.Output,
                        CoordinatesPositive = ToCoordinateJson(PositivePoints),
                        CoordinatesNegative = NegativePoints.Count > 0 ? ToCoordinateJson(NegativePoints) : null,
                        FrameIndex = 0,
                        ObjectIndex = 0,
                        Image = firstFrame.Output,
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

                // Get mask for this frame
                var frameMask = builder.Nodes.AddTypedNode(
                    new ComfyNodeBuilder.ImageFromBatch
                    {
                        Name = $"MaskFrame_{frameIndex}",
                        Image = sam2Segmentation.Output,
                        BatchIndex = 0,
                        Length = 1,
                    }
                );

                var preview = builder.Nodes.AddTypedNode(
                    new ComfyNodeBuilder.PreviewImage { Name = $"PreviewMask_{frameIndex}", Images = firstFrame.Output }
                );
                builder.Connections.OutputNodes.Add(preview);

                var promptTask = await client.QueuePromptAsync(builder.ToNodeDictionary());
                await promptTask.Task;

                var outputs = await client.GetImagesForExecutedPromptAsync(promptTask.Id);
                var frameOutput = outputs.Values.Where(images => images is { Count: > 0 }).Select(images => images![0]).FirstOrDefault();

                if (frame is not null)
                {
                    await using var stream = await client.GetImageStreamAsync(frame);
                    using var memory = new MemoryStream();
                    await stream.CopyToAsync(memory);
                    memory.Position = 0;

                    maskFrames.Add(new Bitmap(memory));
                }
            }

            // Show the mask frames - for now just show the first frame
            if (maskFrames.Count > 0)
            {
                FramePreview?.Dispose();
                FramePreview = maskFrames[0];
                notifications.Show("Mask preview", $"Generated mask preview for {maskFrames.Count} frame(s)", NotificationType.Information);
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

            var sanitizedVideoName = $"sm_inpaint_{Guid.NewGuid():N}{System.IO.Path.GetExtension(VideoInputPath)}";
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
            var frame = outputs.Values.Where(images => images is { Count: > 0 }).Select(images => images![0]).FirstOrDefault();

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
                FramePreview = new Bitmap(memory);
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
                FramePreview = null;

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

    /// <inheritdoc />
    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        var applyArgs = args.ToModuleApplyStepEventArgs();
        var builder = args.Builder;

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
            SamplerCardViewModel.Length,
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

        // Sanitize the filename for ComfyUI's input directory (avoid special chars, spaces)
        var sanitizedVideoName = $"sm_inpaint_{Guid.NewGuid():N}{System.IO.Path.GetExtension(VideoInputPath)}";

        // Copy to ComfyUI's input/ directory so LoadVideo can find it
        // UploadFileAsync copies to LocalServerPath/destinationRelativePath
        var destinationPath = $"input/{sanitizedVideoName}";
        if (!args.FilesToTransfer.Any(x => x.SourcePath == VideoInputPath))
            args.FilesToTransfer.Add((VideoInputPath, destinationPath));

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

        // Only the first <Length> frames are used, so only track/encode those
        var clipLength = SamplerCardViewModel.Length;
        var clipFrames = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.ImageFromBatch
            {
                Name = builder.Nodes.GetUniqueName("ClipFrames"),
                Image = videoComponents.Output,
                BatchIndex = 0,
                Length = clipLength,
            }
        );

        // One mask per frame: white = replace, black = keep
        ImageMaskConnection controlMasks;

        if (UseSam2Tracking)
        {
            if (PositivePoints.Count == 0)
            {
                throw new ValidationException(
                    "Click the object to replace on the frame preview (left click). Right click marks areas to exclude."
                );
            }

            var sam2Model = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.DownloadAndLoadSam2Model
                {
                    Name = builder.Nodes.GetUniqueName("Sam2Loader"),
                    Model = Sam2ModelName,
                    Segmentor = "video",
                    Device = "cuda",
                    Precision = "fp16",
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
            // Static mask image (same mask on every frame). Black/white: white = replace
            if (MaskImageCardViewModel.ImageSource is not { } maskImage)
            {
                throw new ValidationException(
                    "Mask image is required when SAM2 tracking is off (white = replace, black = keep)"
                );
            }

            // The card either has a mask painted with its mask editor (painted = replace),
            // or the card image itself is a black/white mask (white = replace).
            // GetInputImages() returns [image] or [image, rendered mask]; both get uploaded
            // by the base class into the ComfyUI "input/Inference" folder.
            var maskCardImages = MaskImageCardViewModel.GetInputImages().ToList();
            var maskSource = maskCardImages.Count > 1 ? maskCardImages[1] : maskImage;

            var maskLoad = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.LoadImageMask
                {
                    Name = builder.Nodes.GetUniqueName("LoadMaskImage"),
                    Image = maskSource.GetHashGuidFileNameCached("Inference"),
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

        if (MaskGrow > 0)
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

        // Reference image (optional): what the masked area should turn into
        ImageNodeConnection? referenceImage = null;
        if (ReferenceImageCardViewModel.ImageSource is { } refImage)
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
        Logger.Info($"VideoInpaint: UseSam2Tracking = {UseSam2Tracking}");
        
        var wanVaceToVideo = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.WanVaceToVideo
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.WanVaceToVideo)),
                Positive = baseConditioning.Positive,
                Negative = baseConditioning.Negative,
                Vae = builder.Connections.GetDefaultVAE(),
                ControlVideo = clipFrames.Output,
                ControlMasks = controlMasks,
                ReferenceImage = referenceImage,
                Width = SamplerCardViewModel.Width,
                Height = SamplerCardViewModel.Height,
                Length = clipLength,
                BatchSize = BatchSizeCardViewModel.BatchSize,
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
                FilenamePrefix = "wan_vace_inpaint",
                Fps = VideoOutputSettingsCardViewModel.Fps,
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

        VideoOutputSettingsCardViewModel.ApplyStep(applyArgs);
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

        var batches = BatchSizeCardViewModel.BatchCount;
        var batchArgs = new List<ImageGenerationEventArgs>();

        for (var i = 0; i < batches; i++)
        {
            var seed = seedCard.Seed + i;

            var buildPromptArgs = new BuildPromptEventArgs { Overrides = overrides, SeedOverride = seed };
            Logger.Info($"VideoInpaint: BuildPrompt batch {i}, VideoInputPath still={VideoInputPath}");
            BuildPrompt(buildPromptArgs);

            var inferenceProject = InferenceProjectDocument.FromLoadable(this);
            if (inferenceProject.State?["Seed"]?["Seed"] is not null)
                inferenceProject = inferenceProject.WithState(x => x["Seed"]["Seed"] = seed);

            var generationArgs = new ImageGenerationEventArgs
            {
                Client = ClientManager.Client,
                Nodes = buildPromptArgs.Builder.ToNodeDictionary(),
                OutputNodeNames = buildPromptArgs.Builder.Connections.OutputNodeNames.ToArray(),
                Parameters = SaveStateToParameters(new GenerationParameters()) with { Seed = Convert.ToUInt64(seed) },
                Project = inferenceProject,
                FilesToTransfer = buildPromptArgs.FilesToTransfer,
                BatchIndex = i,
                ClearOutputImages = i == 0,
            };

            batchArgs.Add(generationArgs);
        }

        foreach (var args in batchArgs)
        {
            await RunGeneration(args, cancellationToken);

            if (args.Extra != null && args.Extra.TryGetValue("vram_freed_mb", out var freedObj))
            {
                if (freedObj is float freed)
                {
                    Logger.Info($"MemoryCleanup: VRAM Freed = {freed:F2} MB");
                }
                else
                {
                    Logger.Warn("MemoryCleanup: vram_freed_mb returned but not a float");
                }
            }
        }
    }

    /// <inheritdoc />
    protected override IEnumerable<ImageSource> GetInputImages()
    {
        // The static mask (and the rendered mask from its editor) is only used when SAM2 tracking is off
        if (!UseSam2Tracking)
        {
            foreach (var image in MaskImageCardViewModel.GetInputImages())
                yield return image;
        }

        if (ReferenceImageCardViewModel.ImageSource is { } refImage)
            yield return refImage;
    }
}
