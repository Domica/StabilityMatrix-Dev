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
using StabilityMatrix.Avalonia.ViewModels.Inference.Modules;

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

    // DODAJ OVO:
    [JsonPropertyName("TiledVAE")]
    public TiledVAEModule TiledVAEModule { get; }

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

        SamplerCardViewModel = vmFactory.Get<WanSamplerCardViewModel>(samplerCard =>
        {
            samplerCard.IsDimensionsEnabled = true;
            samplerCard.IsCfgScaleEnabled = true;
            samplerCard.IsSamplerSelectionEnabled = true;
            samplerCard.IsSchedulerSelectionEnabled = true;
            samplerCard.DenoiseStrength = 1.0d;
            samplerCard.EnableAddons = false;
            samplerCard.IsLengthEnabled = true;
            samplerCard.Width = 832;
            samplerCard.Height = 480;
            samplerCard.Length = 33;
        });

        PromptCardViewModel = AddDisposable(vmFactory.Get<PromptCardViewModel>());

        BatchSizeCardViewModel = vmFactory.Get<BatchSizeCardViewModel>();

        VideoOutputSettingsCardViewModel = vmFactory.Get<VideoOutputSettingsCardViewModel>(vm =>
            vm.Fps = 16.0d
        );
        
        // PROMIJENI OVO:
        TiledVAEModule = vmFactory.Get<TiledVAEModule>();

        StackCardViewModel = vmFactory.Get<StackCardViewModel>();
        
        // PROMIJENI OVO:
        StackCardViewModel.AddCards(
            ModelCardViewModel,
            SamplerCardViewModel,
            SeedCardViewModel,
            BatchSizeCardViewModel,
            TiledVAEModule,  // ← MODULE, ne CardViewModel
            VideoOutputSettingsCardViewModel
        );
    }

    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        base.BuildPrompt(args);

        var builder = args.Builder;

        builder.Connections.Seed = args.SeedOverride switch
        {
            { } seed => Convert.ToUInt64(seed),
            _ => Convert.ToUInt64(SeedCardViewModel.Seed),
        };

        // Convert to ModuleApplyStepEventArgs
        var moduleArgs = args.ToModuleApplyStepEventArgs();

        ModelCardViewModel.ApplyStep(moduleArgs);

        builder.SetupEmptyLatentSource(
            SamplerCardViewModel.Width,
            SamplerCardViewModel.Height,
            BatchSizeCardViewModel.BatchSize,
            BatchSizeCardViewModel.IsBatchIndexEnabled ? BatchSizeCardViewModel.BatchIndex : null,
            SamplerCardViewModel.Length,
            LatentType.Hunyuan
        );

        BatchSizeCardViewModel.ApplyStep(moduleArgs);
        PromptCardViewModel.ApplyStep(moduleArgs);
        SamplerCardViewModel.ApplyStep(moduleArgs);

        // PROMIJENI OVO:
        TiledVAEModule.ApplyStep(moduleArgs);
        moduleArgs.InvokeAllPreOutputActions();

        VideoOutputSettingsCardViewModel.ApplyStep(moduleArgs);
    }

    // ... rest ostaje isto ...
}
