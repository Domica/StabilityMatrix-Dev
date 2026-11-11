using System.Globalization;
using System.Text;

namespace StabilityMatrix.Avalonia.Models.Inference;

/// <summary>
/// Pure helpers for reasoning about the "generate low-res, then upscale" strategy.
/// Used by both the Director (latent upscale) and the standalone Video Upscale screen.
/// No ComfyUI connection required - this is the estimate/decision aid the user asked for.
/// </summary>
public static class MinimaxUpscaleMath
{
    /// <summary>
    /// Computes the effective output resolution and a plain-language note about the upscale
    /// potential and the quality tradeoff versus rendering natively at the target resolution.
    /// </summary>
    public static string ComputeNote(int genWidth, int genHeight, double scale, string methodLabel)
    {
        if (genWidth <= 0 || genHeight <= 0 || scale <= 0)
        {
            return "Set a generation resolution and an upscale factor to see the projected output size and quality tradeoff.";
        }

        var outW = (int)Math.Round(genWidth * scale / 2.0) * 2;
        var outH = (int)Math.Round(genHeight * scale / 2.0) * 2;

        var genPixels = genWidth * genHeight;
        var outPixels = outW * outH;
        var ratio = genPixels > 0 ? (double)outPixels / genPixels : 0;

        var genMp = genPixels / 1_000_000.0;
        var outMp = outPixels / 1_000_000.0;

        var sb = new StringBuilder();
        sb.AppendLine(
            $"Generate {genWidth}×{genHeight} ({genMp:F2} MP) → {methodLabel} ×{scale:0.##} → "
            + $"{outW}×{outH} ({outMp:F2} MP).");
        sb.AppendLine($"Pixel count grows ~{ratio:0.0}×. VRAM/time to GENERATE stays at the low-res cost; ");
        sb.AppendLine("only the (cheap) upscale pass runs at the larger size.");
        sb.AppendLine();
        sb.AppendLine("Quality note:");
        sb.AppendLine("• Upscaling does NOT invent new detail - it interpolates. Fine texture and crisp edges");
        sb.AppendLine("  are still limited by the low-res generation. Native rendering at the target size is sharper.");
        sb.AppendLine("• Latent upscale (LBH / CGlide) re-samples in latent space → fewer artifacts than a naive");
        sb.AppendLine("  pixel upscale, and pairs well with the Turbo LoRA (fast low-res gen) + Refiner LoRA.");
        sb.AppendLine("• Recommendation: ×2 is the sweet spot (good quality, big speed win). ×3–×4 only with a");
        sb.AppendLine("  strong refiner or when the source motion/detail is simple; expect softer results.");

        return sb.ToString().TrimEnd();
    }

    /// <summary>Effective output width/height for a generation size and scale (even-aligned).</summary>
    public static (int Width, int Height) Project(int genWidth, int genHeight, double scale)
    {
        return (
            (int)Math.Round(genWidth * scale / 2.0) * 2,
            (int)Math.Round(genHeight * scale / 2.0) * 2);
    }

    /// <summary>
    /// Estimate for the standalone Video Upscale screen: an already-rendered video is the source,
    /// so there is no "cheap generation" stage - but the same quality ceiling applies. Upscaling
    /// a finished video is far cheaper than re-generating it natively at the target resolution.
    /// </summary>
    public static string ComputeVideoPostNote(
        int sourceWidth,
        int sourceHeight,
        double scale,
        string methodLabel,
        double fps = 0,
        int frames = 0)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || scale <= 0)
        {
            return "Set the source resolution and an upscale factor to see the projected output size and quality tradeoff. "
                   + "Pick a video to auto-detect it (ffprobe), or enter width/height/fps manually.";
        }

        var (outW, outH) = Project(sourceWidth, sourceHeight, scale);
        var srcPixels = sourceWidth * sourceHeight;
        var outPixels = outW * outH;
        var ratio = srcPixels > 0 ? (double)outPixels / srcPixels : 0;

        var sb = new StringBuilder();
        sb.AppendLine(
            $"Source {sourceWidth}×{sourceHeight} ({srcPixels / 1_000_000.0:F2} MP) → {methodLabel} ×{scale:0.##} → "
            + $"{outW}×{outH} ({outPixels / 1_000_000.0:F2} MP), ~{ratio:0.0}× the pixels.");

        if (fps > 0 && frames > 0)
        {
            sb.AppendLine(
                $"{frames} frames @ {fps:0.##} fps ≈ {frames / Math.Max(fps, 1):0.0} s of video; "
                + $"the upscale pass processes ~{outPixels / 1_000_000.0 * frames:F0} M pixels in total.");
        }
        else if (fps > 0)
        {
            sb.AppendLine($"Output fps: {fps:0.##} (frame count unknown without a video probe).");
        }

        sb.AppendLine();
        sb.AppendLine("Upscale potential:");
        sb.AppendLine("• A post-process upscale is much cheaper than re-generating the clip at the target resolution");
        sb.AppendLine("  (no diffusion sampling). Cost grows roughly with output pixels × frame count, and the");
        sb.AppendLine("  VAE / decoder at 2K–4K is where VRAM peaks - use a tiled VAE workflow for long clips.");
        sb.AppendLine();
        sb.AppendLine("Quality loss:");
        sb.AppendLine("• Upscaling does NOT invent detail. Fine texture, small text and crisp edges are permanently");
        sb.AppendLine("  limited by the source. A 540×280 source at ×2 will still look softer than a native 1080p render.");
        sb.AppendLine("• Pixel interpolation (Lanczos/Bicubic) sharpens edges but can add ringing; model-based VSR");
        sb.AppendLine("  (RelayVSR / RTX VSR / CGlide) hallucinates plausible detail and handles compression noise better.");
        sb.AppendLine("• ×2 is the sweet spot. ×3–×4 amplifies artifacts, motion inconsistency and flicker; use a");
        sb.AppendLine("  refiner LoRA / a second re-sampling pass for high factors, and expect softer motion.");
        sb.AppendLine("• Temporal consistency matters for video: frame-by-frame upscalers can flicker. Prefer VSR");
        sb.AppendLine("  nodes that see neighbouring frames (RelayVSR / CGlide) over plain ImageScaleBy for high factors.");

        return sb.ToString().TrimEnd();
    }
}
