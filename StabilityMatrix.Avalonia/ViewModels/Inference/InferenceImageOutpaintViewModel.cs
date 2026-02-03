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

        // Nadjačavamo komandu u konstruktoru da gumb bude uvijek dostupan (Enabled)
        GenerateImageCommand = new AsyncRelayCommand<object?>(ExecuteGenerateManual, _ => true);
    }

    private async Task ExecuteGenerateManual(object? parameter)
    {
        if (!ClientManager.IsConnected)
        {
            _notificationService.Show("Greška", "ComfyUI klijent nije spojen.");
            return;
        }

        var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();
        if (modelCard?.SelectedModel == null)
        {
            _notificationService.Show("Model nije odabran", "Molimo odaberite checkpoint model.");
            return;
        }

        // Pozivamo baznu metodu za generiranje
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
        var samplerCard = StackCardViewModel.GetCard<SamplerCardViewModel>();
        var promptCard = StackCardViewModel.GetCard<PromptCardViewModel>();
        var seedCard = StackCardViewModel.GetCard<SeedCardViewModel>();

        if (selectImageCard?.ImageSource == null) return;
        selectImageCard.ApplyStep(args);

        // 1. Pad Image for Outpainting (Custom čvor, koristimo AddNamedNode)
        var padImage = nodes.AddNamedNode(new NamedComfyNode<ImageNodeConnection>("OutpaintPad")
        {
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

        // 2. Checkpoint Loader (Tvoj model ima Output1, Output2, Output3)
        var checkpoint = nodes.AddTypedNode(new ComfyNodeBuilder.CheckpointLoaderSimple
        {
            CkptName = modelCard?.SelectedModel?.RelativePath ?? ""
        });

        // 3. Positive Prompt (Text prima OneOf string)
        var positive = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode
        {
            Clip = checkpoint.Output2,
            Text = promptCard?.PromptDocument.Text ?? ""
        });

        // 4. Negative Prompt
        var negative = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode
        {
            Clip = checkpoint.Output2,
            Text = ""
        });

        // 5. VAE Encode (VAE je Output3, Pixels je naš padImage.Output)
        var vaeEncode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEEncode
        {
            Pixels = padImage.Output,
            Vae = checkpoint.Output3
        });

        // 6. KSampler (Model je Output1, LatentImage je vaeEncode.Output)
        var sampler = nodes.AddTypedNode(new ComfyNodeBuilder.KSampler
        {
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

        // 7. VAE Decode
        var vaeDecode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEDecode
        {
            Samples = sampler.Output,
            Vae = checkpoint.Output3
        });

        // Postavljamo finalni izlaz
        builder.Connections.Primary = vaeDecode.Output;

        // Dodajemo Preview čvor
        var preview = nodes.AddTypedNode(new ComfyNodeBuilder.PreviewImage
        {
            Images = vaeDecode.Output
        });
        builder.Connections.OutputNodes.Add(preview);
    }

    protected override async Task GenerateImageImpl(GenerateOverrides overrides, CancellationToken cancellationToken)
    {
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
