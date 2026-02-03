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
    private readonly INotificationService _notificationService;

    public StackCardViewModel StackCardViewModel { get; }

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
            vmFactory.Get<SelectImageCardViewModel>(),           // Cards[0]
            vmFactory.Get<OutpaintCardViewModel>(),             // Cards[1]
            vmFactory.Get<PromptCardViewModel>(),               // Cards[2]
            vmFactory.Get<SamplerCardViewModel>(s => s.IsDenoiseStrengthEnabled = true), // Cards[3]
            vmFactory.Get<ModelCardViewModel>(),                // Cards[4]
            vmFactory.Get<SeedCardViewModel>()                  // Cards[5]
        );

        // SILOVANJE GUMBA: Redefiniramo GenerateImageCommand da zanemari provjere baze
        // Koristimo RelayCommand iz CommunityToolkit-a koji uvijek dopušta klik (treći parametar: () => true)
        GenerateImageCommand = new AsyncRelayCommand<object?>(ExecuteGenerateWithManualChecks, (obj) => true);
    }

    // Pomoćna metoda koja vrši provjere tek NAKON klika
    private async Task ExecuteGenerateWithManualChecks(object? parameter)
    {
        if (!ClientManager.IsConnected)
        {
            _notificationService.Show("Not Connected", "Please start ComfyUI package first.");
            return;
        }

        var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();
        if (modelCard?.SelectedModel == null)
        {
            _notificationService.Show("Model Missing", "Please select a checkpoint model.");
            return;
        }

        await GenerateImage(parameter);
    }

    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        base.BuildPrompt(args);
        var builder = args.Builder;
        var nodes = builder.Nodes;

        var selectImageCard = StackCardViewModel.GetCard<SelectImageCardViewModel>();
        var outpaintCard = StackCardViewModel.GetCard<OutpaintCardViewModel>();
        var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();

        if (selectImageCard?.ImageSource == null) return;
        selectImageCard.ApplyStep(args);

        var padImage = nodes.AddNamedNode(new NamedComfyNode<ImageNodeConnection>("PadImageForOutpainting")
        {
            Name = "OutpaintPadNode",
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
        });

        var checkpoint = nodes.AddTypedNode(new ComfyNodeBuilder.CheckpointLoaderSimple
        {
            Name = "CkptLoader",
            CkptName = modelCard?.SelectedModel?.RelativePath ?? ""
        });

        var prompt = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode
        {
            Name = "PositivePrompt",
            Clip = checkpoint.Output2,
            Text = StackCardViewModel.GetCard<PromptCardViewModel>()?.PromptDocument.Text ?? ""
        });

        var vaeEncode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEEncode
        {
            Name = "VAEEncodeNode",
            Pixels = padImage.Output, 
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

        var vaeDecode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEDecode
        {
            Name = "VAEDecodeNode",
            Samples = sampler.Output,
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
        // Ova metoda se sada poziva unutar osnovnog GenerateImage procesa
        await UploadInputImages(ClientManager.Client!);

        var buildArgs = new BuildPromptEventArgs { Overrides = overrides };
        BuildPrompt(buildArgs);

        var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();

        var genArgs = new ImageGenerationEventArgs
        {
            Client = ClientManager.Client!,
            Nodes = buildArgs.Builder.ToNodeDictionary(),
            OutputNodeNames = buildArgs.Builder.Connections.OutputNodeNames.ToArray(),
            Parameters = new GenerationParameters 
            { 
                ModelName = modelCard?.SelectedModel?.RelativePath ?? "Outpaint" 
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
