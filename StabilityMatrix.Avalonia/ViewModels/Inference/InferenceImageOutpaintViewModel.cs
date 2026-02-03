using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using CommunityToolkit.Mvvm.Input;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(Views.Inference.InferenceImageOutpaintView))]
[ManagedService]
[Transient]
public partial class InferenceImageOutpaintViewModel : InferenceGenerationViewModelBase
{
    public const string ModuleKey = "ImageOutpaint";

    private readonly INotificationService notificationService;
    private readonly SelectImageCardViewModel selectImageCardVm;

    public StackCardViewModel StackCardViewModel { get; }

    public ImageSource? SelectedImage => selectImageCardVm?.ImageSource;

    public InferenceImageOutpaintViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager clientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, clientManager, notificationService, settingsManager, runningPackageService)
    {
        this.notificationService = notificationService;
        StackCardViewModel = vmFactory.Get<StackCardViewModel>();

        var samplerCard = vmFactory.Get<SamplerCardViewModel>(sampler =>
        {
            sampler.IsDenoiseStrengthEnabled = true;
        });

        selectImageCardVm = vmFactory.Get<SelectImageCardViewModel>();

        StackCardViewModel.AddCards(
            selectImageCardVm,
            vmFactory.Get<OutpaintCardViewModel>(),
            vmFactory.Get<PromptCardViewModel>(),
            samplerCard,
            vmFactory.Get<ModelCardViewModel>(),
            vmFactory.Get<SeedCardViewModel>()
        );

        // 1. PRAĆENJE KONEKCIJE: Reagiraj čim log javi "Connected"
        ClientManager.PropertyChanged += OnClientManagerPropertyChanged;

        // 2. PRAĆENJE SLIKE: Reagiraj čim korisnik odabere sliku
        if (selectImageCardVm is not null)
        {
            selectImageCardVm.PropertyChanged += OnSelectImageCardPropertyChanged;
        }

        // Inicijalno osvježi stanje gumba pri otvaranju taba
        GenerateImageCommand.NotifyCanExecuteChanged();
    }

    private void OnClientManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IInferenceClientManager.IsConnected) || 
            e.PropertyName == nameof(IInferenceClientManager.Client))
        {
            GenerateImageCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnSelectImageCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectImageCardViewModel.ImageSource))
        {
            OnPropertyChanged(nameof(SelectedImage));
            GenerateImageCommand.NotifyCanExecuteChanged();
        }
    }

    protected override bool CanGenerateImage()
    {
        // Provjerava: spojen klijent + odabran model (iz baze) + odabrana slika (naš uvjet)
        return base.CanGenerateImage() && selectImageCardVm?.ImageSource != null;
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

        if (selectImageCardVm?.ImageSource == null) return;

        selectImageCardVm.ApplyStep(args);
        var primaryImage = builder.GetPrimaryAsImage();

        // 1. Pad Image (Kreira prazan prostor oko slike)
        var padImage = nodes.AddNamedNode(
            new NamedComfyNode<ImageNodeConnection, ImageMaskConnection>("PadImage")
            {
                ClassType = "ImagePadForOutpaint",
                Inputs = new Dictionary<string, object?>
                {
                    ["image"] = primaryImage,
                    ["left"] = outpaintCard?.ExpandLeft ?? 0,
                    ["right"] = outpaintCard?.ExpandRight ?? 0,
                    ["top"] = outpaintCard?.ExpandTop ?? 0,
                    ["bottom"] = outpaintCard?.ExpandBottom ?? 0,
                    ["feathering"] = outpaintCard?.Feathering ?? 40
                }
            }
        );

        // 2. Load Checkpoint
        var checkpoint = nodes.AddTypedNode(
            new ComfyNodeBuilder.CheckpointLoaderSimple
            {
                Name = "CheckpointLoader",
                CkptName = modelCard?.SelectedModel?.RelativePath ?? ""
            }
        );

        // 3. Prompts
        var positivePrompt = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode { Clip = checkpoint.Output2, Text = promptCard?.PromptDocument.Text ?? "" });
        var negativePrompt = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode { Clip = checkpoint.Output2, Text = promptCard?.NegativePromptDocument.Text ?? "" });

        // 4. VAE Encode (Original i Padded)
        var originalVae = nodes.AddTypedNode(new ComfyNodeBuilder.VAEEncode { Pixels = primaryImage, Vae = checkpoint.Output3 });
        var paddedVae = nodes.AddTypedNode(new ComfyNodeBuilder.VAEEncode { Pixels = padImage.Output1, Vae = checkpoint.Output3 });

        // 5. KSampler
        var sampler = nodes.AddTypedNode(
            new ComfyNodeBuilder.KSampler
            {
                Model = checkpoint.Output1,
                Seed = (ulong)(seedCard?.Seed ?? 0),
                Steps = samplerCard?.Steps ?? 20,
                Cfg = samplerCard?.CfgScale ?? 7.0,
                SamplerName = samplerCard?.SelectedSampler?.Name ?? "euler",
                Scheduler = samplerCard?.SelectedScheduler?.Name ?? "normal",
                Positive = positivePrompt.Output,
                Negative = negativePrompt.Output,
                LatentImage = paddedVae.Output,
                Denoise = Math.Min(samplerCard?.DenoiseStrength ?? 1.0, 0.40)
            }
        );

        // 6. Latent Composite (Spaja staru sliku i novu generaciju preko maske)
        var composite = nodes.AddNamedNode(
            new NamedComfyNode<LatentNodeConnection>("LatentComposite")
            {
                ClassType = "LatentComposite",
                Inputs = new Dictionary<string, object?>
                {
                    ["original"] = originalVae.Output?.Data,
                    ["generated"] = sampler.Output?.Data,
                    ["mask"] = padImage.Output2.Data
                }
            }
        );

        // 7. Decode i Preview
        var vaeDecode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEDecode { Samples = composite.Output, Vae = checkpoint.Output3 });
        builder.Connections.Primary = vaeDecode.Output;

        var previewImage = nodes.AddTypedNode(new ComfyNodeBuilder.PreviewImage { Name = "Preview", Images = vaeDecode.Output });
        builder.Connections.OutputNodes.Add(previewImage);
    }

    protected override async Task GenerateImageImpl(GenerateOverrides overrides, CancellationToken cancellationToken)
    {
        if (!ClientManager.IsConnected) return;

        foreach (var image in GetInputImages())
            await ClientManager.UploadInputImageAsync(image, cancellationToken);

        var buildPromptArgs = new BuildPromptEventArgs { Overrides = overrides };
        BuildPrompt(buildPromptArgs);

        var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();
        var generationArgs = new ImageGenerationEventArgs
        {
            Client = ClientManager.Client,
            Nodes = buildPromptArgs.Builder.ToNodeDictionary(),
            OutputNodeNames = buildPromptArgs.Builder.Connections.OutputNodeNames.ToArray(),
            Parameters = new GenerationParameters { ModelName = modelCard?.SelectedModel?.RelativePath ?? "" },
            Project = InferenceProjectDocument.FromLoadable(this)
        };

        await RunGeneration(generationArgs, cancellationToken);
    }

    protected override IEnumerable<ImageSource> GetInputImages()
    {
        if (selectImageCardVm?.ImageSource is { } imageSource) yield return imageSource;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClientManager.PropertyChanged -= OnClientManagerPropertyChanged;
            if (selectImageCardVm != null) selectImageCardVm.PropertyChanged -= OnSelectImageCardPropertyChanged;
        }
        base.Dispose(disposing);
    }
}
