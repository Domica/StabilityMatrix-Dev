using System;
using System.Collections.Generic;
using System.Linq;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;

namespace StabilityMatrix.Avalonia.Models.Inference;

/// <summary>
/// How thoroughly Florence-2 describes each reference image. More passes return
/// complementary information (a paragraph, a short caption, per-region labels)
/// at the cost of a slower caption-only pass.
/// </summary>
public enum CaptionDetailLevel
{
    /// <summary>Original behaviour: one short caption per image.</summary>
    Standard = 0,

    /// <summary>Default: a full paragraph plus a complementary short caption.</summary>
    Rich = 1,

    /// <summary>Rich plus per-region descriptions (people, clothing, objects).</summary>
    Maximum = 2,
}

/// <summary>A single text output produced for one reference image.</summary>
/// <param name="NodeName">Name of the <c>PreviewAny</c> node whose <c>ui.text</c> holds the text.</param>
/// <param name="Label">Human-readable label used in the descriptions panel.</param>
public sealed record CaptionOutput(string NodeName, string Label);

/// <summary>All text outputs belonging to one reference image, in priority order.</summary>
public sealed record CaptionReadback(IReadOnlyList<CaptionOutput> Parts);

/// <summary>
/// Builds a "reference caption fusion" subgraph using the standard
/// <c>comfyui-florence2</c> custom nodes. Every reference image is described by
/// Florence-2 and all descriptions are joined, together with the user's
/// instruction, into one runtime STRING connection which is then fed into the
/// positive CLIPTextEncode.
///
/// Why this exists: Z-Image-Turbo has no image-conditioning input (no siglip /
/// reference-latent weights), so reference images cannot be fed in as identity
/// conditioning. Plain latent img2img does work for a SINGLE image — VAEEncode +
/// low denoise preserves that one subject — but a collage of many references as
/// the starting latent only gives a distilled 8-step sampler structured noise,
/// which produced garbled "alien" outputs. Reading the references and describing
/// them is the only model-agnostic way to make N references influence one
/// Z-Image generation without extra downloads.
///
/// Note: we deliberately use the standard Florence-2 pack, NOT the local
/// "Florence2" fork. The fork unconditionally initializes Mediapipe BlazePose,
/// which crashes on ComfyUI environments with protobuf >= 5 (onnx and
/// google-api-core require it) because FieldDescriptor.label was removed.
/// The standard pack also returns clean captions without outpaint/NSFW tags.
/// </summary>
public static class ReferenceCaptionFusion
{
    /// <summary>
    /// Name of the Florence-2 model directory as exposed under ComfyUI's
    /// <c>models/LLM</c> folder. On this machine a junction points it at the
    /// existing <c>models/Florence2</c> folder, so no download is required.
    /// </summary>
    private const string FlorenceModel = "Florence-2-large";

    private sealed record CaptionPass(string Task, string Label, int MaxTokens, int Beams, bool Sample);

    /// <summary>
    /// Returns the ordered list of Florence-2 passes for one image. All tasks
    /// listed here are supported by the stock <c>Florence2Run</c> node and by
    /// the installed Florence-2 checkpoint (verified in its processing config:
    /// MORE_DETAILED_CAPTION and DETAILED_CAPTION are pure-text tasks,
    /// DENSE_REGION_CAPTION emits region labels).
    /// </summary>
    private static IReadOnlyList<CaptionPass> GetPasses(CaptionDetailLevel level) => level switch
    {
        CaptionDetailLevel.Standard =>
        [
            new CaptionPass("detailed_caption", "Description", 160, 3, false),
        ],
        CaptionDetailLevel.Maximum =>
        [
            new CaptionPass("more_detailed_caption", "Full description", 384, 3, false),
            new CaptionPass("detailed_caption", "Key details", 256, 3, false),
            new CaptionPass("dense_region_caption", "Detected regions", 384, 3, false),
        ],
        _ =>
        [
            new CaptionPass("more_detailed_caption", "Full description", 384, 3, false),
            new CaptionPass("detailed_caption", "Key details", 256, 3, false),
        ],
    };

