using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Inference.Modules;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Core.Services;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(Views.Inference.InferenceImageOutpaintView))]
[ManagedService]
[Transient]
public partial class InferenceImageOutpaintViewModel : InferenceGenerationViewModelBase
{
    public const string ModuleKey = "ImageOutpaint";
    private readonly INotificationService _notificationService;
    private AsyncRelayCommand? _generateImageCommandOverride;

    public StackCardViewModel StackCardViewModel { get; }

    public ImageSource? SelectedImage => StackCardViewModel.GetCard<SelectImageCardViewModel>()?.ImageSource;

    // Override the command to always be executable
    public new IAsyncRelayCommand GenerateImageCommand => 
        _generateImageCommandOverride ??= new AsyncRelayCommand(GenerateImageAsync);

    public InferenceImageOutpaintViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager clientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    ) : base(vmFactory, clientManager, notificationService, settingsManager, runningPackageService)
    {
        _notificationService = notificationService;
        StackCardViewModel = vmFactory.Get<StackCardViewModel>();

        StackCardViewModel.AddCards(
            vmFactory.Get<SelectImageCardViewModel>(),
            vmFactory.Get<OutpaintCardViewModel>(),
            vmFactory.Get<PromptCardViewModel>(),
            vmFactory.Get<SamplerCardViewModel>(s => s.IsDenoiseStrengthEnabled = true),
            vmFactory.Get<ModelCardViewModel>(),
            vmFactory.Get<SeedCardViewModel>()
        );
    }

    private async Task GenerateImageAsync()
    {
        // Call the base implementation with default overrides and cancellation token
        await GenerateImageImpl(new GenerateOverrides(), CancellationToken.None);
    }

    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        base.BuildPrompt(args);
        var builder = args.Builder;
        var nodes = builder.Nodes;

        var selectImageCard = StackCardViewModel.GetCard<SelectImageCardViewModel>();
        var outpaintCard = StackCardViewModel.GetCard<OutpaintCardViewModel>();

        if (selectImageCard?.ImageSource == null) return;
        selectImageCard.ApplyStep(args);

        // Pad image for outpainting - returns image and mask
        var padImageNode = new NamedComfyNode<ImageNodeConnection, ImageMaskConnection>("OutpaintPadNode")
        {
            ClassType = "ImagePadForOutpainting",
            Inputs = new Dictionary<string, object?>
            {
                ["image"] = builder.GetPrimaryAsImage(),
                ["left"] = outpaintCard?.ExpandLeft ?? 0,
                ["right"] = outpaintCard?.ExpandRight ?? 0,
                ["top"] = outpaintCard?.ExpandTop ?? 0,
                ["bottom"] = outpaintCard?.ExpandBottom ?? 0,
                ["feathering"] = outpaintCard?.Feathering ?? 40
            }
        };
        
        var padImage = nodes.AddNamedNode(padImageNode);
        var paddedImage = padImage.Output1;  // Proširena slika
        var outpaintMask = padImage.Output2; // Mask za outpaint područja

        var checkpoint = nodes.AddTypedNode(new ComfyNodeBuilder.CheckpointLoaderSimple
        {
            Name = "CkptLoader",
            CkptName = StackCardViewModel.GetCard<ModelCardViewModel>()?.SelectedModel?.RelativePath ?? ""
        });

        var prompt = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode
        {
            Name = "PositivePrompt",
            Clip = checkpoint.Output2,
            Text = StackCardViewModel.GetCard<PromptCardViewModel>()?.PromptDocument.Text ?? ""
        });

        // Encode the padded image to latent
        var vaeEncode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEEncode
        {
            Name = "VAEEncodeNode",
            Pixels = paddedImage,
            Vae = checkpoint.Output3
        });

        var sampler = nodes.AddTypedNode(new ComfyNodeBuilder.KSampler
        {
            Name = "MainSampler",
            Model = checkpoint.Output1,
            Seed = (ulong)(StackCardViewModel.GetCard<SeedCardViewModel>()?.Seed ?? 0),
            Steps = StackCardViewModel.GetCard<SamplerCardViewModel>()?.Steps ?? 20,
            Cfg = 7.0,
            SamplerName = "euler",
            Scheduler = "normal",
            Positive = prompt.Output,
            Negative = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode { Name = "EmptyNeg", Clip = checkpoint.Output2, Text = "" }).Output,
            LatentImage = vaeEncode.Output,
            Denoise = StackCardViewModel.GetCard<SamplerCardViewModel>()?.DenoiseStrength ?? 1.0
        });

        // Use LatentComposite to blend original latent and generated latent using mask
        var latentCompositeNode = new NamedComfyNode<LatentNodeConnection>("LatentCompositeNode")
        {
            ClassType = "LatentComposite",
            Inputs = new Dictionary<string, object?>
            {
                ["original"] = vaeEncode.Output,  // Original encoded latent (without generation)
                ["generated"] = sampler.Output,   // Generated latent
                ["mask"] = outpaintMask           // Mask from outpainting
            }
        };
        
        var compositeOutput = nodes.AddNamedNode(latentCompositeNode);

        var vaeDecode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEDecode
        {
            Name = "VAEDecodeNode",
            Samples = compositeOutput.Output,
            Vae = checkpoint.Output3
        });

        builder.Connections.Primary = vaeDecode.Output;
        
        var preview = nodes.AddTypedNode(new ComfyNodeBuilder.PreviewImage
        {
            Name = "PreviewNode",
            Images = vaeDecode.Output
        });
        builder.Connections.OutputNodes.Add(preview);
    }

    protected override async Task GenerateImageImpl(GenerateOverrides overrides, CancellationToken cancellationToken)
    {
        // Provjera da li je ComfyUI pokrenut - isto kao i na upscaler ekranu
        if (!ClientManager.IsConnected)
        {
            _notificationService.Show("Not Connected", "Please start ComfyUI.");
            return;
        }

        // Provjera da li je odabrana slika
        if (SelectedImage == null)
        {
            _notificationService.Show("No Image", "Please select an image first.");
            return;
        }

        // Provjera da li je odabran model
        if (StackCardViewModel.GetCard<ModelCardViewModel>()?.SelectedModel == null)
        {
            _notificationService.Show("No Model", "Please select a model first.");
            return;
        }

        try
        {
            await UploadInputImages(ClientManager.Client!);

            var buildArgs = new BuildPromptEventArgs();
            BuildPrompt(buildArgs);

            var genArgs = new ImageGenerationEventArgs
            {
                Client = ClientManager.Client!,
                Nodes = buildArgs.Builder.ToNodeDictionary(),
                OutputNodeNames = buildArgs.Builder.Connections.OutputNodeNames.ToArray(),
                Parameters = new GenerationParameters 
                { 
                    ModelName = StackCardViewModel.GetCard<ModelCardViewModel>()?.SelectedModel?.RelativePath 
                },
                Project = InferenceProjectDocument.FromLoadable(this)
            };

            // This should trigger progress bar updates through base class
            await RunGeneration(genArgs, cancellationToken);
        }
        catch (Exception ex)
        {
            _notificationService.Show("Error", $"Failed to generate image: {ex.Message}");
        }
    }

    protected override IEnumerable<ImageSource> GetInputImages()
    {
        var img = StackCardViewModel.GetCard<SelectImageCardViewModel>()?.ImageSource;
        if (img != null) yield return img;
    }
}
