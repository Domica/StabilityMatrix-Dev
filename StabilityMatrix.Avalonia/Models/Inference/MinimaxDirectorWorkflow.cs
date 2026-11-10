using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Avalonia.Models.Inference;

/// <summary>
/// Values the Director injects into a MiniMax H3 ComfyUI prompt for one window of one clip.
/// </summary>
public sealed class MinimaxDirectorInjection
{
    public required string Prompt { get; init; }
    public long Seed { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Length { get; init; }
    public int Steps { get; init; }
    public double Fps { get; init; }
    public required string FilenamePrefix { get; init; }

    /// <summary>Paths (relative to ComfyUI's input directory) of reference images to feed into the
    /// H3 ref_images slots. Empty = no reference images.</summary>
    public IReadOnlyList<string> ReferenceImageRelativePaths { get; init; } = Array.Empty<string>();

    public bool DisableAudio { get; init; }
    public IReadOnlyList<string> BypassClassTypes { get; init; } = Array.Empty<string>();

    // ------------------------------------------------------------------ advanced model / plugin selection

    /// <summary>Overrides the UNETLoader checkpoint (blank = keep workflow default).</summary>
    public string? ModelName { get; init; }

    /// <summary>Overrides the video VAELoader (blank = keep workflow default).</summary>
    public string? VaeName { get; init; }

    /// <summary>Overrides the CLIPLoader / text encoder (blank = keep workflow default).</summary>
    public string? ClipName { get; init; }

    /// <summary>
    /// Enables one specific LoRA in an LTX_lora_loader stack and disables the others.
    /// Matched by file name (case-insensitive substring). Blank = keep workflow default.
    /// </summary>
    public string? LoraName { get; init; }

    /// <summary>Strength applied to the selected LoRA. Null = keep the stack's existing strength.</summary>
    public double? LoraStrength { get; init; }

    /// <summary>
    /// Overrides the ModelAttentionBackend "attention" field (sdpa, flash_attn, xformers,
    /// sageattention, ...). This is H3's main VRAM/perf lever. Blank = keep workflow default.
    /// </summary>
    public string? AttentionBackend { get; init; }

    // ------------------------------------------------------------------ memory optimizations

    /// <summary>
    /// Swaps the standard VAEDecode node for VAEDecodeTiled (like wan-tiled-VAE) to cut decode
    /// VRAM. Experimental: the targeted VAE decode node must support tiling in the ComfyUI build.
    /// </summary>
    public bool UseTiledVae { get; init; }

    public int VaeTileSize { get; init; } = 256;
}

/// <summary>
/// Loads a ComfyUI workflow (API-format or UI-format), and injects Director values into it.
///
/// Stability Matrix inference screens normally build graphs with ComfyNodeBuilder. MiniMax H3
/// uses a large set of custom/core nodes whose input order is not known at compile time, so the
/// Director instead drives a workflow template: the bundled H3 API prompt, or any workflow the
/// user exports from ComfyUI. This also means new H3 nodes / LoRAs never require a code change.
/// </summary>
public sealed class MinimaxDirectorWorkflow
{
    private static readonly HashSet<string> VirtualNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Note",
        "MarkdownNote",
        "Reroute",
        "Label (rgthree)",
        "Fast Groups Bypasser (rgthree)",
        "Bookmark (rgthree)",
        "PrimitiveNode",
    };

    private static readonly string[] OutputClassPrefixes =
    {
        "Save",
        "PreviewImage",
        "VHS_VideoCombine",
        "SaveAnimated",
    };

    public NodeDictionary Nodes { get; private set; } = new();

    public List<string> OutputNodeNames { get; private set; } = new();

    public string SourceLabel { get; private set; } = "";

    /// <summary>Human-readable description of the auto-detected injection points.</summary>
    public string MappingSummary { get; private set; } = "";

