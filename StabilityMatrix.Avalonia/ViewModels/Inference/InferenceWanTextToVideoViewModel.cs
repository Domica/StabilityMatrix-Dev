using System.Text.Json.Serialization;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Extensions;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Inference.Video;
using StabilityMatrix.Avalonia.Views.Inference;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Services;
using StabilityMatrix.Avalonia.ViewModels.Settings;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using NLog;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceWanTextToVideoView), IsPersistent = true)]
[RegisterScoped<InferenceWanTextToVideoViewModel>, ManagedService]
public class InferenceWanTextToVideoViewModel : InferenceGenerationViewModelBase, IParametersLoadableState
{
    [JsonIgnore] public StackCardViewModel StackCardViewModel { get; }

    [JsonPropertyName("Model")] public WanModelCardViewModel ModelCardViewModel { get; }
    [JsonPropertyName("Sampler")] public SamplerCardViewModel SamplerCardViewModel { get; }
    [JsonPropertyName("BatchSize")] public BatchSizeCardViewModel BatchSizeCardViewModel { get; }
    [JsonPropertyName("Seed")] public SeedCardViewModel SeedCardViewModel { get; }
    [JsonPropertyName("Prompt")] public PromptCardViewModel PromptCardViewModel { get; }
    [JsonPropertyName("VideoOutput")] public VideoOutputSettingsCardViewModel VideoOutputSettingsCardViewModel { get; }

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public InferenceWanTextToVideoViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
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
        VideoOutputSettingsCardViewModel = vmFactory.Get<VideoOutputSettingsCardViewModel>(vm => vm.Fps = 16.0d);

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

        // Animated webp output
        VideoOutputSettingsCardViewModel.ApplyStep(applyArgs);

        /// WAN Memory Cleanup (toggle-controlled)
        if (SettingsManager.Settings.WanMemoryCleanupEnabled)
        {
            builder.Nodes.AddTypedNode(
                new NamedComfyNode(builder.Nodes.GetUniqueName("WANMemoryCleanup"))
                {
                    ClassType = "WANMemoryCleanupNode",
                    Inputs = new Dictionary<string, object?>
                    {
                    ["anything"] = null,
                    ["offload_wan_models"] = true,
                    ["offload_cache"] = true
                    }
                }
            );
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

            if (args.OutputMetadata != null && args.OutputMetadata.TryGetValue("vram_freed_mb", out var freedObj))
            {
                if (freedObj is float freed)
                {
                    NotificationService.NotifyInformation($"VRAM Freed: {freed:F2} MB");
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
}
