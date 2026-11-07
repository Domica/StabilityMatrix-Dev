using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Views.Inference;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Services;
using StabilityMatrix.Avalonia.Extensions;
using StabilityMatrix.Avalonia.ViewModels.Settings;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using NLog;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceWanVideoInpaintView), IsPersistent = true)]
[RegisterScoped<InferenceWanVideoInpaintViewModel>, ManagedService]
public class InferenceWanVideoInpaintViewModel : InferenceWanTextToVideoViewModel, IParametersLoadableState
{
    [JsonPropertyName("MaskImage")]
    public SelectImageCardViewModel MaskImageCardViewModel { get; }

    [JsonPropertyName("ReferenceImage")]
    public SelectImageCardViewModel ReferenceImageCardViewModel { get; }

    [JsonPropertyName("VideoInput")]
    public string? VideoInputPath { get; set; }

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ISettingsManager settingsManager;

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

        // Override sampler settings for VACE inpainting
        SamplerCardViewModel.Width = 720;
        SamplerCardViewModel.Height = 720;
        SamplerCardViewModel.Length = 81;
        SamplerCardViewModel.DenoiseStrength = 1.0d;
        SamplerCardViewModel.IsLengthEnabled = true;
        SamplerCardViewModel.IsCfgScaleEnabled = true;

        MaskImageCardViewModel = vmFactory.Get<SelectImageCardViewModel>();
        MaskImageCardViewModel.IsMaskEditorEnabled = true;

        ReferenceImageCardViewModel = vmFactory.Get<SelectImageCardViewModel>();

        StackCardViewModel.AddCards(
            ModelCardViewModel,
            SamplerCardViewModel,
            SeedCardViewModel,
            BatchSizeCardViewModel,
            VideoOutputSettingsCardViewModel
        );
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

        var videoFileName = System.IO.Path.GetFileName(VideoInputPath);
        args.FilesToTransfer.Add((VideoInputPath, videoFileName));

        var loadVideo = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LoadVideo
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LoadVideo)),
                Video = videoFileName
            }
        );

        // Load mask image
        if (MaskImageCardViewModel.ImageSource is not { } maskImage)
            throw new ValidationException("Mask image is required for Video Inpaint");

        var maskImageLoad = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LoadImage
            {
                Name = builder.Nodes.GetUniqueName("LoadMaskImage"),
                Image = maskImage.GetHashGuidFileNameCached("Inference")
            }
        );

        // Load reference image (optional - falls back to mask image if not provided)
        ImageNodeConnection referenceImage;
        if (ReferenceImageCardViewModel.ImageSource is { } refImage)
        {
            var refImageLoad = builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.LoadImage
                {
                    Name = builder.Nodes.GetUniqueName("LoadReferenceImage"),
                    Image = refImage.GetHashGuidFileNameCached("Inference")
                }
            );
            referenceImage = refImageLoad.Output1;
        }
        else
        {
            // Use mask image as reference fallback
            referenceImage = maskImageLoad.Output1;
        }

        // WanVaceToVideo node - the core VACE inpainting node
        var baseConditioning = builder.Connections.GetRefinerOrBaseConditioning();
        var wanVaceToVideo = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.WanVaceToVideo
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.WanVaceToVideo)),
                Positive = baseConditioning.Positive,
                Negative = baseConditioning.Negative,
                Vae = builder.Connections.GetDefaultVAE(),
                ControlVideo = loadVideo.Output,
                ControlMasks = maskImageLoad.Output2,
                ReferenceImage = referenceImage,
                Width = SamplerCardViewModel.Width,
                Height = SamplerCardViewModel.Height,
                Length = SamplerCardViewModel.Length,
                BatchSize = BatchSizeCardViewModel.BatchSize,
                Seed = Convert.ToInt32(SeedCardViewModel.Seed)
            }
        );

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

        // VAEDecode
        var vaeDecode = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.VAEDecode
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.VAEDecode)),
                Samples = kSampler.Output,
                Vae = builder.Connections.GetDefaultVAE()
            }
        );

        // SaveAnimatedMP4 for video output
        var saveVideo = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.SaveAnimatedMP4
            {
                Name = builder.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.SaveAnimatedMP4)),
                Images = vaeDecode.Output,
                FilenamePrefix = "wan_vace_inpaint",
                Fps = VideoOutputSettingsCardViewModel.Fps,
                Crf = 18,
                Codec = "libx264",
                Container = "mp4"
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

        var batches = BatchSizeCardViewModel.BatchCount;
        var batchArgs = new List<ImageGenerationEventArgs>();

        for (var i = 0; i < batches; i++)
        {
            var seed = seedCard.Seed + i;

            var buildPromptArgs = new BuildPromptEventArgs { Overrides = overrides, SeedOverride = seed };
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
        if (MaskImageCardViewModel.ImageSource is { } maskImage)
            yield return maskImage;

        if (ReferenceImageCardViewModel.ImageSource is { } refImage)
            yield return refImage;
    }
}
