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
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Core.Services;
using CommunityToolkit.Mvvm.Input;
using StabilityMatrix.Core.Models.Api.Comfy.Connections;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(Views.Inference.InferenceImageOutpaintView))]
[ManagedService]
[Transient]
public partial class InferenceImageOutpaintViewModel : InferenceGenerationViewModelBase
{
    private readonly INotificationService notificationService;

    public StackCardViewModel StackCardViewModel { get; }

    // Ovo koristimo da bismo osvježili UI kad se slika promijeni
    public ImageSource? SelectedImage => StackCardViewModel.GetCard<SelectImageCardViewModel>()?.ImageSource;

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

        var selectImageCardVm = vmFactory.Get<SelectImageCardViewModel>();
        
        // Dodavanje kartica identično tvojoj radnoj verziji
        StackCardViewModel.AddCards(
            selectImageCardVm,                                     // Index 0
            vmFactory.Get<OutpaintCardViewModel>(),               // Index 1
            vmFactory.Get<PromptCardViewModel>(),                 // Index 2
            vmFactory.Get<SamplerCardViewModel>(s => s.IsDenoiseStrengthEnabled = true), // Index 3
            vmFactory.Get<ModelCardViewModel>(),                  // Index 4
            vmFactory.Get<SeedCardViewModel>()                    // Index 5
        );

        // Pretplata na promjenu slike da bi gumb znao kada se re-evaluirati
        if (selectImageCardVm != null)
        {
            selectImageCardVm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SelectImageCardViewModel.ImageSource))
                {
                    OnPropertyChanged(nameof(SelectedImage));
                    // Osvježava ugrađeni gumb iz baze
                    GenerateImageCommand.NotifyCanExecuteChanged();
                }
            };
        }
    }

    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        base.BuildPrompt(args);
        var builder = args.Builder;
        var nodes = builder.Nodes;

        var selectImageCard = StackCardViewModel.GetCard<SelectImageCardViewModel>();
        var outpaintCard = StackCardViewModel.GetCard<OutpaintCardViewModel>();
        var promptCard = StackCardViewModel.GetCard<PromptCardViewModel>();
        var samplerCard = StackCardViewModel.GetCard<SamplerCardViewModel>();
        var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();
        var seedCard = StackCardViewModel.GetCard<SeedCardViewModel>();

        if (selectImageCard?.ImageSource == null) return;

        selectImageCard.ApplyStep(args);
        var primaryImage = builder.GetPrimaryAsImage();

        // Popravljeno: Dodan Name i ispravna konekcija za CS9035 i CS0246
        var padImage = nodes.AddNamedNode(new NamedComfyNode<ComfyImageConnection>("PadImageForOutpainting")
        {
            Name = "OutpaintPad", 
            ClassType = "ImagePadForOutpainting",
            Inputs = new Dictionary<string, object?>
            {
                ["image"] = primaryImage,
                ["left"] = outpaintCard?.ExpandLeft ?? 0,
                ["right"] = outpaintCard?.ExpandRight ?? 0,
                ["top"] = outpaintCard?.ExpandTop ?? 0,
                ["bottom"] = outpaintCard?.ExpandBottom ?? 0,
                ["feathering"] = outpaintCard?.Feathering ?? 40
            }
        });

        var checkpoint = nodes.AddTypedNode(new ComfyNodeBuilder.CheckpointLoaderSimple
        {
            Name = "CheckpointLoader",
            CkptName = modelCard?.SelectedModel?.RelativePath ?? ""
        });

        var pos = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode { Name = "PositivePrompt", Clip = checkpoint.Output2, Text = promptCard?.PromptDocument.Text ?? "" });
        var neg = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode { Name = "NegativePrompt", Clip = checkpoint.Output2, Text = promptCard?.NegativePromptDocument.Text ?? "" });
        
        var vaeEncode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEEncode 
        { 
            Name = "VAEEncode", 
            Pixels = padImage.Output, 
            Vae = checkpoint.Output3 
        });

        var sampler = nodes.AddTypedNode(new ComfyNodeBuilder.KSampler
        {
            Name = "KSampler",
            Model = checkpoint.Output1,
            Seed = (ulong)(seedCard?.Seed ?? 0),
            Steps = samplerCard?.Steps ?? 20,
            Cfg = samplerCard?.CfgScale ?? 7.0,
            SamplerName = samplerCard?.SelectedSampler?.Name ?? "euler",
            Scheduler = samplerCard?.SelectedScheduler?.Name ?? "normal",
            Positive = pos.Output,
            Negative = neg.Output,
            LatentImage = vaeEncode.Output,
            Denoise = samplerCard?.DenoiseStrength ?? 1.0
        });

        var vaeDecode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEDecode { Name = "VAEDecode", Samples = sampler.Output, Vae = checkpoint.Output3 });
        
        builder.Connections.Primary = vaeDecode.Output;

        var preview = nodes.AddTypedNode(new ComfyNodeBuilder.PreviewImage 
        { 
            Name = "PreviewImage",
            Images = vaeDecode.Output 
        });
        builder.Connections.OutputNodes.Add(preview);
    }

    protected override async Task GenerateImageImpl(GenerateOverrides overrides, CancellationToken cancellationToken)
    {
        if (!ClientManager.IsConnected)
        {
            notificationService.Show("Client not connected", "Please start ComfyUI first");
            return;
        }

        var selectImageCard = StackCardViewModel.GetCard<SelectImageCardViewModel>();
        if (selectImageCard?.ImageSource?.LocalFile?.FullPath == null)
        {
            notificationService.Show("No image selected", "Please select an image first");
            return;
        }

        foreach (var image in GetInputImages())
            await ClientManager.UploadInputImageAsync(image, cancellationToken);

        var buildPromptArgs = new BuildPromptEventArgs { Overrides = overrides };
        BuildPrompt(buildPromptArgs);

        var generationArgs = new ImageGenerationEventArgs
        {
            Client = ClientManager.Client,
            Nodes = buildPromptArgs.Builder.ToNodeDictionary(),
            OutputNodeNames = buildPromptArgs.Builder.Connections.OutputNodeNames.ToArray(),
            Parameters = new GenerationParameters { ModelName = StackCardViewModel.GetCard<ModelCardViewModel>()?.SelectedModel?.RelativePath },
            Project = InferenceProjectDocument.FromLoadable(this)
        };

        await RunGeneration(generationArgs, cancellationToken);
    }

    protected override IEnumerable<ImageSource> GetInputImages()
    {
        if (SelectedImage is { } src) yield return src;
    }
}
