using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;   // ← OVO JE KLJUČNO (NodeTypes, NE Nodes!)

namespace StabilityMatrix.Avalonia.ViewModels.Inference.Video;

public enum VideoFormat
{
    WebP,
    Mp4
}

[View(typeof(VideoOutputSettingsCard))]
[ManagedService]
[RegisterTransient<VideoOutputSettingsCardViewModel>]
public partial class VideoOutputSettingsCardViewModel
    : LoadableViewModelBase,
        IParametersLoadableState,
        IComfyStep
{
    // Existing fields
    [ObservableProperty]
    private double fps = 6;

    [ObservableProperty]
    private bool lossless = true;

    [ObservableProperty]
    private int quality = 85;

    [ObservableProperty]
    private VideoOutputMethod selectedMethod = VideoOutputMethod.Default;

    [ObservableProperty]
    private List<VideoOutputMethod> availableMethods = Enum.GetValues<VideoOutputMethod>().ToList();

    // -----------------------------
    // NEW MP4 FIELDS
    // -----------------------------
    [ObservableProperty]
    private VideoFormat format = VideoFormat.WebP;

    [ObservableProperty]
    private int crf = 18;

    [ObservableProperty]
    private string codec = "libx264";

    [ObservableProperty]
    private string container = "mp4";

    [ObservableProperty]
    private int bitrate = 4000;

    public bool IsMp4 => Format == VideoFormat.Mp4;

    // -----------------------------
    // LOAD PARAMETERS
    // -----------------------------
    public void LoadStateFromParameters(GenerationParameters parameters)
    {
        Fps = parameters.OutputFps;
        Lossless = parameters.Lossless;
        Quality = parameters.VideoQuality;

        // Load format
        if (!string.IsNullOrWhiteSpace(parameters.VideoFormat) &&
            Enum.TryParse(parameters.VideoFormat, true, out VideoFormat fmt))
        {
            Format = fmt;
        }

        Crf = parameters.VideoCrf;
        Codec = parameters.VideoCodec ?? "libx264";
        Container = parameters.VideoContainer ?? "mp4";
        Bitrate = parameters.VideoBitrate;

        if (!string.IsNullOrWhiteSpace(parameters.VideoOutputMethod))
        {
            SelectedMethod = Enum.TryParse<VideoOutputMethod>(parameters.VideoOutputMethod, true, out var method)
                ? method
                : VideoOutputMethod.Default;
        }
    }

    // -----------------------------
    // SAVE PARAMETERS
    // -----------------------------
    public GenerationParameters SaveStateToParameters(GenerationParameters parameters)
    {
        return parameters with
        {
            OutputFps = Fps,
            Lossless = Lossless,
            VideoQuality = Quality,
            VideoOutputMethod = SelectedMethod.ToString(),

            // MP4 fields
            VideoFormat = Format.ToString(),
            VideoCrf = Crf,
            VideoCodec = Codec,
            VideoContainer = Container,
            VideoBitrate = Bitrate
        };
    }

    // -----------------------------
    // APPLY STEP (COMFY NODE)
    // -----------------------------
    public void ApplyStep(ModuleApplyStepEventArgs e)
    {
        if (e.Builder.Connections.Primary is null)
            throw new ArgumentException("No Primary");

        var image = e.Builder.Connections.Primary.Match(
            _ =>
                e.Builder.GetPrimaryAsImage(
                    e.Builder.Connections.PrimaryVAE
                        ?? e.Builder.Connections.Refiner.VAE
                        ?? e.Builder.Connections.Base.VAE
                        ?? throw new ArgumentException("No Primary, Refiner, or Base VAE")
                ),
            image => image
        );

        // -----------------------------
        // WEBP EXPORT (existing behavior)
        // -----------------------------
        if (Format == VideoFormat.WebP)
        {
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
            return;
        }

        // -----------------------------
        // MP4 EXPORT (new behavior)
        // -----------------------------
        var mp4Step = e.Nodes.AddTypedNode(
            new SaveAnimatedMP4   // ← sada je NodeTypes.SaveAnimatedMP4
            {
                Name = e.Nodes.GetUniqueName("SaveAnimatedMP4"),
                Images = image,
                FilenamePrefix = "InferenceVideo",
                Fps = Fps,
                Crf = Crf,
                Codec = Codec,
                Container = Container
            }
        );

        e.Builder.Connections.OutputNodes.Add(mp4Step);
    }
}
