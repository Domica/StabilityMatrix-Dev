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

    public StackCardViewModel StackCardViewModel { get; }

    public ImageSource? SelectedImage
    {
        get
        {
            var selectImageCard = StackCardViewModel.GetCard<SelectImageCardViewModel>();
            return selectImageCard?.ImageSource;
        }
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

        StackCardViewModel.AddCards(
            vmFactory.Get<SelectImageCardViewModel>(),
            vmFactory.Get<OutpaintCardViewModel>(),
            vmFactory.Get<PromptCardViewModel>(),
            samplerCard,
            vmFactory.Get<ModelCardViewModel>(),
            vmFactory.Get<SeedCardViewModel>()
        );

        // ⬇⬇⬇ bitno: propagiraj promjenu ImageSource -> SelectedImage ⬇⬇⬇
        var selectImageCard = StackCardViewModel.GetCard<SelectImageCardViewModel>();
        if (selectImageCard is not null)
        {
            selectImageCard.PropertyChanged += OnSelectImageCardPropertyChanged;
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

    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        var nodes = args.Builder.Nodes;

        var selectImageCard = StackCardViewModel.GetCard<SelectImageCardViewModel>();
        var outpaintCard = StackCardViewModel.GetCard<OutpaintCardViewModel>();
        var promptCard = StackCardViewModel.GetCard<PromptCardViewModel>();
        var samplerCard = StackCardViewModel.GetCard<SamplerCardViewModel>();
        var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();
        var seedCard = StackCardViewModel.GetCard<SeedCardViewModel>();

        if (selectImageCard?.ImageSource?.LocalFile is not { } imageFile)
            return;

        var loadImage = nodes.AddTypedNode(
            new ComfyNodeBuilder.LoadImage
            {
                Name = "LoadImage",
                Image = imageFile.Name
            }
        );

        var padImage = nodes.AddNamedNode(
            new NamedComfyNode<ImageNodeConnection>("PadImage")
            {
                ClassType = "ImagePadForOutpaint",
                Inputs = new Dictionary<string, object?>
                {
                    ["image"] = loadImage.Output1.Data,
                    ["left"] = outpaintCard?.ExpandLeft ?? 0,
                    ["right"] = outpaintCard?.ExpandRight ?? 0,
                    ["top"] = outpaintCard?.ExpandTop ?? 0,
                    ["bottom"] = outpaintCard?.ExpandBottom ?? 0,
                    ["feathering"] = outpaintCard?.Feathering ?? 40
                }
            }
        );

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

        var vaeEncode = nodes.AddTypedNode(
            new ComfyNodeBuilder.VAEEncode
            {
                Name = "VAEEncode",
                Pixels = padImage.Output,
                Vae = checkpoint.Output3
            }
        );

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
                LatentImage = vaeEncode.Output,
                Denoise = samplerCard?.DenoiseStrength ?? 1.0
            }
        );

        var vaeDecode = nodes.AddTypedNode(
            new ComfyNodeBuilder.VAEDecode
            {
                Name = "VAEDecode",
                Samples = sampler.Output,
                Vae = checkpoint.Output3
            }
        );

        nodes.AddTypedNode(
            new ComfyNodeBuilder.PreviewImage
            {
                Name = "PreviewImage",
                Images = vaeDecode.Output
            }
        );
    }

    protected override IEnumerable<ImageSource> GetInputImages()
    {
        var selectImageCard = StackCardViewModel.GetCard<SelectImageCardViewModel>();
        if (selectImageCard?.ImageSource is { } imageSource)
            yield return imageSource;
    }

    protected override async Task GenerateImageImpl(
        GenerateOverrides overrides,
        CancellationToken cancellationToken
    )
    {
        if (!ClientManager.IsConnected)
        {
            notificationService.Show("Client not connected", "Please connect first");
            return;
        }

        var selectImageCard = StackCardViewModel.GetCard<SelectImageCardViewModel>();
        if (selectImageCard?.ImageSource?.LocalFile?.FullPath is not { } path)
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
            Parameters = new GenerationParameters
            {
                ModelName = StackCardViewModel
                    .GetCard<ModelCardViewModel>()
                    ?.SelectedModel?.RelativePath
            },
            Project = InferenceProjectDocument.FromLoadable(this)
        };

        await RunGeneration(generationArgs, cancellationToken);
    }
}
