using System.Text.Json.Serialization;
using Injectio.Attributes;
using NLog;
using StabilityMatrix.Avalonia.Extensions;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Inference.Video;
using StabilityMatrix.Avalonia.ViewModels.Settings;
using StabilityMatrix.Avalonia.Views.Inference;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceWanTextToVideoView), IsPersistent = true)]
[RegisterScoped<InferenceWanTextToVideoViewModel>, ManagedService]
public class InferenceWanTextToVideoViewModel : InferenceGenerationViewModelBase, IParametersLoadableState
{
    [JsonIgnore]
    public StackCardViewModel StackCardViewModel { get; }

    [JsonPropertyName("Model")]
    public WanModelCardViewModel ModelCardViewModel { get; }

    [JsonPropertyName("Sampler")]
    public SamplerCardViewModel SamplerCardViewModel { get; }

    [JsonPropertyName("BatchSize")]
    public BatchSizeCardViewModel BatchSizeCardViewModel { get; }

    [JsonPropertyName("Seed")]
    public SeedCardViewModel SeedCardViewModel { get; }

    [JsonPropertyName("Prompt")]
    public PromptCardViewModel PromptCardViewModel { get; }

    [JsonPropertyName("VideoOutput")]
    public VideoOutputSettingsCardViewModel VideoOutputSettingsCardViewModel { get; }

    /// <summary>
    /// When enabled and the positive prompt is left empty, run Florence2 on the reference image and
    /// feed its caption into the positive CLIPTextEncode. Opt-in: requires the Florence2 custom node
    /// pack in ComfyUI. This only supplies the prompt text; the Wan image-to-video latent
    /// conditioning derived from the start_image is untouched.
    /// </summary>
    [JsonPropertyName("UseFlorence2AutoPrompt")]
    public bool UseFlorence2AutoPrompt { get; set; }

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ISettingsManager settingsManager;

    public InferenceWanTextToVideoViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
        this.settingsManager = settingsManager;
        SeedCardViewModel = vmFactory.Get<SeedCardViewModel>();
        SeedCardViewModel.GenerateNewSeed();

        ModelCardViewModel = vmFactory.Get<WanModelCardViewModel>();
        SamplerCardViewModel = vmFactory.Get<WanSamplerCardViewModel>(sampler =>
        {
            sampler.IsDimensionsEnabled = true;
            sampler.IsCfgScaleEnabled = true;
            sampler.IsSamplerSelectionEnabled = true;
            sampler.IsSchedulerSelectionEnabled = true;
            sampler.DenoiseStrength = 1.0d;
            sampler.EnableAddons = true;
            sampler.IsLengthEnabled = true;
            sampler.Width = 832;
            sampler.Height = 480;
            sampler.Length = 33;
        });

        PromptCardViewModel = AddDisposable(vmFactory.Get<PromptCardViewModel>());
        BatchSizeCardViewModel = vmFactory.Get<BatchSizeCardViewModel>();
        VideoOutputSettingsCardViewModel = vmFactory.Get<VideoOutputSettingsCardViewModel>(vm =>
            vm.Fps = 16.0d
        );

        StackCardViewModel = vmFactory.Get<StackCardViewModel>();
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
        base.BuildPrompt(args);

        var applyArgs = args.ToModuleApplyStepEventArgs();
        var builder = args.Builder;

        builder.Connections.Seed = args.SeedOverride switch
        {
            { } seed => Convert.ToUInt64(seed),
            _ => Convert.ToUInt64(SeedCardViewModel.Seed),
        };

        // Load models
        ModelCardViewModel.ApplyStep(applyArgs);

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
        SamplerCardViewModel.ApplyStep(applyArgs);

        applyArgs.InvokeAllPreOutputActions();

        // Animated webp / video output
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
                Parameters = SaveStateToParameters(new GenerationParameters()) with
                {
                    Seed = Convert.ToUInt64(seed),
                },
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
    public GenerationParameters SaveStateToParameters(GenerationParameters parameters)
    {
        parameters = SamplerCardViewModel.SaveStateToParameters(parameters);
        parameters = ModelCardViewModel.SaveStateToParameters(parameters);
        parameters = PromptCardViewModel.SaveStateToParameters(parameters);
        parameters = VideoOutputSettingsCardViewModel.SaveStateToParameters(parameters);

        parameters.Seed = (ulong)SeedCardViewModel.Seed;
        return parameters;
    }

