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
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes; // DODANO: Ovdje žive konekcije prema tvom build erroru
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

        // Dodajemo kartice točno onim redoslijedom koji tvoj XAML očekuje (Cards[0], [1]...)
        StackCardViewModel.AddCards(
            vmFactory.Get<SelectImageCardViewModel>(),           // Cards[0]
            vmFactory.Get<OutpaintCardViewModel>(),             // Cards[1]
            vmFactory.Get<PromptCardViewModel>(),               // Cards[2]
            vmFactory.Get<SamplerCardViewModel>(s => s.IsDenoiseStrengthEnabled = true), // Cards[3]
            vmFactory.Get<ModelCardViewModel>(),                // Cards[4]
            vmFactory.Get<SeedCardViewModel>()                  // Cards[5]
        );
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

        // Popravak CS0311 i CS0266: Koristimo ImageNodeConnection iz NodeTypes namespace-a
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
            CkptName = StackCardViewModel.GetCard<ModelCardViewModel>()?.SelectedModel?.RelativePath ?? ""
        });

        var prompt = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode
        {
            Name = "PositivePrompt",
            Clip = checkpoint.Output2,
            Text = StackCardViewModel.GetCard<PromptCardViewModel>()?.PromptDocument.Text ?? ""
        });

        // Ovdje je kompajler javljao CS0266 - sada je tip usklađen
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
        if (!ClientManager.IsConnected)
        {
            _notificationService.Show("Not Connected", "Please start ComfyUI.");
            return;
        }

        await UploadInputImages(ClientManager.Client!);

        var buildArgs = new BuildPromptEventArgs { Overrides = overrides };
        BuildPrompt(buildArgs);

        var genArgs = new ImageGenerationEventArgs
        {
            Client = ClientManager.Client!,
            Nodes = buildArgs.Builder.ToNodeDictionary(),
            OutputNodeNames = buildArgs.Builder.Connections.OutputNodeNames.ToArray(),
            Parameters = new GenerationParameters { ModelName = "Outpaint" },
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
