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

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(Views.Inference.InferenceImageOutpaintView))]
[ManagedService]
[Transient]
public partial class InferenceImageOutpaintViewModel : InferenceGenerationViewModelBase
{
    public const string ModuleKey = "ImageOutpaint";

    private readonly INotificationService _notificationService;
    private readonly SelectImageCardViewModel _selectImageCardVm;

    public StackCardViewModel StackCardViewModel { get; }

    public InferenceImageOutpaintViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager clientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, clientManager, notificationService, settingsManager, runningPackageService)
    {
        _notificationService = notificationService;
        StackCardViewModel = vmFactory.Get<StackCardViewModel>();

        var samplerCard = vmFactory.Get<SamplerCardViewModel>(sampler =>
        {
            sampler.IsDenoiseStrengthEnabled = true;
        });

        _selectImageCardVm = vmFactory.Get<SelectImageCardViewModel>();

        StackCardViewModel.AddCards(
            _selectImageCardVm,
            vmFactory.Get<OutpaintCardViewModel>(),
            vmFactory.Get<PromptCardViewModel>(),
            samplerCard,
            vmFactory.Get<ModelCardViewModel>(),
            vmFactory.Get<SeedCardViewModel>()
        );

        // Pretplata na promjene klijenta i slike kako bi se gumb ažurirao
        ClientManager.PropertyChanged += OnClientManagerPropertyChanged;
        if (_selectImageCardVm != null)
        {
            _selectImageCardVm.PropertyChanged += OnSelectImageCardPropertyChanged;
        }

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
            GenerateImageCommand.NotifyCanExecuteChanged();
        }
    }

    // ISPRAVAK: Metoda se u bazi zove CanGenerate, a ne CanGenerateImage
    protected override bool CanGenerate()
    {
        return base.CanGenerate() && _selectImageCardVm?.ImageSource != null;
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

        // 1. Image Pad for Outpaint
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

        // 2. Checkpoint Loader
        var checkpoint = nodes.AddTypedNode(
            new ComfyNodeBuilder.CheckpointLoaderSimple
            {
                CkptName = modelCard?.SelectedModel?.RelativePath ?? ""
            }
        );

        // 3. Encode Prompts
        var pos = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode { Clip = checkpoint.Output2, Text = promptCard?.PromptDocument.Text ?? "" });
        var neg = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode { Clip = checkpoint.Output2, Text = promptCard?.NegativePromptDocument.Text ?? "" });

        // 4. Encode Latents
        var vaeEncode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEEncode { Pixels = padImage.Output1, Vae = checkpoint.Output3 });

        // 5. Sampler
        var sampler = nodes.AddTypedNode(
            new ComfyNodeBuilder.KSampler
            {
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
            }
        );

        // 6. Decode & Output
        var decode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEDecode { Samples = sampler.Output, Vae = checkpoint.Output3 });
        builder.Connections.Primary = decode.Output;

        var preview = nodes.AddTypedNode(new ComfyNodeBuilder.PreviewImage { Images = decode.Output });
        builder.Connections.OutputNodes.Add(preview);
    }

    protected override async Task GenerateImageImpl(GenerateOverrides overrides, CancellationToken cancellationToken)
    {
        if (!ClientManager.IsConnected) return;

        foreach (var image in GetInputImages())
            await ClientManager.UploadInputImageAsync(image, cancellationToken);

        var args = new BuildPromptEventArgs { Overrides = overrides };
        BuildPrompt(args);

        var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();
        var genArgs = new ImageGenerationEventArgs
        {
            Client = ClientManager.Client,
            Nodes = args.Builder.ToNodeDictionary(),
            OutputNodeNames = args.Builder.Connections.OutputNodeNames.ToArray(),
            Parameters = new GenerationParameters { ModelName = modelCard?.SelectedModel?.RelativePath ?? "" },
            Project = InferenceProjectDocument.FromLoadable(this)
        };

        await RunGeneration(genArgs, cancellationToken);
    }

    protected override IEnumerable<ImageSource> GetInputImages()
    {
        if (_selectImageCardVm?.ImageSource is { } src) yield return src;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClientManager.PropertyChanged -= OnClientManagerPropertyChanged;
            if (_selectImageCardVm != null) _selectImageCardVm.PropertyChanged -= OnSelectImageCardPropertyChanged;
        }
        base.Dispose(disposing);
    }
}
