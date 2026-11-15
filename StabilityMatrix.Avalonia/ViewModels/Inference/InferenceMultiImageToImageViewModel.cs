using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using NLog;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Extensions;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Inference.Modules;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Inference;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Inference;
using StabilityMatrix.Core.Services;
using InferenceMultiImageToImageView = StabilityMatrix.Avalonia.Views.Inference.InferenceMultiImageToImageView;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

/// <summary>
/// Multi Image to Image: load 1-10 reference images, write a single prompt
/// describing what should happen with them, and produce one shared image
/// (for example a group photo composed from several reference subjects).
///
/// Default path (context-aware): a short caption-only ComfyUI pass reads every
/// reference with Florence-2. The captions come back to C#, where
/// <see cref="ReferenceSceneComposer"/> strips their original backgrounds /
/// locations / poses and rewrites the remaining appearance descriptors into
/// the scene described by the user's prompt. Z-Image then runs its normal
/// text-to-image graph with that single composed prompt.
///
/// If the caption pass cannot run for any reason (missing node, ComfyUI error,
/// no text output), generation automatically falls back to the older in-graph
/// <see cref="ReferenceCaptionFusion"/> path, so the screen never stops working.
///
/// Important: Z-Image-Turbo has NO image-conditioning / identity weights (the
/// checkpoint contains no siglip, ref_latent, omni or clip_vision tensors).
/// Plain latent img2img works for one image via VAEEncode + low denoise, but it
/// cannot fuse N identities. References therefore influence the result through
/// their descriptions, not by cloning exact faces. Exact identity requires a
/// real multi-image edit model supplied as a custom workflow JSON
/// (see <see cref="MultiReferenceWorkflowInjector"/>).
/// </summary>
[View(typeof(InferenceMultiImageToImageView), IsPersistent = true)]
[RegisterScoped<InferenceMultiImageToImageViewModel>, ManagedService]
public partial class InferenceMultiImageToImageViewModel : InferenceTextToImageViewModel
{
    /// <summary>Maximum number of reference images. Change here to raise the cap.</summary>
    public const int MaxReferenceImages = 10;

    private static readonly Logger MultiRefLogger = LogManager.GetCurrentClassLogger();

    private readonly INotificationService notifications;

    /// <summary>
    /// Florence-2 descriptions cached by absolute path. The detail level is part
    /// of the key state: switching it invalidates the cache so the next Generate
    /// re-reads the images at the requested depth. Re-generating with a different
    /// prompt costs nothing extra — only the C# composition is redone.
    /// </summary>
    private readonly Dictionary<string, CachedCaption> captionCache = new();

    private sealed record CachedCaption(
        long LastWriteUtcTicks,
        CaptionDetailLevel DetailLevel,
        string Text,
        string Display
    );

    private static readonly Regex CaptionWhitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex CaptionSpecialToken = new(
        @"</?(?:s|pad|unk)>|<loc_\d+>|<[^>]{1,24}>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex RegionLocationTokens = new(
        @"(?:<loc_\d+>){1,}",
        RegexOptions.Compiled
    );

    private static FilePickerFileType SupportedImages { get; } =
        new("Supported Images")
        {
            Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"],
        };

    /// <summary>Character references (people), in display order. Each item carries an optional per-image note.</summary>
    [JsonIgnore]
    public ObservableCollection<ReferenceItem> ReferenceImagePaths { get; } = new();

    /// <summary>
    /// Optional single reference that defines the SCENE / environment / background
    /// (a room, a street, a beach). Its Florence-2 caption becomes the scene; the
    /// other references are treated as the people. The user's prompt then controls
    /// the arrangement ("all people sitting on the sofa").
    /// </summary>
    [JsonIgnore]
    public string? EnvironmentReferencePath
    {
        get => environmentReferencePath;
        set
        {
            if (!string.Equals(environmentReferencePath, value, StringComparison.Ordinal))
            {
                environmentReferencePath = value;
                OnPropertyChanged();
                // The scene source changed, so the composed prompt must be rebuilt.
                ComposedPrompt = null;
                CaptionsText = null;
                ContextSummary = null;
            }
        }
    }

    private string? environmentReferencePath;

    [ObservableProperty]
    private string? environmentNote;

    /// <summary>
    /// Optional path to a ComfyUI API-format workflow JSON. When empty the
    /// built-in Z-Image image-to-image graph is used instead.
    /// </summary>
    [JsonIgnore]
    public string WorkflowPath { get; set; } = string.Empty;

