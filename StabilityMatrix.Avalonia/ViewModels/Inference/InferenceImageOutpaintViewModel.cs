using System.Collections.Generic;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Inference.Modules;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(Views.Inference.InferenceImageOutpaintView))]
[ManagedService]
[Transient]
public partial class InferenceImageOutpaintViewModel : InferenceGenerationViewModelBase
{
    public const string ModuleKey = "ImageOutpaint";

    public StackCardViewModel StackCardViewModel { get; }

    public InferenceImageOutpaintViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager clientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, clientManager, notificationService, settingsManager, runningPackageService)
    {
        StackCardViewModel = vmFactory.Get<StackCardViewModel>();

        // Add modules as cards
        var samplerCard = vmFactory.Get<SamplerCardViewModel>(sampler =>
        {
            sampler.IsDenoiseStrengthEnabled = true;
        });

        StackCardViewModel.AddCards(
            vmFactory.Get<SelectImageCardViewModel>(),
            vmFactory.Get<OutpaintCardViewModel>(),
            vmFactory.Get<PromptCardViewModel>(),
            samplerCard,
            vmFactory.Get<ModelCardViewModel>(),
            vmFactory.Get<SeedCardViewModel>()
        );
    }

    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        var builder = args.Builder;
        var nodes = builder.Nodes;

        // Get cards
        var selectImageCard = StackCardViewModel.GetCard<SelectImageCardViewModel>();
        var outpaintCard = StackCardViewModel.GetCard<OutpaintCardViewModel>();
        var promptCard = StackCardViewModel.GetCard<PromptCardViewModel>();
        var samplerCard = StackCardViewModel.GetCard<SamplerCardViewModel>();
        var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();
        var seedCard = StackCardViewModel.GetCard<SeedCardViewModel>();

        // Validate input image
        if (selectImageCard?.ImageSource?.LocalFile is not { } imageFile)
        {
            return;
        }

        // Load Image
        var loadImage = nodes.AddTypedNode(
            new ComfyNodeBuilder.LoadImage
            {
                Name = "LoadImage",
                Image = imageFile.Name
            }
        );

        // Outpaint (Pad Image) - using InpaintModelConditioning node as example
        // Note: This assumes you have a custom ComfyUI node for outpainting
        // You may need to install a custom node pack for outpainting functionality
        var padImage = nodes.AddNamedNode(
            ComfyNodeBuilder.NamedNode("ImagePadForOutpaint")
        );
        padImage.InputFrom("image", loadImage.Output1!);
        padImage.Input("left", outpaintCard?.ExpandLeft ?? 0);
        padImage.Input("right", outpaintCard?.ExpandRight ?? 0);
        padImage.Input("top", outpaintCard?.ExpandTop ?? 0);
        padImage.Input("bottom", outpaintCard?.ExpandBottom ?? 0);
        padImage.Input("feathering", outpaintCard?.Feathering ?? 40);

        // Load Checkpoint
        var checkpoint = nodes.AddTypedNode(
            new ComfyNodeBuilder.CheckpointLoaderSimple
            {
                Name = "CheckpointLoader",
                CkptName = modelCard?.SelectedModel?.RelativePath ?? ""
            }
        );

        // CLIP Text Encode - Positive
        var positivePrompt = nodes.AddTypedNode(
            new ComfyNodeBuilder.CLIPTextEncode
            {
                Name = "PositivePrompt",
                Clip = checkpoint.Output2!,
                Text = promptCard?.PositivePrompt ?? ""
            }
        );

        // CLIP Text Encode - Negative
        var negativePrompt = nodes.AddTypedNode(
            new ComfyNodeBuilder.CLIPTextEncode
            {
                Name = "NegativePrompt",
                Clip = checkpoint.Output2!,
                Text = promptCard?.NegativePrompt ?? ""
            }
        );

        // VAE Encode
        var vaeEncode = nodes.AddTypedNode(
            new ComfyNodeBuilder.VAEEncode
            {
                Name = "VAEEncode",
                Pixels = padImage.Output["IMAGE"]!,
                Vae = checkpoint.Output3!
            }
        );

        // KSampler
        var sampler = nodes.AddTypedNode(
            new ComfyNodeBuilder.KSampler
            {
                Name = "KSampler",
                Model = checkpoint.Output1!,
                Seed = seedCard?.Seed ?? 0,
                Steps = samplerCard?.Steps ?? 20,
                Cfg = samplerCard?.CfgScale ?? 7.0,
                SamplerName = samplerCard?.SelectedSampler?.Name ?? "euler",
                Scheduler = samplerCard?.SelectedScheduler?.Name ?? "normal",
                Positive = positivePrompt.Output!,
                Negative = negativePrompt.Output!,
                LatentImage = vaeEncode.Output!,
                Denoise = samplerCard?.DenoiseStrength ?? 1.0
            }
        );

        // VAE Decode
        var vaeDecode = nodes.AddTypedNode(
            new ComfyNodeBuilder.VAEDecode
            {
                Name = "VAEDecode",
                Samples = sampler.Output!,
                Vae = checkpoint.Output3!
            }
        );

        // Preview Image
        nodes.AddTypedNode(
            new ComfyNodeBuilder.PreviewImage
            {
                Name = "PreviewImage",
                Images = vaeDecode.Output!
            }
        );
    }

    protected override IEnumerable<ImageSource> GetInputImages()
    {
        var selectImageCard = StackCardViewModel.GetCard<SelectImageCardViewModel>();
        if (selectImageCard?.ImageSource is { } imageSource)
        {
            yield return imageSource;
        }
    }
}
