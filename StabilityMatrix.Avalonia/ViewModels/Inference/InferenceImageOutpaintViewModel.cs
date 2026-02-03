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
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(Views.Inference.InferenceImageOutpaintView))]
[ManagedService]
[Transient]
public partial class InferenceImageOutpaintViewModel : InferenceGenerationViewModelBase
{
    public const string ModuleKey = "ImageOutpaint";
    private readonly INotificationService _notificationService;
    private readonly SelectImageCardViewModel _selectImageCardVm;
    private AsyncRelayCommand? _generateImageCommandOverride;

    public StackCardViewModel StackCardViewModel { get; }

    // KLJUČ: Sakrivanje bazne komande da gumb postane aktivan
    public new IAsyncRelayCommand GenerateImageCommand => 
        _generateImageCommandOverride ??= new AsyncRelayCommand(() => GenerateImageImpl(new GenerateOverrides(), CancellationToken.None));

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
        _selectImageCardVm = vmFactory.Get<SelectImageCardViewModel>();

        var samplerCard = vmFactory.Get<SamplerCardViewModel>(s => s.IsDenoiseStrengthEnabled = true);

        StackCardViewModel.AddCards(
            _selectImageCardVm,
            vmFactory.Get<OutpaintCardViewModel>(),
            vmFactory.Get<PromptCardViewModel>(),
            samplerCard,
            vmFactory.Get<ModelCardViewModel>(),
            vmFactory.Get<SeedCardViewModel>()
        );
    }

    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        base.BuildPrompt(args);
        var builder = args.Builder;
        var nodes = builder.Nodes;

        var outpaintCard = StackCardViewModel.GetCard<OutpaintCardViewModel>();
        var promptCard = StackCardViewModel.GetCard<PromptCardViewModel>();
        var samplerCard = StackCardViewModel.GetCard<SamplerCardViewModel>();
        var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();
        var seedCard = StackCardViewModel.GetCard<SeedCardViewModel>();

        if (_selectImageCardVm?.ImageSource == null) return;
        _selectImageCardVm.ApplyStep(args);
        
        var primaryImage = builder.GetPrimaryAsImage();

        // 1) Padding Node (Vraćam točan ClassType i Outpute)
        var padImage = nodes.AddNamedNode(
            new NamedComfyNode<ImageNodeConnection, ImageMaskConnection>("PadImage")
            {
                ClassType = "ImagePadForOutpaint",
                Inputs = new Dictionary<string, object?>
                {
                    ["image"] = primaryImage.Data,
                    ["left"] = outpaintCard?.ExpandLeft ?? 0,
                    ["right"] = outpaintCard?.ExpandRight ?? 0,
                    ["top"] = outpaintCard?.ExpandTop ?? 0,
                    ["bottom"] = outpaintCard?.ExpandBottom ?? 0,
                    ["feathering"] = outpaintCard?.Feathering ?? 40
                }
            }
        );

        var checkpoint = nodes.AddTypedNode(new ComfyNodeBuilder.CheckpointLoaderSimple
        {
            Name = "CkptLoader",
            CkptName = modelCard?.SelectedModel?.RelativePath ?? ""
        });

        var positive = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode
        {
            Name = "PositivePrompt",
            Clip = checkpoint.Output2,
            Text = promptCard?.PromptDocument.Text ?? ""
        });

        var negative = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode
        {
            Name = "NegativePrompt",
            Clip = checkpoint.Output2,
            Text = promptCard?.NegativePromptDocument.Text ?? ""
        });

        // VAE Encode Originala (za Composite)
        var originalVaeEncode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEEncode
        {
            Name = "VAEOrig",
            Pixels = primaryImage,
            Vae = checkpoint.Output3
        });

        // VAE Encode Padded slike
        var paddedVaeEncode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEEncode
        {
            Name = "VAEPad",
            Pixels = padImage.Output1,
            Vae = checkpoint.Output3
        });

        var sampler = nodes.AddTypedNode(new ComfyNodeBuilder.KSampler
        {
            Name = "Sampler",
            Model = checkpoint.Output1,
            Seed = (ulong)(seedCard?.Seed ?? 0),
            Steps = samplerCard?.Steps ?? 20,
            Cfg = samplerCard?.CfgScale ?? 7.0,
            SamplerName = samplerCard?.SelectedSampler?.Name ?? "euler",
            Scheduler = samplerCard?.SelectedScheduler?.Name ?? "normal",
            Positive = positive.Output,
            Negative = negative.Output,
            LatentImage = paddedVaeEncode.Output,
            Denoise = Math.Min(samplerCard?.DenoiseStrength ?? 1.0, 0.40)
        });

        // Latent Composite za očuvanje originala
        var composite = nodes.AddNamedNode(new NamedComfyNode<LatentNodeConnection>("Composite")
        {
            ClassType = "LatentComposite",
            Inputs = new Dictionary<string, object?>
            {
                ["samples_to"] = sampler.Output.Data,
                ["samples_from"] = originalVaeEncode.Output.Data,
                ["x"] = outpaintCard?.ExpandLeft ?? 0,
                ["y"] = outpaintCard?.ExpandTop ?? 0,
                ["feather"] = 0
            }
        });

        var vaeDecode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEDecode
        {
            Name = "VAEDecode",
            Samples = composite.Output,
            Vae = checkpoint.Output3
        });

        builder.Connections.Primary = vaeDecode.Output;

        var preview = nodes.AddTypedNode(new ComfyNodeBuilder.PreviewImage
        {
            Name = "Preview",
            Images = vaeDecode.Output
        });
        builder.Connections.OutputNodes.Add(preview);
    }

    protected override async Task GenerateImageImpl(GenerateOverrides overrides, CancellationToken cancellationToken)
    {
        if (!ClientManager.IsConnected)
        {
            _notificationService.Show("Not Connected", "Please start ComfyUI.");
            return;
        }

        if (_selectImageCardVm?.ImageSource == null)
        {
            _notificationService.Show("No Image", "Please select an image first.");
            return;
        }

        try
        {
            foreach (var image in GetInputImages())
                await ClientManager.UploadInputImageAsync(image, cancellationToken);

            var buildArgs = new BuildPromptEventArgs { Overrides = overrides };
            BuildPrompt(buildArgs);

            var genArgs = new ImageGenerationEventArgs
            {
                Client = ClientManager.Client!,
                Nodes = buildArgs.Builder.ToNodeDictionary(),
                OutputNodeNames = buildArgs.Builder.Connections.OutputNodeNames.ToArray(),
                Project = InferenceProjectDocument.FromLoadable(this)
            };

            await RunGeneration(genArgs, cancellationToken);
        }
        catch (Exception ex)
        {
            _notificationService.Show("Error", $"Failed to generate image: {ex.Message}");
        }
    }

    protected override IEnumerable<ImageSource> GetInputImages()
    {
        var img = _selectImageCardVm?.ImageSource;
        if (img != null) yield return img;
    }
}