    [ObservableProperty]
    private int referenceImageCount;

    [ObservableProperty]
    private ReferenceItem? selectedReferenceItem;

    /// <summary>
    /// When on (default), captions are read back into C# and composed with
    /// <see cref="ReferenceSceneComposer"/>. Off restores the original fast
    /// in-graph concatenation for troubleshooting.
    /// </summary>
    [ObservableProperty]
    private bool useContextualFusion = true;

    /// <summary>The exact final prompt that will be sent to CLIPTextEncode.</summary>
    [ObservableProperty]
    private string? composedPrompt;

    /// <summary>Short explanation of how the prompt context was interpreted.</summary>
    [ObservableProperty]
    private string? contextSummary;

    /// <summary>Raw Florence-2 captions, one per line, for inspection.</summary>
    [ObservableProperty]
    private string? captionsText;

    [ObservableProperty]
    private bool isReadingCaptions;

    /// <summary>
    /// ComboBox index for how thoroughly Florence-2 describes each reference.
    /// 0 = Standard (one short caption), 1 = Rich (paragraph + key details),
    /// 2 = Maximum (also detected regions). Rich is the default.
    /// </summary>
    [ObservableProperty]
    private int captionDetailIndex = 1;

    /// <summary>
    /// ComboBox index for how many images / panels to produce. 0 = one output,
    /// 5 = six outputs. Combined with <see cref="ArrangeAsGrid"/> this means
    /// either N separate images or N panels inside one image.
    /// </summary>
    [ObservableProperty]
    private int outputPanelIndex;

    /// <summary>
    /// When false (default) each output is one complete, undivided scene — the
    /// requested count becomes the number of generated images. When true, one
    /// image is generated containing exactly that many clean panels.
    /// </summary>
    [ObservableProperty]
    private bool arrangeAsGrid;

    /// <summary>Number of images / panels requested, always 1-6.</summary>
    public int OutputPanelCount => Math.Clamp(OutputPanelIndex + 1, 1, 6);

    /// <summary>Panel count handed to the prompt composer (1 when generating separate images).</summary>
    private int PanelCountForPrompt => ArrangeAsGrid ? OutputPanelCount : 1;

    /// <summary>Number of independent generations to run.</summary>
    private int BatchCountForGeneration => ArrangeAsGrid ? 1 : OutputPanelCount;

    private string OutputModeLabel => ArrangeAsGrid
        ? $"grid of {OutputPanelCount} panels in one image"
        : $"{OutputPanelCount} separate image(s)";

    /// <summary>Florence-2 pass configuration for the currently selected detail level.</summary>
    private CaptionDetailLevel DetailLevel => CaptionDetailIndex switch
    {
        0 => CaptionDetailLevel.Standard,
        2 => CaptionDetailLevel.Maximum,
        _ => CaptionDetailLevel.Rich,
    };

    /// <summary>How much appearance detail from each description reaches the composed prompt.</summary>
    private int SubjectDetailBudget => CaptionDetailIndex switch
    {
        0 => 120,
        2 => 320,
        _ => 220,
    };

    private string DetailLabel => CaptionDetailIndex switch
    {
        0 => "standard (1 pass)",
        2 => "maximum (3 passes)",
        _ => "rich (2 passes)",
    };

    partial void OnComposedPromptChanged(string? value) => OnPropertyChanged(nameof(FusionModeText));

    partial void OnUseContextualFusionChanged(bool value) => OnPropertyChanged(nameof(FusionModeText));

    partial void OnCaptionDetailIndexChanged(int value)
    {
        captionCache.Clear();
        ComposedPrompt = null;
        CaptionsText = null;
        ContextSummary = "Detail level changed — descriptions will be re-read on the next Generate.";
        OnPropertyChanged(nameof(FusionModeText));
    }

    partial void OnOutputPanelIndexChanged(int value)
    {
        ComposedPrompt = null;
        OnPropertyChanged(nameof(OutputPanelCount));
        OnPropertyChanged(nameof(FusionModeText));
    }

    partial void OnArrangeAsGridChanged(bool value)
    {
        ComposedPrompt = null;
        OnPropertyChanged(nameof(FusionModeText));
    }

