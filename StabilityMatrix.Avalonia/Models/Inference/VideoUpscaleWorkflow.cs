using System.Text;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Avalonia.Models.Inference;

/// <summary>
/// Values injected into a video-upscale ComfyUI workflow for a single run.
/// The workflow itself is either the bundled core template or a user-supplied API/UI workflow
/// (RelayVSR, CGlide, LBH 2K refiner, ...).
/// </summary>
public sealed record VideoUpscaleInjection
{
    /// <summary>Path of the uploaded video, relative to ComfyUI's input directory.</summary>
    public required string InputVideoRelativePath { get; init; }

    public required string FilenamePrefix { get; init; }

    public double Scale { get; init; } = 2.0;

    /// <summary><see cref="VideoUpscaleWorkflow.ModeCore"/>, <see cref="VideoUpscaleWorkflow.ModeRtx"/> or <see cref="VideoUpscaleWorkflow.ModeCustom"/>.</summary>
    public string Mode { get; init; } = VideoUpscaleWorkflow.ModeCore;

    /// <summary>ComfyUI ImageScaleBy interpolation method (lanczos / bicubic / ...).</summary>
    public string InterpolationMethod { get; init; } = "lanczos";

    /// <summary>RTXVideoSuperResolution quality preset.</summary>
    public string RtxQuality { get; init; } = "MEDIUM";

    /// <summary>0 = keep the fps widget in the workflow.</summary>
    public double Fps { get; init; }

    /// <summary>Wire GetVideoComponents.audio into CreateVideo (bundled template only).</summary>
    public bool PreserveAudio { get; init; }

    /// <summary>True when the workflow is the bundled core template (safe to rewrite node classes).</summary>
    public bool IsBuiltInTemplate { get; init; } = true;

    /// <summary>
    /// Add a WANMemoryCleanupNode so ComfyUI offloads Wan models + KV cache to CPU between
    /// steps. Helps avoid OOM on low-VRAM cards (e.g. 16 GB). Requires the WanVideo custom nodes.
    /// </summary>
    public bool UseWanMemoryCleanup { get; init; }

    /// <summary>
    /// Replace every VAEDecode node in the graph with VAEDecodeTiled. Only meaningful for
    /// workflows that actually decode a VAE (the third-party upscaler packs). The bundled core
    /// template has no VAE step, so this is a no-op there.
    /// </summary>
    public bool UseTiledVae { get; init; }

    public int TiledVaeTileSize { get; init; } = 512;

    public int TiledVaeOverlap { get; init; } = 64;

    public int TiledVaeTemporalSize { get; init; } = 64;

    public int TiledVaeTemporalOverlap { get; init; } = 8;
}

public sealed record VideoUpscaleApplyResult(
    int InputTargets,
    int PrefixTargets,
    int ScaleTargets,
    int FpsTargets,
    string Summary);

/// <summary>
/// Pure workflow-injection helpers for the standalone Video Upscale screen.
///
/// The bundled template is deliberately built from ComfyUI core video nodes only
/// (LoadVideo -&gt; GetVideoComponents -&gt; ImageScaleBy -&gt; CreateVideo -&gt; SaveVideo),
/// so it runs without any custom-node pack. Third-party upscalers (RelayVSR / CGlide /
/// the LBH 2K refiner) are supported by loading their exported workflow JSON instead; this
/// helper then injects the input video, output prefix, fps and any scale widgets it can find,
/// and reports exactly which injection points were detected.
/// </summary>
public static class VideoUpscaleWorkflow
{
    public const string BuiltInWorkflowUri =
        "avares://StabilityMatrix.Avalonia/Assets/Workflows/video_upscale_core_api.json";

    public const string BuiltInTemplateNodeName = "3";

    public const string ModeCore = "Core ImageScaleBy";
    public const string ModeRtx = "RTX Video Super Resolution";
    public const string ModeCustom = "Custom workflow";

    private static readonly string[] ScaleWidgetNames =
    {
        "scale_by", "resize_type.scale", "upscale_factor", "upscale_by", "scale_factor",
    };