    /// <summary>
    /// Builds a standalone CAPTION-ONLY graph: one <c>LoadImage</c> per reference
    /// plus one or more <c>Florence2Run</c> + core <c>PreviewAny</c> pairs. It is
    /// queued ahead of the real generation so the app can read the captions back
    /// from ComfyUI history, merge them, compose them in C# with
    /// <see cref="ReferenceSceneComposer"/>, and only then run the diffusion graph
    /// with a plain-string prompt.
    ///
    /// <c>PreviewAny</c> is part of ComfyUI core (<c>comfy_extras/nodes_preview_any.py</c>)
    /// and emits <c>ui.text</c>, which <c>ComfyClient.GetTextOutputsForExecutedPromptAsync</c>
    /// reads. No additional custom node pack is needed for this pass.
    /// </summary>
    /// <returns>
    /// The ready-to-queue node dictionary and, per reference image, the list of
    /// text outputs to read back (in the same order as
    /// <paramref name="imageRelativePaths"/>).
    /// </returns>
    public static (NodeDictionary Nodes, IReadOnlyList<CaptionReadback> Readbacks) BuildCaptionGraph(
        IReadOnlyList<string> imageRelativePaths,
        CaptionDetailLevel detailLevel = CaptionDetailLevel.Rich
    )
    {
        if (imageRelativePaths.Count == 0)
        {
            throw new ArgumentException(
                "At least one reference image is required",
                nameof(imageRelativePaths)
            );
        }

        var builder = new ComfyNodeBuilder();
        var nodes = builder.Nodes;

        var loader = AddNode(
            nodes,
            "Florence2ModelLoader",
            new Dictionary<string, object?> { ["model"] = FlorenceModel, ["precision"] = "fp16" },
            "MultiRefCaptionPassLoader"
        );
        var florenceModel = loader.GetOutput<StringNodeConnection>(0).Data;

        var passes = GetPasses(detailLevel);
        var totalPasses = imageRelativePaths.Count * passes.Count;
        var readbacks = new List<CaptionReadback>(imageRelativePaths.Count);
        var globalPass = 0;

        for (var index = 0; index < imageRelativePaths.Count; index++)
        {
            var image = AddNode(
                    nodes,
                    "LoadImage",
                    new Dictionary<string, object?> { ["image"] = imageRelativePaths[index] },
                    $"MultiRefCaptionPassLoad_{index}"
                )
                .GetOutput<ImageNodeConnection>(0)
                .Data;

            var parts = new List<CaptionOutput>(passes.Count);

            for (var passIndex = 0; passIndex < passes.Count; passIndex++)
            {
                var pass = passes[passIndex];
                globalPass++;

                var caption = AddNode(
                        nodes,
                        "Florence2Run",
                        new Dictionary<string, object?>
                        {
                            ["image"] = image,
                            ["florence2_model"] = florenceModel,
                            ["text_input"] = "",
                            ["task"] = pass.Task,
                            ["fill_mask"] = false,
                            // Keep the VLM resident until the last pass of the last
                            // image, then let ComfyUI free its VRAM before diffusion.
                            ["keep_model_loaded"] = globalPass < totalPasses,
                            ["max_new_tokens"] = pass.MaxTokens,
                            ["num_beams"] = pass.Beams,
                            ["do_sample"] = pass.Sample,
                        },
                        $"MultiRefCaptionPass_{index}_{passIndex}"
                    )
                    .GetOutput<StringNodeConnection>(2)
                    .Data;

                var preview = AddNode(
                    nodes,
                    "PreviewAny",
                    new Dictionary<string, object?> { ["source"] = caption },
                    $"MultiRefCaptionText_{index}_{passIndex}"
                );

                parts.Add(new CaptionOutput(preview.Name, pass.Label));
            }

            readbacks.Add(new CaptionReadback(parts));
        }

        return (builder.ToNodeDictionary(), readbacks);
    }

