using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

/// <summary>
/// Standalone Video Upscale screen - a post-process companion to the MiniMax Director.
///
/// Where the Director upsizes the H3 *latent* before VAE decode, this screen takes an already
/// rendered video and upscales the pixels. It ships a core-ComfyUI-only template
/// (LoadVideo -> GetVideoComponents -> ImageScaleBy -> CreateVideo -> SaveVideo) and can load
/// any exported RelayVSR / CGlide / LBH-2K-refiner workflow JSON instead. The calculation note
/// makes the upscale potential and quality loss explicit before the user spends GPU time.
/// </summary>
[View(typeof(InferenceVideoUpscaleView), IsPersistent = true)]
[RegisterScoped<InferenceVideoUpscaleViewModel>, ManagedService]
public partial class InferenceVideoUpscaleViewModel : InferenceGenerationViewModelBase
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly INotificationService notifications;

    private MinimaxDirectorWorkflow? workflow;

    // ------------------------------------------------------------------ persisted settings

    [ObservableProperty]
    [property: JsonPropertyName("InputVideoPath")]
    private string inputVideoPath = "";

    [ObservableProperty]
    [property: JsonPropertyName("SourceWidth")]
    private int sourceWidth;

    [ObservableProperty]
    [property: JsonPropertyName("SourceHeight")]
    private int sourceHeight;

    [ObservableProperty]
    [property: JsonPropertyName("SourceFps")]
    private double sourceFps;

    [ObservableProperty]
    [property: JsonPropertyName("SourceFrames")]
    private int sourceFrames;

    [ObservableProperty]
    [property: JsonPropertyName("Scale")]
    private double scale = 2.0;

    [ObservableProperty]
    [property: JsonPropertyName("UpscaleMode")]
    private string upscaleMode = VideoUpscaleWorkflow.ModeCore;

    [ObservableProperty]
    [property: JsonPropertyName("InterpolationMethod")]
    private string interpolationMethod = "lanczos";

    [ObservableProperty]
    [property: JsonPropertyName("RtxQuality")]
    private string rtxQuality = "MEDIUM";

    [ObservableProperty]
    [property: JsonPropertyName("PreserveAudio")]
    private bool preserveAudio;

    /// <summary>Offload Wan models + KV cache to CPU between steps (WANMemoryCleanupNode). Helps 16 GB cards avoid OOM.</summary>
    [ObservableProperty]
    [property: JsonPropertyName("UseWanMemoryCleanup")]
    private bool useWanMemoryCleanup = true;

    /// <summary>Swap VAEDecode for VAEDecodeTiled in the loaded workflow. Only affects workflows that decode a VAE.</summary>
    [ObservableProperty]
    [property: JsonPropertyName("UseTiledVae")]
    private bool useTiledVae;

    [ObservableProperty]
    [property: JsonPropertyName("TiledVaeTileSize")]
    private int tiledVaeTileSize = 512;

    [ObservableProperty]
    [property: JsonPropertyName("TiledVaeOverlap")]
    private int tiledVaeOverlap = 64;

    [ObservableProperty]
    [property: JsonPropertyName("TiledVaeTemporalSize")]
    private int tiledVaeTemporalSize = 64;

    [ObservableProperty]
    [property: JsonPropertyName("TiledVaeTemporalOverlap")]
    private int tiledVaeTemporalOverlap = 8;

    [ObservableProperty]
    [property: JsonPropertyName("OutputPrefix")]
    private string outputPrefix = "video/VideoUpscale";

    /// <summary>Custom workflow JSON. Empty = use the bundled core template.</summary>
    [ObservableProperty]
    [property: JsonPropertyName("WorkflowJson")]
    private string workflowJson = "";

    [ObservableProperty]
    [property: JsonPropertyName("WorkflowPath")]
    private string workflowPath = "(built-in core video template)";

    // ------------------------------------------------------------------ display-only state

    [ObservableProperty]
    [property: JsonIgnore]
    private string statusText = "Pick a video, choose an upscaler, then run.";

    [ObservableProperty]
    [property: JsonIgnore]
    private string workflowInfo = "";

    [ObservableProperty]
    [property: JsonIgnore]
    private string mappingSummary = "";

    [JsonIgnore]
    public bool IsCustomWorkflow => !string.IsNullOrWhiteSpace(WorkflowJson);

    [JsonIgnore]
    public IReadOnlyList<string> UpscaleModes { get; } = new[]
    {
        VideoUpscaleWorkflow.ModeCore,
        VideoUpscaleWorkflow.ModeRtx,
        VideoUpscaleWorkflow.ModeCustom,
    };

    [JsonIgnore]
    public IReadOnlyList<string> InterpolationMethods { get; } = new[]
    {
        "lanczos", "bicubic", "bilinear", "area", "nearest-exact",
    };

    [JsonIgnore]
    public IReadOnlyList<string> RtxQualities { get; } = new[] { "LOW", "MEDIUM", "HIGH" };

    [JsonIgnore]
    public IReadOnlyList<double> UpscaleScales { get; } = new[] { 1.5, 2.0, 2.5, 3.0, 4.0 };

    /// <summary>
    /// The decision aid: projected output size, cost balance and the quality that upscaling can
    /// and cannot recover. Shared helper with the Director's latent-upscale estimate.
    /// </summary>
    [JsonIgnore]
    public string UpscaleNote => MinimaxUpscaleMath.ComputeVideoPostNote(
        SourceWidth,
        SourceHeight,
        Scale,
        string.IsNullOrWhiteSpace(UpscaleMode) ? "upscale" : UpscaleMode,
        SourceFps,
        SourceFrames);

    private void RefreshUpscaleNote() => OnPropertyChanged(nameof(UpscaleNote));

    partial void OnSourceWidthChanged(int value) => RefreshUpscaleNote();
    partial void OnSourceHeightChanged(int value) => RefreshUpscaleNote();
    partial void OnSourceFpsChanged(double value) => RefreshUpscaleNote();
    partial void OnSourceFramesChanged(int value) => RefreshUpscaleNote();
    partial void OnScaleChanged(double value) => RefreshUpscaleNote();
    partial void OnUpscaleModeChanged(string value) => RefreshUpscaleNote();

    public InferenceVideoUpscaleViewModel(
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
    }

    // ------------------------------------------------------------------ state persistence

    public override void LoadStateFromJsonObject(JsonObject state)
    {
        base.LoadStateFromJsonObject(state);

        // Rebuild the parsed node graph from the saved JSON (or the bundled template).
        try
        {
            workflow = IsCustomWorkflow
                ? MinimaxDirectorWorkflow.FromJson(WorkflowJson, Path.GetFileName(WorkflowPath))
                : null;
            if (workflow is null)
            {
                LoadBuiltInWorkflow();
            }
            else
            {
                RefreshWorkflowInfo();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "VideoUpscale: could not restore custom workflow, using built-in template");
            LoadBuiltInWorkflow();
        }
    }

    // ------------------------------------------------------------------ workflow loading

    private void LoadBuiltInWorkflow()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(VideoUpscaleWorkflow.BuiltInWorkflowUri));
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            workflow = MinimaxDirectorWorkflow.FromJson(json, "Built-in core video upscale template");
            WorkflowJson = "";
            WorkflowPath = "(built-in core video template)";
            UpscaleMode = VideoUpscaleWorkflow.ModeCore;
            RefreshWorkflowInfo();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VideoUpscale: could not load bundled workflow");
            WorkflowInfo = "Built-in workflow failed to load: " + ex.Message;
        }
    }

    private void RefreshWorkflowInfo()
    {
        if (workflow is null)
        {
            WorkflowInfo = "No workflow loaded.";
            return;
        }

        var custom = workflow.GetCustomNodeTypes();
        WorkflowInfo =
            $"{(IsCustomWorkflow ? Path.GetFileName(WorkflowPath) : "Bundled core template")}: "
            + $"{workflow.Nodes.Count} node(s), {workflow.OutputNodeNames.Count} output node(s). "
            + (custom.Count == 0
                ? "Core ComfyUI nodes only - no extra plugins needed."
                : "Plugin nodes: " + string.Join(", ", custom));
    }

    [RelayCommand]
    private void UseBuiltInWorkflow()
    {
        LoadBuiltInWorkflow();
        StatusText = "Using the bundled core video upscale template (Lanczos / RTX VSR).";
    }

    /// <summary>
    /// Loads an exported ComfyUI workflow (API or UI JSON) for RelayVSR, CGlide, the LBH 2K
    /// refiner, or any other pixel/video upscaler. The screen then injects the input video,
    /// output prefix, fps and scale widgets into it.
    /// </summary>
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
                Title = "Select video upscale ComfyUI workflow (API or UI JSON)",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("ComfyUI workflow JSON") { Patterns = new[] { "*.json" } },
                },
            });

            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            {
                return;
            }

            var json = await File.ReadAllTextAsync(path);
            var loaded = MinimaxDirectorWorkflow.FromJson(json, Path.GetFileName(path));

            workflow = loaded;
            WorkflowJson = json;
            WorkflowPath = path;
            UpscaleMode = VideoUpscaleWorkflow.ModeCustom;
            RefreshWorkflowInfo();
            StatusText = $"Loaded workflow: {Path.GetFileName(path)} ({loaded.Nodes.Count} nodes). "
                         + "Check that it contains a LoadVideo / VHS_LoadVideo node and a SaveVideo output.";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VideoUpscale: failed to load workflow");
            notifications.Show("Workflow error", ex.Message, NotificationType.Error);
        }
    }

    /// <summary>Checks the loaded workflow's plugin nodes against the connected ComfyUI.</summary>
    [RelayCommand]
    private async Task CheckRequiredNodesAsync()
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
                // Presence test: an unregistered node type makes this call throw.
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

    // ------------------------------------------------------------------ input video + probe

    [RelayCommand]
    private async Task PickInputVideoAsync()
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
                Title = "Select the video to upscale",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Video files")
                    {
                        Patterns = new[] { "*.mp4", "*.webm", "*.mkv", "*.mov", "*.avi", "*.m4v" },
                    },
                },
            });

            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            {
                return;
            }

            InputVideoPath = path;
            await ProbeInputVideoAsync(path);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "VideoUpscale: input video picker failed");
            StatusText = "Could not open the video: " + ex.Message;
        }
    }

    /// <summary>
    /// Best-effort ffprobe so the calculation note uses the real source resolution / fps / frames.
    /// If ffprobe is unavailable the user can enter those values manually in the UI.
    /// </summary>
    private async Task ProbeInputVideoAsync(string videoPath)
    {
        try
        {
            var exe = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
            var psi = new ProcessStartInfo
            {
                FileName = ResolveTool(exe),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            foreach (var arg in new[]
                     {
                         "-v", "error", "-select_streams", "v:0",
                         "-show_entries", "stream=width,height,nb_frames,r_frame_rate,avg_frame_rate,duration",
                         "-show_entries", "format=duration",
                         "-of", "json", videoPath,
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                StatusText = "Video selected. ffprobe unavailable - enter source width / height / fps manually.";
                return;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            await stderrTask;

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                StatusText = "Video selected. ffprobe could not read it - enter source width / height / fps manually.";
                return;
            }

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
            {
                StatusText = "Video selected (no video stream detected).";
                return;
            }

            var stream = streams[0];
            var width = GetIntElement(stream, "width");
            var height = GetIntElement(stream, "height");
            var fps = ParseFrameRate(stream, "r_frame_rate");
            if (fps <= 0)
            {
                fps = ParseFrameRate(stream, "avg_frame_rate");
            }

            var frames = GetIntElement(stream, "nb_frames");
            var duration = GetDoubleElement(stream, "duration");
            if (duration <= 0 && root.TryGetProperty("format", out var fmt))
            {
                duration = GetDoubleElement(fmt, "duration");
            }

            if (frames <= 0 && duration > 0 && fps > 0)
            {
                frames = (int)Math.Round(duration * fps);
            }

            if (width > 0)
            {
                SourceWidth = width;
            }

            if (height > 0)
            {
                SourceHeight = height;
            }

            if (fps > 0)
            {
                SourceFps = Math.Round(fps, 3);
            }

            if (frames > 0)
            {
                SourceFrames = frames;
            }

            StatusText = $"Probed {Path.GetFileName(videoPath)}: {SourceWidth}×{SourceHeight}, "
                         + $"{SourceFps:0.##} fps, {SourceFrames} frames.";
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "VideoUpscale: ffprobe unavailable");
            StatusText = "Video selected. ffprobe unavailable - enter source width / height / fps manually.";
        }
    }

    private static string ResolveTool(string exeName)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException) { }
        }

        return exeName;
    }

    private static double ParseFrameRate(JsonElement stream, string name)
    {
        if (!stream.TryGetProperty(name, out var element))
        {
            return 0;
        }

        var text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        if (string.IsNullOrWhiteSpace(text) || !text.Contains('/'))
        {
            return 0;
        }

        var parts = text.Split('/');
        return parts.Length == 2
               && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
               && den > 0
            ? num / den
            : 0;
    }

    private static int GetIntElement(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element))
        {
            return 0;
        }

        var text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static double GetDoubleElement(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element))
        {
            return 0;
        }

        var text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    // ------------------------------------------------------------------ generation

    protected override async Task GenerateImageImpl(
        GenerateOverrides overrides,
        CancellationToken cancellationToken)
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

        if (string.IsNullOrWhiteSpace(InputVideoPath) || !File.Exists(InputVideoPath))
        {
            notifications.Show("No input video", "Pick an existing video file first.", NotificationType.Warning);
            return;
        }

        if (workflow is null || workflow.OutputNodeNames.Count == 0)
        {
            notifications.Show(
                "Workflow problem",
                "No workflow loaded, or no SaveVideo / filename_prefix output node was found in it.",
                NotificationType.Error);
            return;
        }

        var runId = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
                    + "_" + Guid.NewGuid().ToString("N")[..6];
        var prefix = $"{OutputPrefix.Trim('/')}_{runId}";
        var safeFileName = $"VideoUpscale_{runId}{Path.GetExtension(InputVideoPath)}";

        // LoadVideo.file expects a path relative to ComfyUI's input directory; keep it flat so
        // the file always shows up in the node's combo list.
        await client.UploadFileAsync(InputVideoPath, "input/" + safeFileName, cancellationToken);

        var runWorkflow = workflow.Clone();
        var result = VideoUpscaleWorkflow.Apply(runWorkflow.Nodes, new VideoUpscaleInjection
        {
            InputVideoRelativePath = safeFileName,
            FilenamePrefix = prefix,
            Scale = Scale,
            Mode = UpscaleMode,
            InterpolationMethod = InterpolationMethod,
            RtxQuality = RtxQuality,
            Fps = SourceFps > 0 ? SourceFps : 0,
            PreserveAudio = PreserveAudio,
            IsBuiltInTemplate = !IsCustomWorkflow,
            UseWanMemoryCleanup = UseWanMemoryCleanup,
            UseTiledVae = UseTiledVae,
            TiledVaeTileSize = TiledVaeTileSize,
            TiledVaeOverlap = TiledVaeOverlap,
            TiledVaeTemporalSize = TiledVaeTemporalSize,
            TiledVaeTemporalOverlap = TiledVaeTemporalOverlap,
        });

        MappingSummary = result.Summary;
        if (result.InputTargets == 0)
        {
            notifications.Show(
                "Workflow problem",
                "The loaded workflow has no LoadVideo / VHS_LoadVideo node, so it cannot read the input video. "
                + "Load a workflow exported from your upscaler pack, or use the built-in template.",
                NotificationType.Error);
            return;
        }

        ComfyTask? promptTask = null;
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                OutputProgress.IsIndeterminate = true;
                OutputProgress.Text = $"Upscaling {Path.GetFileName(InputVideoPath)} ×{Scale:0.##}...";
            });

            var interrupt = cancellationToken.Register(() =>
                client
                    .InterruptPromptAsync(new CancellationTokenSource(5000).Token)
                    .SafeFireAndForget(ex => Logger.Warn(ex, "VideoUpscale: error while interrupting prompt")));

            try
            {
                promptTask = await client.QueuePromptAsync(runWorkflow.Nodes, cancellationToken);
                promptTask.ProgressUpdate += OnProgressUpdateReceived;
                await promptTask.Task.WaitAsync(cancellationToken);
                Logger.Info("VideoUpscale prompt {Id} finished", promptTask.Id);
            }
            finally
            {
                await interrupt.DisposeAsync();
            }

            var video = await WaitForVideoAsync(outputRoot, prefix, cancellationToken);
            if (video is null)
            {
                StatusText = "Upscale finished, but no output video was found under the output folder.";
                notifications.Show("No output video", StatusText, NotificationType.Warning);
                return;
            }

            AddToGallery(video, $"{UpscaleMode} ×{Scale:0.##}");
            var (outW, outH) = MinimaxUpscaleMath.Project(SourceWidth, SourceHeight, Scale);
            StatusText = $"Done: {video} "
                         + (outW > 0 ? $"(projected {outW}×{outH})" : "");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VideoUpscale: prompt failed");
            StatusText = "Upscale failed: " + ex.Message;
            notifications.Show("Upscale failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            promptTask?.Dispose();
            Dispatcher.UIThread.Post(() => OutputProgress.ClearProgress());
        }
    }

    private static async Task<string?> WaitForVideoAsync(
        string outputRoot,
        string prefix,
        CancellationToken cancellationToken)
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
}