    /// <summary>
    /// Injects the run values into an already-parsed API-format node graph. Call on a fresh clone
    /// for every run so switching modes never leaks state between runs.
    /// </summary>
    public static VideoUpscaleApplyResult Apply(NodeDictionary nodes, VideoUpscaleInjection injection)
    {
        var scale = Math.Clamp(injection.Scale, 1.0, 4.0);

        // 1. Input video -> LoadVideo.file / VHS_LoadVideo.video.
        var inputTargets = 0;
        foreach (var (_, node) in nodes)
        {
            if (node.ClassType.Equals("LoadVideo", StringComparison.OrdinalIgnoreCase)
                && node.Inputs.ContainsKey("file"))
            {
                node.Inputs["file"] = injection.InputVideoRelativePath;
                inputTargets++;
            }
            else if (node.ClassType.StartsWith("VHS_LoadVideo", StringComparison.OrdinalIgnoreCase)
                     && node.Inputs.ContainsKey("video"))
            {
                node.Inputs["video"] = injection.InputVideoRelativePath;
                inputTargets++;
            }
        }

        // 2. Output filename prefix.
        var prefixTargets = 0;
        foreach (var (_, node) in nodes)
        {
            if (!node.Inputs.ContainsKey("filename_prefix"))
            {
                continue;
            }

            node.Inputs["filename_prefix"] = injection.FilenamePrefix;
            prefixTargets++;
        }

        // 3. Upscale node: rewrite only the bundled template; custom workflows keep their own nodes.
        if (injection.IsBuiltInTemplate && injection.Mode != ModeCustom)
        {
            if (nodes.TryGetValue(BuiltInTemplateNodeName, out var scaleNode) && scaleNode is not null)
            {
                var sourceLink = scaleNode.Inputs.TryGetValue("image", out var imageLink)
                    ? imageLink
                    : scaleNode.Inputs.TryGetValue("images", out var imagesLink)
                        ? imagesLink
                        : new object?[] { "2", 0 };

                if (injection.Mode == ModeRtx)
                {
                    // ClassType is init-only, so replace the whole node rather than mutating it.
                    nodes[BuiltInTemplateNodeName] = new ComfyNode
                    {
                        ClassType = "RTXVideoSuperResolution",
                        Inputs = new Dictionary<string, object?>
                        {
                            ["images"] = CloneLink(sourceLink),
                            ["resize_type"] = "scale by multiplier",
                            ["resize_type.scale"] = scale,
                            ["quality"] = injection.RtxQuality,
                        },
                    };
                }
                else
                {
                    nodes[BuiltInTemplateNodeName] = new ComfyNode
                    {
                        ClassType = "ImageScaleBy",
                        Inputs = new Dictionary<string, object?>
                        {
                            ["image"] = CloneLink(sourceLink),
                            ["upscale_method"] = injection.InterpolationMethod,
                            ["scale_by"] = scale,
                        },
                    };
                }
            }
        }

        // 4. Push the scale into any compatible widget (third-party upscaler nodes too).
        var scaleTargets = 0;
        foreach (var (_, node) in nodes)
        {
            foreach (var widget in ScaleWidgetNames)
            {
                if (!node.Inputs.ContainsKey(widget))
                {
                    continue;
                }

                node.Inputs[widget] = scale;
                scaleTargets++;
            }
        }

        // 5. FPS (only when explicitly set; otherwise the source fps from the workflow is used).
        var fpsTargets = 0;
        if (injection.Fps > 0)
        {
            foreach (var (_, node) in nodes)
            {
                if (!node.ClassType.Equals("CreateVideo", StringComparison.OrdinalIgnoreCase)
                    && !node.ClassType.Equals("VHS_VideoCombine", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fpsKey = node.Inputs.ContainsKey("fps")
                    ? "fps"
                    : node.Inputs.ContainsKey("frame_rate") ? "frame_rate" : null;

                if (fpsKey is null)
                {
                    continue;
                }

                node.Inputs[fpsKey] = injection.Fps;
                fpsTargets++;
            }
        }

        // 6. Audio passthrough for the bundled template. GetVideoComponents output 1 = audio.
        if (injection.IsBuiltInTemplate)
        {
            var componentsName = nodes
                .FirstOrDefault(x => x.Value.ClassType.Equals("GetVideoComponents", StringComparison.OrdinalIgnoreCase))
                .Key;

            foreach (var (_, node) in nodes.Where(x =>
                         x.Value.ClassType.Equals("CreateVideo", StringComparison.OrdinalIgnoreCase)))
            {
                if (componentsName is not null && injection.PreserveAudio)
                {
                    node.Inputs["audio"] = new object?[] { componentsName, 1 };
                }
                else
                {
                    node.Inputs.Remove("audio");
                }
            }
        }

        // 7. WAN Memory Cleanup - a standalone node that offloads Wan models + KV cache to CPU
        //    between steps. It is not wired into the data flow, so it works for any workflow.
        var cleanupNodes = 0;
        if (injection.UseWanMemoryCleanup)
        {
            var cleanupName = GetUniqueName(nodes, "WANMemoryCleanup");
            nodes[cleanupName] = new ComfyNode
            {
                ClassType = "WANMemoryCleanupNode",
                Inputs = new Dictionary<string, object?>
                {
                    ["anything"] = null,
                    ["offload_wan_models"] = true,
                    ["offload_cache"] = true,
                },
            };
            cleanupNodes++;
        }

        // 8. Tiled VAE decode - replace each VAEDecode (in place, keeping the same node key so all
        //    downstream links stay valid) with VAEDecodeTiled. Carries the latent + vae links over
        //    and applies the tile / temporal settings. No-op when the workflow has no VAEDecode node.
        var tiledVaeNodes = 0;
        if (injection.UseTiledVae)
        {
            var vaeKeys = nodes
                .Where(x => x.Value.ClassType.Equals("VAEDecode", StringComparison.OrdinalIgnoreCase)
                            || x.Value.ClassType.Equals("VAEDecodeTiled", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Key)
                .ToList();

            foreach (var key in vaeKeys)
            {
                var original = nodes[key];
                original.Inputs.TryGetValue("samples", out var samples);
                original.Inputs.TryGetValue("vae", out var vae);

                nodes[key] = new ComfyNode
                {
                    ClassType = "VAEDecodeTiled",
                    Inputs = new Dictionary<string, object?>
                    {
                        ["samples"] = samples,
                        ["vae"] = vae,
                        ["tile_size"] = injection.TiledVaeTileSize,
                        ["overlap"] = injection.TiledVaeOverlap,
                        ["temporal_size"] = injection.TiledVaeTemporalSize,
                        ["temporal_overlap"] = injection.TiledVaeTemporalOverlap,
                    },
                };
                tiledVaeNodes++;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Input video node(s): {inputTargets} | Output prefix node(s): {prefixTargets} | "
                      + $"Scale widget(s): {scaleTargets} | FPS widget(s): {fpsTargets}");
        sb.AppendLine($"Mode: {injection.Mode} ×{scale:0.##}"
                      + (injection.Mode == ModeCore ? $" ({injection.InterpolationMethod})" : "")
                      + (injection.Mode == ModeRtx ? $" ({injection.RtxQuality})" : ""));
        sb.AppendLine(injection.IsBuiltInTemplate
            ? "Template: bundled core video nodes (no custom plugins required for the Lanczos/Bicubic path)."
            : "Template: custom workflow JSON (RelayVSR / CGlide / refiner packs are used as-is).");

        if (injection.UseWanMemoryCleanup)
        {
            sb.AppendLine($"WAN Memory Cleanup: {cleanupNodes} node added (offload models + cache).");
        }

        if (injection.UseTiledVae)
        {
            sb.AppendLine(tiledVaeNodes > 0
                ? $"Tiled VAE: {tiledVaeNodes} VAEDecode node(s) swapped to VAEDecodeTiled "
                  + $"(tile {injection.TiledVaeTileSize}, overlap {injection.TiledVaeOverlap}, "
                  + $"temporal {injection.TiledVaeTemporalSize}/{injection.TiledVaeTemporalOverlap})."
                : "Tiled VAE requested, but no VAEDecode node was found in this workflow (no-op).");
        }

        if (inputTargets == 0)
        {
            sb.AppendLine("WARNING: no LoadVideo / VHS_LoadVideo node found - the workflow will not read your input video.");
        }
        if (prefixTargets == 0)
        {
            sb.AppendLine("WARNING: no filename_prefix node found - output naming is left to the workflow.");
        }

        return new VideoUpscaleApplyResult(inputTargets, prefixTargets, scaleTargets, fpsTargets, sb.ToString().TrimEnd());
    }

    private static string GetUniqueName(NodeDictionary nodes, string baseName)
    {
        if (!nodes.ContainsKey(baseName))
        {
            return baseName;
        }

        var i = 1;
        while (nodes.ContainsKey($"{baseName}_{i}"))
        {
            i++;
        }

        return $"{baseName}_{i}";
    }

    private static object?[] CloneLink(object? value)
    {
        if (value is object?[] link && link.Length == 2)
        {
            return (object?[])link.Clone();
        }

        return new object?[] { "2", 0 };
    }
}
