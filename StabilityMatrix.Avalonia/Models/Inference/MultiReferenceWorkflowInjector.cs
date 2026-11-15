using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Avalonia.Models.Inference;

/// <summary>
/// Injects a dynamic number of reference images (1 to 10) into a ComfyUI
/// API-format workflow JSON, then hands the resulting nodes to the inference
/// pipeline. The workflow stays swappable, so this screen is not tied to a
/// particular model (Z-Image-Edit, Qwen-Image-Edit, Flux Kontext, ...).
///
/// Placeholders understood by the injector:
///   @SM_REF_0 .. @SM_REF_9   relative path of the Nth reference image
///   @SM_REFS                 connection to an ImageBatch chain of all references
///   {SM_PROMPT}              positive prompt text
///   {SM_NEGATIVE}            negative prompt text
///   {SM_SEED}                current seed
///   {SM_COUNT}               number of reference images
///   {SM_WIDTH} / {SM_HEIGHT} sampler dimensions
///   "SM_OUTPUT": true        marks a node as an output node (key is removed)
/// </summary>
public static class MultiReferenceWorkflowInjector
{
    private static readonly HashSet<string> OutputClassTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SaveImage",
        "PreviewImage",
        "SaveAnimatedWEBP",
        "SaveAnimatedPNG",
        "SaveWEBM",
        "SaveVideo",
        "VHS_VideoCombine",
        "Image Saver Simple",
        "Image Saver Metadata",
        "SaveImageWebsocket",
    };

    public sealed record InjectResult(
        IReadOnlyList<NamedComfyNode> OutputNodes,
        IReadOnlyList<(string SourcePath, string DestinationRelativePath)> FileTransfers
    );

    private sealed class Context
    {
        public required IReadOnlyList<string> ReferencePaths { get; init; }
        public required object?[] BatchConnection { get; init; }
        public required string Prompt { get; init; }
        public required string NegativePrompt { get; init; }
        public required ulong Seed { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
    }

    /// <summary>
    /// Loads the workflow JSON, resolves every placeholder, and writes the nodes
    /// into <paramref name="nodes"/>.
    /// </summary>
    public static InjectResult Inject(
        NodeDictionary nodes,
        string workflowPath,
        IReadOnlyList<string> referencePaths,
        string prompt,
        string negativePrompt,
        ulong seed,
        int width,
        int height)
    {
        if (!File.Exists(workflowPath))
        {
            throw new FileNotFoundException("Multi-reference workflow JSON was not found", workflowPath);
        }

        if (referencePaths.Count == 0)
        {
            throw new InvalidOperationException("At least one reference image is required");
        }

        var runId = Guid.NewGuid().ToString("N");
        var relativePaths = new List<string>(referencePaths.Count);
        var transfers = new List<(string, string)>();

        for (var i = 0; i < referencePaths.Count; i++)
        {
            var source = referencePaths[i];
            var extension = Path.GetExtension(source);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".png";
            }

            var relative = $"sm_multi_ref/{runId}_{i}{extension}";
            relativePaths.Add(relative);
            transfers.Add((source, "input/" + relative));
        }

        // Build a LoadImage node per reference and chain them with ImageBatch so
        // a workflow can consume all references through one @SM_REFS connection.
        var batchConnection = BuildReferenceBatch(nodes, relativePaths, runId);

        var context = new Context
        {
            ReferencePaths = relativePaths,
            BatchConnection = batchConnection,
            Prompt = prompt,
            NegativePrompt = negativePrompt,
            Seed = seed,
            Width = width,
            Height = height,
        };

        var root = JsonNode.Parse(File.ReadAllText(workflowPath))?.AsObject();
        if (root is null)
        {
            throw new InvalidDataException("Multi-reference workflow JSON is empty or not an object");
        }

        var outputNodes = new List<NamedComfyNode>();

        foreach (var (name, nodeValue) in root)
        {
            if (nodeValue is not JsonObject nodeObject)
            {
                continue;
            }

            if (nodeObject["class_type"]?.GetValue<string>() is not { } classType)
            {
                continue;
            }

            var inputs = new Dictionary<string, object?>();
            var isExplicitOutput = false;

            if (nodeObject["inputs"] is JsonObject inputsObject)
            {
                foreach (var (inputName, inputValue) in inputsObject)
                {
                    if (inputName == "SM_OUTPUT")
                    {
                        isExplicitOutput = inputValue is JsonValue outputFlag
                            && outputFlag.TryGetValue<bool>(out var flag)
                            && flag;
                        continue;
                    }

                    inputs[inputName] = ConvertValue(inputValue, context);
                }
            }

            var comfyNode = new ComfyNode { ClassType = classType, Inputs = inputs };
            nodes[name] = comfyNode;

            if (isExplicitOutput || OutputClassTypes.Contains(classType))
            {
                outputNodes.Add(comfyNode.ToNamedNode(name));
            }
        }

        if (outputNodes.Count == 0)
        {
            throw new InvalidDataException(
                "No output node found in the workflow. Add a SaveImage/PreviewImage node "
                + "or set \"SM_OUTPUT\": true on the node that should produce the result.");
        }

        return new InjectResult(outputNodes, transfers);
    }

    private static object?[] BuildReferenceBatch(
        NodeDictionary nodes,
        IReadOnlyList<string> relativePaths,
        string runId)
    {
        object?[]? connection = null;

        for (var i = 0; i < relativePaths.Count; i++)
        {
            var loadName = $"__smref_{runId}_load_{i}";
            nodes[loadName] = new ComfyNode
            {
                ClassType = "LoadImage",
                Inputs = new Dictionary<string, object?> { ["image"] = relativePaths[i] },
            };

            if (connection is null)
            {
                connection = new object?[] { loadName, 0 };
                continue;
            }

            var batchName = $"__smref_{runId}_batch_{i}";
            nodes[batchName] = new ComfyNode
            {
                ClassType = "ImageBatch",
                Inputs = new Dictionary<string, object?>
                {
                    ["image1"] = connection,
                    ["image2"] = new object?[] { loadName, 0 },
                },
            };

            connection = new object?[] { batchName, 0 };
        }

        return connection ?? throw new InvalidOperationException("No reference images to inject");
    }

    private static object? ConvertValue(JsonNode? node, Context context)
    {
        switch (node)
        {
            case null:
                return null;

            case JsonValue value when value.TryGetValue<string>(out var text):
                return Substitute(text, context);

            case JsonValue value when value.TryGetValue<bool>(out var boolean):
                return boolean;

            case JsonValue value when value.TryGetValue<long>(out var integer):
                return integer;

            case JsonValue value when value.TryGetValue<double>(out var number):
                return number;

            case JsonArray array:
                return array.Select(item => ConvertValue(item, context)).ToList();

            case JsonObject obj:
            {
                var dict = new Dictionary<string, object?>();
                foreach (var (key, item) in obj)
                {
                    dict[key] = ConvertValue(item, context);
                }
                return dict;
            }

            default:
                return JsonSerializer.Deserialize<object>(node.ToJsonString());
        }
    }

    private static object? Substitute(string text, Context context)
    {
        // Whole-token connections / paths
        if (text.StartsWith("@SM_REF_", StringComparison.Ordinal))
        {
            if (int.TryParse(text.AsSpan(8), out var index) && context.ReferencePaths.Count > 0)
            {
                // A workflow may declare more slots than images provided. Reuse
                // earlier references so the graph stays valid for any count 1..10.
                return context.ReferencePaths[index % context.ReferencePaths.Count];
            }
        }

        if (text == "@SM_REFS")
        {
            return context.BatchConnection;
        }

        return text
            .Replace("{SM_PROMPT}", context.Prompt)
            .Replace("{SM_NEGATIVE}", context.NegativePrompt)
            .Replace("{SM_SEED}", context.Seed.ToString())
            .Replace("{SM_COUNT}", context.ReferencePaths.Count.ToString())
            .Replace("{SM_WIDTH}", context.Width.ToString())
            .Replace("{SM_HEIGHT}", context.Height.ToString());
    }
}
