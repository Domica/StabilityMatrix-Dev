namespace StabilityMatrix.Avalonia.Models.Inference;

/// <summary>
/// A known MiniMax H3 LoRA that supports the "generate low-res fast, then upscale" workflow.
/// <paramref name="Match"/> is matched by substring against the LTX_lora_loader stack, or appended
/// if not present. <paramref name="Source"/> is a reference link for downloading the checkpoint.
/// </summary>
public sealed record MinimaxDirectorLoraPreset(
    string Label,
    string Match,
    double Strength,
    string Source,
    string Description);
