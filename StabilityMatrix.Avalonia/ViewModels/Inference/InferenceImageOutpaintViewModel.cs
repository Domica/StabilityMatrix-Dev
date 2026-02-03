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
    private AsyncRelayCommand? _generateImageCommandOverride; // ✅ KLJUČNO ZA UVIJEK OMogućen GUMB

    public StackCardViewModel StackCardViewModel { get; }

    public ImageSource? SelectedImage => selectImageCardVm?.ImageSource;

    // ✅ OVERRIDE ZA UVIJEK OMogućen GUMB (kao u DeepSeek kodu)
    public new IAsyncRelayCommand GenerateImageCommand => 
        _generateImageCommandOverride ??= new AsyncRelayCommand(GenerateImageAsync);

    private async Task GenerateImageAsync()
    {
        await GenerateImageImpl(new GenerateOverrides(), CancellationToken.None);
    }

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

        selectImageCardVm = vmFactory.Get<SelectImageCardViewModel>();

        var samplerCard = vmFactory.Get<SamplerCardViewModel>(sampler =>
        {
            sampler.IsDenoiseStrengthEnabled = true;
        });

        StackCardViewModel.AddCards(
            selectImageCardVm,
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

        if (selectImageCardVm?.ImageSource == null)
            return;

        selectImageCardVm.ApplyStep(args);
        var primaryImage = builder.GetPrimaryAsImage();

        //
        // 1) PadImageForOutpainting (IMAGE + MASK)
        // ⚠️ KLJUČNO: TOČNO IME IZ PYTHON DATOTEKE (SA "ing"!)
        //
        var padImage = nodes.AddNamedNode(
            new NamedComfyNode<ImageNodeConnection, ImageMaskConnection>("PadImage")
            {
                ClassType = "ImagePadForOutpainting", // ✅ SA "ing" - TOČNO KAO U PYTHONU
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

        //
        // 2) Checkpoint loader
        //
        var checkpoint = nodes.AddTypedNode(
            new ComfyNodeBuilder.CheckpointLoaderSimple
            {
                Name = "CheckpointLoader",
                CkptName = modelCard?.SelectedModel?.RelativePath ?? ""
            }
        );

        var positivePrompt = nodes.AddTypedNode(
            new ComfyNodeBuilder.CLIPTextEncode
            {
                Name = "PositivePrompt",
                Clip = checkpoint.Output2,
                Text = promptCard?.PromptDocument.Text ?? ""
            }
        );

        var negativePrompt = nodes.AddTypedNode(
            new ComfyNodeBuilder.CLIPTextEncode
            {
                Name = "NegativePrompt",
                Clip = checkpoint.Output2,
                Text = promptCard?.NegativePromptDocument.Text ?? ""
            }
        );

        //
        // 3) VAEEncodeForInpaint - KLJUČNO ZA OUTPAINTING!
        // Koristi masku da generira SAMO proširene rubove
        //
        var vaeEncode = nodes.AddTypedNode(
            new ComfyNodeBuilder.VAEEncodeForInpaint
            {
                Name = "VAEEncodeForInpaint",
                Pixels = padImage.Output1, // ✅ Padded IMAGE
                Mask = padImage.Output2,   // ✅ MASK iz PadImage node-a
                Vae = checkpoint.Output3,
                GrowMaskBy = 6             // ✅ Malo proširenje maske za bolji prijelaz
            }
        );

        //
        // 4) KSampler - generira SAMO novi sadržaj na maskiranim područjima
        //
        var sampler = nodes.AddTypedNode(
            new ComfyNodeBuilder.KSampler
            {
                Name = "KSampler",
                Model = checkpoint.Output1,
                Seed = (ulong)(seedCard?.Seed ?? 0),
                Steps = samplerCard?.Steps ?? 20,
                Cfg = samplerCard?.CfgScale ?? 7.0,
                SamplerName = samplerCard?.SelectedSampler?.Name ?? "euler",
                Scheduler = samplerCard?.SelectedScheduler?.Name ?? "normal",
                Positive = positivePrompt.Output,
                Negative = negativePrompt.Output,
                LatentImage = vaeEncode.Output, // ✅ Latent s maskom
                Denoise = Math.Min(samplerCard?.DenoiseStrength ?? 1.0, 0.65) // ✅ Viši denoise za outpainting
            }
        );

        //
        // 5) Decode finalne slike
        //
        var vaeDecode = nodes.AddTypedNode(
            new ComfyNodeBuilder.VAEDecode
            {
                Name = "VAEDecode",
                Samples = sampler.Output,
                Vae = checkpoint.Output3
            }
        );

        builder.Connections.Primary = vaeDecode.Output;

        var previewImage = nodes.AddTypedNode(
            new ComfyNodeBuilder.PreviewImage
            {
                Name = nodes.GetUniqueName("PreviewImage"),
                Images = vaeDecode.Output
            }
        );

        builder.Connections.OutputNodes.Add(previewImage);
    }

    protected override IEnumerable<ImageSource> GetInputImages()
    {
        if (selectImageCardVm?.ImageSource is { } imageSource)
            yield return imageSource;
    }

    protected override async Task GenerateImageImpl(
        GenerateOverrides overrides,
        CancellationToken cancellationToken
    )
    {
        if (!ClientManager.IsConnected)
        {
            notificationService.Show("Client not connected", "Please start ComfyUI first");
            return;
        }

        if (selectImageCardVm?.ImageSource?.LocalFile?.FullPath is not { })
        {
            notificationService.Show("No image selected", "Please select an image first");
            return;
        }

        var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();
        if (modelCard?.SelectedModel == null)
        {
            notificationService.Show("No model selected", "Please select a model first");
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
            Parameters = new GenerationParameters
            {
                ModelName = modelCard.SelectedModel.RelativePath
            },
            Project = InferenceProjectDocument.FromLoadable(this)
        };

        await RunGeneration(generationArgs, cancellationToken);
    }
}
