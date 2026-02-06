using StabilityMatrix.Avalonia.Services.Prompting;


namespace StabilityMatrix.Avalonia.Services
{
    public readonly struct OutpaintPromptInjection
    {
        public string Positive { get; }
        public string Negative { get; }

        public OutpaintPromptInjection(string positive, string negative)
        {
            Positive = positive;
            Negative = negative;
        }

        public static readonly OutpaintPromptInjection Empty = new("", "");
    }

    public static class PromptInjectionOutpaint
    {
        public static OutpaintPromptInjection Build(
            int expandLeft,
            int expandRight,
            int expandTop,
            int expandBottom,
            double strength // 0.0 – 1.0
        )
        {
            if (strength <= 0.0)
                return OutpaintPromptInjection.Empty;

            var pos = new List<string>();
            var neg = new List<string>();

            // scale factor ovisno o veličini outpainta
            var total = expandLeft + expandRight + expandTop + expandBottom;
            var sizeFactor = total <= 0 ? 0.0 : Math.Clamp(total / 512.0, 0.25, 1.5);
            var w = strength * sizeFactor; // ukupna “težina” injekcije

            // helper za weight
            string W(string text, double weight)
                => weight == 0.0 ? text : $"{text}::{weight:0.##}";

            bool down = expandBottom > 0;
            bool up = expandTop > 0;
            bool left = expandLeft > 0;
            bool right = expandRight > 0;
            bool horizontal = left || right;
            bool vertical = up || down;

            // DOLJE – noge, donji dio tijela
            if (down)
            {
                pos.Add("full body continuation");
                pos.Add("anatomically correct legs");
                pos.Add("natural posture");
                pos.Add("consistent proportions");

                neg.Add(W("disfigured legs", -1.0 * w));
                neg.Add(W("broken anatomy", -1.0 * w));
                neg.Add(W("extra limbs", -1.2 * w));
                neg.Add(W("distorted proportions", -0.8 * w));
            }

            // GORE – gornji dio tijela, glava
            if (up)
            {
                pos.Add("upper body continuation");
                pos.Add("head and shoulders");
                pos.Add("natural anatomy");
                pos.Add("consistent proportions");

                neg.Add(W("deformed head", -1.0 * w));
                neg.Add(W("broken neck", -1.0 * w));
                neg.Add(W("extra faces", -1.2 * w));
            }

            // LIJEVO / DESNO – scena, pozadina
            if (horizontal)
            {
                pos.Add("scene continuation");
                pos.Add("consistent background");
                pos.Add("matching lighting");
                pos.Add("seamless environment");

                neg.Add(W("mismatched lighting", -1.0 * w));
                neg.Add(W("warped background", -1.0 * w));
                neg.Add(W("duplicate objects", -1.0 * w));
            }

            // KOMBINACIJE – ako je sve, naglasi koherenciju
            if (horizontal && vertical)
            {
                pos.Add("coherent composition");
                pos.Add("consistent perspective");
                pos.Add("seamless extension of the original image");

                neg.Add(W("incoherent composition", -0.8 * w));
                neg.Add(W("perspective distortion", -0.8 * w));
            }

            if (pos.Count == 0 && neg.Count == 0)
                return OutpaintPromptInjection.Empty;

            var positive = " " + string.Join(", ", pos.Distinct());
            var negative = " " + string.Join(", ", neg.Distinct());

            return new OutpaintPromptInjection(positive, negative);
        }
    }
}
