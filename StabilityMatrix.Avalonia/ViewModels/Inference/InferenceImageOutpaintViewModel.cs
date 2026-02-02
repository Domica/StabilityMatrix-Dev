using System.Collections.Generic;
using System.Linq;
using StabilityMatrix.Avalonia.Models;  // DODAJ
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Inference.Modules;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

// ISPRAVI View atribut - treba Views.Inference, ne samo Views
[View(typeof(Views.Inference.InferenceImageOutpaintView))]  // ✅ ISPRAVI
[ManagedService]
[Transient]
public partial class InferenceImageOutpaintViewModel : InferenceGenerationViewModelBase
{
    public const string ModuleKey = "ImageOutpaint";

    public StackCardViewModel StackCardViewModel { get; }

    /// <inheritdoc />
    public InferenceImageOutpaintViewModel(
        ServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager clientManager
    )
        : base(vmFactory, clientManager)
    {
        StackCardViewModel = vmFactory.Get<StackCardViewModel>();

        // Add by default the original cards as steps
        var samplerModule = vmFactory.Get<SamplerModule>(module =>
        {
            module.IsDenoiseEnabled = true;
        });

        ModulesCardViewModel.AddModule<HiresFixModule>(module =>
        {
            module.IsEnabled = false;
        });

        // Add outpaint module
        ModulesCardViewModel.AddModule<OutpaintModule>(module =>
        {
            module.IsEnabled = true; // Enabled by default
        });

        ModulesCardViewModel.AddModule(samplerModule);
    }

    /// <inheritdoc />
    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        var builder = args.Builder;
        var nodes = builder.Nodes;

        // Get selected image
        var imageSource = builder.GetPrimaryInputImage();
        if (imageSource is null)
        {
            return;
        }

        // Load Image
        var loadImage = nodes.AddTypedNode(
            new ComfyNodeBuilder.LoadImage
            {
                Name = "LoadImage",
                Image = imageSource.LocalFile?.ToString() ?? imageSource.RemoteUrl ?? ""
            }
        );

        // Get outpaint settings
        var outpaintModule = ModulesCardViewModel.GetModule<OutpaintModule>();
        if (outpaintModule?.IsEnabled == true)
        {
            // Pad Image for Outpainting
            var padImage = nodes.AddNamedNode(
                ComfyNodeBuilder
                    .NamedNode("PadImageForOutpainting")
                    .WithInput("image", loadImage.Output1!)
                    .WithInput("left", outpaintModule.ExpandLeft)
                    .WithInput("right", outpaintModule.ExpandRight)
                    .WithInput("top", outpaintModule.ExpandTop)
                    .WithInput("bottom", outpaintModule.ExpandBottom)
                    .WithInput("feathering", outpaintModule.Feathering)
            );

            // Update primary image to padded version
            builder.Connections.Primary.Image = padImage.Output1;
            builder.Connections.PrimarySize = builder
                .Connections
                .PrimarySize
                .WithWidth(
                    builder.Connections.PrimarySize.Width
                        + outpaintModule.ExpandLeft
                        + outpaintModule.ExpandRight
                )
                .WithHeight(
                    builder.Connections.PrimarySize.Height
                        + outpaintModule.ExpandTop
                        + outpaintModule.ExpandBottom
                );
        }
        else
        {
            builder.Connections.Primary = builder.Connections.Primary with
            {
                Image = loadImage.Output1
            };
        }

        // Apply modules
        var moduleApplySteps = ModulesCardViewModel.GetModuleApplySteps();
        builder.Connections = moduleApplySteps.Apply(
            builder,
            builder.Connections,
            ModuleApplyStepTemporaryArgs.Default
        );

        // Get output image node
        var outputImage = builder.Connections.Primary.Image;
        if (outputImage is null)
        {
            return;
        }

        // Preview Image
        nodes.AddTypedNode(
            new ComfyNodeBuilder.PreviewImage
            {
                Name = "PreviewImage",
                Images = outputImage
            }
        );

        // Save Image
        nodes.AddTypedNode(
            new ComfyNodeBuilder.SaveImage
            {
                Name = "SaveImage",
                Images = outputImage,
                FilenamePrefix = "outpaint"
            }
        );
    }

    /// <inheritdoc />
    protected override IEnumerable<ImageSource> GetInputImages()
    {
        var selectImageCard = StackCardViewModel.GetCard<SelectImageCardViewModel>();
    if (selectImageCard?.ImageSource?.LocalFile is { } localFile)
    {
        yield return new ImageSource(localFile);
    }
    }
}
