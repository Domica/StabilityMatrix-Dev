using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Core.Models.Inference;

namespace StabilityMatrix.Avalonia.Services;

public static class PromptInjectionOutpaint
{
    public static void ApplyOutpaintPromptInjection(
        GenerationParameters parameters,
        string modelName,
        ref string positivePrompt,
        ref string negativePrompt)
    {
        // 1) Pose suppression
        var poseSuppression = GetPoseSuppression(parameters.OutpaintTop, parameters.OutpaintBottom);

        // 2) Model-aware injection
        var modelInjection = GetModelAwareInjection(modelName);

        // 3) Scene-aware injection
        var sceneInjection = GetSceneAwareInjection(
            parameters.OutpaintLeft,
            parameters.OutpaintRight,
            parameters.OutpaintTop,
            parameters.OutpaintBottom);

        // 4) Strength-aware injection
        var strengthInjection = GetStrengthAwareInjection(
            parameters.SmartOutpaintInjectionStrength,
            modelName);

        // Build final positive
        positivePrompt = string.Join(", ",
            positivePrompt,
            poseSuppression,
            modelInjection.Positive,
            sceneInjection.Positive,
            strengthInjection.Positive
        );

        // Build final negative
        negativePrompt = string.Join(", ",
            negativePrompt,
            poseSuppression,
            modelInjection.Negative,
            sceneInjection.Negative,
            strengthInjection.Negative
        );
    }

    // ------------------------------------------------------------
    //  POSE SUPPRESSION
    // ------------------------------------------------------------
    private static string GetPoseSuppression(int expandTop, int expandBottom)
    {
        bool vertical = expandTop > 0 || expandBottom > 0;

        if (vertical)
        {
            return "avoid pose continuation, avoid extending limbs, avoid extending torso, avoid adding hands above head, avoid adding arms, disconnected limbs, extra arms, extra hands, incorrect anatomy";
        }

        return "avoid pose continuation, avoid extending limbs, disconnected limbs, extra arms, extra hands";
    }

    // ------------------------------------------------------------
    //  MODEL-AWARE INJECTION
    // ------------------------------------------------------------
    private static (string Positive, string Negative) GetModelAwareInjection(string modelName)
    {
        var name = modelName.ToLowerInvariant();

        bool isRv6 =
            name.Contains("v6") ||
            name.Contains("hyper") ||
            name.Contains("inpaint") ||
            name.Contains("b1");

        if (!isRv6)
            return ("", "");

        return (
            Positive:
                "avoid HDR contrast, avoid clarity boost, avoid stylized anatomy, maintain natural lighting, maintain original proportions",
            Negative:
                "HDR, overprocessed, oversharpened, extra limbs, pose completion, distorted anatomy"
        );
    }

    // ------------------------------------------------------------
    //  SCENE-AWARE INJECTION
    // ------------------------------------------------------------
    private static (string Positive, string Negative) GetSceneAwareInjection(
        int left, int right, int top, int bottom)
    {
        bool horizontal = left > 0 || right > 0;
        bool vertical = top > 0 || bottom > 0;

        string pos = "";
        string neg = "";

        if (horizontal)
        {
            pos += "extend background, extend environment, match lighting, match perspective, seamless continuation";
        }

        if (vertical)
        {
            pos += ", extend composition, maintain proportions, avoid stretching body";
            neg += ", distorted proportions, stretched anatomy";
        }

        return (pos, neg);
    }

    // ------------------------------------------------------------
    //  STRENGTH-AWARE INJECTION
    // ------------------------------------------------------------
    private static (string Positive, string Negative) GetStrengthAwareInjection(
        double strength,
        string modelName)
    {
        var name = modelName.ToLowerInvariant();
        bool isRv6 = name.Contains("v6") || name.Contains("hyper");

        if (!isRv6)
            return ("", "");

        if (strength < 0.6)
        {
            return (
                Positive: "strong scene continuation, strong composition preservation",
                Negative: "pose completion, stylized anatomy"
            );
        }

        return ("", "");
    }
}