    public static MinimaxDirectorWorkflow FromJson(string json, string sourceLabel)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ValidationException("Workflow root must be a JSON object.");
        }

        var workflow = IsApiFormat(root)
            ? FromApi(root.GetRawText(), sourceLabel)
            : FromUi(root.GetRawText(), sourceLabel);

        if (workflow.Nodes.Count == 0)
        {
            throw new ValidationException("Workflow contains no usable nodes.");
        }

        workflow.DetectOutputs();
        workflow.MappingSummary = workflow.DescribeMapping();
        return workflow;
    }

    /// <summary>A deep copy so every clip/window gets its own independent node graph.</summary>
    public MinimaxDirectorWorkflow Clone()
    {
        var copy = new MinimaxDirectorWorkflow { SourceLabel = SourceLabel };

        foreach (var (name, node) in Nodes)
        {
            copy.Nodes[name] = new ComfyNode
            {
                ClassType = node.ClassType,
                Inputs = node.Inputs.ToDictionary(x => x.Key, x => DeepClone(x.Value)),
            };
        }

        copy.OutputNodeNames = new List<string>(OutputNodeNames);
        copy.MappingSummary = MappingSummary;
        return copy;
    }

    /// <summary>Applies per-window values. Call on a clone.</summary>
    public void Apply(MinimaxDirectorInjection injection)
    {
        SetRole("prompt", injection.Prompt);
        SetRole("seed", injection.Seed);
        SetRole("width", injection.Width);
        SetRole("height", injection.Height);
        SetRole("length", injection.Length);
        SetRole("steps", injection.Steps);
        SetRole("fps", injection.Fps);
        SetRole("prefix", injection.FilenamePrefix);

        if (!string.IsNullOrWhiteSpace(injection.ModelName))
        {
            SetRole("unet_name", injection.ModelName);
        }

        if (!string.IsNullOrWhiteSpace(injection.VaeName))
        {
            SetRole("vae_name", injection.VaeName);
        }

        if (!string.IsNullOrWhiteSpace(injection.ClipName))
        {
            SetRole("clip_name", injection.ClipName);
        }

        if (!string.IsNullOrWhiteSpace(injection.AttentionBackend))
        {
            SetAttentionBackend(injection.AttentionBackend!);
        }

        if (!string.IsNullOrWhiteSpace(injection.LoraName))
        {
            SetLora(injection.LoraName!, injection.LoraStrength);
        }

        if (injection.UseTiledVae)
        {
            EnableTiledVae(injection.VaeTileSize);
        }

        if (injection.DisableAudio)
        {
            StripAudio();
        }

        foreach (var classType in injection.BypassClassTypes)
        {
            BypassNodesOfType(classType);
        }

        if (injection.ReferenceImageRelativePaths.Count > 0)
        {
            InjectReferenceImages(injection.ReferenceImageRelativePaths);
        }

        DetectOutputs();
        MappingSummary = DescribeMapping();
    }

    /// <summary>Serializes back to API-format JSON (useful for diagnostics / export).</summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(Nodes, new JsonSerializerOptions { WriteIndented = true });
    }

    // ------------------------------------------------------------------ loading

    private static bool IsApiFormat(JsonElement root)
    {
        if (root.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            return false;
        }

        var seen = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!property.Value.TryGetProperty("class_type", out _))
            {
                return false;
            }

            if (++seen >= 3)
            {
                break;
            }
        }

        return seen > 0;
    }

    private static MinimaxDirectorWorkflow FromApi(string json, string sourceLabel)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, ComfyNode>>(json)
            ?? throw new ValidationException("Could not parse API-format workflow.");

        var workflow = new MinimaxDirectorWorkflow { SourceLabel = sourceLabel };

        foreach (var (name, node) in parsed)
        {
            workflow.Nodes[name] = new ComfyNode
            {
                ClassType = node.ClassType,
                Inputs = node.Inputs.ToDictionary(x => x.Key, x => NormalizeValue(x.Value)),
            };
        }

        return workflow;
    }

    /// <summary>
    /// Converts a ComfyUI UI-format workflow into an API prompt. Supports:
    /// widgets_values_named, linked inputs, bypassed pass-through nodes, virtual nodes,
    /// and subgraph instances whose outputs are exposed as literal values.
    /// </summary>
    private static MinimaxDirectorWorkflow FromUi(string json, string sourceLabel)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("nodes", out var nodesElement) || nodesElement.ValueKind != JsonValueKind.Array)
        {
            throw new ValidationException("UI-format workflow is missing its 'nodes' array.");
        }

        var nodes = nodesElement.EnumerateArray().Select(x => x.Clone()).ToList();
        var nodesById = new Dictionary<int, JsonElement>();
        foreach (var node in nodes)
        {
            nodesById[GetInt(node, "id")] = node;
        }

        var links = new Dictionary<int, JsonElement>();
        if (root.TryGetProperty("links", out var linksElement) && linksElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in linksElement.EnumerateArray())
            {
                if (link.ValueKind == JsonValueKind.Array && link.GetArrayLength() >= 3)
                {
                    links[(int)link[0].GetDouble()] = link.Clone();
                }
            }
        }

        // Subgraph instances are not part of the API format. Their outputs are exposed to the
        // main graph as links, so replace those links with their literal widget values.
        var subgraphLiterals = new Dictionary<int, object?>();
        foreach (var node in nodes)
        {
            var type = GetString(node, "type");
            if (!Guid.TryParse(type, out _))
            {
                continue;
            }

            var widgets = node.TryGetProperty("widgets_values", out var wv) && wv.ValueKind == JsonValueKind.Array
                ? wv.EnumerateArray().Select(ElementToClr).ToArray()
                : Array.Empty<object?>();

            var prompt = widgets.OfType<string>().FirstOrDefault(x => x.Length > 60) ?? "";
            var duration = widgets.FirstOrDefault(x => (x is double or long) && x is not bool);
            var outputValues = new object?[] { prompt, duration ?? 0.4d, 512, 224 };

            if (!node.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var slot = 0;
            foreach (var output in outputs.EnumerateArray())
            {
                var value = slot < outputValues.Length ? outputValues[slot] : null;
                if (output.TryGetProperty("links", out var linkIds) && linkIds.ValueKind == JsonValueKind.Array)
                {
                    foreach (var linkId in linkIds.EnumerateArray())
                    {
                        if (linkId.ValueKind == JsonValueKind.Number)
                        {
                            subgraphLiterals[(int)linkId.GetDouble()] = value;
                        }
                    }
                }

                slot++;
            }
        }

        (object? Value, bool Ok) ResolveLink(int linkId, int depth)
        {
            if (depth > 16)
            {
                return (null, false);
            }

            if (subgraphLiterals.TryGetValue(linkId, out var literal))
            {
                return (literal, true);
            }

            if (!links.TryGetValue(linkId, out var link) || link.GetArrayLength() < 3)
            {
                return (null, false);
            }

            var originId = (int)link[1].GetDouble();
            var originSlot = (int)link[2].GetDouble();

            if (!nodesById.TryGetValue(originId, out var origin) || IsVirtual(origin))
            {
                return (null, false);
            }

            if (GetInt(origin, "mode") == 4)
            {
                // Bypassed node: forward the first linked input (matching-type pass-through).
                if (origin.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var input in inputs.EnumerateArray())
                    {
                        if (input.TryGetProperty("link", out var inner) && inner.ValueKind == JsonValueKind.Number)
                        {
                            return ResolveLink((int)inner.GetDouble(), depth + 1);
                        }
                    }
                }

                return (null, false);
            }

            return (new object?[] { originId.ToString(CultureInfo.InvariantCulture), originSlot }, true);
        }

        var workflow = new MinimaxDirectorWorkflow { SourceLabel = sourceLabel };

        foreach (var node in nodes)
        {
            var id = GetInt(node, "id");
            var type = GetString(node, "type");
            if (string.IsNullOrWhiteSpace(type) || IsVirtual(node) || Guid.TryParse(type, out _) || GetInt(node, "mode") == 4)
            {
                continue;
            }

            var inputs = new Dictionary<string, object?>();

            if (
                node.TryGetProperty("widgets_values_named", out var named)
                && named.ValueKind == JsonValueKind.Object
            )
            {
                foreach (var property in named.EnumerateObject())
                {
                    if (property.Name.Equals("control_after_generate", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    inputs[property.Name] = ElementToClr(property.Value);
                }
            }
            else if (
                node.TryGetProperty("widgets_values", out var widgets)
                && widgets.ValueKind == JsonValueKind.Array
            )
            {
                var index = 0;
                foreach (var widget in widgets.EnumerateArray())
                {
                    inputs[$"widget_{index++}"] = ElementToClr(widget);
                }
            }

            if (node.TryGetProperty("inputs", out var nodeInputs) && nodeInputs.ValueKind == JsonValueKind.Array)
            {
                foreach (var input in nodeInputs.EnumerateArray())
                {
                    var name = GetString(input, "name");
                    if (
                        string.IsNullOrEmpty(name)
                        || !input.TryGetProperty("link", out var linkElement)
                        || linkElement.ValueKind != JsonValueKind.Number
                    )
                    {
                        continue;
                    }

                    var resolved = ResolveLink((int)linkElement.GetDouble(), 0);
                    if (resolved.Ok)
                    {
                        inputs[name] = resolved.Value;
                    }
                    else
                    {
                        inputs.Remove(name);
                    }
                }
            }

            workflow.Nodes[id.ToString(CultureInfo.InvariantCulture)] = new ComfyNode
            {
                ClassType = type,
                Inputs = inputs,
            };
        }

        RemoveDanglingReferences(workflow.Nodes);
        return workflow;
    }

    // ------------------------------------------------------------------ mapping

    private void DetectOutputs()
    {
        OutputNodeNames = Nodes
            .Where(x => OutputClassPrefixes.Any(prefix => x.Value.ClassType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Select(x => x.Key)
            .ToList();

        // Some workflows save through a node with a filename_prefix but no Save* class name.
        if (OutputNodeNames.Count == 0)
        {
            OutputNodeNames = Nodes
                .Where(x => x.Value.Inputs.ContainsKey("filename_prefix"))
                .Select(x => x.Key)
                .ToList();
        }
    }

    private List<(string Node, string Input)> FindTargets(string role)
    {
        var result = new List<(string, string)>();

        bool IsLiteral(object? value) => value is not object?[] && value is not JsonElement;

        switch (role)
        {
            case "prompt":
            {
                var primitives = Nodes
                    .Where(x => x.Value.ClassType.Contains("PrimitiveString", StringComparison.OrdinalIgnoreCase)
                                && x.Value.Inputs.ContainsKey("value"))
                    .Select(x => (x.Key, "value"))
                    .ToList();
                if (primitives.Count > 0)
                {
                    result.AddRange(primitives);
                    break;
                }

                result.AddRange(
                    Nodes
                        .Where(x => x.Value.Inputs.TryGetValue("prompt", out var v) && IsLiteral(v) && v is string)
                        .Select(x => (x.Key, "prompt"))
                );
                result.AddRange(
                    Nodes
                        .Where(x => x.Value.ClassType.Contains("CLIPText", StringComparison.OrdinalIgnoreCase)
                                    && x.Value.Inputs.ContainsKey("text"))
                        .Select(x => (x.Key, "text"))
                );
                break;
            }
            case "seed":
            {
                var noise = Nodes
                    .Where(x => x.Value.ClassType.Equals("RandomNoise", StringComparison.OrdinalIgnoreCase)
                                && x.Value.Inputs.ContainsKey("noise_seed"))
                    .Select(x => (x.Key, "noise_seed"))
                    .ToList();
                result.AddRange(noise.Count > 0 ? noise : Nodes
                    .Where(x => x.Value.Inputs.Any(i =>
                        (i.Key is "seed" or "noise_seed") && IsLiteral(i.Value) && i.Value is not string))
                    .SelectMany(x => x.Value.Inputs
                        .Where(i => (i.Key is "seed" or "noise_seed") && IsLiteral(i.Value) && i.Value is not string)
                        .Select(i => (x.Key, i.Key))));
                break;
            }
            case "length":
                result.AddRange(
                    Nodes
                        .Where(x => x.Value.ClassType.Contains("MiniMaxH3", StringComparison.OrdinalIgnoreCase)
                                    && x.Value.Inputs.ContainsKey("length"))
                        .Select(x => (x.Key, "length"))
                );
                if (result.Count == 0)
                {
                    result.AddRange(
                        Nodes.SelectMany(x => x.Value.Inputs
                            .Where(i => i.Key is "length" or "num_frames" or "frame_num")
                            .Select(i => (x.Key, i.Key)))
                    );
                }

                break;
            case "width":
            case "height":
                result.AddRange(
                    Nodes.SelectMany(x => x.Value.Inputs
                        .Where(i => i.Key.Equals(role, StringComparison.OrdinalIgnoreCase) && IsLiteral(i.Value))
                        .Select(i => (x.Key, i.Key)))
                );
                break;
            case "steps":
                result.AddRange(
                    Nodes.SelectMany(x => x.Value.Inputs
                        .Where(i => i.Key.Equals("steps", StringComparison.OrdinalIgnoreCase) && IsLiteral(i.Value))
                        .Select(i => (x.Key, i.Key)))
                );
                break;
            case "unet_name":
                result.AddRange(
                    Nodes.Where(x => x.Value.ClassType.Equals("UNETLoader", StringComparison.OrdinalIgnoreCase)
                                    && x.Value.Inputs.ContainsKey("unet_name"))
                        .Select(x => (x.Key, "unet_name")));
                break;
            case "clip_name":
                result.AddRange(
                    Nodes.Where(x => x.Value.ClassType.Equals("CLIPLoader", StringComparison.OrdinalIgnoreCase)
                                    && x.Value.Inputs.ContainsKey("clip_name"))
                        .Select(x => (x.Key, "clip_name")));
                break;
            case "vae_name":
            {
                var videoVae = GetVideoVaeLoaderName();
                if (videoVae is not null
                    && Nodes.TryGetValue(videoVae, out var videoVaeNode)
                    && videoVaeNode.Inputs.ContainsKey("vae_name"))
                {
                    result.Add((videoVae, "vae_name"));
                }
                else
                {
                    result.AddRange(
                        Nodes.Where(x => x.Value.ClassType.Equals("VAELoader", StringComparison.OrdinalIgnoreCase)
                                        && x.Value.Inputs.ContainsKey("vae_name"))
                            .Select(x => (x.Key, "vae_name")));
                }

                break;
            }
            case "fps":
                result.AddRange(
                    Nodes.SelectMany(x => x.Value.Inputs
                        .Where(i => i.Key.Equals("fps", StringComparison.OrdinalIgnoreCase) && IsLiteral(i.Value))
                        .Select(i => (x.Key, i.Key)))
                );
                break;
            case "prefix":
                result.AddRange(
                    Nodes.SelectMany(x => x.Value.Inputs
                        .Where(i => i.Key.Equals("filename_prefix", StringComparison.OrdinalIgnoreCase))
                        .Select(i => (x.Key, i.Key)))
                );
                break;
        }

        return result.Distinct().ToList();
    }

    private void SetRole(string role, object? value)
    {
        foreach (var (nodeName, inputName) in FindTargets(role))
        {
            Nodes[nodeName].Inputs[inputName] = value;
        }
    }

    private void SetAttentionBackend(string value)
    {
        foreach (var node in Nodes.Values.Where(n =>
                     n.ClassType.Equals("ModelAttentionBackend", StringComparison.OrdinalIgnoreCase)))
        {
            node.Inputs["attention"] = value;
        }
    }

    /// <summary>
    /// Enables one specific LoRA in an LTX_lora_loader "stack_data" JSON string and disables the
    /// others. This is the Director's "plugin" selection: it lets the user switch which LoRA the
    /// H3 workflow applies without editing the workflow JSON.
    /// </summary>
    private void SetLora(string loraName, double? strength)
    {
        foreach (var (_, node) in Nodes.Where(x =>
                     x.Value.ClassType.Equals("LTX_lora_loader", StringComparison.OrdinalIgnoreCase)))
        {
            if (!node.Inputs.TryGetValue("stack_data", out var raw))
            {
                continue;
            }

            JsonNode? root = raw switch
            {
                string s => JsonNode.Parse(s),
                JsonNode j => j,
                _ => null,
            };

            if (root is null)
            {
                continue;
            }

            var array = root as JsonArray ?? root["stack_data"] as JsonArray;
            if (array is null)
            {
                continue;
            }

            var matched = false;
            foreach (var item in array.OfType<JsonObject>())
            {
                var name = item["lora"]?.GetValue<string>() ?? "";
                var isMatch = name.Contains(loraName, StringComparison.OrdinalIgnoreCase);
                item["on"] = isMatch;
                if (isMatch)
                {
                    matched = true;
                    if (strength is not null)
                    {
                        item["str"] = strength.Value;
                    }
                }
            }

            if (!matched)
            {
                array.Add(new JsonObject
                {
                    ["on"] = true,
                    ["lora"] = loraName,
                    ["str"] = strength ?? 1.0,
                    ["v"] = 1,
                    ["a"] = 0,
                    ["t"] = 1,
                });
            }

            // Keep the value as a JSON string so it round-trips with the original workflow shape.
            node.Inputs["stack_data"] = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
    }

    /// <summary>
    /// Swaps the standard VAEDecode node for VAEDecodeTiled. Mirrors wan-tiled-VAE: the latent is
    /// decoded in tiles so peak VRAM stays low. Experimental - the targeted node must exist and
    /// support tiling in the connected ComfyUI build.
    /// </summary>
    private void EnableTiledVae(int tileSize)
    {
        // ComfyNode.ClassType is init-only, so replace the entry (keeping its key, which is what
        // downstream links reference) rather than mutating the record.
        foreach (var (name, node) in Nodes.Where(x =>
                     x.Value.ClassType.Equals("VAEDecode", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var inputs = new Dictionary<string, object?>(node.Inputs);
            if (inputs.TryGetValue("samples", out var samples))
            {
                inputs["pixels"] = samples;
                inputs.Remove("samples");
            }

            inputs["tile_size"] = Math.Clamp(tileSize, 64, 1024);
            inputs["overlap"] = Math.Max(16, tileSize / 4);

            Nodes[name] = new ComfyNode
            {
                ClassType = "VAEDecodeTiled",
                Inputs = inputs,
            };
        }
    }

    private static string? GetSourceNodeName(NodeDictionary nodes, string targetNode, string inputName)
    {
        if (!nodes.TryGetValue(targetNode, out var node) || !node.Inputs.TryGetValue(inputName, out var v))
        {
            return null;
        }

        return v is object?[] link && link.Length == 2 && link[0] is string s ? s : null;
    }

    /// <summary>The VAELoader that feeds MiniMaxH3ReferenceToVideo.vae (the video VAE, not the audio VAE).</summary>
    private string? GetVideoVaeLoaderName()
    {
        var h3 = Nodes
            .FirstOrDefault(x => x.Value.ClassType.Contains("ReferenceToVideo", StringComparison.OrdinalIgnoreCase))
            .Key;
        return h3 is not null ? GetSourceNodeName(Nodes, h3, "vae") : null;
    }

    private static readonly HashSet<string> CoreNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "LoadImage", "LoadImages", "SaveImage", "PreviewImage", "VAELoader", "VAEDecode", "VAEDecodeTiled",
        "UNETLoader", "CLIPLoader", "CLIPTextEncode", "CLIPTextEncodeFlux", "RandomNoise", "BasicScheduler",
        "BasicGuider", "KSampler", "KSamplerAdvanced", "SamplerCustomAdvanced", "Note", "PrimitiveNode",
        "PrimitiveStringMultiline", "PrimitiveInt", "PrimitiveFloat", "PrimitiveBoolean", "StringLiteralNode",
        "ImageScale", "ImageScaleBy", "ImageResize", "ImageCrop", "ModelLoader", "CheckpointLoader",
        "CheckpointLoaderSimple", "DiffusersLoader", "LoraLoader", "LoraLoaderModelOnly", "EmptyLatentImage",
        "VAEEncode", "VAEEncodeForInpaint", "ControlNetLoader",
    };

    /// <summary>
    /// Lists the custom (non-core) node types the workflow uses. These are the ComfyUI plugins the
    /// user must have installed for the workflow to run (e.g. MiniMaxH3*, ModelAttentionBackend,
    /// RTXVideoSuperResolution, LTX_lora_loader).
    /// </summary>
    public IReadOnlyList<string> GetCustomNodeTypes()
    {
        return Nodes.Values
            .Select(n => n.ClassType)
            .Where(t => !CoreNodeTypes.Contains(t))
            .Distinct()
            .OrderBy(t => t)
            .ToList();
    }

    /// <summary>
    /// Feeds one or more reference images into the H3 workflow. Each image becomes its own
    /// LoadImage node and is linked to a distinct ref_images.ref_image_N slot on the H3
    /// ReferenceToVideo node (H3 supports multiple reference / character images).
    /// </summary>
    private void InjectReferenceImages(IReadOnlyList<string> relativePaths)
    {
        if (relativePaths.Count == 0)
        {
            return;
        }

        var refNodeNames = new List<string>();
        for (var i = 0; i < relativePaths.Count; i++)
        {
            var nodeName = $"minimax_director_ref_{i}";
            Nodes[nodeName] = new ComfyNode
            {
                ClassType = "LoadImage",
                Inputs = new Dictionary<string, object?> { ["image"] = relativePaths[i] },
            };
            refNodeNames.Add(nodeName);
        }

        // Prefer a node that already declares a reference-image input. The H3 ReferenceToVideo
        // node's ref_images.* slots are dynamic and are usually absent from a saved API prompt,
        // so fall back to the ReferenceToVideo node itself (we create the slots below).
        var primaryTarget = Nodes
            .Where(x => !refNodeNames.Contains(x.Key) && x.Value.Inputs.Keys.Any(IsReferenceImageInput))
            .Select(x => x.Key)
            .FirstOrDefault();

        if (primaryTarget is null)
        {
            primaryTarget = Nodes
                .Where(x => !refNodeNames.Contains(x.Key)
                            && x.Value.ClassType.Contains("ReferenceToVideo", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Key)
                .FirstOrDefault();
        }

        if (primaryTarget is not null)
        {
            for (var i = 0; i < refNodeNames.Count; i++)
            {
                Nodes[primaryTarget].Inputs[$"ref_images.ref_image_{i}"] =
                    new object?[] { refNodeNames[i], 0 };
            }

            return;
        }

        // Last resort: reuse any LoadImage node already in the workflow (single shared image).
        var loaders = Nodes
            .Where(x => !refNodeNames.Contains(x.Key) && x.Value.ClassType == "LoadImage")
            .Select(x => x.Value)
            .ToList();

        foreach (var loader in loaders)
        {
            loader.Inputs["image"] = relativePaths[0];
        }

        if (loaders.Count == 0)
        {
            foreach (var nodeName in refNodeNames)
            {
                Nodes.Remove(nodeName);
            }
        }
    }

    private static bool IsReferenceImageInput(string key) =>
        key.StartsWith("ref_images.", StringComparison.OrdinalIgnoreCase)
        || key.Equals("ref_image", StringComparison.OrdinalIgnoreCase)
        || key.Equals("start_image", StringComparison.OrdinalIgnoreCase)
        || key.Equals("first_frame_image", StringComparison.OrdinalIgnoreCase);

    private void StripAudio()
    {
        var audioNodes = Nodes
            .Where(x => x.Value.ClassType.Contains("VAEDecodeAudio", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Key)
            .ToList();

        // Only the optional "audio" output input. Do NOT remove H3's required "audio_vae" input.
        foreach (var node in Nodes.Values)
        {
            foreach (var key in node.Inputs.Keys
                         .Where(k => k.Equals("audio", StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                node.Inputs.Remove(key);
            }
        }

        foreach (var name in audioNodes)
        {
            Nodes.Remove(name);
        }

        RemoveDanglingReferences(Nodes);
    }

    /// <summary>Removes nodes of a class type by forwarding their first input to their consumers.</summary>
    private void BypassNodesOfType(string classType)
    {
        if (string.IsNullOrWhiteSpace(classType))
        {
            return;
        }

        foreach (var (name, node) in Nodes
                     .Where(x => x.Value.ClassType.Equals(classType.Trim(), StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            var source = node.Inputs.Values.OfType<object?[]>().FirstOrDefault(x => x.Length == 2 && x[0] is string);

            foreach (var consumer in Nodes.Values)
            {
                foreach (var key in consumer.Inputs.Keys.ToList())
                {
                    if (
                        consumer.Inputs[key] is object?[] reference
                        && reference.Length == 2
                        && reference[0] is string referenceName
                        && referenceName == name
                    )
                    {
                        if (source is not null)
                        {
                            consumer.Inputs[key] = (object?[])source.Clone();
                        }
                        else
                        {
                            consumer.Inputs.Remove(key);
                        }
                    }
                }
            }

            Nodes.Remove(name);
        }

        RemoveDanglingReferences(Nodes);
    }

    private static void RemoveDanglingReferences(NodeDictionary nodes)
    {
        foreach (var node in nodes.Values)
        {
            foreach (var key in node.Inputs.Keys.ToList())
            {
                if (
                    node.Inputs[key] is object?[] reference
                    && reference.Length == 2
                    && reference[0] is string target
                    && !nodes.ContainsKey(target)
                )
                {
                    node.Inputs.Remove(key);
                }
            }
        }
    }

    public string DescribeMapping()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Source: {SourceLabel}");
        builder.AppendLine($"Nodes: {Nodes.Count} | Output nodes: {(OutputNodeNames.Count > 0 ? string.Join(", ", OutputNodeNames) : "none")}");

        foreach (var role in new[] { "prompt", "seed", "width", "height", "length", "steps", "fps", "prefix" })
        {
            var targets = FindTargets(role);
            builder.AppendLine(
                targets.Count > 0
                    ? $"{role}: " + string.Join(", ", targets.Select(x => $"{x.Node}.{x.Input}"))
                    : $"{role}: NOT FOUND (will stay at workflow defaults)"
            );
        }

        // H3 workflows reference large, very specific checkpoints. Listing them here makes a
        // "model not found" ComfyUI error diagnosable without opening the JSON.
        var modelKeys = new[] { "unet_name", "clip_name", "vae_name", "ckpt_name", "lora_name" };
        var models = Nodes
            .SelectMany(x => x.Value.Inputs
                .Where(i => modelKeys.Contains(i.Key, StringComparer.OrdinalIgnoreCase)
                            && i.Value is string text
                            && !string.IsNullOrWhiteSpace(text))
                .Select(i => $"  {x.Value.ClassType}.{i.Key}: {i.Value}"))
            .Distinct()
            .ToList();

        if (models.Count > 0)
        {
            builder.AppendLine("Models referenced by this workflow (must exist in ComfyUI):");
            foreach (var model in models)
            {
                builder.AppendLine(model);
            }
        }

        var plugins = GetCustomNodeTypes();
        if (plugins.Count > 0)
        {
            builder.AppendLine("Required ComfyUI custom nodes / plugins:");
            foreach (var plugin in plugins)
            {
                builder.AppendLine($"  - {plugin}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ helpers

    private static object? DeepClone(object? value)
    {
        return value switch
        {
            null => null,
            object?[] array => array.Select(DeepClone).ToArray(),
            Dictionary<string, object?> dictionary => dictionary.ToDictionary(x => x.Key, x => DeepClone(x.Value)),
            JsonElement element => ElementToClr(element),
            _ => value,
        };
    }

    private static object? NormalizeValue(object? value)
    {
        return value is JsonElement element ? ElementToClr(element) : value;
    }

    private static object? ElementToClr(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.TryGetInt64(out var integer) ? integer : element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(ElementToClr).ToArray();
            case JsonValueKind.Object:
                return element.EnumerateObject().ToDictionary(x => x.Name, x => ElementToClr(x.Value));
            default:
                return null;
        }
    }

    private static string GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int GetInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? (int)value.GetDouble()
            : 0;

    private static bool IsVirtual(JsonElement node)
    {
        var type = GetString(node, "type");
        return VirtualNodeTypes.Contains(type) || Guid.TryParse(type, out _);
    }
}
