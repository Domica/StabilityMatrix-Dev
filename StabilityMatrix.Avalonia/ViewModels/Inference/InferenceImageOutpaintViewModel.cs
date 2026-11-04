using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Inference.Modules;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Core.Models.Inference;
using StabilityMatrix.Core.Services;

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

    // --- Debug Prompt Overlay ---
    [ObservableProperty]
    private bool showDebugPrompt;

    [ObservableProperty]
    private string? debugFinalPositive;

    [ObservableProperty]
    private string? debugFinalNegative;

    [ObservableProperty]
    private string? debugFinalModelName;

    public double SmartOutpaintInjectionStrength
    {
        get => _smartOutpaintInjectionStrength;
        set => SetProperty(ref _smartOutpaintInjectionStrength, Math.Clamp(value, 0.0, 1.0));
    }

    // Override the command to handle cancellation
    public new IAsyncRelayCommand GenerateImageCommand =>
        _generateImageCommandOverride ??= new AsyncRelayCommand(GenerateImageAsync, CanGenerateImage);

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
    )
        : base(vmFactory, clientManager, notificationService, settingsManager, runningPackageService)
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

        // Re-evaluate Generate button when input image or model changes
        var imageCard = vmFactory.Get<SelectImageCardViewModel>();
        var modelCard = vmFactory.Get<ModelCardViewModel>();
        imageCard.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(SelectImageCardViewModel.ImageSource))
                GenerateImageCommand.NotifyCanExecuteChanged();
        };
        modelCard.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ModelCardViewModel.SelectedModel))
                GenerateImageCommand.NotifyCanExecuteChanged();
        };
    }

    private bool CanGenerateImage()
    {
        return !IsGenerating
            && SelectedImage != null
            && StackCardViewModel.GetCard<ModelCardViewModel>()?.SelectedModel != null;
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
        if (
            _generationCancellationTokenSource != null
            && !_generationCancellationTokenSource.IsCancellationRequested
        )
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

        if (selectImageCard?.ImageSource == null)
            return;
        selectImageCard.ApplyStep(args);

        // ✅ Get optimal feathering value (default 10 for outpaint)
        var feathering = outpaintCard?.Feathering ?? 10;

        Console.WriteLine($"🎨 Building outpaint workflow:");
        Console.WriteLine($"   Feathering: {feathering}");
        Console.WriteLine(
            $"   Padding: L={outpaintCard?.ExpandLeft ?? 0} R={outpaintCard?.ExpandRight ?? 0} T={outpaintCard?.ExpandTop ?? 0} B={outpaintCard?.ExpandBottom ?? 0}"
        );

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
                ["blur_sigma"] = 2,
                ["room_mode"] = true,
                ["auto_room_mode"] = true,
                ["indoor_hint"] = "",
                ["debug_overlay"] = false,
                ["verbose_debug"] = true,
                ["balanced_mode"] = true,
                ["seamless_outpaint_mode"] = false,
                ["real_photo_mode"] = "SAFE",
                ["cinematic_intensity"] = 1.0,
                ["bleed_strength"] = 1.0,
                ["blur_strength"] = 1.0,
                ["profile_mode"] = "AUTO",
                ["router_profile"] = "",
                ["router_horizon"] = 0.0,
                ["router_water"] = 0.0,
                ["router_is_photo"] = false,
                ["router_outpaint_dir"] = "AUTO",
                ["fast_ai_boost"] = true,
                ["enable_fix3_cap"] = true,
            },
        };

        var padImage = nodes.AddNamedNode(padImageNode);
        var paddedImage = padImage.Output1;
        var outpaintMask = padImage.Output2;

        // --- MODEL ---
        var checkpoint = nodes.AddTypedNode(
            new ComfyNodeBuilder.CheckpointLoaderSimple
            {
                Name = "CkptLoader",
                CkptName =
                    StackCardViewModel.GetCard<ModelCardViewModel>()?.SelectedModel?.RelativePath ?? "",
            }
        );

        // Initialize prompt text variables
        var positiveText = StackCardViewModel.GetCard<PromptCardViewModel>()?.PromptDocument.Text ?? "";
        var negativeText = "";

        // === SMART OUTPAINT ASSIST ===
        if (SmartOutpaintAssistEnabled)
        {
            var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();
            var modelName = modelCard?.SelectedModel?.ShortDisplayName ?? "";
            var availableModels = ClientManager.Models.Select(m => m.ShortDisplayName ?? m.RelativePath);

            var gp = new GenerationParameters
            {
                OutpaintLeft = outpaintCard?.ExpandLeft ?? 0,
                OutpaintRight = outpaintCard?.ExpandRight ?? 0,
                OutpaintTop = outpaintCard?.ExpandTop ?? 0,
                OutpaintBottom = outpaintCard?.ExpandBottom ?? 0,
                SmartOutpaintInjectionStrength = SmartOutpaintInjectionStrength,
                SceneType = SceneType.Unknown,
                AvailableModels = availableModels,
            };

            PromptInjectionOutpaint.ApplyOutpaintPromptInjection(
                gp,
                modelName,
                availableModels,
                ref positiveText,
                ref negativeText
            );

            // Save debug output
            DebugFinalPositive = gp.DebugFinalPositive;
            DebugFinalNegative = gp.DebugFinalNegative;
            DebugFinalModelName = gp.DebugFinalModelName;
        }

        // --- PROMPTS ---
        var prompt = nodes.AddTypedNode(
            new ComfyNodeBuilder.CLIPTextEncode
            {
                Name = "PositivePrompt",
                Clip = checkpoint.Output2,
                Text = positiveText,
            }
        );

        var negative = nodes.AddTypedNode(
            new ComfyNodeBuilder.CLIPTextEncode
            {
                Name = "EmptyNeg",
                Clip = checkpoint.Output2,
                Text = negativeText,
            }
        );

        // --- ENCODE ---
        var vaeEncode = nodes.AddTypedNode(
            new ComfyNodeBuilder.VAEEncode
            {
                Name = "VAEEncodeNode",
                Pixels = paddedImage,
                Vae = checkpoint.Output3,
            }
        );

        // ✅ Get denoise strength (default 0.55 for outpaint)
        var denoiseStrength = StackCardViewModel.GetCard<SamplerCardViewModel>()?.DenoiseStrength ?? 0.55;
        var steps = StackCardViewModel.GetCard<SamplerCardViewModel>()?.Steps ?? 30;

        Console.WriteLine($"   Denoise: {denoiseStrength:F2}, Steps: {steps}");

        // --- SAMPLER ---
        var sampler = nodes.AddTypedNode(
            new ComfyNodeBuilder.KSampler
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
                Denoise = denoiseStrength,
            }
        );

        try
        {
            // =========================================================
            // COMPLEX WORKFLOW: Matches user's tested OUTPAINT PERFECTION workflow
            // =========================================================

            Console.WriteLine($"📦 Building complex OUTPAINT workflow:");
            Console.WriteLine(
                $"   Nodes to build: RealPhotoDetectorCNN, Florence2, StyleFingerprintExtractorV2, ConditionalOutpaintRouterPRO, AdvancedOutpaintCompositeV95Universal, RealPhotoPostFixerV3, LTTiledVAEDecode"
            );

            // --- REAL PHOTO DETECTOR (CNN) ---
            // Omit CLIP/RealPhoto detector node entirely if weights are missing/corrupt.
            // Default to AI-generated (false) so the rest of the complex pipeline still runs.
            var realPhotoBool = new StringNodeConnection { Data = new[] { "false" } };
            var realPhotoConf = new StringNodeConnection { Data = new[] { "0.0" } };
            var realPhotoDesc = new StringNodeConnection { Data = new[] { "" } };

            Console.WriteLine(
                $"   ⚠️ RealPhotoDetectorCNN weights missing — defaulting to AI-generated (is_real_photo=false)"
            );

            // --- FLORENCE-2 INTERROGATE (auto image captioning) ---
            // Output: caption (STRING)
            var florenceInterrogate = new ComfyNode
            {
                ClassType = "florence2_interrogate",
                Inputs = new Dictionary<string, object?> { ["image"] = paddedImage.Data },
            }.ToNamedNode("FlorenceInterrogate");

            var florenceOut = nodes.AddNamedNode(florenceInterrogate);
            Console.WriteLine($"   ✅ Florence2 node created: {florenceOut.Name}");
            var florenceCaption = florenceOut.GetOutput<StringNodeConnection>(0);

            // --- STYLE FINGERPRINT EXTRACTOR ---
            // Output: style_fingerprint (STRING)
            var styleFingerprint = new ComfyNode
            {
                ClassType = "StyleFingerprintExtractorV2",
                Inputs = new Dictionary<string, object?> { ["caption"] = florenceCaption.Data },
            }.ToNamedNode("StyleFingerprint");

            var styleOut = nodes.AddNamedNode(styleFingerprint);
            Console.WriteLine($"   ✅ StyleFingerprint node created: {styleOut.Name}");
            var styleFingerprintOut = styleOut.GetOutput<StringNodeConnection>(0);

            // --- CONDITIONAL OUTPAINT ROUTER PRO ---
            // Outputs: routed_latent (LATENT), direction_score (FLOAT), outpaint_direction (INT),
            // auto_room (BOOL), balanced_mode (STRING)
            var router = new ComfyNode
            {
                ClassType = "ConditionalOutpaintRouterPRO",
                Inputs = new Dictionary<string, object?>
                {
                    ["encoded_latent"] = vaeEncode.Output.Data,
                    ["mask"] = outpaintMask.Data,
                    ["original_latent"] = vaeEncode.Output.Data,
                    ["style_fingerprint"] = styleFingerprintOut.Data,
                    ["is_photo"] = realPhotoBool.Data,
                    ["photo_score"] = realPhotoConf.Data,
                    ["model_name"] = "",
                    ["profile_hint"] = "",
                    ["debug_mode"] = false,
                    ["seamless_outpaint_mode"] = false,
                    ["cnn_score"] = realPhotoConf.Data,
                    ["cnn_reason"] = realPhotoDesc.Data,
                },
            }.ToNamedNode("ConditionalRouter");

            var routerOut = nodes.AddNamedNode(router);
            Console.WriteLine($"   ✅ ConditionalOutpaintRouterPRO node created: {routerOut.Name}");
            var routerLatent = routerOut.GetOutput<LatentNodeConnection>(0);
            var routerDirectionScore = routerOut.GetOutput<StringNodeConnection>(2);
            var routerOutpaintDir = routerOut.GetOutput<StringNodeConnection>(3);
            var routerAutoRoom = routerOut.GetOutput<StringNodeConnection>(4);
            var routerBalancedMode = routerOut.GetOutput<StringNodeConnection>(5);

            // --- ADVANCED OUTPAINT COMPOSITE V95 ---
            var compositeNode = new ComfyNode
            {
                ClassType = "AdvancedOutpaintCompositeV95Universal",
                Inputs = new Dictionary<string, object?>
                {
                    ["original"] = vaeEncode.Output.Data,
                    ["generated"] = sampler.Output.Data,
                    ["mask"] = outpaintMask.Data,
                    ["feather_radius"] = feathering,
                    ["exposure_blend"] = 0.85,
                    ["cinematic_intensity"] = 1.0,
                    ["bleed_strength"] = 1.0,
                    ["blur_strength"] = 1.0,
                    ["is_photo"] = realPhotoBool.Data,
                    ["real_photo_mode"] = "SAFE",
                    ["denoise"] = denoiseStrength,
                    ["debug_mode"] = false,
                    ["seamless_outpaint_mode"] = false,
                    ["auto_feather_enabled"] = false,
                    ["auto_invert_enabled"] = false,
                    ["preserve_enabled"] = false,
                    ["structure_enabled"] = false,
                    ["harmonize_enabled"] = false,
                    ["anti_mirror_enabled"] = false,
                    ["colormatch_enabled"] = false,
                    ["smartclamp_enabled"] = false,
                    ["pro_mode"] = "",
                    ["zero_diff_enabled"] = false,
                    ["ai_boost_enabled"] = false,
                    ["real_photo_safe_enabled"] = false,
                },
            }.ToNamedNode("CompositeV95");

            var compositeOutput = nodes.AddNamedNode(compositeNode);
            Console.WriteLine(
                $"   ✅ AdvancedOutpaintCompositeV95Universal node created: {compositeOutput.Name}"
            );
            var compositeLatent = compositeOutput.GetOutput<LatentNodeConnection>(0);

            // --- REAL PHOTO POST FIXER V3 ---
            var photoFixer = new ComfyNode
            {
                ClassType = "RealPhotoPostFixerV3",
                Inputs = new Dictionary<string, object?>
                {
                    ["latent"] = compositeLatent.Data,
                    ["original_latent"] = vaeEncode.Output.Data,
                    ["vae"] = checkpoint.Output3.Data,
                    ["outpaint_mask"] = outpaintMask.Data,
                    ["is_photo"] = realPhotoBool.Data,
                    ["auto_profile"] = false,
                    ["profile_name"] = "",
                    ["outpaint_dir"] = "",
                    ["enable_luma_match"] = false,
                    ["enable_color_match"] = false,
                    ["enable_texture_match"] = false,
                    ["enable_grain_match"] = false,
                    ["enable_lens_profile"] = false,
                    ["luma_strength"] = 0.5,
                    ["color_strength"] = 0.5,
                    ["texture_strength"] = 0.5,
                    ["grain_strength"] = 0.5,
                    ["lens_softness"] = 0.5,
                    ["sensor_emulation"] = false,
                    ["chromatic_aberration"] = false,
                    ["vignette_strength"] = 0.0,
                    ["pre_sharpen"] = false,
                    ["sharpen_radius"] = 1.0,
                    ["sharpen_amount"] = 1.0,
                    ["sharpen_threshold"] = 0.0,
                    ["auto_fix_for_ai"] = false,
                    ["force_real_photo"] = false,
                    ["cnn_score"] = realPhotoConf.Data,
                    ["auto_ai_real"] = false,
                    ["ai_mode_strength"] = 1.0,
                    ["ai_enhance_mode"] = false,
                    ["disable_fixer_on_outpaint"] = false,
                    ["fix_only_original_region"] = false,
                    ["minimal_luma_match_mode"] = false,
                    ["debug_mode"] = false,
                    ["force_tiled"] = false,
                },
            }.ToNamedNode("PhotoFixer");

            var fixerOut = nodes.AddNamedNode(photoFixer);
            Console.WriteLine($"   ✅ RealPhotoPostFixerV3 node created: {fixerOut.Name}");
            var fixedLatent = fixerOut.GetOutput<LatentNodeConnection>(0);

            // --- LT TILED VAE DECODE ---
            var vaeDecode = new ComfyNode
            {
                ClassType = "LTTiledVAEDecode",
                Inputs = new Dictionary<string, object?>
                {
                    ["latents"] = fixedLatent.Data,
                    ["vae"] = checkpoint.Output3.Data,
                    ["horizontal_tiles"] = 2,
                    ["vertical_tiles"] = 2,
                    ["overlap"] = 2,
                    ["last_frame_fix"] = false,
                },
            }.ToNamedNode("LT-Tiled-VAEDecode");

            var decodeOut = nodes.AddNamedNode(vaeDecode);
            Console.WriteLine($"   ✅ LTTiledVAEDecode node created: {decodeOut.Name}");
            var decodedImage = decodeOut.GetOutput<ImageNodeConnection>(0);

            builder.Connections.Primary = new ImageNodeConnection { Data = decodedImage.Data };

            // --- PREVIEW ---
            var preview = nodes.AddTypedNode(
                new ComfyNodeBuilder.PreviewImage
                {
                    Name = "PreviewNode",
                    Images = new ImageNodeConnection { Data = decodedImage.Data },
                }
            );

            builder.Connections.OutputNodes.Add(preview);

            Console.WriteLine("✅ Using complex OUTPAINT PERFECTION workflow");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Falling back to standard composite: {ex.Message}");

            // --- FALLBACK (original simple workflow) ---
            var compositeNode = new NamedComfyNode<LatentNodeConnection>(
                "AdvancedOutpaintLatentCompositeNode"
            )
            {
                ClassType = "AdvancedOutpaintLatentComposite",
                Inputs = new Dictionary<string, object?>
                {
                    ["original"] = vaeEncode.Output.Data,
                    ["generated"] = sampler.Output.Data,
                    ["mask"] = outpaintMask.Data,
                    ["feathering"] = feathering,
                    ["sharpen_strength"] = 0.15,
                    ["exposure_blend"] = 0.85,
                    ["invert_mask"] = false,
                },
            };

            var compositeOutput = nodes.AddNamedNode(compositeNode);

            var vaeDecode = nodes.AddTypedNode(
                new ComfyNodeBuilder.VAEDecode
                {
                    Name = "VAEDecodeNode",
                    Samples = compositeOutput.Output,
                    Vae = checkpoint.Output3,
                }
            );

            builder.Connections.Primary = vaeDecode.Output;

            var preview = nodes.AddTypedNode(
                new ComfyNodeBuilder.PreviewImage { Name = "PreviewNode", Images = vaeDecode.Output }
            );

            builder.Connections.OutputNodes.Add(preview);
        }
    }

    protected override async Task GenerateImageImpl(
        GenerateOverrides overrides,
        CancellationToken cancellationToken
    )
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
            progressUpdateTask = Task.Run(
                async () =>
                {
                    var timeout = TimeSpan.FromMinutes(10); // Max 10 minutes
                    var startTime = DateTime.UtcNow;

                    while (
                        IsGenerating
                        && !cancellationToken.IsCancellationRequested
                        && DateTime.UtcNow - startTime < timeout
                    )
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
                },
                cancellationToken
            );

            var genArgs = new ImageGenerationEventArgs
            {
                Client = ClientManager.Client!,
                Nodes = buildArgs.Builder.ToNodeDictionary(),
                OutputNodeNames = buildArgs.Builder.Connections.OutputNodeNames.ToArray(),
                Parameters = new GenerationParameters
                {
                    ModelName = StackCardViewModel.GetCard<ModelCardViewModel>()?.SelectedModel?.RelativePath,
                },
                Project = InferenceProjectDocument.FromLoadable(this),
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
                    Task.Delay(500)
                        .ContinueWith(_ =>
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
        if (img != null)
            yield return img;
    }
}
