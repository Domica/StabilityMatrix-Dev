using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Inference.Modules;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Core.Models.Inference; // FIX ZA CS0246: Ovdje živi GenerationParameters
using StabilityMatrix.Core.Services;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media.Imaging;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(Views.Inference.InferenceImageOutpaintView))]
[ManagedService]
[Transient]
public partial class InferenceImageOutpaintViewModel : InferenceGenerationViewModelBase
{
    public const string ModuleKey = "ImageOutpaint";
    private readonly INotificationService _notificationService;

    public StackCardViewModel StackCardViewModel { get; }

    // Nova komanda koja će pokrenuti generiranje bez obzira na sve
    public IRelayCommand RunOutpaintCommand { get; }

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

        // FIX ZA CS0122: Umjesto poziva privatne metode, zovemo Execute na baznoj komandi.
        // To je trik koji Upscale koristi da zaobiđe zaštitu.
        RunOutpaintCommand = new RelayCommand(() => GenerateImageCommand.Execute(null));
    }

    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        base.BuildPrompt(args);
        var builder = args.Builder;
        var nodes = builder.Nodes;

        var selectImageCard = StackCardViewModel.GetCard<SelectImageCardViewModel>();
        var outpaintCard = StackCardViewModel.GetCard<OutpaintCardViewModel>();
        var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();
        var samplerCard = StackCardViewModel.GetCard<SamplerCardViewModel>();
        var promptCard = StackCardViewModel.GetCard<PromptCardViewModel>();
        var seedCard = StackCardViewModel.GetCard<SeedCardViewModel>();

        if (selectImageCard?.ImageSource == null) return;
        selectImageCard.ApplyStep(args);

        // Dodajemo Name u svaki čvor radi CS9035
        var padImage = nodes.AddNamedNode(new NamedComfyNode<ImageNodeConnection>("OutpaintPad")
        {
            Name = "OutpaintPad",
            ClassType = "ImagePadForOutpainting",
            Inputs = new Dictionary<string, object?>
            {
                ["image"] = builder.GetPrimaryAsImage().Data,
                ["left"] = outpaintCard?.ExpandLeft ?? 0,
                ["right"] = outpaintCard?.ExpandRight ?? 0,
                ["top"] = outpaintCard?.ExpandTop ?? 0,
                ["bottom"] = outpaintCard?.ExpandBottom ?? 0,
                ["feathering"] = outpaintCard?.Feathering ?? 40
            }
        });

        var checkpoint = nodes.AddTypedNode(new ComfyNodeBuilder.CheckpointLoaderSimple
        {
            Name = "Loader",
            CkptName = modelCard?.SelectedModel?.RelativePath ?? ""
        });

        var positive = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode
        {
            Name = "Positive",
            Clip = checkpoint.Output2,
            Text = promptCard?.PromptDocument.Text ?? ""
        });

        var negative = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode
        {
            Name = "Negative",
            Clip = checkpoint.Output2,
            Text = ""
        });

        var vaeEncode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEEncode
        {
            Name = "VaeEncode",
            Pixels = padImage.Output,
            Vae = checkpoint.Output3
        });

        var sampler = nodes.AddTypedNode(new ComfyNodeBuilder.KSampler
        {
            Name = "Sampler",
            Model = checkpoint.Output1,
            Seed = (ulong)(seedCard?.Seed ?? 0),
            Steps = samplerCard?.Steps ?? 20,
            Cfg = 7.0,
            SamplerName = "euler",
            Scheduler = "normal",
            Positive = positive.Output,
            Negative = negative.Output,
            LatentImage = vaeEncode.Output,
            Denoise = samplerCard?.DenoiseStrength ?? 1.0
        });

        var vaeDecode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEDecode
        {
            Name = "VaeDecode",
            Samples = sampler.Output,
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
        if (ClientManager.Client == null)
        {
            _notificationService.Show("Not Connected", "ComfyUI is not running or connected.");
            return;
        }

        await UploadInputImages(ClientManager.Client);

        var buildArgs = new BuildPromptEventArgs { Overrides = overrides };
        BuildPrompt(buildArgs);

        var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();

        var genArgs = new ImageGenerationEventArgs
        {
            Client = ClientManager.Client!,
            Nodes = buildArgs.Builder.ToNodeDictionary(),
            OutputNodeNames = buildArgs.Builder.Connections.OutputNodeNames.ToArray(),
            Parameters = new StabilityMatrix.Core.Models.Inference.GenerationParameters // Puna putanja za svaki slučaj
            { 
                ModelName = modelCard?.SelectedModel?.RelativePath ?? "unknown" 
            },
            Project = InferenceProjectDocument.FromLoadable(this)
        };

        await RunGeneration(genArgs, cancellationToken);
    }

    protected override IEnumerable<ImageSource> GetInputImages()
    {
        var img = StackCardViewModel.GetCard<SelectImageCardViewModel>()?.ImageSource;
        if (img != null) yield return img;
    }
}
