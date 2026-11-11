namespace StabilityMatrix.Core.Models.Inference;

public enum InferenceProjectType
{
    Unknown,
    TextToImage,
    ImageToImage,
    Inpainting,
    Upscale,
    ImageToVideo,
    FluxTextToImage,
    WanTextToVideo,
    WanImageToVideo,
    Wan2ImageToVideo,
    Outpaint,
    WanVideoInpaint,
    MinimaxDirector,
    VideoUpscale,
}