    /// <summary>Friendly label shown above the reference list.</summary>
    public string ReferenceCountText => $"{ReferenceImagePaths.Count} / {MaxReferenceImages}";

    public bool HasWorkflow => !string.IsNullOrWhiteSpace(WorkflowPath) && File.Exists(WorkflowPath);

    /// <summary>Shown in the UI so it is obvious which fusion path will run.</summary>
    public string FusionModeText => HasWorkflow
        ? "Custom workflow active — the built-in context composer is not used."
        : UseContextualFusion
            ? ComposedPrompt is { Length: > 0 }
                ? "Context-aware fusion ready: the descriptions were rewritten into your scene. The exact prompt sent is shown below."
                : "Context-aware fusion ON: on Generate, Florence-2 descriptions are read back, filtered to appearance only, and rewritten into your scene."
            : "Context-aware fusion OFF: captions are joined with your prompt inside ComfyUI (fast, but original backgrounds stay in the text).";

    /// <summary>Shown in the UI so it is obvious which graph will run.</summary>
    public string GraphModeText => HasWorkflow
        ? "Custom workflow JSON — use a multi-image edit model here for exact identity."
        : "Built-in: Florence-2 reads every reference image, keeps only the people's appearance, and rewrites it into the scene from your prompt. Z-Image then generates one image. Z-Image-Turbo has no identity conditioning, so faces are described rather than cloned — for exact faces use a custom edit workflow.";

    public InferenceMultiImageToImageViewModel(
        INotificationService notificationService,
        IInferenceClientManager inferenceClientManager,
        ISettingsManager settingsManager,
        IServiceManager<ViewModelBase> vmFactory,
        IModelIndexService modelIndexService,
        RunningPackageService runningPackageService,
        TabContext tabContext
    )
        : base(
            notificationService,
            inferenceClientManager,
            settingsManager,
            vmFactory,
            modelIndexService,
            runningPackageService,
            tabContext
        )
    {
        notifications = notificationService;
        ReferenceImagePaths.CollectionChanged += OnReferenceImagesChanged;

        // The built-in graph is pure text-to-image (empty latent). Denoise must
        // stay at 1.0; anything lower would start from a meaningless latent.
        SamplerCardViewModel.IsDenoiseStrengthEnabled = false;

        // The 1-6 output selector on this screen is authoritative: exactly one
        // image per generation, with the count driven by OutputPanelCount. Keep
        // the generic Batch Size card from silently multiplying the results.
        BatchSizeCardViewModel.BatchSize = 1;
    }

    private void OnReferenceImagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ReferenceImageCount = ReferenceImagePaths.Count;
        OnPropertyChanged(nameof(ReferenceCountText));
        AddReferenceImagesCommand.NotifyCanExecuteChanged();
        ClearReferenceImagesCommand.NotifyCanExecuteChanged();

