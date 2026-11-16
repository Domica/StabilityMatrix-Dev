using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Core.Models.FileInterfaces;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(UnetModelCard))]
[ManagedService]
[RegisterTransient<UnetModelCardViewModel>]
public partial class UnetModelCardViewModel(IInferenceClientManager clientManager)
    : LoadableViewModelBase,
        IParametersLoadableState,
        IComfyStep
{
    [ObservableProperty]
    private HybridModelFile? selectedModel;

    [ObservableProperty]
    private HybridModelFile? selectedVae;

    [ObservableProperty]
    private HybridModelFile? selectedClip1;

    [ObservableProperty]
    private HybridModelFile? selectedClip2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMossClip))]
    private string? selectedClipType;

    [ObservableProperty]
    private string selectedDType = "default";

    public List<string> WeightDTypes { get; set; } = ["default", "fp8_e4m3fn", "fp8_e5m2"];

    public List<string> ClipTypes { get; set; } = ["flux", "sd3", "HiDream", "moss_image"];

    public bool IsMossClip => SelectedClipType == "moss_image";

    public IInferenceClientManager ClientManager { get; } = clientManager;

    public async Task<bool> ValidateModel()
    {
        if (SelectedModel != null)
            return true;

        var dialog = DialogHelper.CreateMarkdownDialog(
            "Please select a model to continue.",
            "No Model Selected"
        );
        await dialog.ShowAsync();
        return false;
    }

    public void ApplyStep(ModuleApplyStepEventArgs e)
    {
        var checkpointLoader = e.Nodes.AddTypedNode(GetModelLoader(e, SelectedModel!, SelectedDType));
        e.Builder.Connections.Base.Model = checkpointLoader.Output;

        // Auto-detect Flux 2 VAE and select compatible VAE if none selected
        if (
            SelectedModel?.Local is { } localModel
            && localModel.IsFlux2VaeArchitecture
            && (SelectedVae is null || SelectedVae.IsDefault)
        )
        {
            var fluxVae = ClientManager.VaeModels.FirstOrDefault(v =>
                v.RelativePath.Contains("ae.safetensors", StringComparison.OrdinalIgnoreCase)
                || v.RelativePath.Contains("flux", StringComparison.OrdinalIgnoreCase)
            );

            if (fluxVae != null)
                SelectedVae = fluxVae;
        }

        // Auto-select Qwen3.5 as CLIP 1 for MOSS-Image if type is moss_image and not already selected
        // IMPORTANT: MOSS-Image requires the official safetensors Qwen3.5 text encoder
        // (e.g. qwen3.5_9b_w4a8_gptq.safetensors / qwen3.5_9b_int8_convrot.safetensors).
        // A generic llama.cpp Qwen3.5 GGUF is NOT a valid MOSS encoder and produces
        // unprompted collage images even when the GGUF loader accepts it, so we prefer
        // the safetensors TE and only fall back to a GGUF if nothing else is present.
        if (IsMossClip && (SelectedClip1 == null || SelectedClip1.IsNone))
        {
            var mossQwenClips = ClientManager
                .ClipModels.Where(c =>
                    (
                        c.RelativePath.Contains("qwen", StringComparison.OrdinalIgnoreCase)
                        || c.RelativePath.Contains("Qwen", StringComparison.Ordinal)
                    ) && c.RelativePath.Contains("3.5", StringComparison.Ordinal)
                )
                .ToList();

            // Prefer the official safetensors MOSS text encoder (not .gguf).
            var qwenClip =
                mossQwenClips.FirstOrDefault(c =>
                    !c.RelativePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                )
                // Then any safetensors qwen, then any qwen (gguf) as last resort.
                ?? mossQwenClips.FirstOrDefault()
                ?? ClientManager.ClipModels.FirstOrDefault(c =>
                    c.RelativePath.Contains("qwen", StringComparison.OrdinalIgnoreCase)
                );
            if (qwenClip != null)
                SelectedClip1 = qwenClip;
        }
        else
        {
            // Auto-select Flux CLIP models for regular flux type
            if (SelectedClip1 == null || SelectedClip1.IsNone)
            {
                var clipL = ClientManager.ClipModels.FirstOrDefault(c =>
                    c.RelativePath.Contains("clip_l", StringComparison.OrdinalIgnoreCase)
                );
                if (clipL != null)
                    SelectedClip1 = clipL;
            }

            if (SelectedClip2 == null || SelectedClip2.IsNone)
            {
                var clipG = ClientManager.ClipModels.FirstOrDefault(c =>
                    c.RelativePath.Contains("clip_g", StringComparison.OrdinalIgnoreCase)
                );
                if (clipG != null)
                    SelectedClip2 = clipG;
            }
        }

        var vaeLoader = e.Nodes.AddTypedNode(
            new ComfyNodeBuilder.VAELoader
            {
                Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.VAELoader)),
                VaeName = SelectedVae?.RelativePath ?? throw new ValidationException("No VAE Selected"),
            }
        );
        e.Builder.Connections.Base.VAE = vaeLoader.Output;

        // CLIP loading: single loader for moss_image (MOSS-Image Qwen3.5), DualCLIPLoader for flux
        if (IsMossClip && SelectedClip1 is { IsNone: false })
        {
            // MOSS-Image uses a single Qwen3.5 text encoder.
            // - safetensors -> stock CLIPLoader (natively supported by ComfyUI core)
            // - gguf        -> CLIPLoaderGGUF (requires ComfyUI-GGUF with qwen35 support)
            var isGgufClip = SelectedClip1.RelativePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);

            ClipNodeConnection clipConnection;

            if (isGgufClip)
            {
                var clipLoader = e.Nodes.AddTypedNode(
                    new ComfyNodeBuilder.CLIPLoaderGGUF
                    {
                        Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.CLIPLoaderGGUF)),
                        ClipName = SelectedClip1.RelativePath,
                        Type = "moss_image",
                    }
                );
                clipConnection = clipLoader.Output;
            }
            else
            {
                var clipLoader = e.Nodes.AddTypedNode(
                    new ComfyNodeBuilder.CLIPLoader
                    {
                        Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.CLIPLoader)),
                        ClipName = SelectedClip1.RelativePath,
                        Type = "moss_image",
                    }
                );
                clipConnection = clipLoader.Output;
            }

            e.Builder.Connections.Base.Clip = clipConnection;
        }
        else
        {
            // DualCLIPLoader for flux/sd3/HiDream (requires both clip_name1 and clip_name2)
            var clipLoader = e.Nodes.AddTypedNode(
                new ComfyNodeBuilder.DualCLIPLoader
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.DualCLIPLoader)),
                    ClipName1 =
                        SelectedClip1?.RelativePath ?? throw new ValidationException("No Clip1 Selected"),
                    ClipName2 =
                        SelectedClip2?.RelativePath ?? throw new ValidationException("No Clip2 Selected"),
                    Type = SelectedClipType ?? "flux",
                }
            );
            e.Builder.Connections.Base.Clip = clipLoader.Output;
        }
    }

    private static ComfyTypedNodeBase<ModelNodeConnection> GetModelLoader(
        ModuleApplyStepEventArgs e,
        HybridModelFile model,
        string selectedDType
    )
    {
        // Simple loader for UNET
        return new ComfyNodeBuilder.UNETLoader
        {
            Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.UNETLoader)),
            UnetName = model.RelativePath,
            WeightDtype = selectedDType,
        };
    }

    /// <inheritdoc />
    public override JsonObject SaveStateToJsonObject()
    {
        return SerializeModel(
            new UnetModelCardModel
            {
                SelectedModelName = SelectedModel?.RelativePath,
                SelectedVaeName = SelectedVae?.RelativePath,
                SelectedClip1Name = SelectedClip1?.RelativePath,
                SelectedClip2Name = SelectedClip2?.RelativePath,
                SelectedClipType = SelectedClipType,
            }
        );
    }

    /// <inheritdoc />
    public override void LoadStateFromJsonObject(JsonObject state)
    {
        var model = DeserializeModel<UnetModelCardModel>(state);

        SelectedModel = model.SelectedModelName is null
            ? null
            : ClientManager.UnetModels.FirstOrDefault(x => x.RelativePath == model.SelectedModelName);

        SelectedVae = model.SelectedVaeName is null
            ? HybridModelFile.Default
            : ClientManager.VaeModels.FirstOrDefault(x => x.RelativePath == model.SelectedVaeName);

        SelectedClip1 = model.SelectedClip1Name is null
            ? HybridModelFile.None
            : ClientManager.ClipModels.FirstOrDefault(x => x.RelativePath == model.SelectedClip1Name);

        SelectedClip2 = model.SelectedClip2Name is null
            ? HybridModelFile.None
            : ClientManager.ClipModels.FirstOrDefault(x => x.RelativePath == model.SelectedClip2Name);

        SelectedClipType = model.SelectedClipType ?? "flux";
    }

    internal class UnetModelCardModel
    {
        public string? SelectedModelName { get; set; }
        public string? SelectedVaeName { get; set; }
        public string? SelectedClip1Name { get; set; }
        public string? SelectedClip2Name { get; set; }
        public string? SelectedClipType { get; set; }
    }

    /// <inheritdoc />
    public void LoadStateFromParameters(GenerationParameters parameters)
    {
        if (parameters.ModelName is not { } paramsModelName)
            return;

        var currentModels = ClientManager.UnetModels;

        HybridModelFile? model;

        // First try hash match
        if (parameters.ModelHash is not null)
        {
            model = currentModels.FirstOrDefault(m =>
                m.Local?.ConnectedModelInfo?.Hashes.SHA256 is { } sha256
                && sha256.StartsWith(parameters.ModelHash, StringComparison.InvariantCultureIgnoreCase)
            );
        }
        else
        {
            // Name matches
            model = currentModels.FirstOrDefault(m => m.RelativePath.EndsWith(paramsModelName));
            model ??= currentModels.FirstOrDefault(m => m.ShortDisplayName.StartsWith(paramsModelName));
        }

        if (model is not null)
        {
            SelectedModel = model;
        }
    }

    /// <inheritdoc />
    public GenerationParameters SaveStateToParameters(GenerationParameters parameters)
    {
        return parameters with
        {
            ModelName = SelectedModel?.FileName,
            ModelHash = SelectedModel?.Local?.ConnectedModelInfo?.Hashes.SHA256,
        };
    }
}