    /// <inheritdoc />
    public void LoadStateFromParameters(GenerationParameters parameters)
    {
        SamplerCardViewModel.LoadStateFromParameters(parameters);
        ModelCardViewModel.LoadStateFromParameters(parameters);
        PromptCardViewModel.LoadStateFromParameters(parameters);
        VideoOutputSettingsCardViewModel.LoadStateFromParameters(parameters);

        SeedCardViewModel.Seed = (long)parameters.Seed;
    }

    /// <summary>
    /// When <see cref="UseFlorence2AutoPrompt"/> is enabled and a positive CLIPTextEncode prompt is
    /// blank, builds a Florence2 captioner subgraph from <paramref name="imageLoad"/> and rewires the
    /// blank positive prompts to its caption output. The Wan image-to-video latent conditioning from
    /// the start image is left intact. No-op when the toggle is off or when every positive prompt is
    /// already filled, so a supplied prompt never pays for (or risks the failure of) an unused
    /// captioner. Requires the Florence2 custom node pack in ComfyUI.
    /// </summary>
    protected void ApplyFlorence2AutoPromptIfEnabled(
        ComfyNodeBuilder builder,
        ComfyNodeBuilder.LoadImage imageLoad
    )
    {
        if (!UseFlorence2AutoPrompt)
        {
            return;
        }

        // Positive CLIP nodes are named PositiveCLIP_<model> and carry the positive prompt as a plain
        // string (empty when the user left the box blank). Collect the blank ones to rewire.
        var blankPositiveClips = builder
            .Nodes.Where(kv =>
                kv.Key.StartsWith("PositiveCLIP_", StringComparison.Ordinal)
                && kv.Value.ClassType == "CLIPTextEncode"
                && IsBlankPromptText(
                    kv.Value.Inputs.TryGetValue("text", out var textValue) ? textValue : null
                )
            )
            .ToList();

        if (blankPositiveClips.Count == 0)
        {
            return;
        }

        var florenceLoader = new NamedComfyNode(builder.Nodes.GetUniqueName("Florence2Loader"))
        {
            ClassType = "load_florence2",
            Inputs = new Dictionary<string, object?>(),
        };
        builder.Nodes.Add(florenceLoader.Name, florenceLoader);

        // Appended so the generated video describes motion rather than a static scene.
        const string motionSuffix =
            " The subject begins to move naturally while the lighting, background and colors stay "
            + "identical to the reference frame; subtle, smooth camera motion.";

        var florenceInterrogate = new NamedComfyNode(builder.Nodes.GetUniqueName("Florence2Interrogate"))
        {
            ClassType = "florence2_interrogate",
            Inputs = new Dictionary<string, object?>
            {
                ["image"] = imageLoad.Output1.Data,
                ["model"] = new object[] { florenceLoader.Name, 0 },
                ["processor"] = new object[] { florenceLoader.Name, 1 },
                ["prefix"] = "",
                ["suffix"] = motionSuffix,
                ["negative"] = "",
                ["auto_negative"] = "",
                ["use_negative"] = false,
                ["detailed_mode"] = true,
                ["very_detailed_mode"] = false,
                ["minimal_mode"] = false,
                ["caption_only"] = true,
                ["prompt_cleaner"] = true,
                ["auto_enrich_prompt"] = false,
                ["max_tokens"] = 512,
                ["nsfw_model"] = null,
                ["nsfw_processor"] = null,
                ["nsfw_ml_threshold"] = 0.72,
                ["nsfw_tags_only"] = false,
                ["ultra_detailed_nsfw"] = false,
                ["fairface_model_path"] = "",
                ["face_detect_model_path"] = "",
                ["pose_model_path"] = "",
                ["dwpose_model_path"] = "",
                ["seg_model_path"] = "",
            },
        };
        builder.Nodes.Add(florenceInterrogate.Name, florenceInterrogate);

        // Rewire every blank positive CLIPTextEncode to the Florence2 caption output.
        foreach (var (_, node) in blankPositiveClips)
        {
            node.Inputs["text"] = new object[] { florenceInterrogate.Name, 0 };
        }
    }

    /// <summary>
    /// A positive prompt counts as "blank" when its CLIPTextEncode text input is null or whitespace.
    /// Any other value (e.g. an object[] link to a StringNodeConnection) means the prompt is supplied
    /// by another node and must not be overridden by the Florence2 caption.
    /// </summary>
    private static bool IsBlankPromptText(object? textValue) =>
        textValue switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            _ => false,
        };
}
