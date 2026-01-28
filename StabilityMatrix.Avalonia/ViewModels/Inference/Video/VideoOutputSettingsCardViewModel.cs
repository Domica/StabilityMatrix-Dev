using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Injectio.Attributes;
using NLog;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;


namespace StabilityMatrix.Avalonia.ViewModels.Inference.Video;

/// <summary>
/// Výstupný formát videa
/// </summary>
public enum VideoFormat
{
    /// <summary>WebP animirana slika - malo manja datoteka, spora kompresija</summary>
    WebP = 0,

    /// <summary>MP4 video - bolja kompresija, brža obrada</summary>
    Mp4 = 1
}

/// <summary>
/// Metoda kodiranja WebP videa
/// </summary>
public enum VideoOutputMethod
{
    /// <summary>Uobičajeno kodiranje</summary>
    Default = 0,

    /// <summary>Brže kodiranje (niža kvaliteta)</summary>
    Fast = 1,

    /// <summary>Sporije kodiranje (viša kvaliteta)</summary>
    Slow = 2,
}

/// <summary>
/// ViewModel for Video Output Settings Card
/// Handles video export as MP4 or WebP
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
    // OBSERVABLE PROPERTIES – WebP & MP4 Common
    // ============================================================

    /// <summary>
    /// Frames per second (1–120)
    /// </summary>
    [ObservableProperty]
    private double fps = 6;

    /// <summary>
    /// WebP: Lossless compression
    /// </summary>
    [ObservableProperty]
    private bool lossless = true;

    /// <summary>
    /// WebP: Compression quality (0–100)
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
    private List<VideoOutputMethod> availableMethods =
        Enum.GetValues<VideoOutputMethod>().ToList();

    // ============================================================
    // OBSERVABLE PROPERTIES – MP4 Specific
    // ============================================================

    /// <summary>
    /// Selected output format (WebP or MP4)
    /// </summary>
    [ObservableProperty]
    private VideoFormat format = VideoFormat.WebP;

    /// <summary>
    /// MP4: Constant Rate Factor – compression quality (0–51)
    /// Recommended range: 18–28
    /// </summary>
    [ObservableProperty]
    private int crf = 18;

    /// <summary>
    /// MP4: Video codec (libx264, libx265)
    /// </summary>
    [ObservableProperty]
    private string codec = "libx264";

    /// <summary>
    /// MP4: Container format (mp4, mkv)
    /// </summary>
    [ObservableProperty]
    private string container = "mp4";

    /// <summary>
    /// MP4: Bitrate in kbps (500–50000)
    /// </summary>
    [ObservableProperty]
    private int bitrate = 4000;

    // ============================================================
    // PARTIAL METHODS FOR CLAMPING
    // ============================================================

    partial void OnCrfChanging(int value)
    {
        crf = Math.Clamp(value, 0, 51);
    }

    partial void OnBitrateChanging(int value)
    {
        bitrate = Math.Clamp(value, 500, 50000);
    }

    // ============================================================
    // COMPUTED PROPERTIES
    // ============================================================

    /// <summary>
    /// Indicates whether the current format is MP4
    /// </summary>
    public bool IsMp4 => Format == VideoFormat.Mp4;

    // ============================================================
    // STATE MANAGEMENT
    // ============================================================

    /// <summary>
    /// Load state from GenerationParameters
    /// </summary>
    // implementation continues...
}


    public void LoadStateFromParameters(GenerationParameters parameters)
    {
        try
        {
            Fps = Math.Clamp(parameters.OutputFps, 1, 120);
            Lossless = parameters.Lossless;
            Quality = Math.Clamp(parameters.VideoQuality, 0, 100);

            // Učitaj format sa fallback-om
            if (!string.IsNullOrWhiteSpace(parameters.VideoFormat) &&
                Enum.TryParse(parameters.VideoFormat, true, out VideoFormat fmt))
            {
                Format = fmt;
            }

            // MP4 specifične opcije
            Crf = Math.Clamp(parameters.VideoCrf, 0, 51);
            Codec = parameters.VideoCodec ?? "libx264";
            Container = parameters.VideoContainer ?? "mp4";
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
    /// Spremi stanje u GenerationParameters
    /// </summary>
    public GenerationParameters SaveStateToParameters(GenerationParameters parameters)
    {
        try
        {
            // Validacija
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
                VideoCodec = Codec ?? "libx264",
                VideoContainer = Container ?? "mp4",
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
    // COMFY NODE GENERATION
    // ============================================================

    /// <summary>
    /// Primijeni video output korak na Comfy node builder
    /// </summary>
    public void ApplyStep(ModuleApplyStepEventArgs e)
    {
        try
        {
            Logger.Info($"Applying video output: Format={Format}, FPS={Fps}");

            // ========== VALIDACIJA ==========
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

            // Validacija vrijednosti
            if (Fps < 1 || Fps > 120)
                throw new InvalidOperationException($"FPS must be between 1 and 120, got: {Fps}");

            if (Format == VideoFormat.Mp4)
            {
                if (Crf < 0 || Crf > 51)
                    throw new InvalidOperationException($"CRF must be between 0 and 51, got: {Crf}");

                if (Bitrate < 500 || Bitrate > 50000)
                    throw new InvalidOperationException($"Bitrate must be between 500 and 50000 kbps, got: {Bitrate}");

                if (string.IsNullOrWhiteSpace(Codec))
                    throw new InvalidOperationException("Codec cannot be empty");

                if (string.IsNullOrWhiteSpace(Container))
                    throw new InvalidOperationException("Container cannot be empty");
            }

            // ========== KONVERZIJA PRIMARNE KONEKCIJE ==========
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

                e.Builder.Connections.OutputNodes.Add(outputStep);
                Logger.Info($"WebP node added: {outputStep.Name}");
                return;
            }

            // ========== MP4 EXPORT ==========
            Logger.Debug("Creating SaveAnimatedMP4 node");

            var mp4Step = e.Nodes.AddTypedNode(
                new SaveAnimatedMP4
                {
                    Name = e.Nodes.GetUniqueName("SaveAnimatedMP4"),
                    Images = image,
                    FilenamePrefix = "InferenceVideo",
                    Fps = Fps,
                    Crf = Crf,
                    Codec = Codec,
                    Container = Container,
                    Bitrate = Bitrate  // ← VAŽNO: Dodaj Bitrate!
                }
            );

            e.Builder.Connections.OutputNodes.Add(mp4Step);
            Logger.Info($"MP4 node added: {mp4Step.Name} (CRF={Crf}, Codec={Codec}, Bitrate={Bitrate}kbps)");
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
