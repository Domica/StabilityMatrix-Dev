using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using AsyncAwaitBestPractices;
using Avalonia.Controls.Notifications;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Injectio.Attributes;
using NLog;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Views.Inference;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Inference;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

/// <summary>
/// MiniMax Director - a VENO Director style session runner inside Stability Matrix.
///
/// A session is a queue of clips. Each clip is split into H3 sliding windows
/// (5 + 17k frame grid, 124-frame native max, 1 + 17k overlap grid), queued into ComfyUI
/// one prompt at a time, then joined with ffmpeg. Continuity between clips/windows is
/// achieved by extracting the last frame of the previous video and feeding it back as a
/// reference image. This is the ComfyUI equivalent of VENO's "last frame = first frame"
/// chaining and its run_session() concatenation.
///
/// Unlike the Wan screens, the node graph is not hard-coded: it is a workflow template
/// (bundled H3 API workflow, or any ComfyUI API/UI workflow the user loads). See
/// MinimaxDirectorWorkflow for the injection logic.
/// </summary>
[View(typeof(InferenceMinimaxDirectorView), IsPersistent = true)]
[RegisterScoped<InferenceMinimaxDirectorViewModel>, ManagedService]
public partial class InferenceMinimaxDirectorViewModel : InferenceGenerationViewModelBase
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const string BuiltInWorkflowUri =
        "avares://StabilityMatrix.Avalonia/Assets/Workflows/minimax_h3_director_api.json";

    private static readonly JsonSerializerOptions ClipSerializerOptions = new()
    {
        IgnoreReadOnlyProperties = true,
        WriteIndented = true,
    };

    private readonly INotificationService notifications;

    private MinimaxDirectorWorkflow? workflow;

    // ------------------------------------------------------------------ persisted settings

    [ObservableProperty]
    [property: JsonPropertyName("WorkflowPath")]
    private string workflowPath = "(built-in H3 template)";

    /// <summary>The API-format workflow itself, so saved projects are self-contained.</summary>
    [ObservableProperty]
    [property: JsonPropertyName("WorkflowJson")]
    private string workflowJson = "";

    [ObservableProperty]
    [property: JsonPropertyName("GlobalFps")]
    private int globalFps = 24;

    [ObservableProperty]
    [property: JsonPropertyName("GlobalWidth")]
    private int globalWidth = 512;

    [ObservableProperty]
    [property: JsonPropertyName("GlobalHeight")]
    private int globalHeight = 224;

    [ObservableProperty]
    [property: JsonPropertyName("GlobalSteps")]
    private int globalSteps = 4;

    /// <summary>-1 = pick a new random session seed on every run.</summary>
    [ObservableProperty]
    [property: JsonPropertyName("GlobalSeed")]
    private long globalSeed = -1;

    [ObservableProperty]
    [property: JsonPropertyName("MaxWindowFrames")]
    private int maxWindowFrames = 124;

    [ObservableProperty]
    [property: JsonPropertyName("WindowOverlapFrames")]
    private int windowOverlapFrames = 18;

    [ObservableProperty]
    [property: JsonPropertyName("EnableWindowSplitting")]
    private bool enableWindowSplitting = true;

    /// <summary>inherit | last_frame | none | manual</summary>
    [ObservableProperty]
    [property: JsonPropertyName("ContinuityMode")]
    private string continuityMode = "last_frame";

    [ObservableProperty]
    [property: JsonPropertyName("AutoExpandPrompt")]
    private bool autoExpandPrompt = true;

    [ObservableProperty]
    [property: JsonPropertyName("ChainSession")]
    private bool chainSession = true;

    [ObservableProperty]
    [property: JsonPropertyName("DisableAudio")]
    private bool disableAudio = true;

    [ObservableProperty]
    [property: JsonPropertyName("DisableRtxSuperResolution")]
    private bool disableRtxSuperResolution = false;

    [ObservableProperty]
    [property: JsonPropertyName("FfmpegPath")]
    private string ffmpegPath = "";

    [ObservableProperty]
    [property: JsonPropertyName("OutputPrefix")]
    private string outputPrefix = "video/MinimaxDirector";

    // ------------------------------------------------------------------ advanced model / plugin selection

    /// <summary>Override the UNETLoader checkpoint. Blank = workflow default.</summary>
    [ObservableProperty]
    [property: JsonPropertyName("ModelOverride")]
    private string modelOverride = "";

    /// <summary>Override the video VAELoader. Blank = workflow default.</summary>
    [ObservableProperty]
    [property: JsonPropertyName("VaeOverride")]
    private string vaeOverride = "";

    /// <summary>Override the CLIPLoader / text encoder. Blank = workflow default.</summary>
    [ObservableProperty]
    [property: JsonPropertyName("ClipOverride")]
    private string clipOverride = "";

    /// <summary>Enable one specific LoRA in the LTX_lora_loader stack (by file name). Blank = default.</summary>
    [ObservableProperty]
    [property: JsonPropertyName("LoraName")]
    private string loraName = "";

    [ObservableProperty]
    [property: JsonPropertyName("LoraStrength")]
    private double loraStrength = 1.0;

    /// <summary>Override the ModelAttentionBackend attention field (H3 VRAM/perf lever). Blank = default.</summary>
    [ObservableProperty]
    [property: JsonPropertyName("AttentionBackend")]
    private string attentionBackend = "";

    [ObservableProperty]
    [property: JsonPropertyName("UseTiledVae")]
    private bool useTiledVae;

    [ObservableProperty]
    [property: JsonPropertyName("VaeTileSize")]
    private int vaeTileSize = 256;

    /// <summary>GC + working-set trim between windows/clips to keep host RAM down.</summary>
    [ObservableProperty]
    [property: JsonPropertyName("AggressiveMemoryCleanup")]
    private bool aggressiveMemoryCleanup = true;

    /// <summary>Forces tiled VAE + a low-VRAM attention backend. Pair with ComfyUI --lowvram.</summary>
    [ObservableProperty]
    [property: JsonPropertyName("LowVramMode")]
    private bool lowVramMode;

    /// <summary>The custom ComfyUI node types the loaded workflow needs (read-only, for display).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string requiredPlugins = "";

    // ------------------------------------------------------------------ runtime state

    [JsonIgnore]
    public ObservableCollection<MinimaxDirectorClip> Clips { get; } = new();

    [ObservableProperty]
    [property: JsonIgnore]
    private MinimaxDirectorClip? selectedClip;

    [ObservableProperty]
    [property: JsonIgnore]
    private string statusText = "Ready.";

    [ObservableProperty]
    [property: JsonIgnore]
    private string planSummary = "";

    [ObservableProperty]
    [property: JsonIgnore]
    private string mappingSummary = "";

    [ObservableProperty]
    [property: JsonIgnore]
    private string workflowInfo = "Loading template...";

    public override string TabTitle => ProjectFile?.NameWithoutExtension ?? "MiniMax Director";

    /// <summary>Choices for the session-wide continuity mode.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ContinuityModes { get; } =
        new[] { "last_frame", "none", "manual" };

    /// <summary>Choices for a single clip ("inherit" uses <see cref="ContinuityMode"/>).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ClipContinuityModes { get; } =
        new[] { "inherit", "last_frame", "none", "manual" };

    /// <summary>Known MiniMax H3 attention backends. Empty string = keep the workflow default.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> AttentionBackends { get; } =
        new[]
        {
            "", "sdpa", "flash_attn", "xformers", "comfy kitchen attention",
            "sageattention", "torch_impl", "substitute",
        };

    public InferenceMinimaxDirectorViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
        notifications = notificationService;

        LoadBuiltInWorkflow();

        // Start with one editable clip so the screen is usable immediately.
        AddClip();
        UpdatePlanSummary();
    }

    // ------------------------------------------------------------------ state persistence

    public override JsonObject SaveStateToJsonObject()
    {
        var state = base.SaveStateToJsonObject();
        state["Clips"] = JsonSerializer.SerializeToNode(Clips.ToList(), ClipSerializerOptions);
        return state;
    }

    public override void LoadStateFromJsonObject(JsonObject state)
    {
        base.LoadStateFromJsonObject(state);

        Clips.Clear();
        if (state["Clips"] is JsonArray clipsArray)
        {
            foreach (var node in clipsArray)
            {
                var clip = node.Deserialize<MinimaxDirectorClip>(ClipSerializerOptions);
                if (clip is not null)
                {
                    Clips.Add(clip);
                }
            }
        }

        if (Clips.Count == 0)
        {
            AddClip();
        }

        SelectedClip = Clips.FirstOrDefault();
        RebuildWorkflowFromState();
        UpdatePlanSummary();
    }

    private void RebuildWorkflowFromState()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(WorkflowJson))
            {
                workflow = MinimaxDirectorWorkflow.FromJson(WorkflowJson, WorkflowPath);
            }
            else
            {
                LoadBuiltInWorkflow();
                return;
            }

            RefreshWorkflowInfo();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Could not restore Director workflow from project state");
            LoadBuiltInWorkflow();
        }
    }

    private void RefreshWorkflowInfo()
    {
        if (workflow is null)
        {
            WorkflowInfo = "No workflow loaded";
            MappingSummary = "";
            return;
        }

        WorkflowInfo = $"{workflow.SourceLabel} - {workflow.Nodes.Count} nodes, "
                       + $"{workflow.OutputNodeNames.Count} output node(s)";
        MappingSummary = workflow.MappingSummary;
        RequiredPlugins = string.Join(", ", workflow.GetCustomNodeTypes());
    }

    private void LoadBuiltInWorkflow()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(BuiltInWorkflowUri));
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            workflow = MinimaxDirectorWorkflow.FromJson(json, "Built-in MiniMax H3 template");
            WorkflowJson = workflow.ToJson();
            WorkflowPath = "(built-in H3 template)";
            RefreshWorkflowInfo();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not load built-in MiniMax H3 workflow");
            WorkflowInfo = "Built-in workflow failed to load: " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void AddClip()
    {
        var clip = new MinimaxDirectorClip
        {
            Seconds = 6,
            Seed = -1,
            ContinuityMode = "inherit",
        };
        Clips.Add(clip);
        SelectedClip = clip;
        UpdatePlanSummary();
    }

    [RelayCommand]
    private void RemoveClip()
    {
        if (SelectedClip is null)
        {
            return;
        }

        var index = Clips.IndexOf(SelectedClip);
        Clips.Remove(SelectedClip);
        SelectedClip = Clips.Count > 0 ? Clips[Math.Min(index, Clips.Count - 1)] : null;
        UpdatePlanSummary();
    }

    [RelayCommand]
    private void MoveClipUp() => MoveClip(-1);

    [RelayCommand]
    private void MoveClipDown() => MoveClip(1);

    private void MoveClip(int direction)
    {
        if (SelectedClip is null)
        {
            return;
        }

        var index = Clips.IndexOf(SelectedClip);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Clips.Count)
        {
            return;
        }

        Clips.Move(index, target);
        SelectedClip = Clips[target];
        UpdatePlanSummary();
    }

    [RelayCommand]
    private void ClearClips()
    {
        Clips.Clear();
        SelectedClip = null;
        UpdatePlanSummary();
    }

    [RelayCommand]
    private void UseBuiltInWorkflow()
    {
        LoadBuiltInWorkflow();
        StatusText = "Restored the built-in MiniMax H3 workflow.";
    }

    [RelayCommand]
    private async Task LoadWorkflowAsync()
    {
        try
        {
            var provider = App.StorageProvider;
            if (provider is null)
            {
                return;
            }

            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select MiniMax H3 ComfyUI workflow (API or UI JSON)",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("ComfyUI workflow JSON")
                    {
                        Patterns = new[] { "*.json" },
                    },
                },
            });

            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            {
                return;
            }

            var json = await File.ReadAllTextAsync(path);
            var loaded = MinimaxDirectorWorkflow.FromJson(json, Path.GetFileName(path));

            workflow = loaded;
            WorkflowJson = loaded.ToJson();
            WorkflowPath = path;
            RefreshWorkflowInfo();
            StatusText = $"Loaded workflow: {Path.GetFileName(path)} ({loaded.Nodes.Count} nodes).";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load Director workflow");
            notifications.Show("Workflow error", ex.Message, NotificationType.Error);
        }
    }

    /// <summary>Imports VENO Director's queue.json format (array of clip objects).</summary>
    [RelayCommand]
    private async Task ImportQueueAsync()
    {
        try
        {
            var provider = App.StorageProvider;
            if (provider is null)
            {
                return;
            }

            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import clip queue (VENO queue.json)",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("JSON")
                    {
                        Patterns = new[] { "*.json" },
                    },
                },
            });

            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            {
                return;
            }

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ValidationException("Queue file must be a JSON array of clips.");
            }

            var imported = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var clip = new MinimaxDirectorClip
                {
                    Prompt = GetString(item, "prompt"),
                    Seconds = GetDouble(item, "seconds", 6),
                    Seed = GetLong(item, "seed", -1),
                    Label = GetString(item, "label"),
                    ContinuityMode = "inherit",
                };

                if (GetNullableInt(item, "width") is { } w)
                {
                    clip.WidthOverride = w;
                }

                if (GetNullableInt(item, "height") is { } h)
                {
                    clip.HeightOverride = h;
                }

                if (GetNullableInt(item, "fps") is { } fps)
                {
                    clip.FpsOverride = fps;
                }

                if (GetNullableInt(item, "steps") is { } steps)
                {
                    clip.StepsOverride = steps;
                }

                if (item.TryGetProperty("ref_images", out var refs) && refs.ValueKind == JsonValueKind.Array)
                {
                    var first = refs.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.String)
                    {
                        clip.ReferenceImagePath = first.GetString() ?? "";
                    }
                }

                Clips.Add(clip);
                imported++;
            }

            SelectedClip = Clips.LastOrDefault();
            UpdatePlanSummary();
            StatusText = $"Imported {imported} clip(s) from {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to import Director queue");
            notifications.Show("Import error", ex.Message, NotificationType.Error);
        }
    }

    [RelayCommand]
    private async Task ExportQueueAsync()
    {
        try
        {
            var provider = App.StorageProvider;
            if (provider is null)
            {
                return;
            }

            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export clip queue",
                SuggestedFileName = "queue.json",
                DefaultExtension = "json",
                ShowOverwritePrompt = true,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
                },
            });

            if (file?.TryGetLocalPath() is not { } path)
            {
                return;
            }

            var payload = Clips.Select(clip => new Dictionary<string, object?>
            {
                ["prompt"] = clip.Prompt,
                ["seconds"] = clip.Seconds,
                ["seed"] = clip.Seed,
                ["label"] = string.IsNullOrWhiteSpace(clip.Label) ? null : clip.Label,
                ["width"] = clip.WidthOverride,
                ["height"] = clip.HeightOverride,
                ["fps"] = clip.FpsOverride,
                ["steps"] = clip.StepsOverride,
                ["continuity_mode"] = clip.ContinuityMode,
                ["ref_images"] = string.IsNullOrWhiteSpace(clip.ReferenceImagePath)
                    ? Array.Empty<string>()
                    : new[] { clip.ReferenceImagePath },
            });

            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true })
            );
            StatusText = $"Exported {Clips.Count} clip(s).";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to export Director queue");
            notifications.Show("Export error", ex.Message, NotificationType.Error);
        }
    }

    [RelayCommand]
    private async Task ImportPromptsAsync()
    {
        try
        {
            var provider = App.StorageProvider;
            if (provider is null)
            {
                return;
            }

            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import prompts from a text / markdown file",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Text files") { Patterns = new[] { "*.txt", "*.md" } },
                },
            });

            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            {
                return;
            }

            var text = await File.ReadAllTextAsync(path);
            var blocks = System.Text.RegularExpressions.Regex
                .Split(text, @"\r?\n\s*\r?\n")
                .Select(x => x.Trim())
                .Where(x => x.Length > 20)
                .ToList();

            foreach (var block in blocks)
            {
                var firstLine = block.Split('\n')[0].Trim().TrimStart('#').Trim();
                Clips.Add(new MinimaxDirectorClip
                {
                    Prompt = block,
                    Label = firstLine.Length <= 80 ? firstLine : "",
                    Seconds = 6,
                    Seed = -1,
                    ContinuityMode = "inherit",
                });
            }

            SelectedClip = Clips.LastOrDefault();
            UpdatePlanSummary();
            StatusText = $"Added {blocks.Count} clip(s) from {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to import prompts");
            notifications.Show("Import error", ex.Message, NotificationType.Error);
        }
    }

    [RelayCommand]
    private void PreviewPlan()
    {
        UpdatePlanSummary();
        StatusText = "Plan refreshed (nothing was generated).";
    }

    /// <summary>Picks the local image used when a clip's continuity mode is "manual".</summary>
    [RelayCommand]
    private async Task BrowseReferenceImageAsync()
    {
        if (SelectedClip is null)
        {
            return;
        }

        try
        {
            var provider = App.StorageProvider;
            if (provider is null)
            {
                return;
            }

            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select first-frame reference image",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Image files")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" },
                    },
                },
            });

            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            {
                SelectedClip.ReferenceImagePath = path;
                StatusText = $"Reference image: {Path.GetFileName(path)}";
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to pick Director reference image");
            notifications.Show("Image error", ex.Message, NotificationType.Error);
        }
    }

    /// <summary>
    /// Checks each custom node the loaded workflow uses against the connected ComfyUI instance
    /// so the user knows which plugins are missing before running.
    /// </summary>
    [RelayCommand]
    private async Task CheckRequiredNodesAsync()
    {
        try
        {
            if (!ClientManager.IsConnected || ClientManager.Client is not { } client)
            {
                StatusText = "Connect to ComfyUI first, then check plugins.";
                return;
            }

            if (workflow is null)
            {
                return;
            }

            var custom = workflow.GetCustomNodeTypes();
            if (custom.Count == 0)
            {
                StatusText = "This workflow uses only core ComfyUI nodes - no extra plugins needed.";
                return;
            }

            var missing = new List<string>();
            foreach (var type in custom)
            {
                try
                {
                    // Presence test: GetNodeOptionNamesAsync throws if the node type is unregistered.
                    await client.GetNodeOptionNamesAsync(type, "anything", CancellationToken.None);
                }
                catch
                {
                    missing.Add(type);
                }
            }

            StatusText = missing.Count == 0
                ? $"All {custom.Count} required plugin node(s) are installed in ComfyUI."
                : "Missing ComfyUI plugins: " + string.Join(", ", missing);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Director plugin check failed");
            StatusText = "Plugin check failed: " + ex.Message;
        }
    }

    private void UpdatePlanSummary()
    {
        if (Clips.Count == 0)
        {
            PlanSummary = "No clips queued.";
            return;
        }

        var builder = new StringBuilder();
        var totalSeconds = 0d;
        var totalWindows = 0;

        for (var i = 0; i < Clips.Count; i++)
        {
            var clip = Clips[i];
            var fps = clip.FpsOverride ?? GlobalFps;
            var windows = BuildWindowPlan(clip.Seconds, fps);
            totalSeconds += clip.Seconds;
            totalWindows += windows.Count;
            builder.AppendLine(
                $"#{i + 1} {clip.Seconds:0.#}s @ {fps}fps -> {windows.Count} window(s): "
                + string.Join(", ", windows.Select(w => $"{w.Length}f (trim {w.TrimStart}, keep {w.KeepFrames})"))
            );
        }

        builder.AppendLine($"Total: {Clips.Count} clip(s), {totalSeconds:0.#}s, {totalWindows} ComfyUI prompt(s)");
        PlanSummary = builder.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ H3 window planning

    private sealed record DirectorWindow(int Index, int Length, int TrimStart, int KeepFrames);

    private static int NormalizeFrameCount(int frames)
    {
        frames = Math.Max(5, frames);
        return (int)Math.Ceiling((frames - 5) / 17d) * 17 + 5;
    }

    private static int NormalizeOverlap(int overlap)
    {
        if (overlap <= 0)
        {
            return 0;
        }

        var k = Math.Max(0, (int)Math.Round((overlap - 1) / 17d));
        return k * 17 + 1;
    }

    private List<DirectorWindow> BuildWindowPlan(double seconds, int fps)
    {
        fps = Math.Clamp(fps, 1, 60);
        var totalFrames = NormalizeFrameCount((int)Math.Round(Math.Max(0.2, seconds) * fps));

        if (!EnableWindowSplitting)
        {
            return new List<DirectorWindow> { new(0, totalFrames, 0, totalFrames) };
        }

        var maxWindow = Math.Min(124, NormalizeFrameCount(Math.Clamp(MaxWindowFrames, 22, 124)));
        var overlap = NormalizeOverlap(WindowOverlapFrames);
        if (overlap >= maxWindow / 2)
        {
            overlap = 18;
        }

        var windows = new List<DirectorWindow>();
        var remaining = totalFrames;
        var index = 0;

        while (remaining > 0 && index < 256)
        {
            var trim = index == 0 ? 0 : overlap;
            var length = Math.Min(maxWindow, NormalizeFrameCount(remaining + trim));
            var keep = Math.Min(remaining, length - trim);
            if (keep <= 0)
            {
                break;
            }

            windows.Add(new DirectorWindow(index, length, trim, keep));
            remaining -= keep;
            index++;
        }

        if (windows.Count == 0)
        {
            windows.Add(new DirectorWindow(0, totalFrames, 0, totalFrames));
        }

        return windows;
    }

    // ------------------------------------------------------------------ generation

    protected override async Task GenerateImageImpl(
        GenerateOverrides overrides,
        CancellationToken cancellationToken
    )
    {
        if (!await CheckClientConnectedWithPrompt() || !ClientManager.IsConnected)
        {
            return;
        }

        var client = ClientManager.Client;
        if (client?.OutputImagesDir?.FullPath is not { } outputRoot)
        {
            notifications.Show("Not connected", "ComfyUI output directory is unavailable.", NotificationType.Error);
            return;
        }

        if (workflow is null || workflow.OutputNodeNames.Count == 0)
        {
            notifications.Show(
                "Workflow problem",
                "No workflow loaded, or no output node (SaveVideo / SaveImage) was found in it.",
                NotificationType.Error
            );
            return;
        }

        if (Clips.Count == 0)
        {
            notifications.Show("Empty queue", "Add at least one clip before generating.", NotificationType.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(WorkflowJson))
        {
            notifications.Show("Workflow problem", "Workflow JSON is empty.", NotificationType.Error);
            return;
        }

        var ffmpeg = ResolveFfmpeg(client);
        var runId = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
                    + "_" + Guid.NewGuid().ToString("N")[..6];
        var workDir = Path.Combine(Path.GetTempPath(), "StabilityMatrix_MinimaxDirector", runId);
        Directory.CreateDirectory(workDir);

        var sessionSeed = GlobalSeed >= 0
            ? GlobalSeed
            : Random.Shared.NextInt64(0, int.MaxValue);

        var sessionVideos = new List<string>();
        var promptIndex = 0;
        string? previousClipPrompt = null;
        string? continuityFrame = null;
        var totalWindows = Clips.Sum(c => BuildWindowPlan(c.Seconds, c.FpsOverride ?? GlobalFps).Count);

        try
        {
            UpdatePlanSummary();

            for (var clipIndex = 0; clipIndex < Clips.Count; clipIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var clip = Clips[clipIndex];
                clip.IsRunning = true;
                clip.Status = "Running";
                OnPropertyChanged(nameof(Clips));

                var fps = Math.Clamp(clip.FpsOverride ?? GlobalFps, 1, 60);
                var width = AlignDimension(clip.WidthOverride ?? GlobalWidth);
                var height = AlignDimension(clip.HeightOverride ?? GlobalHeight);
                var steps = Math.Clamp(clip.StepsOverride ?? GlobalSteps, 1, 100);
                var effectiveContinuity = clip.ContinuityMode == "inherit" ? ContinuityMode : clip.ContinuityMode;
                var windows = BuildWindowPlan(clip.Seconds, fps);
                var clipVideos = new List<(string Path, int TrimStart, int KeepFrames)>();

                var prompt = clip.Prompt?.Trim() ?? "";
                if (
                    AutoExpandPrompt
                    && prompt.Length < 300
                    && !string.IsNullOrWhiteSpace(previousClipPrompt)
                    && clipIndex > 0
                )
                {
                    var anchor = previousClipPrompt.Trim().Length > 200
                        ? previousClipPrompt.Trim()[..200]
                        : previousClipPrompt.Trim();
                    prompt = $"CONTINUITY from previous clip: {anchor}...\n\nContinue the scene: {prompt}";
                }

                for (var windowIndex = 0; windowIndex < windows.Count; windowIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var window = windows[windowIndex];
                    var seed = clip.Seed >= 0 ? clip.Seed + windowIndex : sessionSeed + clipIndex * 1000 + windowIndex;
                    var prefix = $"{OutputPrefix.Trim('/')}_{runId}_c{clipIndex + 1:00}_w{windowIndex + 1:00}";
                    var relativeFrame = $"minimax_director/{runId}_frame_{promptIndex:000}.png";
                    var files = new List<(string SourcePath, string DestinationRelativePath)>();

                    string? firstFrame = null;
                    if (effectiveContinuity == "manual" && !string.IsNullOrWhiteSpace(clip.ReferenceImagePath))
                    {
                        firstFrame = await PrepareManualFrameAsync(
                            clip.ReferenceImagePath, workDir, promptIndex, relativeFrame, files, cancellationToken
                        );
                    }
                    else if (effectiveContinuity == "last_frame" && continuityFrame is not null)
                    {
                        files.Add((continuityFrame, $"input/{relativeFrame}"));
                        firstFrame = relativeFrame;
                    }

                    var windowWorkflow = workflow.Clone();
                    windowWorkflow.Apply(new MinimaxDirectorInjection
                    {
                        Prompt = prompt,
                        Seed = seed,
                        Width = width,
                        Height = height,
                        Length = window.Length,
                        Steps = steps,
                        Fps = fps,
                        FilenamePrefix = prefix,
                        FirstFrameRelativePath = firstFrame,
                        DisableAudio = DisableAudio,
                        BypassClassTypes = DisableRtxSuperResolution
                            ? new[] { "RTXVideoSuperResolution" }
                            : Array.Empty<string>(),
                        ModelName = ModelOverride.Trim(),
                        VaeName = VaeOverride.Trim(),
                        ClipName = ClipOverride.Trim(),
                        LoraName = LoraName.Trim(),
                        LoraStrength = LoraStrength,
                        AttentionBackend = !string.IsNullOrWhiteSpace(AttentionBackend)
                            ? AttentionBackend
                            : (LowVramMode ? "sageattention" : null),
                        UseTiledVae = UseTiledVae || LowVramMode,
                        VaeTileSize = VaeTileSize,
                    });

                    promptIndex++;
                    StatusText = $"Clip {clipIndex + 1}/{Clips.Count}, window {windowIndex + 1}/{windows.Count} "
                                 + $"(prompt {promptIndex}/{totalWindows}) - {promptIndex} queued...";

                    await QueueWindowAsync(client, windowWorkflow.Nodes, files, StatusText, cancellationToken);

                    var video = await WaitForVideoAsync(outputRoot, prefix, cancellationToken);
                    if (video is null)
                    {
                        Logger.Warn("Director: no video found for prefix {Prefix} under {Dir}", prefix, outputRoot);
                        clip.Status = $"Window {windowIndex + 1}: no output video";
                        continue;
                    }

                    AddToGallery(video, $"Clip {clipIndex + 1} / window {windowIndex + 1}");
                    clipVideos.Add((video, window.TrimStart, window.KeepFrames));

                    if (effectiveContinuity == "last_frame" && !string.IsNullOrEmpty(ffmpeg))
                    {
                        var frame = Path.Combine(workDir, $"last_{promptIndex:000}.png");
                        if (await ExtractLastFrameAsync(ffmpeg, video, frame, cancellationToken))
                        {
                            continuityFrame = frame;
                        }
                    }

                    // Memory optimization: drop retained references between windows so the host
                    // process RAM does not grow across a long session (avoids page-file thrashing).
                    if (AggressiveMemoryCleanup)
                    {
                        TrimMemory();
                    }
                }

                if (clipVideos.Count == 0)
                {
                    clip.Status = "Failed (no output)";
                    clip.IsRunning = false;
                    previousClipPrompt = clip.Prompt;
                    continue;
                }

                string clipVideo;
                if (clipVideos.Count == 1)
                {
                    clipVideo = clipVideos[0].Path;
                }
                else if (string.IsNullOrEmpty(ffmpeg))
                {
                    clipVideo = clipVideos[0].Path;
                    Logger.Warn("Director: ffmpeg unavailable, skipping clip window join");
                }
                else
                {
                    clipVideo = Path.Combine(outputRoot, $"MinimaxDirector_{runId}_clip{clipIndex + 1:00}.mp4");
                    var joined = await JoinWindowsAsync(ffmpeg, clipVideos, clipVideo, fps, cancellationToken);
                    if (!joined)
                    {
                        clipVideo = clipVideos[0].Path;
                    }
                    else
                    {
                        AddToGallery(clipVideo, $"Clip {clipIndex + 1} (joined)");
                    }
                }

                sessionVideos.Add(clipVideo);
                clip.Status = $"Done ({clipVideos.Count} window(s))";
                clip.IsRunning = false;
                previousClipPrompt = clip.Prompt;
                StatusText = $"Clip {clipIndex + 1}/{Clips.Count} done.";

                if (AggressiveMemoryCleanup)
                {
                    TrimMemory();
                }
            }

            if (ChainSession && sessionVideos.Count >= 2 && !string.IsNullOrEmpty(ffmpeg))
            {
                var sessionPath = Path.Combine(outputRoot, $"MinimaxDirector_{runId}_SESSION.mp4");
                if (await ConcatVideosAsync(ffmpeg, sessionVideos, sessionPath, cancellationToken))
                {
                    AddToGallery(sessionPath, "Session (all clips)");
                    StatusText = $"Session complete: {sessionPath}";
                    notifications.Show("Director complete", $"Session video: {sessionPath}", NotificationType.Success);
                }
            }
            else if (StatusText.StartsWith("Clip", StringComparison.Ordinal))
            {
                StatusText = $"Session complete. {sessionVideos.Count} clip video(s) saved.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Session cancelled.";
            foreach (var clip in Clips.Where(c => c.IsRunning))
            {
                clip.IsRunning = false;
                clip.Status = "Cancelled";
            }

            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "MiniMax Director session failed");
            StatusText = "Error: " + ex.Message;
            notifications.Show("Director error", ex.Message, NotificationType.Error);
        }
        finally
        {
            foreach (var clip in Clips)
            {
                clip.IsRunning = false;
            }

            Dispatcher.UIThread.Post(() => OutputProgress.ClearProgress());
        }
    }

    private async Task QueueWindowAsync(
        ComfyClient client,
        NodeDictionary nodes,
        IReadOnlyList<(string SourcePath, string DestinationRelativePath)> files,
        string status,
        CancellationToken cancellationToken
    )
    {
        foreach (var (source, destination) in files)
        {
            await client.UploadFileAsync(source, destination, cancellationToken);
        }

        Dispatcher.UIThread.Post(() =>
        {
            OutputProgress.IsIndeterminate = true;
            OutputProgress.Text = status;
        });

        var interrupt = cancellationToken.Register(() =>
            client
                .InterruptPromptAsync(new CancellationTokenSource(5000).Token)
                .SafeFireAndForget(ex => Logger.Warn(ex, "Error while interrupting prompt"))
        );

        ComfyTask? promptTask = null;
        try
        {
            promptTask = await client.QueuePromptAsync(nodes, cancellationToken);
            promptTask.ProgressUpdate += OnProgressUpdateReceived;
            await promptTask.Task.WaitAsync(cancellationToken);
            Logger.Info("Director prompt {Id} finished", promptTask.Id);
        }
        finally
        {
            await interrupt.DisposeAsync();
            promptTask?.Dispose();
        }
    }

    private static async Task<string?> WaitForVideoAsync(
        string outputRoot,
        string prefix,
        CancellationToken cancellationToken
    )
    {
        var normalized = prefix.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var directory = Path.Combine(outputRoot, Path.GetDirectoryName(normalized) ?? "");
        var filePrefix = Path.GetFileName(normalized);

        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(directory))
            {
                var matches = Directory
                    .GetFiles(directory, filePrefix + "*")
                    .Where(x => x.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                                || x.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
                                || x.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();

                if (matches.Count > 0)
                {
                    return matches[0];
                }
            }

            await Task.Delay(1000, cancellationToken);
        }

        return null;
    }

    private void AddToGallery(string path, string label)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var filePath = new FilePath(path);
            ImageGalleryCardViewModel.ImageSources.Add(new ImageSource(filePath) { Label = label });
            EventManager.Instance.OnImageFileAdded(filePath);
        });
    }

    private static async Task<string?> PrepareManualFrameAsync(
        string sourcePath,
        string workDir,
        int index,
        string relativePath,
        List<(string SourcePath, string DestinationRelativePath)> files,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(sourcePath))
        {
            return null;
        }

        var localCopy = Path.Combine(workDir, $"manual_{index:000}{Path.GetExtension(sourcePath)}");
        await Task.Run(() => File.Copy(sourcePath, localCopy, true), cancellationToken);
        files.Add((localCopy, $"input/{relativePath.Replace(".png", Path.GetExtension(sourcePath))}"));
        return relativeFrameName(relativePath, sourcePath);
    }

    private static string relativeFrameName(string relativePath, string sourcePath) =>
        relativePath.Replace(".png", Path.GetExtension(sourcePath));

    private string ResolveFfmpeg(ComfyClient client)
    {
        if (!string.IsNullOrWhiteSpace(FfmpegPath) && File.Exists(FfmpegPath))
        {
            return FfmpegPath;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(dir.Trim('"'), OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException) { }
        }

        var comfyRoot = client.LocalServerPath?.FullPath;
        if (!string.IsNullOrEmpty(comfyRoot))
        {
            var binaries = Path.Combine(comfyRoot, "venv", "Lib", "site-packages", "imageio_ffmpeg", "binaries");
            if (Directory.Exists(binaries))
            {
                var bundled = Directory
                    .GetFiles(binaries, OperatingSystem.IsWindows() ? "ffmpeg*.exe" : "ffmpeg*")
                    .FirstOrDefault();
                if (bundled is not null)
                {
                    return bundled;
                }
            }
        }

        return OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    }

    private static async Task<bool> ExtractLastFrameAsync(
        string ffmpeg,
        string video,
        string output,
        CancellationToken cancellationToken
    )
    {
        var args = new[]
        {
            "-y", "-sseof", "-0.4", "-i", video,
            "-frames:v", "1", "-update", "1", "-q:v", "2", output,
        };

        var (code, stderr) = await RunProcessAsync(ffmpeg, args, cancellationToken);
        if (code != 0 || !File.Exists(output))
        {
            Logger.Warn("ffmpeg last-frame extraction failed ({Code}): {Error}", code, stderr);
            return false;
        }

        return true;
    }

    private static async Task<bool> JoinWindowsAsync(
        string ffmpeg,
        IReadOnlyList<(string Path, int TrimStart, int KeepFrames)> segments,
        string output,
        double fps,
        CancellationToken cancellationToken
    )
    {
        var args = new List<string> { "-y" };
        foreach (var segment in segments)
        {
            args.Add("-i");
            args.Add(segment.Path);
        }

        var filter = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            filter.Append(
                $"[{i}:v]trim=start_frame={segment.TrimStart}:end_frame={segment.TrimStart + segment.KeepFrames},"
                + $"setpts=PTS-STARTPTS[v{i}];"
            );
        }

        filter.Append(string.Concat(Enumerable.Range(0, segments.Count).Select(i => $"[v{i}]")));
        filter.Append($"concat=n={segments.Count}:v=1:a=0[outv]");

        args.AddRange(new[]
        {
            "-filter_complex", filter.ToString(),
            "-map", "[outv]",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
            "-pix_fmt", "yuv420p",
            "-r", fps.ToString(CultureInfo.InvariantCulture),
            output,
        });

        var (code, stderr) = await RunProcessAsync(ffmpeg, args, cancellationToken);
        if (code != 0 || !File.Exists(output))
        {
            Logger.Warn("ffmpeg window join failed ({Code}): {Error}", code, stderr);
            return false;
        }

        return true;
    }

    private static async Task<bool> ConcatVideosAsync(
        string ffmpeg,
        IReadOnlyList<string> videos,
        string output,
        CancellationToken cancellationToken
    )
    {
        var listPath = Path.Combine(Path.GetDirectoryName(output) ?? Path.GetTempPath(),
            Path.GetFileNameWithoutExtension(output) + "_concat.txt");

        var lines = videos.Select(v => $"file '{v.Replace("\\", "/").Replace("'", "'\\''")}'");
        await File.WriteAllLinesAsync(listPath, lines, cancellationToken);

        var (code, stderr) = await RunProcessAsync(
            ffmpeg,
            new[] { "-y", "-f", "concat", "-safe", "0", "-i", listPath, "-c", "copy", output },
            cancellationToken
        );

        if (code != 0 || !File.Exists(output))
        {
            Logger.Warn("ffmpeg session concat failed ({Code}): {Error}", code, stderr);
            return false;
        }

        return true;
    }

    private static async Task<(int ExitCode, string Stderr)> RunProcessAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"Could not start {executable}");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        await stdout;
        return (process.ExitCode, await stderr);
    }

    private static int AlignDimension(int value) => Math.Max(16, value / 16 * 16);

    /// <summary>
    /// Host-side memory hygiene. Forces a full GC and, on Windows, trims the process working set
    /// back to what is actually in use. This is the cheapest, safest way to stop a long Director
    /// session from slowly pushing the host into the page file (RAM thrashing) as we queue many
    /// windows. It does not touch ComfyUI's VRAM, which is governed by the workflow / attention
    /// backend / tiled VAE instead.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(IntPtr hProcess, nint min, nint max);

    public static void TrimMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                SetProcessWorkingSetSize(
                    Process.GetCurrentProcess().Handle,
                    (nint)(-1),
                    (nint)(-1)
                );
            }
            catch
            {
                // Best-effort; ignore if it fails (e.g. insufficient privileges).
            }
        }
    }

    // ------------------------------------------------------------------ small JSON helpers

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static double GetDouble(JsonElement element, string name, double fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : fallback;

    private static long GetLong(JsonElement element, string name, long fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? (long)value.GetDouble()
            : fallback;

    private static int? GetNullableInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? (int)value.GetDouble()
            : null;
}
