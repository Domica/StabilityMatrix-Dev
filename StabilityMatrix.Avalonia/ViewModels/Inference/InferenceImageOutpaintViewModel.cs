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
        var promptCard = StackCardViewModel.GetCard<PromptCardViewModel>();
        var modelCard = StackCardViewModel.GetCard<ModelCardViewModel>();

        if (selectImageCard?.ImageSource == null)
            return;
        selectImageCard.ApplyStep(args);

        // ✅ Get optimal feathering value (default 10 for outpaint)
        var feathering = outpaintCard?.Feathering ?? 10;

        Console.WriteLine($"🎨 Building outpaint workflow (matching JSON exactly):");
        Console.WriteLine($"   Feathering: {feathering}");
        Console.WriteLine(
            $"   Padding: L={outpaintCard?.ExpandLeft ?? 0} R={outpaintCard?.ExpandRight ?? 0} T={outpaintCard?.ExpandTop ?? 0} B={outpaintCard?.ExpandBottom ?? 0}"
        );

        // Original (un-padded) image — used as pad input and as Florence-2 image source.
        // In the JSON this is node 9 (LoadImage); in C# it is the builder primary image.
        var originalImage = builder.GetPrimaryAsImage();

        // ============================================================
        // 1. CHECKPOINT LOADER — JSON node 4 (CheckpointLoaderSimple)
        //    Outputs: model(0), clip(1), vae(2)
        // ============================================================
        var ckptName = modelCard?.SelectedModel?.RelativePath ?? "";
        var checkpoint = new ComfyNode
        {
            ClassType = "CheckpointLoaderSimple",
            Inputs = new Dictionary<string, object?> { ["ckpt_name"] = ckptName },
        }.ToNamedNode("CkptLoader");
        var checkpointOut = nodes.AddNamedNode(checkpoint);
        var ckptModel = checkpointOut.GetOutput<ModelNodeConnection>(0).Data;
        var ckptClip = checkpointOut.GetOutput<ClipNodeConnection>(1).Data;
        var ckptVae = checkpointOut.GetOutput<VAENodeConnection>(2).Data;

        // ============================================================
        // 2. IMAGE PAD FOR OUTPAINTING — JSON node 188
        //    Outputs: image(0), mask(1), + additional outputs (index 4 = bool for AntiMirror)
        // ============================================================
        var padNode = new ComfyNode
        {
            ClassType = "ImagePadForOutpainting",
            Inputs = new Dictionary<string, object?>
            {
                ["image"] = originalImage.Data,
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
        }.ToNamedNode("OutpaintPadNode");
        var padOut = nodes.AddNamedNode(padNode);
        var paddedImage = padOut.GetOutput<ImageNodeConnection>(0);
        var outpaintMask = padOut.GetOutput<ImageMaskConnection>(1);
        var padOutput4 = padOut.GetOutput<StringNodeConnection>(4).Data; // bool output → AntiMirror is_real_photo

        // ============================================================
        // 3. VAE ENCODE — JSON node 111
        //    Inputs: pixels, vae → Output: latent(0)
        // ============================================================
        var vaeEncodeNode = new ComfyNode
        {
            ClassType = "VAEEncode",
            Inputs = new Dictionary<string, object?> { ["pixels"] = paddedImage.Data, ["vae"] = ckptVae },
        }.ToNamedNode("VAEEncodeNode");
        var vaeEncodeOut = nodes.AddNamedNode(vaeEncodeNode);
        var vaeLatent = vaeEncodeOut.GetOutput<LatentNodeConnection>(0).Data;

        // ============================================================
        // 4. LOAD FLORENCE-2 — JSON node 277 (load_florence2)
        //    No inputs → Outputs: model(0), processor(1)
        // ============================================================
        var florenceLoader = new ComfyNode
        {
            ClassType = "load_florence2",
            Inputs = new Dictionary<string, object?>(),
        }.ToNamedNode("FlorenceLoader");
        var florenceLoaderOut = nodes.AddNamedNode(florenceLoader);
        var florenceModel = florenceLoaderOut.GetOutput<StringNodeConnection>(0).Data;
        var florenceProcessor = florenceLoaderOut.GetOutput<StringNodeConnection>(1).Data;

        // ============================================================
        // 5. NSFW FALCON LOADER — JSON node 280 (nsfw_falcon_loader)
        //    Outputs: nsfw_model(0), nsfw_processor(1)
        // ============================================================
        var nsfwLoader = new ComfyNode
        {
            ClassType = "nsfw_falcon_loader",
            Inputs = new Dictionary<string, object?>
            {
                ["model_repo_or_path"] = "F:/StabilityMatrix/Packages/ComfyUI/models/nsfw_image_detection",
                ["local_only"] = true,
                ["force_cpu"] = false,
                ["use_float16"] = true,
                ["dwpose_model_path"] = "",
                ["seg_model_path"] = "",
            },
        }.ToNamedNode("NsfwLoader");
        var nsfwLoaderOut = nodes.AddNamedNode(nsfwLoader);
        var nsfwModel = nsfwLoaderOut.GetOutput<StringNodeConnection>(0).Data;
        var nsfwProcessor = nsfwLoaderOut.GetOutput<StringNodeConnection>(1).Data;

        // ============================================================
        // 6. FLORENCE-2 NEGATIVE — JSON node 276 (florence2_negative)
        //    Inputs: image, user_suffix → Output: auto_negative(0)
        // ============================================================
        var florenceNegativeNode = new ComfyNode
        {
            ClassType = "florence2_negative",
            Inputs = new Dictionary<string, object?> { ["image"] = originalImage.Data, ["user_suffix"] = "" },
        }.ToNamedNode("FlorenceNegative");
        var florenceNegativeOut = nodes.AddNamedNode(florenceNegativeNode);
        var florenceAutoNegative = florenceNegativeOut.GetOutput<StringNodeConnection>(0).Data;

        // ============================================================
        // 7. PROMPT TEXTS (C# side) — fed into Florence as prefix/negative
        // ============================================================
        var positiveText = promptCard?.PromptDocument.Text ?? "";
        var negativeText = promptCard?.NegativePromptDocument.Text ?? "";

        // === SMART OUTPAINT ASSIST ===
        if (SmartOutpaintAssistEnabled)
        {
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

            DebugFinalPositive = gp.DebugFinalPositive;
            DebugFinalNegative = gp.DebugFinalNegative;
            DebugFinalModelName = gp.DebugFinalModelName;
        }

        // ============================================================
        // 8. FLORENCE-2 INTERROGATE — JSON node 275 (florence2_interrogate)
        //    Inputs: image, model, processor, prefix, suffix, negative, auto_negative, ...
        //    Outputs: positive(0), negative_out(1), ...
        // ============================================================
        var florenceInterrogate = new ComfyNode
        {
            ClassType = "florence2_interrogate",
            Inputs = new Dictionary<string, object?>
            {
                ["image"] = originalImage.Data,
                ["model"] = florenceModel,
                ["processor"] = florenceProcessor,
                ["prefix"] = positiveText,
                ["suffix"] = "",
                ["negative"] = negativeText,
                ["auto_negative"] = florenceAutoNegative,
                ["use_negative"] = true,
                ["detailed_mode"] = true,
                ["very_detailed_mode"] = false,
                ["minimal_mode"] = false,
                ["caption_only"] = false,
                ["prompt_cleaner"] = true,
                ["auto_enrich_prompt"] = false,
                ["max_tokens"] = 512,
                ["nsfw_model"] = nsfwModel,
                ["nsfw_processor"] = nsfwProcessor,
                ["nsfw_ml_threshold"] = 0.72,
                ["nsfw_tags_only"] = false,
                ["ultra_detailed_nsfw"] = false,
                ["fairface_model_path"] =
                    "F:/StabilityMatrix/Packages/ComfyUI/models/fairface/Race-CLS-FairFace_yolov8s.pt",
                ["face_detect_model_path"] = "F:/StabilityMatrix/Packages/ComfyUI/models/yolo-face/model.pt",
                ["pose_model_path"] = "F:/StabilityMatrix/Packages/ComfyUI/models/yolo-pose/yolov8m-pose.pt",
                ["dwpose_model_path"] = "",
                ["seg_model_path"] = "",
            },
        }.ToNamedNode("FlorenceInterrogate");
        var florenceOut = nodes.AddNamedNode(florenceInterrogate);
        Console.WriteLine($"   ✅ Florence2 node created: {florenceOut.Name}");
        var florencePositive = florenceOut.GetOutput<StringNodeConnection>(0).Data;

        // ============================================================
        // 9. CLIP TEXT ENCODE (POSITIVE) — JSON node 70
        //    Inputs: clip, text ← Florence positive → Output: conditioning(0)
        // ============================================================
        var clipPositive = new ComfyNode
        {
            ClassType = "CLIPTextEncode",
            Inputs = new Dictionary<string, object?> { ["clip"] = ckptClip, ["text"] = florencePositive },
        }.ToNamedNode("PositivePrompt");
        var clipPositiveOut = nodes.AddNamedNode(clipPositive);
        var condPositive = clipPositiveOut.GetOutput<ConditioningNodeConnection>(0).Data;

        // ============================================================
        // 10. CLIP TEXT ENCODE (NEGATIVE) — JSON node 6
        //     Inputs: clip, text ← florence2_negative auto_negative → Output: conditioning(0)
        // ============================================================
        var clipNegative = new ComfyNode
        {
            ClassType = "CLIPTextEncode",
            Inputs = new Dictionary<string, object?> { ["clip"] = ckptClip, ["text"] = florenceAutoNegative },
        }.ToNamedNode("NegativePrompt");
        var clipNegativeOut = nodes.AddNamedNode(clipNegative);
        var condNegative = clipNegativeOut.GetOutput<ConditioningNodeConnection>(0).Data;

        // ============================================================
        // 11. REAL PHOTO DETECTOR CNN — JSON node 178 (RealPhotoDetectorCNN)
        //     Inputs: image (original, NOT padded) → Outputs: is_photo(0), score(1), reason(2)
        // ============================================================
        var realPhotoDetector = new ComfyNode
        {
            ClassType = "RealPhotoDetectorCNN",
            Inputs = new Dictionary<string, object?> { ["image"] = originalImage.Data },
        }.ToNamedNode("RealPhotoDetector");
        var realPhotoOut = nodes.AddNamedNode(realPhotoDetector);
        Console.WriteLine($"   ✅ RealPhotoDetectorCNN node created: {realPhotoOut.Name}");
        var realPhotoIsPhoto = realPhotoOut.GetOutput<StringNodeConnection>(0).Data;
        var realPhotoScore = realPhotoOut.GetOutput<StringNodeConnection>(1).Data;
        var realPhotoReason = realPhotoOut.GetOutput<StringNodeConnection>(2).Data;

        // ============================================================
        // 12. STYLE FINGERPRINT EXTRACTOR V2 — JSON node 215
        //     Inputs: latent → Outputs: style_fingerprint(0), fp_*(1..14)
        // ============================================================
        var styleFingerprint = new ComfyNode
        {
            ClassType = "StyleFingerprintExtractorV2",
            Inputs = new Dictionary<string, object?> { ["latent"] = vaeLatent },
        }.ToNamedNode("StyleFingerprint");
        var styleOut = nodes.AddNamedNode(styleFingerprint);
        Console.WriteLine($"   ✅ StyleFingerprint node created: {styleOut.Name}");
        var styleFp = styleOut.GetOutput<StringNodeConnection>(0).Data;
        var styleFpExpMean = styleOut.GetOutput<StringNodeConnection>(1).Data;
        var styleFpExpStd = styleOut.GetOutput<StringNodeConnection>(2).Data;
        var styleFpExpRange = styleOut.GetOutput<StringNodeConnection>(3).Data;
        var styleFpColorEnergy = styleOut.GetOutput<StringNodeConnection>(4).Data;
        var styleFpWbShift = styleOut.GetOutput<StringNodeConnection>(5).Data;
        var styleFpEdgeMean = styleOut.GetOutput<StringNodeConnection>(6).Data;
        var styleFpEdgeStd = styleOut.GetOutput<StringNodeConnection>(7).Data;
        var styleFpNoiseLevel = styleOut.GetOutput<StringNodeConnection>(8).Data;

        // ============================================================
        // 13. CONDITIONAL OUTPAINT ROUTER PRO — JSON node 206
        //     Inputs: encoded_latent, mask, original_latent, is_photo, photo_score,
        //            model_name, profile_hint, debug_mode, seamless_outpaint_mode,
        //            cnn_score, cnn_reason, style_fingerprint, outpaint_mask
        //     Outputs: latent(0), is_photo(1), photo_score(2), profile_name(3),
        //            denoise(4), anti_strength(5), composite_strength(6), debug_info(7),
        //            auto_preserve(8), auto_colormatch(9), auto_blur(10), zero_diff(11),
        //            outpaint_mask(12)
        // ============================================================
        var router = new ComfyNode
        {
            ClassType = "ConditionalOutpaintRouterPRO",
            Inputs = new Dictionary<string, object?>
            {
                ["encoded_latent"] = vaeLatent,
                ["mask"] = outpaintMask.Data,
                ["original_latent"] = vaeLatent,
                ["is_photo"] = realPhotoIsPhoto,
                ["photo_score"] = realPhotoScore,
                ["model_name"] = "sd15",
                ["profile_hint"] = "",
                ["debug_mode"] = false,
                ["seamless_outpaint_mode"] = false,
                ["cnn_score"] = realPhotoScore,
                ["cnn_reason"] = realPhotoReason,
                ["style_fingerprint"] = styleFp,
                ["outpaint_mask"] = outpaintMask.Data,
            },
        }.ToNamedNode("ConditionalRouter");
        var routerOut = nodes.AddNamedNode(router);
        Console.WriteLine($"   ✅ ConditionalOutpaintRouterPRO node created: {routerOut.Name}");
        var routerLatent = routerOut.GetOutput<LatentNodeConnection>(0).Data;
        var routerIsPhoto = routerOut.GetOutput<StringNodeConnection>(1).Data;
        var routerPhotoScore = routerOut.GetOutput<StringNodeConnection>(2).Data;
        var routerProfileName = routerOut.GetOutput<StringNodeConnection>(3).Data;
        var routerDenoise = routerOut.GetOutput<StringNodeConnection>(4).Data;
        var routerAntiStrength = routerOut.GetOutput<StringNodeConnection>(5).Data;
        var routerCompositeStrength = routerOut.GetOutput<StringNodeConnection>(6).Data;
        var routerDebugInfo = routerOut.GetOutput<StringNodeConnection>(7).Data;
        var routerAutoPreserve = routerOut.GetOutput<StringNodeConnection>(8).Data;
        var routerAutoColormatch = routerOut.GetOutput<StringNodeConnection>(9).Data;
        var routerAutoBlur = routerOut.GetOutput<StringNodeConnection>(10).Data;
        var routerZeroDiff = routerOut.GetOutput<StringNodeConnection>(11).Data;
        var routerOutpaintMask = routerOut.GetOutput<StringNodeConnection>(12).Data;

        // ============================================================
        // 14. ANTI MIRROR SAMPLER WRAPPER — JSON node 186
        //     Inputs: latent_image, mask, original_latent, is_real_photo
        //     Outputs: latent(0), outpaint_mask(1), auto_denoise(2), auto_anti_mirror(3),
        //            force_steps(4), profile_name(5), profile_params(6)
        // ============================================================
        var antiMirror = new ComfyNode
        {
            ClassType = "AntiMirrorSamplerWrapper",
            Inputs = new Dictionary<string, object?>
            {
                ["latent_image"] = routerLatent,
                ["mask"] = outpaintMask.Data,
                ["original_latent"] = vaeLatent,
                ["is_real_photo"] = padOutput4,
            },
        }.ToNamedNode("AntiMirror");
        var antiMirrorOut = nodes.AddNamedNode(antiMirror);
        Console.WriteLine($"   ✅ AntiMirrorSamplerWrapper node created: {antiMirrorOut.Name}");
        var antiMirrorLatent = antiMirrorOut.GetOutput<LatentNodeConnection>(0).Data;
        var antiMirrorOutpaintMask = antiMirrorOut.GetOutput<ImageMaskConnection>(1).Data;
        var autoDenoise = antiMirrorOut.GetOutput<StringNodeConnection>(2).Data;
        var autoAntiMirror = antiMirrorOut.GetOutput<StringNodeConnection>(3).Data;
        var forceSteps = antiMirrorOut.GetOutput<StringNodeConnection>(4).Data;

        // ============================================================
        // 15. KSAMPLER — JSON node 136 (KSampler)
        //     Inputs: model, seed, steps, cfg, sampler_name, scheduler,
        //            positive, negative, latent_image, denoise → Output: latent(0)
        //     Uses raw ComfyNode because steps/denoise come from AntiMirror connections.
        // ============================================================
        var seedCard = StackCardViewModel.GetCard<SeedCardViewModel>();
        var seed = (ulong)(seedCard?.Seed ?? 0);

        var sampler = new ComfyNode
        {
            ClassType = "KSampler",
            Inputs = new Dictionary<string, object?>
            {
                ["model"] = ckptModel,
                ["seed"] = seed,
                ["steps"] = forceSteps,
                ["cfg"] = 7.0,
                ["sampler_name"] = "dpmpp_2m",
                ["scheduler"] = "karras",
                ["positive"] = condPositive,
                ["negative"] = condNegative,
                ["latent_image"] = antiMirrorLatent,
                ["denoise"] = autoDenoise,
            },
        }.ToNamedNode("MainSampler");
        var samplerOut = nodes.AddNamedNode(sampler);
        var samplerLatent = samplerOut.GetOutput<LatentNodeConnection>(0).Data;

        // ============================================================
        // 16. ADVANCED OUTPAINT COMPOSITE V95 — JSON node 225
        //     Inputs: original, generated, mask, feather_radius, ...,
        //            is_photo, profile_name, denoise, composite_strength,
        //            zero_diff_enabled, auto_preserve, auto_colormatch, auto_blur,
        //            fp_exposure_mean, ... → Outputs: composited(0), outpaint_mask(1)
        // ============================================================
        var composite = new ComfyNode
        {
            ClassType = "AdvancedOutpaintCompositeV95Universal",
            Inputs = new Dictionary<string, object?>
            {
                ["original"] = vaeLatent,
                ["generated"] = samplerLatent,
                ["mask"] = outpaintMask.Data,
                ["feather_radius"] = feathering,
                ["mask_boost"] = 1.0,
                ["preserve_original"] = 0.0,
                ["structure_weight"] = 0.3,
                ["exposure_blend"] = 0.5,
                ["anti_mirror_strength"] = 0.0,
                ["auto_feather_enabled"] = false,
                ["auto_invert_enabled"] = true,
                ["preserve_enabled"] = false,
                ["structure_enabled"] = true,
                ["harmonize_enabled"] = false,
                ["anti_mirror_enabled"] = false,
                ["colormatch_enabled"] = false,
                ["colormatch_strength"] = 1.0,
                ["cinematic_intensity"] = 1.0,
                ["bleed_strength"] = 1.0,
                ["blur_strength"] = 1.0,
                ["smartclamp_enabled"] = true,
                ["smartclamp_strength"] = 2.0,
                ["pro_mode"] = "NORMAL",
                ["zero_diff_enabled"] = routerZeroDiff,
                ["is_photo"] = routerIsPhoto,
                ["profile_name"] = routerProfileName,
                ["denoise"] = routerDenoise,
                ["composite_strength"] = routerCompositeStrength,
                ["debug_mode"] = true,
                ["seamless_outpaint_mode"] = false,
                ["ultra_analysis"] = false,
                ["real_photo_mode"] = "SAFE",
                ["auto_preserve"] = routerAutoPreserve,
                ["auto_colormatch"] = routerAutoColormatch,
                ["auto_blur"] = routerAutoBlur,
                ["fp_exposure_mean"] = styleFpExpMean,
                ["fp_exposure_std"] = styleFpExpStd,
                ["fp_exposure_range"] = styleFpExpRange,
                ["fp_color_energy"] = styleFpColorEnergy,
                ["fp_wb_shift"] = styleFpWbShift,
                ["fp_edge_mean"] = styleFpEdgeMean,
                ["fp_edge_std"] = styleFpEdgeStd,
                ["fp_noise_level"] = styleFpNoiseLevel,
                ["limb"] = 0.0,
                ["entropy"] = 0.0,
                ["ai_boost_enabled"] = false,
                ["real_photo_safe_enabled"] = true,
            },
        }.ToNamedNode("CompositeV95");
        var compositeOut = nodes.AddNamedNode(composite);
        Console.WriteLine($"   ✅ AdvancedOutpaintCompositeV95Universal node created: {compositeOut.Name}");
        var compositeLatent = compositeOut.GetOutput<LatentNodeConnection>(0).Data;
        var compositeMask = compositeOut.GetOutput<ImageMaskConnection>(1).Data;

        // ============================================================
        // 17. REAL PHOTO POST FIXER V3 — JSON node 223
        //     Inputs: latent, original_latent, vae, is_photo, auto_profile,
        //            ..., cnn_score, ..., outpaint_mask → Output: fixed_latent(0)
        // ============================================================
        var photoFixer = new ComfyNode
        {
            ClassType = "RealPhotoPostFixerV3",
            Inputs = new Dictionary<string, object?>
            {
                ["latent"] = compositeLatent,
                ["original_latent"] = vaeLatent,
                ["vae"] = ckptVae,
                ["is_photo"] = routerIsPhoto,
                ["auto_profile"] = true,
                ["profile_name"] = "",
                ["outpaint_dir"] = "",
                ["enable_luma_match"] = true,
                ["enable_color_match"] = true,
                ["enable_texture_match"] = true,
                ["enable_grain_match"] = true,
                ["enable_lens_profile"] = true,
                ["luma_strength"] = 0.20,
                ["color_strength"] = 0.18,
                ["texture_strength"] = 0.10,
                ["grain_strength"] = 0.08,
                ["lens_softness"] = 0.06,
                ["sensor_emulation"] = true,
                ["chromatic_aberration"] = true,
                ["vignette_strength"] = 0.06,
                ["pre_sharpen"] = true,
                ["sharpen_radius"] = 0.8,
                ["sharpen_amount"] = 0.4,
                ["sharpen_threshold"] = 0.0,
                ["auto_fix_for_ai"] = true,
                ["force_real_photo"] = false,
                ["cnn_score"] = realPhotoScore,
                ["auto_ai_real"] = true,
                ["ai_mode_strength"] = 0.6,
                ["ai_enhance_mode"] = false,
                ["disable_fixer_on_outpaint"] = true,
                ["fix_only_original_region"] = false,
                ["minimal_luma_match_mode"] = false,
                ["outpaint_mask"] = compositeMask,
                ["debug_mode"] = true,
                ["force_tiled"] = false,
            },
        }.ToNamedNode("PhotoFixer");
        var fixerOut = nodes.AddNamedNode(photoFixer);
        Console.WriteLine($"   ✅ RealPhotoPostFixerV3 node created: {fixerOut.Name}");
        var fixedLatent = fixerOut.GetOutput<LatentNodeConnection>(0).Data;

        // ============================================================
        // 18. LT TILED VAE DECODE — JSON node 224 (LTTiledVAEDecode)
        //     Inputs: latents, vae, horizontal_tiles, vertical_tiles, overlap, last_frame_fix
        //     Output: image(0)
        // ============================================================
        var vaeDecode = new ComfyNode
        {
            ClassType = "LTTiledVAEDecode",
            Inputs = new Dictionary<string, object?>
            {
                ["latents"] = fixedLatent,
                ["vae"] = ckptVae,
                ["horizontal_tiles"] = 2,
                ["vertical_tiles"] = 2,
                ["overlap"] = 2,
                ["last_frame_fix"] = false,
            },
        }.ToNamedNode("LT-Tiled-VAEDecode");
        var decodeOut = nodes.AddNamedNode(vaeDecode);
        Console.WriteLine($"   ✅ LTTiledVAEDecode node created: {decodeOut.Name}");
        var decodedImage = decodeOut.GetOutput<ImageNodeConnection>(0);

        builder.Connections.Primary = decodedImage;

        // ============================================================
        // 19. PREVIEW — final output image
        // ============================================================
        var preview = nodes.AddTypedNode(
            new ComfyNodeBuilder.PreviewImage { Name = "PreviewNode", Images = decodedImage }
        );

        builder.Connections.OutputNodes.Add(preview);

        Console.WriteLine("✅ Using OUTPAINT PERFECTION workflow matching JSON exactly");
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
