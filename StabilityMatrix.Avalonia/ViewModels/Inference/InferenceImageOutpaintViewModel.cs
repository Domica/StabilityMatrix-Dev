using System.Collections.Generic;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Inference.Modules;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Core.Services;
using CommunityToolkit.Mvvm.Input; // ⬅️ potrebno za RelayCommand / AsyncRelayCommand
using System.Threading.Tasks;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(Views.Inference.InferenceImageOutpaintView))]
[ManagedService]
[Transient]
public partial class InferenceImageOutpaintViewModel : InferenceGenerationViewModelBase
{
    public const string ModuleKey = "ImageOutpaint";

    public StackCardViewModel StackCardViewModel { get; }

    public ImageSource? SelectedImage
    { 
        get 
        { 
            var selectImageCard = StackCardViewModel.GetCard<SelectImageCardViewModel>();
            return selectImageCard?.ImageSource; 
        } 
    }

    // ⬇️⬇️⬇️ DODANO — GenerateCommand
    public IAsyncRelayCommand GenerateCommand { get; }
    // ⬆️⬆️⬆️

    public InferenceImageOutpaintViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager clientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, clientManager, notificationService, settingsManager, runningPackageService)
    {
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

        // ⬇️⬇️⬇️ DODANO — inicijalizacija GenerateCommand
        GenerateCommand = new AsyncRelayCommand(GenerateAsync);
        // ⬆️⬆️⬆️
    }

    // ⬇️⬇️⬇️ DODANO — metoda koju poziva GenerateCommand
    private async Task GenerateAsync()
    {
        // Pokreće standardni SM workflow za generiranje
        await base.GenerateAsync();
    }
    // ⬆️⬆️⬆️

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
}
