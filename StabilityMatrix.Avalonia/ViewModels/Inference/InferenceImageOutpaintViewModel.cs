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

    public ImageSource? SelectedImage
    {
        get => selectImageCardVm?.ImageSource;
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

        var samplerCard = vmFactory.Get<SamplerCardViewModel>(sampler =>
        {
            sampler.IsDenoiseStrengthEnabled = true;
        });

        // Čuvaj referencu na SelectImageCard za event handler
        selectImageCardVm = vmFactory.Get<SelectImageCardViewModel>();

        StackCardViewModel.AddCards(
            selectImageCardVm,
            vmFactory.Get<OutpaintCardViewModel>(),
            vmFactory.Get<PromptCardViewModel>(),
            samplerCard,
            vmFactory.Get<ModelCardViewModel>(),
            vmFactory.Get<SeedCardViewModel>()
        );

        // Poveži event handler za ažuriranje stanja gumba
        if (selectImageCardVm is not null)
        {
            selectImageCardVm.PropertyChanged += OnSelectImageCardPropertyChanged;
        }
    }

    private void OnSelectImageCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectImageCardViewModel.ImageSource))
        {
            OnPropertyChanged(nameof(SelectedImage));
            GenerateImageCommand.NotifyCanExecuteChanged(); // 🔑 Ažuriraj stanje gumba kad se promijeni slika
        }
    }

    // ✅ ISPRAVNO: Samo provjera slike (BEZ provjere modela - standardno SM ponašanje)
    protected override bool CanGenerateImage() => 
        base.CanGenerateImage() && 
        selectImageCardVm?.ImageSource != null;

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
        // 1) PadImageForOutpaint (IMAGE + MASK)
        // Python: pad_image_for_outpainting.py → RETURN_TYPES = ("IMAGE", "MASK")
        // ⚠️ IME NODE-A MORA BITI TOČNO: "ImagePadForOutpaint" (BEZ "ing" na kraju)
        //
        var padImage = nodes.AddNamedNode(
            new NamedComfyNode<ImageNodeConnection, ImageMaskConnection>("PadImage")
            {
                ClassType = "ImagePadForOutpaint", // ✅ TOČNO IME (bez "ing")
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
        // 3) Original latent (nepromijenjena originalna slika)
        //
        var originalVaeEncode = nodes.AddTypedNode(
            new ComfyNodeBuilder.VAEEncode
            {
                Name = "OriginalVAEEncode",
                Pixels = primaryImage, // ✅ ORIGINALNA SLIKA (bez paddinga)
                Vae = checkpoint.Output3
            }
        );

        //
        // 4) Padded latent (za generiranje novog sadržaja)
        // ✅ Pixels očekuje ImageNodeConnection (cijeli objekt, BEZ .Data)
        //
        var paddedVaeEncode = nodes.AddTypedNode(
            new ComfyNodeBuilder.VAEEncode
            {
                Name = "PaddedVAEEncode",
                Pixels = padImage.Output1, // ✅ ImageNodeConnection
                Vae = checkpoint.Output3
            }
        );

        //
        // 5) KSampler (generira SAMO novi sadržaj na praznim rubovima)
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
                LatentImage = paddedVaeEncode.Output,
                Denoise = Math.Min(samplerCard?.DenoiseStrength ?? 1.0, 0.35) // Niski denoise za outpainting
            }
        );

        //
        // 6) LatentComposite
        // ✅ Inputs dictionary očekuje object[] (.Data property)
        //
        var composite = nodes.AddNamedNode(
            new NamedComfyNode<LatentNodeConnection>("LatentComposite")
            {
                ClassType = "LatentComposite",
                Inputs = new Dictionary<string, object?>
                {
                    ["original"] = originalVaeEncode.Output?.Data, // ✅ object[] za Inputs
                    ["generated"] = sampler.Output?.Data,          // ✅ object[] za Inputs
                    ["mask"] = padImage.Output2.Data               // ✅ object[] za Inputs (ImageMaskConnection → object[])
                }
            }
        );

        //
        // 7) Decode finalne slike
        // ✅ Samples očekuje LatentNodeConnection (cijeli objekt, BEZ .Data)
        //
        var vaeDecode = nodes.AddTypedNode(
            new ComfyNodeBuilder.VAEDecode
            {
                Name = "VAEDecode",
                Samples = composite.Output, // ✅ LatentNodeConnection
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

        // Provjeri model PRIJE generacije (standardno SM ponašanje)
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

    // Cleanup event handler kad se ViewModel uništi
    protected override void Dispose(bool disposing)
    {
        if (disposing && selectImageCardVm is not null)
        {
            selectImageCardVm.PropertyChanged -= OnSelectImageCardPropertyChanged;
        }
        base.Dispose(disposing);
    }
}
