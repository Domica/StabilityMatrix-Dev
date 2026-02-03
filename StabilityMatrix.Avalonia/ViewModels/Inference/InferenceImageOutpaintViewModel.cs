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

        // Gumb koji uvijek radi, zove bazu tek na klik
        RunOutpaintCommand = new RelayCommand(() => GenerateImageCommand.Execute(null));
    }

    private string GetRandomPrefix() => Guid.NewGuid().ToString().Substring(0, 8);

    private string GetUniqueName(string baseName, HashSet<string> existingNames)
    {
        int suffix = 1;
        string name = baseName;
        while (existingNames.Contains(name)) { name = $"{baseName}_{suffix++}"; }
        existingNames.Add(name);
        return name;
    }

    // --- SVI TVOJI ORIGINALNI ČVOROVI (CS9035 popravljeni) ---

    public record ImageUpscaleWithModel : ComfyTypedNodeBase<ImageNodeConnection>
    {
        public override string ClassType => "ImageUpscaleWithModel";
        public required ImageNodeConnection Image { get; init; }
        public required UpscaleModelNodeConnection UpscaleModel { get; init; }
        public required string Name { get; init; }
    }

    public record LoraLoader : ComfyTypedNodeBase<ModelNodeConnection, CLIPNodeConnection>
    {
        public override string ClassType => "LoraLoader";
        public required ModelNodeConnection Model { get; init; }
        public required CLIPNodeConnection Clip { get; init; }
        public required string LoraName { get; init; }
        public double StrengthModel { get; init; } = 1.0;
        public double StrengthClip { get; init; } = 1.0;
        public required string Name { get; init; }
    }

    public record ControlNetApplyAdvanced : ComfyTypedNodeBase<ConditioningNodeConnection>
    {
        public override string ClassType => "ControlNetApplyAdvanced";
        public required ConditioningNodeConnection Positive { get; init; }
        public required ConditioningNodeConnection Negative { get; init; }
        public required ControlNetNodeConnection ControlNet { get; init; }
        public required ImageNodeConnection Image { get; init; }
        public double Strength { get; init; } = 1.0;
        public double StartPercent { get; init; } = 0.0;
        public double EndPercent { get; init; } = 1.0;
        public required string Name { get; init; }
    }

    public record TiledVAEDecode : ComfyTypedNodeBase<ImageNodeConnection>
    {
        public override string ClassType => "TiledVAEDecode";
        public required LatentNodeConnection Samples { get; init; }
        public required VAENodeConnection Vae { get; init; }
        public int TileSize { get; init; } = 512;
        public required string Name { get; init; }
    }

    public record SVDSampler : ComfyTypedNodeBase<LatentNodeConnection>
    {
        public override string ClassType => "VideoLinearSampler";
        public required ModelNodeConnection Model { get; init; }
        public required LatentNodeConnection LatentImage { get; init; }
        public required ConditioningNodeConnection Positive { get; init; }
        public required ConditioningNodeConnection Negative { get; init; }
        public required string Name { get; init; }
    }

    public record HunyuanVideoSampler : ComfyTypedNodeBase<LatentNodeConnection>
    {
        public override string ClassType => "HunyuanVideoSampler";
        public required ModelNodeConnection Model { get; init; }
        public required string Name { get; init; }
    }

    public record WanImageToVideo : ComfyTypedNodeBase<LatentNodeConnection>
    {
        public override string ClassType => "WanImageToVideo";
        public required ModelNodeConnection Model { get; init; }
        public required ImageNodeConnection Image { get; init; }
        public required string Name { get; init; }
    }

    public record FreeU : ComfyTypedNodeBase<ModelNodeConnection>
    {
        public override string ClassType => "FreeU";
        public required ModelNodeConnection Model { get; init; }
        public double B1 { get; init; } = 1.1;
        public double B2 { get; init; } = 1.2;
        public double S1 { get; init; } = 0.9;
        public double S2 { get; init; } = 0.2;
        public required string Name { get; init; }
    }

    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        base.BuildPrompt(args);
        var builder = args.Builder;
        var nodes = builder.Nodes;
        var names = new HashSet<string>();

        var selectImageCard = StackCardViewModel.GetCard<SelectImageCardViewModel>();
        var outpaintCard = StackCardViewModel.GetCard<OutpaintCardViewModel>();
        var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();
        var samplerCard = StackCardViewModel.GetCard<SamplerCardViewModel>();
        var promptCard = StackCardViewModel.GetCard<PromptCardViewModel>();
        var seedCard = StackCardViewModel.GetCard<SeedCardViewModel>();

        if (selectImageCard?.ImageSource == null) return;
        selectImageCard.ApplyStep(args);

        var padNode = new NamedComfyNode<ImageNodeConnection>(GetUniqueName("OutpaintPad", names))
        {
            Name = GetUniqueName("OutpaintPad", names),
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
        };
        var padImage = nodes.AddNamedNode(padNode);

        var checkpoint = nodes.AddTypedNode(new ComfyNodeBuilder.CheckpointLoaderSimple
        {
            Name = GetUniqueName("Loader", names),
            CkptName = modelCard?.SelectedModel?.RelativePath ?? ""
        });

        var positive = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode
        {
            Name = GetUniqueName("PosPrompt", names),
            Clip = checkpoint.Output2,
            Text = promptCard?.PromptDocument.Text ?? ""
        });

        var negative = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode
        {
            Name = GetUniqueName("NegPrompt", names),
            Clip = checkpoint.Output2,
            Text = ""
        });

        var vaeEncode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEEncode
        {
            Name = GetUniqueName("VaeEncode", names),
            Pixels = padImage.Output,
            Vae = checkpoint.Output3
        });

        var sampler = nodes.AddTypedNode(new ComfyNodeBuilder.KSampler
        {
            Name = GetUniqueName("Sampler", names),
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
            Name = GetUniqueName("VaeDecode", names),
            Samples = sampler.Output,
            Vae = checkpoint.Output3
        });

        builder.Connections.Primary = vaeDecode.Output;

        var preview = nodes.AddTypedNode(new ComfyNodeBuilder.PreviewImage
        {
            Name = GetUniqueName("Preview", names),
            Images = vaeDecode.Output
        });
        builder.Connections.OutputNodes.Add(preview);
    }

    protected override async Task GenerateImageImpl(GenerateOverrides overrides, CancellationToken cancellationToken)
    {
        if (ClientManager.Client == null)
        {
            _notificationService.Show("Not Connected", "ComfyUI is not running.");
            return;
        }

        await UploadInputImages(ClientManager.Client);
        var buildArgs = new BuildPromptEventArgs { Overrides = overrides };
        BuildPrompt(buildArgs);

        var genArgs = new ImageGenerationEventArgs
        {
            Client = ClientManager.Client!,
            Nodes = buildArgs.Builder.ToNodeDictionary(),
            OutputNodeNames = buildArgs.Builder.Connections.OutputNodeNames.ToArray(),
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
