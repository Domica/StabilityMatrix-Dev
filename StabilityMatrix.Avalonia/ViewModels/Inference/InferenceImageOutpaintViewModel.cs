using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Injectio.Attributes;
using NLog;
using StabilityMatrix.Avalonia.Extensions;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Views.Inference;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceImageOutpaintView), persistent: true)]
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
[ManagedService]
[RegisterScoped<InferenceImageOutpaintViewModel>]
public class InferenceImageOutpaintViewModel : InferenceGenerationViewModelBase
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly INotificationService notificationService;

    [JsonIgnore]
    public StackCardViewModel StackCardViewModel { get; }

    [JsonPropertyName("SelectImage")]
    public SelectImageCardViewModel SelectImageCardViewModel { get; }

    [JsonPropertyName("Outpaint")]
    public OutpaintCardViewModel OutpaintCardViewModel { get; }

    public InferenceImageOutpaintViewModel(
        INotificationService notificationService,
        IInferenceClientManager inferenceClientManager,
        ISettingsManager settingsManager,
        IServiceManager<ViewModelBase> vmFactory,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
        this.notificationService = notificationService;

        SelectImageCardViewModel = vmFactory.Get<SelectImageCardViewModel>();
        OutpaintCardViewModel = vmFactory.Get<OutpaintCardViewModel>();

        StackCardViewModel = vmFactory.Get<StackCardViewModel>();
        
        // Dodajemo kartice točno onako kako ih tvoj View očekuje (indeksi 0, 1, 2...)
        StackCardViewModel.AddCards(
            SelectImageCardViewModel,                    // Index 0
            OutpaintCardViewModel,                       // Index 1
            vmFactory.Get<PromptCardViewModel>(),        // Index 2
            vmFactory.Get<SamplerCardViewModel>(s => s.IsDenoiseStrengthEnabled = true), // Index 3
            vmFactory.Get<ModelCardViewModel>(),         // Index 4
            vmFactory.Get<SeedCardViewModel>()           // Index 5
        );
    }

    protected override IEnumerable<ImageSource> GetInputImages()
    {
        if (SelectImageCardViewModel.ImageSource is { } imageSource)
        {
            yield return imageSource;
        }
    }

    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        base.BuildPrompt(args);

        var builder = args.Builder;
        var nodes = builder.Nodes;

        // Setup image source (isto kao Upscaler)
        SelectImageCardViewModel.ApplyStep(args);

        var promptCard = StackCardViewModel.GetCard<PromptCardViewModel>();
        var samplerCard = StackCardViewModel.GetCard<SamplerCardViewModel>();
        var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();
        var seedCard = StackCardViewModel.GetCard<SeedCardViewModel>();

        // 1. Pad Image for Outpaint (Rješava CS9035 dodavanjem Name)
        var padImage = nodes.AddNamedNode(new NamedComfyNode<ComfyImageConnection, ComfyMaskConnection>("PadImage")
        {
            Name = "OutpaintPad",
            ClassType = "ImagePadForOutpaint",
            Inputs = new Dictionary<string, object?>
            {
                ["image"] = builder.GetPrimaryAsImage(),
                ["left"] = OutpaintCardViewModel.ExpandLeft,
                ["right"] = OutpaintCardViewModel.ExpandRight,
                ["top"] = OutpaintCardViewModel.ExpandTop,
                ["bottom"] = OutpaintCardViewModel.ExpandBottom,
                ["feathering"] = OutpaintCardViewModel.Feathering
            }
        });

        // 2. Load Checkpoint
        var checkpoint = nodes.AddTypedNode(new ComfyNodeBuilder.CheckpointLoaderSimple
        {
            Name = "Loader",
            CkptName = modelCard?.SelectedModel?.RelativePath ?? ""
        });

        // 3. Positive/Negative Prompts
        var pos = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode 
        { 
            Name = "Positive", 
            Clip = checkpoint.Output2, 
            Text = promptCard?.PromptDocument.Text ?? "" 
        });
        
        var neg = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode 
        { 
            Name = "Negative", 
            Clip = checkpoint.Output2, 
            Text = promptCard?.NegativePromptDocument.Text ?? "" 
        });

        // 4. VAE Encode
        var vaeEncode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEEncode 
        { 
            Name = "VAEEncode", 
            Pixels = padImage.Output1, 
            Vae = checkpoint.Output3 
        });

        // 5. KSampler
        var sampler = nodes.AddTypedNode(new ComfyNodeBuilder.KSampler
        {
            Name = "Sampler",
            Model = checkpoint.Output1,
            Seed = (ulong)(seedCard?.Seed ?? 0),
            Steps = samplerCard?.Steps ?? 20,
            Cfg = samplerCard?.CfgScale ?? 7.0,
            SamplerName = samplerCard?.SelectedSampler?.Name ?? "euler",
            Scheduler = samplerCard?.SelectedScheduler?.Name ?? "normal",
            Positive = pos.Output,
            Negative = neg.Output,
            LatentImage = vaeEncode.Output,
            Denoise = samplerCard?.DenoiseStrength ?? 0.75
        });

        // 6. VAE Decode
        var decode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEDecode 
        { 
            Name = "VAEDecode", 
            Samples = sampler.Output, 
            Vae = checkpoint.Output3 
        });

        builder.Connections.Primary = decode.Output;
        builder.SetupOutputImage();
    }

    protected override async Task GenerateImageImpl(GenerateOverrides overrides, CancellationToken cancellationToken)
    {
        // Provjere identične Upscaleru
        if (!ClientManager.IsConnected)
        {
            notificationService.Show("Client not connected", "Please connect first");
            return;
        }

        if (SelectImageCardViewModel.ImageSource?.LocalFile?.FullPath is not { } path)
        {
            notificationService.Show("No image selected", "Please select an image first");
            return;
        }

        foreach (var image in GetInputImages())
        {
            await ClientManager.UploadInputImageAsync(image, cancellationToken);
        }

        var buildPromptArgs = new BuildPromptEventArgs { Overrides = overrides };
        BuildPrompt(buildPromptArgs);

        var generationArgs = new ImageGenerationEventArgs
        {
            Client = ClientManager.Client,
            Nodes = buildPromptArgs.Builder.ToNodeDictionary(),
            OutputNodeNames = buildPromptArgs.Builder.Connections.OutputNodeNames.ToArray(),
            Parameters = new GenerationParameters
            {
                ModelName = StackCardViewModel.GetCard<ModelCardViewModel>()?.SelectedModel?.RelativePath,
            },
            Project = InferenceProjectDocument.FromLoadable(this)
        };

        await RunGeneration(generationArgs, cancellationToken);
    }
}