        // The composed prompt belongs to the previous reference set. Clearing it
        // also makes FusionModeText accurately say "will read on Generate".
        ComposedPrompt = null;
        CaptionsText = null;
        ContextSummary = null;
    }

    /// <summary>
    /// Forces Florence-2 to re-read the references on the next generation.
    /// Useful after editing an image on disk without changing its path.
    /// </summary>
    [RelayCommand]
    private void RefreshCaptions()
    {
        captionCache.Clear();
        ComposedPrompt = null;
        CaptionsText = null;
        ContextSummary = "Descriptions will be re-read on the next Generate.";
    }

    private bool CanAddReferenceImages() => ReferenceImagePaths.Count < MaxReferenceImages;

    private bool HasReferenceImages() => ReferenceImagePaths.Count > 0;

    [RelayCommand(CanExecute = nameof(CanAddReferenceImages))]
    private async Task AddReferenceImagesAsync()
    {
        var files = await App.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                AllowMultiple = true,
                Title = "Select reference images",
                FileTypeFilter = [FilePickerFileTypes.ImagePng, FilePickerFileTypes.ImageJpg, SupportedImages],
            }
        );

        foreach (var path in files.Select(f => f.TryGetLocalPath()).OfType<string>())
        {
            if (ReferenceImagePaths.Count >= MaxReferenceImages)
            {
                break;
            }

            if (!ReferenceImagePaths.Any(item => item.Path == path))
            {
                ReferenceImagePaths.Add(new ReferenceItem(path));
            }
        }
    }

    [RelayCommand]
    private void RemoveSelectedReference()
    {
        if (SelectedReferenceItem is { } item)
        {
            var index = ReferenceImagePaths.IndexOf(item);
            ReferenceImagePaths.Remove(item);
            SelectedReferenceItem = index < ReferenceImagePaths.Count
                ? ReferenceImagePaths.ElementAtOrDefault(index)
                : ReferenceImagePaths.LastOrDefault();
        }
    }

    [RelayCommand(CanExecute = nameof(HasReferenceImages))]
    private void ClearReferenceImages() => ReferenceImagePaths.Clear();

    [RelayCommand]
    private void MoveSelectedReferenceUp()
    {
        var index = SelectedReferenceItem is null ? -1 : ReferenceImagePaths.IndexOf(SelectedReferenceItem);
        if (index > 0)
        {
            ReferenceImagePaths.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveSelectedReferenceDown()
    {
        var index = SelectedReferenceItem is null ? -1 : ReferenceImagePaths.IndexOf(SelectedReferenceItem);
        if (index >= 0 && index < ReferenceImagePaths.Count - 1)
        {
            ReferenceImagePaths.Move(index, index + 1);
        }
    }

    [RelayCommand]
    private async Task SelectWorkflowAsync()
    {
        var files = await App.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Select ComfyUI API workflow JSON",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("JSON workflow") { Patterns = ["*.json"] },
                ],
            }
        );

        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            WorkflowPath = path;
            NotifyWorkflowChanged();
        }
    }

    [RelayCommand]
    private void ClearWorkflow()
    {
        WorkflowPath = string.Empty;
        NotifyWorkflowChanged();
    }

    [RelayCommand]
    private async Task SetEnvironmentReferenceAsync()
    {
        var files = await App.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Select environment / scene image",
                FileTypeFilter =
                [
                    FilePickerFileTypes.ImagePng, FilePickerFileTypes.ImageJpg, SupportedImages,
                ],
            }
        );

        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            EnvironmentReferencePath = path;
        }
    }

    [RelayCommand]
    private void ClearEnvironmentReference()
    {
        EnvironmentReferencePath = null;
    }

    partial void OnEnvironmentNoteChanged(string? value) => ComposedPrompt = null;

    private void NotifyWorkflowChanged()
    {
        OnPropertyChanged(nameof(HasWorkflow));
        OnPropertyChanged(nameof(WorkflowPath));
        OnPropertyChanged(nameof(GraphModeText));
        OnPropertyChanged(nameof(FusionModeText));
    }

    /// <inheritdoc />
    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        var paths = ReferenceImagePaths
            .Select(item => item.Path)
            .Where(File.Exists)
            .Distinct()
            .Take(MaxReferenceImages)
            .ToList();

        if (paths.Count == 0)
        {
            throw new ValidationException("Add at least one reference image before generating.");
        }

        var seed = args.SeedOverride switch
        {
            { } seedOverride => Convert.ToUInt64(seedOverride),
            _ => Convert.ToUInt64(SeedCardViewModel.Seed),
        };

        args.Builder.Connections.Seed = seed;

        if (HasWorkflow)
        {
            BuildFromCustomWorkflow(args, paths, seed);
        }
        else
        {
            BuildFromReferenceCaptions(args, paths);
        }
    }

    /// <summary>
    /// Default path: upload every reference, describe it with the local
    /// Florence-2 VLM, and feed the fused runtime prompt into the standard
    /// Z-Image text-to-image graph. One prompt, one output image.
    /// </summary>
    private void BuildFromReferenceCaptions(BuildPromptEventArgs args, IReadOnlyList<string> paths)
    {
        if (ComposedPrompt is { Length: > 0 })
        {
            // Context-aware path: the caption pass already ran and C# composed
            // the final text. No Florence-2 nodes (and no uploads) are needed in
            // the generation graph at all, which also makes it faster.
            args.Builder.Connections.RuntimePositivePrompt = ComposedPrompt;
        }
        else
        {
            // Fallback path: the proven in-graph fusion. If caption read-back
            // fails for any reason, the screen still generates exactly as before.
            var relativePaths = new List<string>(paths.Count);

            for (var index = 0; index < paths.Count; index++)
            {
                var extension = Path.GetExtension(paths[index]);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".png";
                }

                var relativePath = $"sm_multi_ref/{Guid.NewGuid():N}_{index}{extension}";
                args.FilesToTransfer.Add((paths[index], "input/" + relativePath));
                relativePaths.Add(relativePath);
            }

            var fusedPrompt = ReferenceCaptionFusion.Build(
                args.Builder,
                relativePaths,
                PromptCardViewModel.PromptDocument.Text ?? string.Empty,
                PanelCountForPrompt
            );

            args.Builder.Connections.RuntimePositivePrompt = fusedPrompt;
        }

        // PromptCardViewModel uses RuntimePositivePrompt for CLIPTextEncode,
        // while still parsing LoRAs / styles from the static prompt text.

        // This graph starts from an EMPTY latent, so denoise must be 1.0.
        // A saved project created while the screen was still img2img can restore
        // IsDenoiseStrengthEnabled = true / DenoiseStrength = 0.7, which would
        // give the sampler only ~9 of 13 steps and leave a half-formed image.
        SamplerCardViewModel.IsDenoiseStrengthEnabled = false;
        SamplerCardViewModel.DenoiseStrength = 1.0d;

        // Plain Z-Image text-to-image. No collage latent: a distilled 8-step
        // model cannot reconstruct or interpret a grid of reference faces.
        base.BuildPrompt(args);
    }

    /// <summary>
    /// Runs a caption-only ComfyUI pass, reads the captions back, caches them by
    /// file, and composes the final context-aware prompt in C#. Any failure
    /// returns <c>null</c> so generation silently falls back to the proven
    /// in-graph fusion instead of breaking.
    /// </summary>
    private async Task<ReferenceSceneComposer.Composition?> TryPrepareContextualPromptAsync(
        ComfyClient client,
        IReadOnlyList<ReferenceItem> characterItems,
        CancellationToken cancellationToken
    )
    {
        if (characterItems.Count == 0)
        {
            return null;
        }

        try
        {
            IsReadingCaptions = true;

            // PreviewAny is ComfyUI core; the other two come from the standard
            // comfyui-florence2 pack. Check before queuing so a missing pack
            // degrades gracefully rather than failing the generation.
            if (!await client.HasNodeTypeAsync("PreviewAny", cancellationToken)
                || !await client.HasNodeTypeAsync("Florence2Run", cancellationToken)
                || !await client.HasNodeTypeAsync("Florence2ModelLoader", cancellationToken))
            {
                MultiRefLogger.Warn(
                    "MultiRef: caption read-back nodes unavailable, falling back to in-graph fusion"
                );
                return null;
            }

            // The optional environment reference (the scene) is read first, then
            // every character reference. The environment caption becomes the scene;
            // the character captions become the people. Per-image notes travel with
            // each item and are appended to the description so Z-Image knows what to
            // align with what (e.g. "the groom", "on the left").
            var allPaths = new List<string>(characterItems.Count + 1);
            var isEnv = new List<bool>(characterItems.Count + 1);
            if (!string.IsNullOrWhiteSpace(EnvironmentReferencePath)
                && File.Exists(EnvironmentReferencePath))
            {
                allPaths.Add(EnvironmentReferencePath);
                isEnv.Add(true);
            }

            var characterPaths = new List<string>(characterItems.Count);
            var characterNotes = new List<string?>(characterItems.Count);
            foreach (var item in characterItems)
            {
                allPaths.Add(item.Path);
                isEnv.Add(false);
                characterPaths.Add(item.Path);
                characterNotes.Add(item.Note);
            }

            var captions = new string?[allPaths.Count];
            var displays = new string?[allPaths.Count];
            var stamps = new long[allPaths.Count];
            var uncached = new List<(int Index, string RelativePath)>();
            var transfers = new List<(string SourcePath, string DestinationRelativePath)>();

            for (var index = 0; index < allPaths.Count; index++)
            {
                var stamp = File.GetLastWriteTimeUtc(allPaths[index]).Ticks;
                stamps[index] = stamp;

                if (captionCache.TryGetValue(allPaths[index], out var entry)
                    && entry.LastWriteUtcTicks == stamp
                    && entry.DetailLevel == DetailLevel
                    && !string.IsNullOrWhiteSpace(entry.Text))
                {
                    captions[index] = entry.Text;
                    displays[index] = entry.Display;
                    continue;
                }

                var extension = Path.GetExtension(allPaths[index]);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".png";
                }

                var relativePath = $"sm_multi_ref/{Guid.NewGuid():N}_{index}{extension}";
                transfers.Add((allPaths[index], "input/" + relativePath));
                uncached.Add((index, relativePath));
            }

            if (uncached.Count > 0)
            {
                await UploadPromptFiles(transfers, client);

                var (nodes, readbacks) = ReferenceCaptionFusion.BuildCaptionGraph(
                    uncached.Select(item => item.RelativePath).ToList(),
                    DetailLevel
                );

                var promptTask = await client.QueuePromptAsync(nodes, cancellationToken);
                await promptTask.Task.WaitAsync(cancellationToken);

                var texts = await client.GetTextOutputsForExecutedPromptAsync(
                    promptTask.Id,
                    cancellationToken
                );

                for (var i = 0; i < uncached.Count; i++)
                {
                    // Each image can produce several complementary descriptions.
                    // Missing optional passes are skipped; only a total failure
                    // falls back to the in-graph fusion path.
                    var parts = new List<(string Label, string Text)>();
                    foreach (var part in readbacks[i].Parts)
                    {
                        if (!texts.TryGetValue(part.NodeName, out var lines) || lines.Count == 0)
                        {
                            MultiRefLogger.Warn(
                                "MultiRef: no text from caption node {Node}, skipping this pass",
                                part.NodeName
                            );
                            continue;
                        }

                        var raw = string
                            .Join(" ", lines.Where(line => !string.IsNullOrWhiteSpace(line)))
                            .Trim();
                        var text = part.Label == "Detected regions"
                            ? CleanRegionCaption(raw)
                            : NormalizeCaption(raw);

                        if (text.Length > 0)
                        {
                            parts.Add((part.Label, text));
                        }
                    }

                    if (parts.Count == 0)
                    {
                        MultiRefLogger.Warn(
                            "MultiRef: no descriptions returned for reference {Index}, falling back to in-graph fusion",
                            i
                        );
                        return null;
                    }

                    var block = BuildCaptionBlock(parts);
                    var originalIndex = uncached[i].Index;
                    captions[originalIndex] = block.Text;
                    displays[originalIndex] = block.Display;
                    captionCache[allPaths[originalIndex]] = new CachedCaption(
                        stamps[originalIndex],
                        DetailLevel,
                        block.Text,
                        block.Display
                    );
                }
            }

            var orderedCaptions = captions.Select(c => c ?? string.Empty).ToList();

            var environmentCaption = isEnv.Count > 0 && isEnv[0] ? orderedCaptions[0] : null;
            var characterCaptions = isEnv.Count > 0 && isEnv[0]
                ? orderedCaptions.Skip(1).ToList()
                : orderedCaptions;

            var composition = ReferenceSceneComposer.Compose(
                PromptCardViewModel.PromptDocument.Text ?? string.Empty,
                characterCaptions,
                environmentCaption,
                EnvironmentNote,
                characterNotes,
                SubjectDetailBudget,
                PanelCountForPrompt
            );

            var captionLines = new List<string>();
            for (var index = 0; index < allPaths.Count; index++)
            {
                var offset = isEnv[0] ? 1 : 0;
                var label = isEnv[index]
                    ? "Scene / environment"
                    : $"Character {index - offset + 1}";
                captionLines.Add($"{label} — {Path.GetFileName(allPaths[index])}");
                captionLines.Add(
                    string.IsNullOrWhiteSpace(displays[index])
                        ? $"    {orderedCaptions[index]}"
                        : displays[index]!
                );
                captionLines.Add(string.Empty);
            }

            CaptionsText = string.Join(Environment.NewLine, captionLines).TrimEnd();
            ContextSummary =
                $"{composition.ContextSummary} · descriptions: {DetailLabel} · output: {OutputModeLabel}";

            return composition;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            MultiRefLogger.Warn(ex, "MultiRef: contextual caption pass failed, using in-graph fusion");
            return null;
        }
        finally
        {
            IsReadingCaptions = false;
        }
    }

    /// <summary>Strips Florence-2 special tokens and normalizes whitespace.</summary>
    private static string NormalizeCaption(string raw)
    {
        var text = CaptionSpecialToken.Replace(raw, " ");
        return CaptionWhitespace.Replace(text, " ").Trim().Trim('.', ' ');
    }

    /// <summary>
    /// Converts a dense-region-caption string (region labels interleaved with
    /// &lt;loc_*&gt; coordinates) into a plain, de-duplicated detail list.
    /// </summary>
    private static string CleanRegionCaption(string raw)
    {
        var text = RegionLocationTokens.Replace(raw, "; ");
        text = CaptionSpecialToken.Replace(text, " ");

        var phrases = text
            .Split([';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(phrase => CaptionWhitespace.Replace(phrase, " ").Trim().Trim('.', ' '))
            .Where(phrase => phrase.Length > 8 && phrase.Contains(' '))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = string.Join(", ", phrases);
        return result.Length > 500 ? result[..500].TrimEnd(' ', ',') : result;
    }

    /// <summary>
    /// Merges the complementary descriptions of one image into a single text for
    /// the composer and a labelled multi-line block for the descriptions panel.
    /// A later part is dropped when it is already contained in an earlier one
    /// (the short caption is usually a subset of the full paragraph).
    /// </summary>
    private static (string Text, string Display) BuildCaptionBlock(
        IReadOnlyList<(string Label, string Text)> parts
    )
    {
        var kept = new List<(string Label, string Text)>();

        foreach (var part in parts)
        {
            var needle = part.Text.ToLowerInvariant();
            var redundant = kept.Any(existing =>
                existing.Text.Length >= part.Text.Length
                && existing.Text.ToLowerInvariant().Contains(needle, StringComparison.Ordinal)
            );

            if (!redundant)
            {
                kept.Add(part);
            }
        }

        var display = string.Join(
            Environment.NewLine,
            kept.Select(part => $"    {part.Label}: {part.Text}")
        );

        var text = string.Join("; ", kept.Select(part => part.Text));
        if (text.Length > 1400)
        {
            text = text[..1400].TrimEnd(' ', ';', ',');
        }

        return (text, display);
    }

    /// <summary>Optional path: inject references into a user-supplied API workflow.</summary>
    private void BuildFromCustomWorkflow(
        BuildPromptEventArgs args,
        IReadOnlyList<string> paths,
        ulong seed
    )
    {
        var result = MultiReferenceWorkflowInjector.Inject(
            args.Builder.Nodes,
            WorkflowPath,
            paths,
            PromptCardViewModel.PromptDocument.Text ?? string.Empty,
            PromptCardViewModel.NegativePromptDocument.Text ?? string.Empty,
            seed,
            SamplerCardViewModel.Width,
            SamplerCardViewModel.Height
        );

        foreach (var transfer in result.FileTransfers)
        {
            args.FilesToTransfer.Add(transfer);
        }

        foreach (var outputNode in result.OutputNodes)
        {
            args.Builder.Connections.OutputNodes.Add(outputNode);
        }
    }

    /// <inheritdoc />
    protected override async Task GenerateImageImpl(
        GenerateOverrides overrides,
        CancellationToken cancellationToken
    )
    {
        // With the built-in graph the real prompt is assembled inside the graph
        // from the reference captions, so an empty static prompt is valid there.
        if (HasWorkflow && !await PromptCardViewModel.ValidatePrompts())
        {
            return;
        }

        // The built-in graph uses the Model card; a custom workflow may not.
        if (!HasWorkflow && !await ModelCardViewModel.ValidateModel())
        {
            return;
        }

        if (!await CheckClientConnectedWithPrompt() || !ClientManager.IsConnected)
        {
            return;
        }

        // --- Context-aware fusion pre-pass (built-in graph only).
        // Captions are cached, so changing only the prompt and re-generating
        // skips this ComfyUI round trip entirely.
        ComposedPrompt = null;
        if (!HasWorkflow && UseContextualFusion && ClientManager.Client is { } comfyClient)
        {
            var validItems = ReferenceImagePaths
                .Where(item => File.Exists(item.Path))
                .Take(MaxReferenceImages)
                .ToList();

            var composition = await TryPrepareContextualPromptAsync(
                comfyClient,
                validItems,
                cancellationToken
            );

            if (composition is not null)
            {
                ComposedPrompt = composition.Prompt;
            }
            else
            {
                // Automatic fallback: BuildFromReferenceCaptions will build the
                // original in-graph JoinStrings fusion instead.
                ContextSummary = "Fallback: in-graph caption fusion (context-aware read-back unavailable).";
                MultiRefLogger.Warn("MultiRef: using in-graph caption fusion fallback");
            }
        }
        else
        {
            CaptionsText = null;
            ContextSummary = null;
        }

        var seedCard = StackCardViewModel.GetCard<SeedCardViewModel>();
        if (overrides is not { UseCurrentSeed: true } && seedCard.IsRandomizeEnabled)
        {
            seedCard.GenerateNewSeed();
        }

        // This screen has its own explicit 1-6 output selector, so it is
        // authoritative here: "separate images" runs that many independent
        // single-scene generations, "grid" runs exactly one generation whose
        // prompt asks for that many panels. The generic Batch Size card is
        // intentionally not used on this screen.
        var batches = BatchCountForGeneration;

        for (var i = 0; i < batches; i++)
        {
            var seed = seedCard.Seed + i;
            var buildPromptArgs = new BuildPromptEventArgs { Overrides = overrides, SeedOverride = seed };
            BuildPrompt(buildPromptArgs);

            var project = InferenceProjectDocument.FromLoadable(this);
            if (project.State?["Seed"]?["Seed"] is not null)
            {
                project = project.WithState(x => x["Seed"]["Seed"] = seed);
            }

            var generationArgs = new ImageGenerationEventArgs
            {
                Client = ClientManager.Client,
                Nodes = buildPromptArgs.Builder.ToNodeDictionary(),
                OutputNodeNames = buildPromptArgs.Builder.Connections.OutputNodeNames.ToArray(),
                Parameters = SaveStateToParameters(new GenerationParameters()) with
                {
                    Seed = Convert.ToUInt64(seed),
                },
                Project = project,
                FilesToTransfer = buildPromptArgs.FilesToTransfer,
                BatchIndex = i,
                ClearOutputImages = i == 0,
            };

            await RunGeneration(generationArgs, cancellationToken);
        }
    }

    /// <inheritdoc />
    public override JsonObject SaveStateToJsonObject()
    {
        var state = base.SaveStateToJsonObject();

        var items = new JsonArray();
        foreach (var item in ReferenceImagePaths)
        {
            var obj = new JsonObject
            {
                ["Path"] = System.Text.Json.Nodes.JsonValue.Create(item.Path),
            };
            if (!string.IsNullOrEmpty(item.Note))
            {
                obj["Note"] = System.Text.Json.Nodes.JsonValue.Create(item.Note);
            }

            items.Add(obj);
        }

        state["ReferenceImages"] = items;

        if (!string.IsNullOrEmpty(EnvironmentReferencePath))
        {
            state["EnvironmentReference"] = System.Text.Json.Nodes.JsonValue.Create(EnvironmentReferencePath);
        }

        if (!string.IsNullOrEmpty(EnvironmentNote))
        {
            state["EnvironmentNote"] = System.Text.Json.Nodes.JsonValue.Create(EnvironmentNote);
        }

        state["WorkflowPath"] = WorkflowPath;

        return state;
    }

    /// <inheritdoc />
    public override void LoadStateFromJsonObject(JsonObject state, int version)
    {
        base.LoadStateFromJsonObject(state, version);

        if (state["ReferenceImages"] is JsonArray paths)
        {
            ReferenceImagePaths.Clear();
            foreach (var node in paths)
            {
                // Backwards compatibility: an older save stored plain path strings.
                if (node is JsonValue value && value.TryGetValue<string>(out var plain))
                {
                    ReferenceImagePaths.Add(new ReferenceItem(plain));
                }
                else if (node is JsonObject obj)
                {
                    var path = obj["Path"]?.GetValue<string>();
                    var note = obj["Note"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(path))
                    {
                        ReferenceImagePaths.Add(new ReferenceItem(path, note));
                    }
                }
            }
        }

        if (state["EnvironmentReference"]?.GetValue<string>() is { } envPath)
        {
            EnvironmentReferencePath = envPath;
        }

        if (state["EnvironmentNote"]?.GetValue<string>() is { } envNote)
        {
            EnvironmentNote = envNote;
        }

        if (state["WorkflowPath"]?.GetValue<string>() is { } workflowPath)
        {
            WorkflowPath = workflowPath;
        }

        // Older saved projects (and the base class state) can restore the
        // img2img denoise flag. The built-in graph is pure text-to-image,
        // so force a full 1.0 denoise unless a custom workflow is in use.
        if (!HasWorkflow)
        {
            SamplerCardViewModel.IsDenoiseStrengthEnabled = false;
            SamplerCardViewModel.DenoiseStrength = 1.0d;
        }

        NotifyWorkflowChanged();
    }
}