    /// <summary>
    /// Adds the reference <c>LoadImage</c> + Florence-2 + <c>JoinStrings</c> nodes
    /// to <paramref name="builder"/> and returns the final prompt connection.
    /// This is the in-graph fallback used when caption read-back is unavailable.
    /// </summary>
    /// <param name="builder">Graph builder for the current generation.</param>
    /// <param name="imageRelativePaths">
    /// Paths of the already-uploaded references, relative to ComfyUI's input folder.
    /// </param>
    /// <param name="userPrompt">The instruction typed in the Prompt card.</param>
    /// <param name="panelCount">1 = single undivided image; 2-6 = clean grid of that many panels.</param>
    public static StringNodeConnection Build(
        ComfyNodeBuilder builder,
        IReadOnlyList<string> imageRelativePaths,
        string userPrompt,
        int panelCount = 1
    )
    {
        if (imageRelativePaths.Count == 0)
        {
            throw new ArgumentException(
                "At least one reference image is required",
                nameof(imageRelativePaths)
            );
        }

        var nodes = builder.Nodes;

        // --- Florence-2 model (loaded once, shared by every caption node).
        var florenceLoader = AddNode(
            nodes,
            "Florence2ModelLoader",
            new Dictionary<string, object?>
            {
                ["model"] = FlorenceModel,
                ["precision"] = "fp16",
            },
            "MultiRefFlorenceLoader"
        );
        var florenceModel = florenceLoader.GetOutput<StringNodeConnection>(0).Data;

        // --- One LoadImage node per reference (files are uploaded by the caller).
        var images = imageRelativePaths
            .Select((path, index) =>
                AddNode(
                        nodes,
                        "LoadImage",
                        new Dictionary<string, object?> { ["image"] = path },
                        $"MultiRefLoad_{index}"
                    )
                    .GetOutput<ImageNodeConnection>(0)
                    .Data
            )
            .ToList();

        // The user's instruction is the FRAME of the prompt, not an appendix.
        // Earlier builds prefixed a fixed "group photo" sentence and then listed
        // eight captions as "Reference N: <a whole separate scene>". Z-Image
        // interpreted that literally and drew a collage of eight panels, while
        // the actual instruction was drowned out. So: instruction first and last,
        // short captions joined as a plain list, and an explicit anti-collage cue.
        var instruction = string.IsNullOrWhiteSpace(userPrompt)
            ? "A single group photograph of all the people together."
            : userPrompt.Trim();

        var panels = Math.Clamp(panelCount, 1, 6);
        var isGrid = panels > 1;

        var headNode = AddNode(
            nodes,
            "StringConstant",
            new Dictionary<string, object?>
            {
                ["string"] = isGrid
                    ? $"A clean grid of exactly {panels} equal panels with thin white borders, all showing the same scene. "
                      + $"{instruction} The same people together in every panel: "
                    : $"One single continuous, seamless photograph. {instruction} Everyone together in this one photo: ",
            },
            "MultiRefInstruction"
        );
        var accumulated = headNode.GetOutput<StringNodeConnection>(0);

        // --- Describe each reference briefly. No "Reference N" labels: those
        //     encourage the diffusion model to render a multi-panel collage.
        for (var index = 0; index < images.Count; index++)
        {
            var captionNode = AddNode(
                nodes,
                "Florence2Run",
                new Dictionary<string, object?>
                {
                    ["image"] = images[index],
                    ["florence2_model"] = florenceModel,
                    ["text_input"] = "",
                    ["task"] = "detailed_caption",
                    ["fill_mask"] = false,
                    // Keep the VLM resident for all but the last pass, then let
                    // ComfyUI free its VRAM before the diffusion model loads.
                    ["keep_model_loaded"] = index < images.Count - 1,
                    ["max_new_tokens"] = 256,
                    ["num_beams"] = 3,
                    ["do_sample"] = false,
                },
                $"MultiRefCaption_{index}"
            );
            var caption = captionNode.GetOutput<StringNodeConnection>(2);

            var joinNode = AddNode(
                nodes,
                "JoinStrings",
                new Dictionary<string, object?>
                {
                    ["delimiter"] = index == 0 ? "" : ", ",
                    ["string1"] = accumulated.Data,
                    ["string2"] = caption.Data,
                },
                $"MultiRefJoin_{index}"
            );

            accumulated = joinNode.GetOutput<StringNodeConnection>(0);
        }

        // Recap at the very end: recency matters for long prompts, and this is
        // what actually makes the requested scene win over the caption list.
        var tailText = isGrid
            ? $". Exactly {panels} panels, no more and no fewer, all showing this one scene: {instruction} "
              + "No text, no captions, no nested images."
            : $". One single seamless, edge-to-edge photo of this one scene: {instruction} "
              + "One continuous frame, one moment, one place, everyone together in the same space.";

        var tailNode = AddNode(
            nodes,
            "StringConstant",
            new Dictionary<string, object?> { ["string"] = tailText },
            "MultiRefRecap"
        );

        var finalNode = AddNode(
            nodes,
            "JoinStrings",
            new Dictionary<string, object?>
            {
                ["delimiter"] = "",
                ["string1"] = accumulated.Data,
                ["string2"] = tailNode.GetOutput<StringNodeConnection>(0).Data,
            },
            "MultiRefFinal"
        );

        return finalNode.GetOutput<StringNodeConnection>(0);
    }

    private static NamedComfyNode AddNode(
        NodeDictionary nodes,
        string classType,
        Dictionary<string, object?> inputs,
        string nameBase
    )
    {
        return nodes.AddNamedNode(
            new ComfyNode { ClassType = classType, Inputs = inputs }
                .ToNamedNode(nodes.GetUniqueName(nameBase))
        );
    }
}
