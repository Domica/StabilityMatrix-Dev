using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Injectio.Attributes;
using NLog;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Video;

/// <summary>
/// Video output format
/// </summary>
public enum VideoFormat
{
    /// <summary>WebP animated image - smaller file, slower compression</summary>
    WebP = 0,

    /// <summary>MP4 video - better compression, faster processing</summary>
    Mp4 = 1
}

/// <summary>
/// WebP video encoding method
/// Uses custom JsonConverter for compatibility with old .smproj files
/// </summary>
[JsonConverter(typeof(VideoOutputMethodJsonConverter))]
public enum VideoOutputMethod
{
    /// <summary>Standard encoding</summary>
    Default = 0,

    /// <summary>Faster encoding (lower quality)</summary>
    Fast = 1,

    /// <summary>Slower encoding (higher quality)</summary>
    Slow = 2,
}

/// <summary>
/// ViewModel for Video Output Settings Card
/// Manages video export as MP4 or WebP
/// </summary>
[View(typeof(VideoOutputSettingsCard))]
[ManagedService]
[RegisterTransient<VideoOutputSettingsCardViewModel>]
public partial class VideoOutputSettingsCardViewModel
    : LoadableViewModelBase,
        IParametersLoadableState,
        IComfyStep
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    // ============================================================
    // OBSERVABLE PROPERTIES - WebP & MP4 Common
    // ============================================================

    /// <summary>
    /// Frames per second (1-120)
    /// </summary>
    [ObservableProperty]
    private double fps = 6;

    /// <summary>
    /// WebP: Lossless compression
    /// </summary>
    [ObservableProperty]
    private bool lossless = true;

    /// <summary>
    /// WebP: Compression quality (0-100)
    /// </summary>
    [ObservableProperty]
    private int quality = 85;

    /// <summary>
    /// WebP: Encoding method
    /// </summary>
    [ObservableProperty]
    private VideoOutputMethod selectedMethod = VideoOutputMethod.Default;

    /// <summary>
    /// Available encoding methods
    /// </summary>
    [ObservableProperty]
    private List<VideoOutputMethod> availableMethods = Enum.GetValues<VideoOutputMethod>().ToList();

    // ============================================================
    // OBSERVABLE PROPERTIES - MP4 Specific
    // ============================================================

    /// <summary>
    /// Selected video format (WebP or MP4)
    /// </summary>
    [ObservableProperty]
    private VideoFormat format = VideoFormat.WebP;

    /// <summary>
    /// MP4: Constant Rate Factor - compression quality (0-51)
    /// Recommended: 18-28
    /// </summary>
    [ObservableProperty]
    private int crf = 18;

    /// <summary>
    /// MP4: Video codec (libx264, libx265)
    /// NOTE: This may be set from ComboBoxItem.Content (string value)
    /// </summary>
    [ObservableProperty]
    private string codec = "libx264";

    /// <summary>
    /// MP4: Container format (mp4, mkv)
    /// NOTE: This may be set from ComboBoxItem.Content (string value)
    /// </summary>
    [ObservableProperty]
    private string container = "mp4";

    /// <summary>
    /// MP4: Bitrate in kbps (500-50000)
    /// </summary>
    [ObservableProperty]
    private int bitrate = 4000;

    // ============================================================
    // COMPUTED PROPERTIES
    // ============================================================

    /// <summary>
    /// Current format is MP4
    /// </summary>
    public bool IsMp4 => Format == VideoFormat.Mp4;

    // ============================================================
    // STATE MANAGEMENT
    // ============================================================

    /// <summary>
    /// Load state from GenerationParameters
    /// </summary>
    public void LoadStateFromParameters(GenerationParameters parameters)
    {
        try
        {
            Fps = Math.Clamp(parameters.OutputFps, 1, 120);
            Lossless = parameters.Lossless;
            Quality = Math.Clamp(parameters.VideoQuality, 0, 100);

            // Load format with fallback
            if (!string.IsNullOrWhiteSpace(parameters.VideoFormat) &&
                Enum.TryParse(parameters.VideoFormat, true, out VideoFormat fmt))
            {
                Format = fmt;
            }

            // MP4 specific options
            Crf = Math.Clamp(parameters.VideoCrf, 0, 51);
            Codec = ExtractStringValue(parameters.VideoCodec ?? "libx264");
            Container = ExtractStringValue(parameters.VideoContainer ?? "mp4");
            Bitrate = Math.Clamp(parameters.VideoBitrate, 500, 50000);

            // Video output method
            if (!string.IsNullOrWhiteSpace(parameters.VideoOutputMethod))
            {
                SelectedMethod = Enum.TryParse<VideoOutputMethod>(parameters.VideoOutputMethod, true, out var method)
                    ? method
                    : VideoOutputMethod.Default;
            }

            Logger.Debug($"Video settings loaded: Format={Format}, CRF={Crf}, Bitrate={Bitrate}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load video settings from parameters");
            throw;
        }
    }

    /// <summary>
    /// Save state to GenerationParameters
    /// </summary>
    public GenerationParameters SaveStateToParameters(GenerationParameters parameters)
    {
        try
        {
            // Validation
            var validFps = Math.Clamp(Fps, 1, 120);
            var validCrf = Math.Clamp(Crf, 0, 51);
            var validBitrate = Math.Clamp(Bitrate, 500, 50000);
            var validQuality = Math.Clamp(Quality, 0, 100);

            var result = parameters with
            {
                OutputFps = validFps,
                Lossless = Lossless,
                VideoQuality = validQuality,
                VideoOutputMethod = SelectedMethod.ToString(),
                VideoFormat = Format.ToString(),
                VideoCrf = validCrf,
                VideoCodec = ExtractStringValue(Codec) ?? "libx264",
                VideoContainer = ExtractStringValue(Container) ?? "mp4",
                VideoBitrate = validBitrate
            };

            Logger.Debug($"Video settings saved: Format={Format}, CRF={validCrf}, Bitrate={validBitrate}");
            return result;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to save video settings to parameters");
            throw;
        }
    }

    // ============================================================
    // PROPERTY CHANGED HANDLERS - VALIDATION
    // ============================================================

    /// <summary>
    /// Capture CRF value changes and validate them
    /// </summary>
    partial void OnCrfChanged(int value)
    {
        // Validation - clamp to 0-51
        if (value < 0 || value > 51)
        {
            Crf = Math.Clamp(value, 0, 51);
        }
    }

    /// <summary>
    /// Capture Bitrate value changes and validate them
    /// </summary>
    partial void OnBitrateChanged(int value)
    {
        // Validation - clamp to 500-50000
        if (value < 500 || value > 50000)
        {
            Bitrate = Math.Clamp(value, 500, 50000);
        }
    }

    /// <summary>
    /// Capture FPS value changes and validate them
    /// </summary>
    partial void OnFpsChanged(double value)
    {
        // Validation - clamp to 1-120
        if (value < 1 || value > 120)
        {
            Fps = Math.Clamp(value, 1, 120);
        }
    }

    // ============================================================
    // HELPER METHODS
    // ============================================================

    /// <summary>
    /// Extracts string value from potential ComboBoxItem object.
    /// 
    /// XAML ComboBox with hard-coded ComboBoxItem elements may return
    /// the ComboBoxItem object instead of just the Content string.
    /// This method handles both cases:
    /// - If input is ComboBoxItem, extract its Content
    /// - If input is string, return as-is
    /// - Otherwise, return ToString()
    /// </summary>
    private static string? ExtractStringValue(object? value)
    {
        if (value == null)
            return null;

        // If it's a ComboBoxItem, extract the Content
        if (value is ComboBoxItem item)
        {
            return item.Content?.ToString();
        }

        // If it's already a string, return it
        if (value is string str)
        {
            return str;
        }

        // Otherwise, convert to string
        return value.ToString();
    }

    // ============================================================
    // COMFY NODE GENERATION
    // ============================================================

    /// <summary>
    /// Apply video output step to Comfy node builder
    /// 
    /// Creates SaveAnimatedWEBP or SaveAnimatedMP4 nodes, adds them to the node dictionary,
    /// and registers them as output nodes via OutputNodes list.
    /// 
    /// How it works:
    /// 1. Create the node with AddTypedNode()
    /// 2. Add to OutputNodes list
    /// 3. OutputNodeNames is auto-generated from OutputNodes
    /// </summary>
    public void ApplyStep(ModuleApplyStepEventArgs e)
    {
        try
        {
            Logger.Info($"Applying video output: Format={Format}, FPS={Fps}");

            // ========== VALIDATION ==========
            if (e.Builder.Connections.Primary is null)
                throw new InvalidOperationException(
                    "Cannot apply video output settings: No primary connection available. " +
                    "Ensure an image or latent output is connected."
                );

            if (e.Builder.Connections.PrimaryVAE is null)
                throw new InvalidOperationException(
                    "Cannot apply video output settings: No VAE available. " +
                    "Ensure a model with VAE is loaded."
                );

            // Validate values
            if (Fps < 1 || Fps > 120)
                throw new InvalidOperationException($"FPS must be between 1 and 120, got: {Fps}");

            if (Format == VideoFormat.Mp4)
            {
                if (Crf < 0 || Crf > 51)
                    throw new InvalidOperationException($"CRF must be between 0 and 51, got: {Crf}");

                if (Bitrate < 500 || Bitrate > 50000)
                    throw new InvalidOperationException($"Bitrate must be between 500 and 50000 kbps, got: {Bitrate}");

                // Extract codec value (may be ComboBoxItem)
                var codecValue = ExtractStringValue(Codec);
                if (string.IsNullOrWhiteSpace(codecValue))
                    throw new InvalidOperationException("Codec cannot be empty");

                // Extract container value (may be ComboBoxItem)
                var containerValue = ExtractStringValue(Container);
                if (string.IsNullOrWhiteSpace(containerValue))
                    throw new InvalidOperationException("Container cannot be empty");
            }

            // ========== CONVERT PRIMARY CONNECTION ==========
            var image = e.Builder.Connections.Primary.Match(
                _ =>
                    e.Builder.GetPrimaryAsImage(
                        e.Builder.Connections.PrimaryVAE
                            ?? e.Builder.Connections.Refiner.VAE
                            ?? e.Builder.Connections.Base.VAE
                            ?? throw new InvalidOperationException("No VAE found")
                    ),
                image => image
            );

            // ========== WEBP EXPORT ==========
            if (Format == VideoFormat.WebP)
            {
                Logger.Debug("Creating SaveAnimatedWEBP node");

                var outputStep = e.Nodes.AddTypedNode(
                    new ComfyNodeBuilder.SaveAnimatedWEBP
                    {
                        Name = e.Nodes.GetUniqueName("SaveAnimatedWEBP"),
                        Images = image,
                        FilenamePrefix = "InferenceVideo",
                        Fps = Fps,
                        Lossless = Lossless,
                        Quality = Quality,
                        Method = SelectedMethod.ToString().ToLowerInvariant()
                    }
                );

                // ✅ CORRECT: Add to OutputNodes - this registers it as an output
                e.Builder.Connections.OutputNodes.Add(outputStep);
                Logger.Info($"WebP node added to outputs: {outputStep.Name}");
                return;
            }

            // ========== MP4 EXPORT ==========
            Logger.Debug("Creating SaveAnimatedMP4 node");

            // IMPORTANT: Extract string values from potential ComboBoxItem objects
            var finalCodec = ExtractStringValue(Codec) ?? "libx264";
            var finalContainer = ExtractStringValue(Container) ?? "mp4";

            Logger.Debug($"Codec value: {finalCodec} (type: {finalCodec.GetType().Name})");
            Logger.Debug($"Container value: {finalContainer} (type: {finalContainer.GetType().Name})");

            var mp4Step = e.Nodes.AddTypedNode(
                new SaveAnimatedMP4
                {
                    Name = e.Nodes.GetUniqueName("SaveAnimatedMP4"),
                    Images = image,
                    FilenamePrefix = "InferenceVideo",
                    Fps = Fps,
                    Crf = Crf,
                    Codec = finalCodec,
                    Container = finalContainer,
                    Bitrate = Bitrate
                }
            );

            // ✅ CORRECT: Add to OutputNodes - this registers it as an output
            e.Builder.Connections.OutputNodes.Add(mp4Step);
            Logger.Info($"MP4 node added to outputs: {mp4Step.Name} (CRF={Crf}, Codec={finalCodec}, Container={finalContainer}, Bitrate={Bitrate}kbps)");
        }
        catch (InvalidOperationException ex)
        {
            Logger.Error(ex, "Invalid video output configuration");
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to apply video output settings");
            throw;
        }
    }
}
