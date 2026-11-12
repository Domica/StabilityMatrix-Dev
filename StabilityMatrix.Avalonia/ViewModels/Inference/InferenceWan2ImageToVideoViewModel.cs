using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Views.Inference;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceWan2ImageToVideoView), IsPersistent = true)]
[RegisterScoped<InferenceWan2ImageToVideoViewModel>, ManagedService]
public class InferenceWan2ImageToVideoViewModel : InferenceWanTextToVideoViewModel
{
    public InferenceWan2ImageToVideoViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
        SelectImageCardViewModel = vmFactory.Get<SelectImageCardViewModel>();

        SamplerCardViewModel.IsDenoiseStrengthEnabled = true;
        SamplerCardViewModel.Width = 512;
        SamplerCardViewModel.Height = 512;

        // Wan 2.2 TI2V 5B (e.g. Wan2.2-TI2V-5B-Q4_K_S) is a WAN22_T2V model: 48-channel
        // latents at 1/16 spatial scale. It must use Wan22ImageToVideoLatent, which encodes the
        // reference image into the first latent frames and emits a noise_mask. Building the Wan
        // 2.1 WanImageToVideo node here makes the model ignore the input image entirely.
        ((WanSamplerCardViewModel)SamplerCardViewModel).UseWan22Ti2V = true;

        // Wan 2.2 GGUF (5B/TI2V) does not need CLIP Vision; enable manually for Wan 2.1 if needed
        ModelCardViewModel.IsClipVisionEnabled = false;
    }

    [JsonPropertyName("ImageLoader")]
    public SelectImageCardViewModel SelectImageCardViewModel { get; }

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

        // Load models
        ModelCardViewModel.ApplyStep(applyArgs);

        // Setup latent from image
        var imageLoad = builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.LoadImage
            {
                Name = builder.Nodes.GetUniqueName("ControlNet_LoadImage"),
                Image =
                    SelectImageCardViewModel.ImageSource?.GetHashGuidFileNameCached("Inference")
                    ?? throw new ValidationException(),
            }
        );
        builder.Connections.Primary = imageLoad.Output1;
        builder.Connections.PrimarySize = SelectImageCardViewModel.CurrentBitmapSize;

        BatchSizeCardViewModel.ApplyStep(applyArgs);

        SelectImageCardViewModel.ApplyStep(applyArgs);

        PromptCardViewModel.ApplyStep(applyArgs);

        SamplerCardViewModel.ApplyStep(applyArgs);

        applyArgs.InvokeAllPreOutputActions();

        // Animated webp output
        VideoOutputSettingsCardViewModel.ApplyStep(applyArgs);
    }

    /// <inheritdoc />
    protected override IEnumerable<ImageSource> GetInputImages()
    {
        if (SelectImageCardViewModel.ImageSource is { } image)
        {
            yield return image;
        }
    }
}
