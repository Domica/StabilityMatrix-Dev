using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
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
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Avalonia.Controls;
using Avalonia.Threading;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(Views.Inference.InferenceImageOutpaintView))]
[ManagedService]
[Transient]
public partial class InferenceImageOutpaintViewModel : InferenceGenerationViewModelBase
{
    public const string ModuleKey = "ImageOutpaint";
    private readonly INotificationService _notificationService;
    private AsyncRelayCommand? _generateImageCommandOverride;
    private CancellationTokenSource? _generationCancellationTokenSource;
    private bool _isGenerating;
    private bool _isProgressIndeterminate;
    
    public StackCardViewModel StackCardViewModel { get; }

    public ImageSource? SelectedImage => StackCardViewModel.GetCard<SelectImageCardViewModel>()?.ImageSource;

    // Status properties for UI
    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            if (SetProperty(ref _isGenerating, value))
            {
                GenerateImageCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }
    
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }
    
    private string _generationStatus = "Ready";
    public string GenerationStatus
    {
        get => _generationStatus;
        private set => SetProperty(ref _generationStatus, value);
    }
    
    private double _generationProgress;
    public double GenerationProgress
    {
        get => _generationProgress;
        private set
        {
            SetProperty(ref _generationProgress, value);
            IsProgressIndeterminate = value < 5; // Show indeterminate if progress < 5%
        }
    }

    // Override the command to handle cancellation
    public new IAsyncRelayCommand GenerateImageCommand => 
        _generateImageCommandOverride ??= new AsyncRelayCommand(
            GenerateImageAsync,
            CanGenerateImage);

    // Cancel command
    private IAsyncRelayCommand? _cancelCommand;
    public IAsyncRelayCommand CancelCommand => 
        _cancelCommand ??= new AsyncRelayCommand(CancelGenerationAsync, CanCancelGeneration);

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
    }

    private bool CanGenerateImage()
    {
        return !IsGenerating && SelectedImage != null && 
               StackCardViewModel.GetCard<ModelCardViewModel>()?.SelectedModel != null;
    }

    private bool CanCancelGeneration()
    {
        return IsGenerating && _generationCancellationTokenSource != null;
    }

    private async Task GenerateImageAsync()
    {
        if (IsGenerating) return;
        
        try
        {
            IsGenerating = true;
            GenerationStatus = "Preparing generation...";
            GenerationProgress = 0;
            
            _generationCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _generationCancellationTokenSource.Token;

            // Call the base implementation with cancellation
            await GenerateImageImpl(new GenerateOverrides(), cancellationToken);
            
            GenerationStatus = "Generation completed";
            GenerationProgress = 100;
            
            // Reset after a short delay
            await Task.Delay(1000, cancellationToken);
            GenerationStatus = "Ready";
            GenerationProgress = 0;
        }
        catch (OperationCanceledException)
        {
            GenerationStatus = "Generation cancelled";
            _notificationService.Show("Cancelled", "Image generation was cancelled.");
        }
        catch (Exception ex)
        {
            GenerationStatus = "Error occurred";
            _notificationService.Show("Error", $"Failed to generate image: {ex.Message}");
        }
        finally
        {
            IsGenerating = false;
            _generationCancellationTokenSource?.Dispose();
            _generationCancellationTokenSource = null;
        }
    }

    private async Task CancelGenerationAsync()
    {
        if (_generationCancellationTokenSource != null && !_generationCancellationTokenSource.IsCancellationRequested)
        {
            GenerationStatus = "Cancelling...";
            _generationCancellationTokenSource.Cancel();
            
            // Wait a bit for cancellation to propagate
            await Task.Delay(500);
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

    // --- PRO PAD NODE ---
    var padImageNode = new NamedComfyNode<ImageNodeConnection, ImageMaskConnection>("OutpaintPadNode")
    {
        ClassType = "ImagePadForOutpainting",
        Inputs = new Dictionary<string, object?>
        {
            ["image"] = builder.GetPrimaryAsImage(),
            ["left"] = outpaintCard?.ExpandLeft ?? 0,
            ["right"] = outpaintCard?.ExpandRight ?? 0,
            ["top"] = outpaintCard?.ExpandTop ?? 0,
            ["bottom"] = outpaintCard?.ExpandBottom ?? 0,
            ["feathering"] = outpaintCard?.Feathering ?? 40,
            ["padding_mode"] = "tile"
        }
    };

    var padImage = nodes.AddNamedNode(padImageNode);
    var paddedImage = padImage.Output1;
    var outpaintMask = padImage.Output2;

    // --- MODEL ---
    var checkpoint = nodes.AddTypedNode(new ComfyNodeBuilder.CheckpointLoaderSimple
    {
        Name = "CkptLoader",
        CkptName = StackCardViewModel.GetCard<ModelCardViewModel>()?.SelectedModel?.RelativePath ?? ""
    });

    // --- PROMPTS ---
    var prompt = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode
    {
        Name = "PositivePrompt",
        Clip = checkpoint.Output2,
        Text = StackCardViewModel.GetCard<PromptCardViewModel>()?.PromptDocument.Text ?? ""
    });

    var negative = nodes.AddTypedNode(new ComfyNodeBuilder.CLIPTextEncode
    {
        Name = "EmptyNeg",
        Clip = checkpoint.Output2,
        Text = ""
    });

    // --- ENCODE ---
    var vaeEncode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEEncode
    {
        Name = "VAEEncodeNode",
        Pixels = paddedImage,
        Vae = checkpoint.Output3
    });

    // --- SAMPLER ---
    var sampler = nodes.AddTypedNode(new ComfyNodeBuilder.KSampler
    {
        Name = "MainSampler",
        Model = checkpoint.Output1,
        Seed = (ulong)(StackCardViewModel.GetCard<SeedCardViewModel>()?.Seed ?? 0),
        Steps = StackCardViewModel.GetCard<SamplerCardViewModel>()?.Steps ?? 30,
        Cfg = 6.0,
        SamplerName = "dpmpp_2m",
        Scheduler = "karras",
        Positive = prompt.Output,
        Negative = negative.Output,
        LatentImage = vaeEncode.Output,
        Denoise = StackCardViewModel.GetCard<SamplerCardViewModel>()?.DenoiseStrength ?? 0.35
    });

    try
    {
        // --- PRO COMPOSITE ---
        var compositeNode = new NamedComfyNode<LatentNodeConnection>("AdvancedOutpaintLatentCompositeNode")
        {
            ClassType = "AdvancedOutpaintLatentComposite",
            Inputs = new Dictionary<string, object?>
            {
                ["original"] = vaeEncode.Output,
                ["generated"] = sampler.Output,
                ["mask"] = outpaintMask,
                ["feathering"] = outpaintCard?.Feathering ?? 40,
                ["sharpen_strength"] = 0.15,
                ["exposure_blend"] = 0.85,
                ["invert_mask"] = false
            }
        };

        var compositeOutput = nodes.AddNamedNode(compositeNode);

        // --- DECODE ---
        var vaeDecode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEDecode
        {
            Name = "VAEDecodeNode",
            Samples = compositeOutput.Output,
            Vae = checkpoint.Output3
        });

        builder.Connections.Primary = vaeDecode.Output;

        // --- PREVIEW ---
        var preview = nodes.AddTypedNode(new ComfyNodeBuilder.PreviewImage
        {
            Name = "PreviewNode",
            Images = vaeDecode.Output
        });

        builder.Connections.OutputNodes.Add(preview);
    }
    catch (Exception)
    {
        // --- FALLBACK ---
        var fallbackNode = new NamedComfyNode<LatentNodeConnection>("LatentCompositeNode")
        {
            ClassType = "LatentComposite",
            Inputs = new Dictionary<string, object?>
            {
                ["samples_from"] = vaeEncode.Output,
                ["samples_to"] = sampler.Output,
                ["x"] = 0,
                ["y"] = 0,
                ["feather"] = outpaintCard?.Feathering ?? 40
            }
        };

        var fallbackOutput = nodes.AddNamedNode(fallbackNode);

        var vaeDecode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEDecode
        {
            Name = "VAEDecodeNode",
            Samples = fallbackOutput.Output,
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
}


    protected override async Task GenerateImageImpl(GenerateOverrides overrides, CancellationToken cancellationToken)
    {
        // Update status
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            GenerationStatus = "Checking connection...";
        });
        
        if (!ClientManager.IsConnected)
        {
            _notificationService.Show("Not Connected", "Please start ComfyUI.");
            return;
        }

        if (SelectedImage == null)
        {
            _notificationService.Show("No Image", "Please select an image first.");
            return;
        }

        if (StackCardViewModel.GetCard<ModelCardViewModel>()?.SelectedModel == null)
        {
            _notificationService.Show("No Model", "Please select a model first.");
            return;
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                GenerationStatus = "Uploading images...";
                GenerationProgress = 10;
            });
            
            await UploadInputImages(ClientManager.Client!);

            var buildArgs = new BuildPromptEventArgs();
            BuildPrompt(buildArgs);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                GenerationStatus = "Building workflow...";
                GenerationProgress = 30;
            });

            // Simulate progress during generation since we don't have ProgressCallback
            // We'll update progress periodically during the generation
            var progressUpdateTask = Task.Run(async () =>
            {
                while (IsGenerating && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(500, cancellationToken);
                    
                    // Increment progress slowly to show activity
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (GenerationProgress < 90)
                        {
                            GenerationProgress += 1;
                        }
                    });
                }
            });

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

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                GenerationStatus = "Generating image...";
                GenerationProgress = 40;
            });

            await RunGeneration(genArgs, cancellationToken);
            
            // Cancel the progress update task
            await progressUpdateTask;
        }
        catch (OperationCanceledException)
        {
            throw; // Re-throw to be handled in GenerateImageAsync
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                GenerationStatus = "Error occurred";
            });
            _notificationService.Show("Error", $"Failed to generate image: {ex.Message}");
        }
    }

    protected override IEnumerable<ImageSource> GetInputImages()
    {
        var img = StackCardViewModel.GetCard<SelectImageCardViewModel>()?.ImageSource;
        if (img != null) yield return img;
    }
}
