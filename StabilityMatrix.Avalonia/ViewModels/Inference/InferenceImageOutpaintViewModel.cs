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
                Console.WriteLine($"🔄 IsGenerating changed to: {value}");
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

    private bool _smartOutpaintAssistEnabled = true;
    public bool SmartOutpaintAssistEnabled
    {
        get => _smartOutpaintAssistEnabled;
        set => SetProperty(ref _smartOutpaintAssistEnabled, value);
    }

    private double _smartOutpaintInjectionStrength = 0.7;
    public double SmartOutpaintInjectionStrength
    {
        get => _smartOutpaintInjectionStrength;
        set => SetProperty(ref _smartOutpaintInjectionStrength, Math.Clamp(value, 0.0, 1.0));
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
        var instanceId = Guid.NewGuid().ToString().Substring(0, 8);
        Console.WriteLine($"🆕 NEW InferenceImageOutpaintViewModel created: {instanceId}");
        
        _notificationService = notificationService;
        StackCardViewModel = vmFactory.Get<StackCardViewModel>();
        
        Console.WriteLine($"   StackCardViewModel instance: {StackCardViewModel.GetHashCode()}");

        StackCardViewModel.AddCards(
            vmFactory.Get<SelectImageCardViewModel>(),
            vmFactory.Get<OutpaintCardViewModel>(),
            vmFactory.Get<PromptCardViewModel>(),
            vmFactory.Get<SamplerCardViewModel>(s => 
            {
                s.IsDenoiseStrengthEnabled = true;
                // ✅ Set optimal outpaint defaults
                s.DenoiseStrength = 0.55; // Optimal for outpainting
                s.Steps = 30; // Good balance of quality/speed
            }),
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
        if (IsGenerating)
        {
            Console.WriteLine("⚠️ GenerateImageAsync called while already generating");
            return;
        }
        
        try
        {
            // ✅ Set IsGenerating on UI thread
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsGenerating = true;
                GenerationStatus = "Preparing generation...";
                GenerationProgress = 0;
            });
            
            _generationCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _generationCancellationTokenSource.Token;

            // Call the base implementation with cancellation
            await GenerateImageImpl(new GenerateOverrides(), cancellationToken);
            
            // ✅ Success handled in GenerateImageImpl's finally block
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                GenerationStatus = "Generation cancelled";
                GenerationProgress = 0;
            });
            _notificationService.Show("Cancelled", "Image generation was cancelled.");
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                GenerationStatus = "Error occurred";
                GenerationProgress = 0;
            });
            _notificationService.Show("Error", $"Failed to generate image: {ex.Message}");
            Console.WriteLine($"❌ GenerateImageAsync error: {ex}");
        }
        finally
        {
            // ✅ CRITICAL: Ensure IsGenerating is reset on UI thread
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsGenerating = false;
            });
            
            _generationCancellationTokenSource?.Dispose();
            _generationCancellationTokenSource = null;
            
            Console.WriteLine($"✅ GenerateImageAsync finally block - IsGenerating: {IsGenerating}");
        }
    }

    private async Task CancelGenerationAsync()
    {
        if (_generationCancellationTokenSource != null && !_generationCancellationTokenSource.IsCancellationRequested)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                GenerationStatus = "Cancelling...";
            });
            
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

        // ✅ Get optimal feathering value (default 10 for outpaint)
        var feathering = outpaintCard?.Feathering ?? 10;
        
        Console.WriteLine($"🎨 Building outpaint workflow:");
        Console.WriteLine($"   Feathering: {feathering}");
        Console.WriteLine($"   Padding: L={outpaintCard?.ExpandLeft ?? 0} R={outpaintCard?.ExpandRight ?? 0} T={outpaintCard?.ExpandTop ?? 0} B={outpaintCard?.ExpandBottom ?? 0}");

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
                ["feathering"] = feathering,
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
    // === SMART OUTPAINT ASSIST ===
    if (SmartOutpaintAssistEnabled)
    {
        var injection = PromptInjectionOutpaint.Build(
            outpaintCard?.ExpandLeft ?? 0,
            outpaintCard?.ExpandRight ?? 0,
            outpaintCard?.ExpandTop ?? 0,
            outpaintCard?.ExpandBottom ?? 0,
            SmartOutpaintInjectionStrength
        );

    // Positive
    if (!string.IsNullOrWhiteSpace(injection.Positive))
    {
        var originalText = prompt.Text.AsT0 ?? "";
        builder.SetNodeInput(prompt, "text", originalText + injection.Positive);
        Console.WriteLine($"🧠 SmartOutpaintAssist Positive: {injection.Positive}");
    }

    // Negative
    if (!string.IsNullOrWhiteSpace(injection.Negative))
    {
        var originalNeg = negative.Text.AsT0 ?? "";
        builder.SetNodeInput(negative, "text", originalNeg + injection.Negative);
        Console.WriteLine($"🧠 SmartOutpaintAssist Negative: {injection.Negative}");
    }
}


        
        // --- ENCODE ---
        var vaeEncode = nodes.AddTypedNode(new ComfyNodeBuilder.VAEEncode
        {
            Name = "VAEEncodeNode",
            Pixels = paddedImage,
            Vae = checkpoint.Output3
        });

        // ✅ Get denoise strength (default 0.55 for outpaint)
        var denoiseStrength = StackCardViewModel.GetCard<SamplerCardViewModel>()?.DenoiseStrength ?? 0.55;
        var steps = StackCardViewModel.GetCard<SamplerCardViewModel>()?.Steps ?? 30;
        
        Console.WriteLine($"   Denoise: {denoiseStrength:F2}, Steps: {steps}");

        // --- SAMPLER ---
        var sampler = nodes.AddTypedNode(new ComfyNodeBuilder.KSampler
        {
            Name = "MainSampler",
            Model = checkpoint.Output1,
            Seed = (ulong)(StackCardViewModel.GetCard<SeedCardViewModel>()?.Seed ?? 0),
            Steps = steps,
            Cfg = 7.0,
            SamplerName = "dpmpp_2m",
            Scheduler = "karras",
            Positive = prompt.Output,
            Negative = negative.Output,
            LatentImage = vaeEncode.Output,
            Denoise = denoiseStrength
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
                    ["feathering"] = feathering,
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
            
            Console.WriteLine("✅ Using AdvancedOutpaintLatentComposite workflow");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Falling back to LatentComposite: {ex.Message}");
            
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
                    ["feather"] = feathering
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
        Task? progressUpdateTask = null;
        
        try
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

            // ✅ Start progress tracking task with timeout
            progressUpdateTask = Task.Run(async () =>
            {
                var timeout = TimeSpan.FromMinutes(10); // Max 10 minutes
                var startTime = DateTime.UtcNow;
                
                while (IsGenerating && 
                       !cancellationToken.IsCancellationRequested &&
                       DateTime.UtcNow - startTime < timeout)
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
                
                // ✅ If timeout occurred
                if (DateTime.UtcNow - startTime >= timeout)
                {
                    Console.WriteLine("⚠️ Progress tracking timeout");
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        GenerationStatus = "Timeout - taking too long";
                    });
                }
            }, cancellationToken);

            var genArgs = new ImageGenerationEventArgs
            {
                Client = ClientManager.Client!,
                Nodes = buildArgs.Builder.ToNodeDictionary(),
                OutputNodeNames = buildArgs.Builder.Connections.OutputNodeNames.ToArray(),
                Parameters = new GenerationParameters 
                { 
                    ModelName = StackCardViewModel.GetCard<ModelCardViewModel>()
                        ?.SelectedModel?.RelativePath 
                },
                Project = InferenceProjectDocument.FromLoadable(this)
            };

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                GenerationStatus = "Generating image...";
                GenerationProgress = 40;
            });

            Console.WriteLine("🚀 Starting RunGeneration...");
            
            // ✅ Run generation
            await RunGeneration(genArgs, cancellationToken);
            
            Console.WriteLine("✅ RunGeneration completed successfully");
            
            // ✅ CRITICAL: Stop progress task immediately after RunGeneration completes
            if (progressUpdateTask != null)
            {
                try 
                { 
                    await progressUpdateTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); 
                    Console.WriteLine("✅ Progress task stopped naturally");
                } 
                catch (TimeoutException)
                {
                    Console.WriteLine("⚠️ Progress task didn't stop within 2 seconds");
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("✅ Progress task cancelled");
                }
            }
            
            // ✅ CRITICAL: Explicitly mark as complete
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                GenerationProgress = 100;
                GenerationStatus = "Generation completed";
            });
            
            Console.WriteLine("✅ Completion status set");
            
            // ✅ Short delay to show completion (don't use cancellationToken here)
            await Task.Delay(1500, CancellationToken.None);
            
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("⚠️ Generation cancelled");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                GenerationStatus = "Cancelled";
                GenerationProgress = 0;
            });
            throw; // Re-throw to be handled in GenerateImageAsync
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Generation error: {ex.Message}");
            Console.WriteLine($"   Stack trace: {ex.StackTrace}");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                GenerationStatus = "Error occurred";
                GenerationProgress = 0;
            });
            _notificationService.Show("Error", $"Failed to generate image: {ex.Message}");
        }
        finally
        {
            Console.WriteLine("🧹 GenerateImageImpl finally block started");
            
            // ✅ CRITICAL: Always cleanup progress task
            if (progressUpdateTask != null)
            {
                try 
                { 
                    await progressUpdateTask; 
                    Console.WriteLine("✅ Progress task awaited in finally");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Progress task exception in finally: {ex.Message}");
                }
            }
            
            // ✅ CRITICAL: Reset UI state on UI thread
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // If we completed successfully, reset after a brief moment
                if (GenerationProgress == 100)
                {
                    // Success case - schedule reset
                    Task.Delay(500).ContinueWith(_ => 
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!IsGenerating) // Only reset if not generating again
                            {
                                GenerationStatus = "Ready";
                                GenerationProgress = 0;
                                Console.WriteLine("✅ UI reset to Ready state");
                            }
                        });
                    });
                }
                else
                {
                    // Error/cancel case - reset immediately
                    GenerationStatus = "Ready";
                    GenerationProgress = 0;
                    Console.WriteLine("✅ UI reset immediately (error/cancel)");
                }
            });
            
            Console.WriteLine("✅ GenerateImageImpl finally block completed");
        }
    }

    protected override IEnumerable<ImageSource> GetInputImages()
    {
        var img = StackCardViewModel.GetCard<SelectImageCardViewModel>()?.ImageSource;
        if (img != null) yield return img;
    }
}
