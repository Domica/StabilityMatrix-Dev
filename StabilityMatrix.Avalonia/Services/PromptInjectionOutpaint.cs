using System;
using System.Collections.Generic;
using System.Linq;
using StabilityMatrix.Core.Models.Inference;

namespace StabilityMatrix.Avalonia.Services;

public enum SceneType
{
    Unknown,
    People,
    Landscape,
    Architecture
}

public static class PromptInjectionOutpaint
{
    public static void ApplyOutpaintPromptInjection(
        GenerationParameters parameters,
        string modelName,
        IEnumerable<string> availableModels,
        ref string positivePrompt,
        ref string negativePrompt)
    {
        // 1) Auto-switch model if it's bad for outpaint (RV6, Hyper-Inpaint, etc.)
        modelName = AutoSwitchModelIfNeeded(modelName, availableModels);

        // 2) Pose suppression (vertical vs horizontal)
        var poseSuppression = GetPoseSuppression(
            parameters.OutpaintTop,
            parameters.OutpaintBottom);

        // 3) Model-aware injection (RV6, Hyper, Inpaint)
        var modelInjection = GetModelAwareInjection(modelName);

        // 4) Scene-aware injection (people / landscape / architecture)
        var sceneTypeInjection = GetSceneTypeInjection(parameters.SceneType);

        // 5) Outpaint-direction-aware injection (horizontal vs vertical)
        var sceneDirectionInjection = GetSceneDirectionInjection(
            parameters.OutpaintLeft,
            parameters.OutpaintRight,
            parameters.OutpaintTop,
            parameters.OutpaintBottom);

        // 6) Strength-aware injection (if too low for RV6, reinforce)
        var strengthInjection = GetStrengthAwareInjection(
            parameters.SmartOutpaintInjectionStrength,
            modelName);

        // Build final positive
        positivePrompt = JoinNonEmpty(
            positivePrompt,
            poseSuppression,
            modelInjection.Positive,
            sceneTypeInjection.Positive,
            sceneDirectionInjection.Positive,
            strengthInjection.Positive
        );

        // Build final negative
        negativePrompt = JoinNonEmpty(
            negativePrompt,
            poseSuppression,
            modelInjection.Negative,
            sceneTypeInjection.Negative,
            sceneDirectionInjection.Negative,
            strengthInjection.Negative
        );

        // Debug overlay
        parameters.DebugFinalPositive = positivePrompt;
        parameters.DebugFinalNegative = negativePrompt;
        parameters.DebugFinalModelName = modelName;
    }

    // ------------------------------------------------------------
    //  HELPERS
    // ------------------------------------------------------------

    private static string JoinNonEmpty(params string?[] parts)
    {
        return string.Join(", ",
            parts.Where(p => !string.IsNullOrWhiteSpace(p))!);
    }

    // ------------------------------------------------------------
    //  AUTO-SWITCH MODEL
    // ------------------------------------------------------------

    private static bool HasPoseCompletionBias(string modelName)
    {
        var n = modelName.ToLowerInvariant();
        return n.Contains("v6")
               || n.Contains("hyper")
               || n.Contains("inpaint")
               || n.Contains("b1");
    }

    public static string AutoSwitchModelIfNeeded(string modelName, IEnumerable<string> availableModels)
    {
        if (!HasPoseCompletionBias(modelName))
            return modelName;

        // Prefer Realistic Vision V5.1
        var rv51 = availableModels.FirstOrDefault(m =>
            m.Contains("Realistic Vision V5.1", StringComparison.InvariantCultureIgnoreCase));

        if (!string.IsNullOrWhiteSpace(rv51))
            return rv51!;

        // Fallback: any Realistic Vision V5
        var rv5 = availableModels.FirstOrDefault(m =>
            m.Contains("Realistic Vision V5", StringComparison.InvariantCultureIgnoreCase));

        return string.IsNullOrWhiteSpace(rv5) ? modelName : rv5!;
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
    //  SCENE-TYPE INJECTION (people / landscape / architecture)
    // ------------------------------------------------------------

    private static (string Positive, string Negative) GetSceneTypeInjection(SceneType scene)
    {
        return scene switch
        {
            SceneType.People => (
                Positive:
                    "maintain facial structure, maintain body proportions, match skin tone, match lighting, natural anatomy",
                Negative:
                    "distorted anatomy, extra limbs, stretched body, incorrect face, uncanny face"
            ),

            SceneType.Landscape => (
                Positive:
                    "extend environment, extend horizon, match sky gradient, match terrain, seamless background continuation",
                Negative:
                    "warped horizon, mismatched lighting, inconsistent terrain"
            ),

            SceneType.Architecture => (
                Positive:
                    "extend building structure, maintain straight lines, maintain perspective, match materials, match lighting",
                Negative:
                    "warped geometry, bent lines, incorrect perspective, broken architecture"
            ),

            _ => ("", "")
        };
    }

    // ------------------------------------------------------------
    //  SCENE-DIRECTION INJECTION (horizontal vs vertical)
    // ------------------------------------------------------------

    private static (string Positive, string Negative) GetSceneDirectionInjection(
        int left, int right, int top, int bottom)
    {
        bool horizontal = left > 0 || right > 0;
        bool vertical = top > 0 || bottom > 0;

        string pos = "";
        string neg = "";

        if (horizontal)
        {
            pos = JoinNonEmpty(
                pos,
                "extend background, extend environment, match lighting, match perspective, seamless continuation"
            );
        }

        if (vertical)
        {
            pos = JoinNonEmpty(
                pos,
                "extend composition, maintain proportions, avoid stretching body"
            );
            neg = JoinNonEmpty(
                neg,
                "distorted proportions, stretched anatomy"
            );
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
