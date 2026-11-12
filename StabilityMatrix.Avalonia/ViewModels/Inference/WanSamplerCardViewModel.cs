using System.ComponentModel.DataAnnotations;
using System.Linq;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Inference.Modules;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Extensions;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(SamplerCard))]
[ManagedService]
[RegisterTransient<WanSamplerCardViewModel>]
public class WanSamplerCardViewModel : SamplerCardViewModel
{
    public WanSamplerCardViewModel(
        IInferenceClientManager clientManager,
        IServiceManager<ViewModelBase> vmFactory,
        ISettingsManager settingsManager,
        TabContext tabContext
    )
        : base(clientManager, vmFactory, settingsManager, tabContext)
    {
        EnableAddons = false;
        IsLengthEnabled = true;
        SelectedSampler = ComfySampler.UniPC;
        SelectedScheduler = ComfyScheduler.Simple;
        Length = 33;
    }

    /// <summary>
    /// When true, builds a Wan 2.2 TI2V (5B) graph: <c>Wan22ImageToVideoLatent</c> (48-channel,
    /// /16 latents with a <c>noise_mask</c> holding the encoded start image) and connects the
    /// prompt conditioning directly to the sampler.
    /// <para>
    /// When false, builds the Wan 2.1 I2V graph: <c>WanImageToVideo</c> (16-channel, /8 latents
    /// with <c>concat_latent_image</c> / <c>concat_mask</c> conditioning and optional CLIP Vision).
    /// </para>
    /// TI2V models such as Wan2.2-TI2V-5B-Q4_K_S do not read the Wan 2.1 concat conditioning,
    /// so using the Wan 2.1 graph makes them silently ignore the reference image.
    /// </summary>
    public bool UseWan22Ti2V { get; set; }

    public override void ApplyStep(ModuleApplyStepEventArgs e)
    {
        if (EnableAddons)
        {
            foreach (var module in ModulesCardViewModel.Cards.OfType<ModuleBase>())
            {
                module.ApplyStep(e);
            }
        }

        // Set primary sampler and scheduler
        var primarySampler = SelectedSampler ?? throw new ValidationException("Sampler not selected");
        e.Builder.Connections.PrimarySampler = primarySampler;

        var primaryScheduler = SelectedScheduler ?? throw new ValidationException("Scheduler not selected");
        e.Builder.Connections.PrimaryScheduler = primaryScheduler;

        // for later inheritance if needed
        e.Builder.Connections.PrimaryCfg = CfgScale;
        e.Builder.Connections.PrimarySteps = Steps;
        e.Builder.Connections.PrimaryModelType = SelectedModelType;

        e.Temp = e.CreateTempFromBuilder();

        var conditioning = e.Temp.Base.Conditioning.Unwrap();

        var isImgToVid = IsDenoiseStrengthEnabled;

        if (isImgToVid)
        {
            ConditioningNodeConnection positive;
            ConditioningNodeConnection negative;
            LatentNodeConnection latentImage;

            if (UseWan22Ti2V)
            {
                // Wan 2.2 TI2V 5B: the reference image must become an actual latent sample.
                // Wan22ImageToVideoLatent VAE-encodes it into the first latent frames and emits
                // a noise_mask so the sampler cannot overwrite those frames. No CLIP Vision and
                // no concat conditioning are involved - the prompt conditioning goes straight
                // into the sampler.
                var ti2vLatent = e.Nodes.AddTypedNode(
                    new ComfyNodeBuilder.Wan22ImageToVideoLatent
                    {
                        Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.Wan22ImageToVideoLatent)),
                        Vae = e.Builder.Connections.GetDefaultVAE(),
                        StartImage = e.Builder.GetPrimaryAsImage(),
                        Width = Width,
                        Height = Height,
                        Length = Length,
                        BatchSize = e.Builder.Connections.BatchSize,
                    }
                );

                positive = conditioning.Positive;
                negative = conditioning.Negative;
                latentImage = ti2vLatent.Output;
            }
            else
            {
                // Wan 2.1 I2V: CLIP Vision is optional. Only build CLIPVisionEncode when a
                // CLIP Vision model was actually loaded, otherwise WanImageToVideo is built
                // without the clip_vision_output input.
                var baseClipVision = e.Builder.Connections.BaseClipVision;
                ComfyNodeBuilder.CLIPVisionEncode? clipVisionEncode = null;
                if (baseClipVision is not null)
                {
                    clipVisionEncode = e.Nodes.AddTypedNode(
                        new ComfyNodeBuilder.CLIPVisionEncode
                        {
                            Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.CLIPVisionEncode)),
                            ClipVision = baseClipVision,
                            Image = e.Builder.GetPrimaryAsImage(),
                            Crop = "none",
                        }
                    );
                }

                var wanImageToVideo = e.Nodes.AddTypedNode(
                    new ComfyNodeBuilder.WanImageToVideo
                    {
                        Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.WanImageToVideo)),
                        Positive = conditioning.Positive,
                        Negative = conditioning.Negative,
                        Vae = e.Builder.Connections.GetDefaultVAE(),
                        ClipVisionOutput = clipVisionEncode?.Output,
                        StartImage = e.Builder.GetPrimaryAsImage(),
                        Width = Width,
                        Height = Height,
                        Length = Length,
                        BatchSize = e.Builder.Connections.BatchSize,
                    }
                );

                positive = wanImageToVideo.Output1;
                negative = wanImageToVideo.Output2;
                latentImage = wanImageToVideo.Output3;
            }

            var kSampler = e.Nodes.AddTypedNode(
                new ComfyNodeBuilder.KSampler
                {
                    Name = "Sampler",
                    Model = e.Temp.Base.Model!.Unwrap(),
                    Seed = e.Builder.Connections.Seed,
                    SamplerName = primarySampler.Name,
                    Scheduler = primaryScheduler.Name,
                    Steps = Steps,
                    Cfg = CfgScale,
                    Positive = positive,
                    Negative = negative,
                    LatentImage = latentImage,
                    Denoise = DenoiseStrength,
                }
            );
            e.Builder.Connections.Primary = kSampler.Output;
        }
        else
        {
            var primaryLatent = e.Builder.GetPrimaryAsLatent(
                e.Temp.Primary!.Unwrap(),
                e.Builder.Connections.GetDefaultVAE()
            );

            var kSampler = e.Nodes.AddTypedNode(
                new ComfyNodeBuilder.KSampler
                {
                    Name = "Sampler",
                    Model = e.Temp.Base.Model!.Unwrap(),
                    Seed = e.Builder.Connections.Seed,
                    SamplerName = primarySampler.Name,
                    Scheduler = primaryScheduler.Name,
                    Steps = Steps,
                    Cfg = CfgScale,
                    Positive = conditioning.Positive,
                    Negative = conditioning.Negative,
                    LatentImage = primaryLatent,
                    Denoise = DenoiseStrength,
                }
            );
            e.Builder.Connections.Primary = kSampler.Output;
        }
    }
}
