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
    private bool _isGenerating;
    private SelectImageCardViewModel? _selectImageCardVm;
    private ModelCardViewModel? _modelCardVm;

    public StackCardViewModel StackCardViewModel { get; }

    public ImageSource? SelectedImage => StackCardViewModel.GetCard<SelectImageCardViewModel>()?.ImageSource;

    public bool CanGenerate => !_isGenerating && 
                              SelectedImage != null && 
                              StackCardViewModel.GetCard<ModelCardViewModel>()?.SelectedModel != null &&
                              ClientManager.IsConnected;

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

        _selectImageCardVm = vmFactory.Get<SelectImageCardViewModel>();
        _modelCardVm = vmFactory.Get<ModelCardViewModel>();

        StackCardViewModel.AddCards(
            _selectImageCardVm,
            vmFactory.Get<OutpaintCardViewModel>(),
            vmFactory.Get<PromptCardViewModel>(),
            vmFactory.Get<SamplerCardViewModel>(s => s.IsDenoiseStrengthEnabled = true),
            _modelCardVm,
            vmFactory.Get<SeedCardViewModel>()
        );

        // Subscribe to property changes
        if (_selectImageCardVm != null)
        {
            _selectImageCardVm.PropertyChanged += OnSelectImageCardPropertyChanged;
        }

        if (_modelCardVm != null)
        {
            _modelCardVm.PropertyChanged += OnModelCardPropertyChanged;
        }

        // Subscribe to client manager connection changes
        ClientManager.PropertyChanged += OnClientManagerPropertyChanged;
    }

    private void OnSelectImageCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectImageCardViewModel.ImageSource))
        {
            OnPropertyChanged(nameof(SelectedImage));
            OnPropertyChanged(nameof(CanGenerate));
            GenerateImageCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnModelCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModelCardViewModel.SelectedModel))
        {
            OnPropertyChanged(nameof(CanGenerate));
            GenerateImageCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnClientManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IInferenceClientManager.IsConnected))
        {
            OnPropertyChanged(nameof(CanGenerate));
            GenerateImageCommand.NotifyCanExecuteChanged();
        }
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

        // Pad image for outpainting
        var padImage = nodes.AddNamedNode(new NamedComfyNode<object>("PadImageForOutpainting")
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

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateImageAsync()
    {
        if (!CanGenerate)
        {
            if (!ClientManager.IsConnected)
            {
                _notificationService.Show("Not Connected", "Please start ComfyUI.");
            }
            else if (SelectedImage == null)
            {
                _notificationService.Show("No Image", "Please select an image first.");
            }
            else if (StackCardViewModel.GetCard<ModelCardViewModel>()?.SelectedModel == null)
            {
                _notificationService.Show("No Model", "Please select a model first.");
            }
            return;
        }

        try
        {
            _isGenerating = true;
            OnPropertyChanged(nameof(CanGenerate));
            GenerateImageCommand.NotifyCanExecuteChanged();

            await UploadInputImages(ClientManager.Client!);

            var buildArgs = new BuildPromptEventArgs();
            BuildPrompt(buildArgs);

            var genArgs = new ImageGenerationEventArgs
            {
                Client = ClientManager.Client!,
                Nodes = buildArgs.Builder.ToNodeDictionary(),
                OutputNodeNames = buildArgs.Builder.Connections.OutputNodeNames.ToArray(),
                Parameters = new GenerationParameters 
                { 
                    ModelName = StackCardViewModel.GetCard<ModelCardViewModel>()?.SelectedModel?.RelativePath 
                },
                Project = InferenceProjectDocument.FromLoadable(this)
            };

            await RunGeneration(genArgs, CancellationToken.None);
        }
        finally
        {
            _isGenerating = false;
            OnPropertyChanged(nameof(CanGenerate));
            GenerateImageCommand.NotifyCanExecuteChanged();
        }
    }

    // Override the base GenerateImageImpl to use our async command
    protected override async Task GenerateImageImpl(GenerateOverrides overrides, CancellationToken cancellationToken)
    {
        // This will be called by the base class if needed, but we're using our own command
        await GenerateImageAsync();
    }

    protected override IEnumerable<ImageSource> GetInputImages()
    {
        var img = StackCardViewModel.GetCard<SelectImageCardViewModel>()?.ImageSource;
        if (img != null) yield return img;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_selectImageCardVm != null)
            {
                _selectImageCardVm.PropertyChanged -= OnSelectImageCardPropertyChanged;
            }

            if (_modelCardVm != null)
            {
                _modelCardVm.PropertyChanged -= OnModelCardPropertyChanged;
            }

            ClientManager.PropertyChanged -= OnClientManagerPropertyChanged;
        }
        
        base.Dispose(disposing);
    }
}
