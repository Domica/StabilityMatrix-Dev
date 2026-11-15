using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace StabilityMatrix.Avalonia.Models.Inference;

/// <summary>
/// Turns N Florence-2 reference captions plus the user's instruction into ONE
/// context-aware scene prompt.
///
/// This is the piece that was missing from the earlier "reference caption fusion":
/// the captions were only concatenated and framed. A caption such as
/// "a man in a blue shirt standing on a beach" dragged its own beach into the
/// result, so a prompt like "all people sitting in the living room" lost to the
/// eight literal mini-scenes and Z-Image drew a collage.
///
/// The composer assigns roles explicitly:
///   * the USER PROMPT owns the scene, action, arrangement, lighting and style;
///   * the CAPTIONS own only the subjects' appearance.
/// Original backgrounds, locations and poses are scrubbed out of the captions,
/// and the resulting prompt is framed so the user's instruction is both the
/// first and the last thing the text encoder sees.
///
/// Deliberately deterministic and dependency-free: no LLM, API key or extra
/// model is required, so it works on any ComfyUI install that has Florence-2.
/// An external LLM could later replace <see cref="Compose"/> without touching
/// the graph plumbing.
/// </summary>
public static class ReferenceSceneComposer
{
    private const int MaxSubjectChars = 120;

    /// <summary>
    /// The environment caption is kept as the scene definition. With the richer
    /// "paragraph" captions it can grow to several hundred words, which would
    /// drown out the user's instruction, so keep the leading (most relevant)
    /// sentences and cut at a sentence boundary.
    /// </summary>
    private const int MaxSceneChars = 420;

    private const string DefaultInstruction =
        "A single group photograph of all the people together in one shared scene.";

    /// <summary>Strips prompt syntax such as &lt;lora:...&gt; before it can leak into CLIP text.</summary>
    private static readonly Regex AngleTokens = new(@"<[^>]*>", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>"A photo of ...", "The image shows ...", etc. — Florence-2 caption boilerplate.</summary>
    private static readonly Regex CaptionBoilerplate = new(
        @"^\s*(?:(?:a|an|the)\s+)?(?:close[- ]?up\s+)?(?:photo|photograph|photographic\s+image|picture|image|snapshot|selfie|portrait|render|rendering|illustration|cropped\s+image)\s+(?:of|showing|depicting)\s+"
        + @"|^\s*(?:the\s+)?(?:image|photo|picture|photo(?:graph)?)\s+(?:shows|depicts|features|contains|displays|is\s+of)\s+"
        + @"|^\s*(?:there\s+is|there\s+are|this\s+is|it\s+shows)\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>
    /// Location phrases that belong to the ORIGINAL reference photo, not to the
    /// scene the user asked for. Note that clothing ("in a blue shirt") is safe
    /// because the noun list contains places/things, not garments.
    /// </summary>
    private static readonly Regex LocationPhrase = new(
        @"\s*,?\s*\b(?:in|on|at|near|beside|next\s+to|outside|inside|against|in\s+front\s+of|behind|under|over)\s+"
        + @"(?:the\s+|a\s+|an\s+|his\s+|her\s+|their\s+)?"
        + @"(?:beach|kitchen|living\s+room|bedroom|bathroom|office|street|park|garden|forest|woods|"
        + @"city|room|house|building|road|car|sofa|couch|chair|table|floor|wall|walls|background|"
        + @"sky|water|sand|grass|mountain|mountains|studio|restaurant|cafe|bar|field|boat|train|bus|"
        + @"balcony|stairs|corridor|pool|sea|ocean|desk|bed|window|door|yard|patio|porch|stage|"
        + @"backdrop|forest|playground|market|shop|store|airport|station|bridge|river|lake|snow|"
        + @"sunset|sunlight|crowd|mirror|counter|shelf|television|tv|pavement|sidewalk)\b[^,;]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>Pose verbs describe the reference photo, so they must not override the user's action.</summary>
    private static readonly Regex PosePhrase = new(
        @"\s*,?\s*\b(?:standing|sitting|kneeling|lying|lying\s+down|walking|running|leaning|"
        + @"posing|dancing|jumping|crouching|bending|looking\s+at|facing|turned\s+away)\b[^,;]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>
    /// Language that describes a CONTACT SHEET (the reference file), not the real
    /// place or person the user wants generated. Florence-2 produces this when a
    /// reference image is a grid of several photos — injecting it verbatim made
    /// the prompt say both "no grid" and "this is a collage arranged in a grid".
    /// </summary>
    private static readonly Regex SheetLayoutClause = new(
        @"\b(?:collage|montage|contact\s+sheet|grid[- ]like|grid|panels?|arranged|"
        + @"(?:top|bottom)\s+(?:left|right)|\d+x\d+|different\s+(?:views?|angles?)|"
        + @"each\s+photograph|multiple\s+(?:images|frames|scenes)|series\s+of|"
        + @"closer\s+look|angles\s+of|photo\s+grid|set\s+of)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>
    /// "The image is a collage of four photographs of the interior of a car"
    /// -&gt; "the interior of a car" (keep the actual subject, drop the sheet).
    /// </summary>
    private static readonly Regex SheetLeadPhrase = new(
        @"(?:\b(?:the\s+)?image\s+(?:is|shows|depicts|features)\s+)?"
        + @"(?:a|an|the)?\s*(?:collage|montage|grid|contact\s+sheet|series|set|collection|arrangement)\s+of\s+"
        + @"(?:\w+\s+){0,3}?(?:photographs?|photos?|images?|pictures?|panels?)\s+of\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>"The first photograph on the top left shows X" -&gt; "X".</summary>
    private static readonly Regex SheetPanelLeadPhrase = new(
        @"\b(?:in\s+the\s+)?(?:the\s+)?(?:first|second|third|fourth|fifth|sixth|last|next|final)\s+"
        + @"(?:photographs?|photos?|images?|pictures?|panels?)\s+"
        + @"(?:on\s+the\s+|in\s+the\s+|at\s+the\s+)?[^,;:.]{0,60}?\b"
        + @"(?:shows?|depicts?|features?|displays?|contains?)\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>"In the first photo, the man is..." -&gt; "the man is...".</summary>
    private static readonly Regex SheetPhotoCommaPhrase = new(
        @"\b(?:in\s+the\s+)?(?:the\s+)?(?:first|second|third|fourth|fifth|sixth)\s+"
        + @"(?:photographs?|photos?|images?|pictures?|panels?)\s*,\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>Fragments like "a man" or "the man is" add no information.</summary>
    private static readonly Regex BarePersonClause = new(
        @"^\s*(?:a|an|the|he|she|this)?\s*"
        + @"(?:man|men|woman|women|boy|boys|girl|girls|person|people|guy|lady|male|female)\s*"
        + @"(?:is|are|was|were)?\s*\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>Meta prefixes that carry no visual information.</summary>
    private static readonly Regex SceneMetaPrefix = new(
        @"^\s*(?:the\s+)?(?:image|photo|picture|photograph|scene)\s+"
        + @"(?:is|shows|depicts|features|contains|displays)\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>
    /// Kept clauses must describe a person or their appearance. The richer
    /// Florence-2 paragraphs mention materials, facial features and accessories,
    /// so those are included too. Bare colour words are deliberately NOT on the
    /// list: garment nouns ("blue shirt", "navy jacket") already let the colour
    /// through, while a bare colour would also admit "blue sky" / "white wall".
    /// </summary>
    private static readonly Regex PersonOrAppearance = new(
        @"\b(?:man|men|woman|women|boy|boys|girl|girls|person|people|child|children|kid|kids|"
        + @"teenager|toddler|baby|guy|lady|male|female|gentleman|adult|elderly|young|middle[- ]aged|"
        + @"face|head|portrait|complexion|skin|eyes|eye|eyebrows|lashes|nose|lips|mouth|chin|cheeks|jaw|"
        + @"freckles|wrinkles|dimples|smile|smiling|grin|expression|frown|makeup|lipstick|"
        + @"hair|blonde|brunette|redhead|curly|wavy|straight|bald|braided|ponytail|bangs|fringe|"
        + @"shirt|t[- ]?shirt|tank\s+top|vest|blazer|cardigan|jumpsuit|sweatshirt|pullover|"
        + @"dress|jacket|coat|sweater|hoodie|suit|jeans|trousers|shorts|skirt|blouse|"
        + @"uniform|tie|collar|sleeve|sleeves|zipper|pocket|pockets|belt|boots|shoes|gloves|"
        + @"glasses|sunglasses|beard|moustache|mustache|stubble|hat|cap|scarf|helmet|"
        + @"tattoo|tattoos|piercing|jewellery|jewelry|necklace|earrings|watch|bracelet|ring|"
        + @"striped|plaid|checkered|floral|patterned|pattern|leather|denim|cotton|wool|silk|linen|velvet|"
        + @"long[- ]sleeved|short[- ]sleeved|sleeveless|"
        + @"čovjek|muškarac|žena|dijete|djeca|osoba|kosa|majica|haljina|odijelo)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly string[] PaintingWords =
    [
        "oil painting", "watercolor", "watercolour", "painting", "painted", "sketch", "drawing",
        "illustration", "fresco", "canvas", "impressionist", "baroque", "renaissance", "slika",
        "ulje na platnu", "crtež"
    ];

    private static readonly string[] RenderWords =
    [
        "3d", "3d render", "render", "cgi", "blender", "unreal", "octane", "claymation", "pixar"
    ];

    private static readonly string[] AnimeWords =
    [
        "anime", "manga", "cartoon", "comic", "cel shaded", "chibi", "crtani", "strip"
    ];

    private static readonly string[] PhotoWords =
    [
        "photo", "photograph", "photography", "snapshot", "selfie", "polaroid", "film photo",
        "fotografija", "fotka"
    ];

    private static readonly string[] SceneWords =
    [
        "living room", "kitchen", "bedroom", "bathroom", "office", "garden", "beach", "street",
        "park", "forest", "studio", "restaurant", "cafe", "bar", "room", "house", "building",
        "city", "church", "school", "sofa", "couch", "table", "in front of", "at the", "on the",
        "outdoors", "indoors", "sunset", "night", "dnevna soba", "sobi", "kuhinji", "plaži"
    ];

    private static readonly string[] ActionWords =
    [
        "sitting", "standing", "hugging", "waving", "walking", "dancing", "laughing", "smiling",
        "holding", "looking", "posing", "talking", "eating", "drinking", "kneeling", "lying",
        "group photo", "group portrait", "in a row", "side by side", "sjede", "stoje", "zajedno"
    ];

    /// <summary>Result of a composition. <see cref="Prompt"/> is what goes to CLIPTextEncode.</summary>
    public sealed record Composition(
        string Prompt,
        IReadOnlyList<string> Subjects,
        IReadOnlyList<string> RawCaptions,
        string ContextSummary
    );

    private enum Medium
    {
        Unspecified,
        Photograph,
        Painting,
        Render,
        Anime,
    }

    /// <summary>
    /// Build the final generation prompt from the user's instruction and the
    /// Florence-2 captions for every reference image.
    /// </summary>
    /// <param name="characterNotes">Optional per-image notes (e.g. "the groom").</param>
    /// <param name="subjectDetailBudget">
    /// Maximum characters of appearance detail kept per reference. The caller
    /// raises this with the description detail level; the default preserves the
    /// original short-caption behaviour.
    /// </param>
    /// <param name="panelCount">
    /// 1 = one single undivided image (default). 2-6 = an intentional, clean
    /// grid of exactly that many panels. Values outside 1-6 fall back to 1.
    /// </param>
    public static Composition Compose(
        string? userPrompt,
        IReadOnlyList<string> captions,
        string? environmentCaption = null,
        string? environmentNote = null,
        IReadOnlyList<string>? characterNotes = null,
        int subjectDetailBudget = MaxSubjectChars,
        int panelCount = 1
    )
    {
        var panels = Math.Clamp(panelCount, 1, 6);
        var isGrid = panels > 1;
        var instruction = CleanInstruction(userPrompt);
        var rawCaptions = (captions ?? Array.Empty<string>())
            .Select(c => (c ?? string.Empty).Trim())
            .Where(c => c.Length > 0)
            .ToList();

        var medium = DetectMedium(instruction);
        var hasScene = ContainsAny(instruction, SceneWords);
        var hasAction = ContainsAny(instruction, ActionWords);

        // The environment reference (if any) defines the room / scene / background.
        // Its caption is kept largely as-is (only the leading caption boilerplate
        // is stripped) and the user's optional environment note is appended so Z-Image
        // knows exactly what to keep. Character captions stay appearance-only.
        var scene = BuildScene(environmentCaption, environmentNote);

        var subjects = rawCaptions
            .Select((caption, i) =>
            {
                var baseDesc = ScrubCaption(caption, subjectDetailBudget);
                var note = characterNotes is not null && i < characterNotes.Count ? characterNotes[i] : null;
                if (!string.IsNullOrWhiteSpace(note))
                {
                    baseDesc = string.IsNullOrWhiteSpace(baseDesc)
                        ? note.Trim()
                        : $"{baseDesc.TrimEnd('.')}. {note.Trim().TrimEnd('.')}";
                }

                return baseDesc;
            })
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var singleFrame = medium switch
        {
            Medium.Photograph => "One single coherent photograph of one continuous scene.",
            Medium.Painting => "One single coherent painting of one continuous scene.",
            Medium.Render => "One single coherent 3D render of one continuous scene.",
            Medium.Anime => "One single coherent illustration of one continuous scene.",
            _ => "One single coherent image of one continuous scene.",
        };

        var allCaptionText = string.Join(
            " ",
            rawCaptions.Append(environmentCaption ?? string.Empty)
        );
        var hasSheetRefs =
            SheetLayoutClause.IsMatch(allCaptionText) || SheetLeadPhrase.IsMatch(allCaptionText);

        // Positive wording only. Negation is unreliable on a distilled 8-step
        // model: the old prompt literally said "no collage, no grid, no panels"
        // and still produced a 2x3 collage because those very words prime the
        // concept. Describe the desired single frame instead of naming the
        // artefacts we do not want.
        var sheetLine = hasSheetRefs && !isGrid
            ? "The reference files contain several separate photos; use them only as descriptions of ONE real place and ONE real group of people. "
              + "Render one single real photograph of that place with everyone together. "
            : string.Empty;

        // Put the layout instruction FIRST as well as last. With six rich
        // descriptions the model used to read "six descriptions" as "six panels"
        // and drew a 2x3 collage even though the old tail said "no grid" — that
        // cue was ~2000 characters into the prompt and simply lost its weight.
        var frame = isGrid
            ? $"A clean contact sheet of exactly {panels} equally sized rectangular panels, {GridShape(panels)}, "
              + "separated by thin plain white borders. Every panel shows the same single continuous scene "
              + "and the same group of people, with consistent lighting and style. "
            : $"{singleFrame} {(hasSheetRefs ? "References show one real place. " : "")}"
              + "One continuous, seamless, edge-to-edge frame with everyone together in the same space. ";

        var subjectLead = isGrid
            ? "The same people appear together in every panel, described by appearance only: "
            : subjects.Count switch
            {
                0 => "All the people described by the reference images appear together. ",
                1 => "The person in this one scene, described by appearance only: ",
                _ => "Everyone is together in this one single frame, described by appearance only: ",
            };

        // Commas, not "Reference N:" or semicolon-separated mini-scenes: a list
        // of six semicolon clauses reads to the model like six separate pictures.
        var subjectList = subjects.Count == 0 ? string.Empty : string.Join(", ", subjects) + ". ";

        // Scene source: the environment reference wins; otherwise fall back to a
        // scene mentioned in the user prompt. Either way the user's instruction
        // (e.g. "all people sitting on the sofa") drives the arrangement.
        var sceneClause = scene is not null ? $"{scene}. {instruction}" : instruction;

        // When a scene is fixed (environment or prompt), force the characters into
        // it and strip their own backgrounds so they don't spawn competing scenes.
        var overrideLine = scene is not null || hasScene || hasAction
            ? "Place all of these people into this single scene. Discard every original background, location and pose from the character photos. "
            : "Keep the appearance of these people but place all of them together in one shared scene. ";

        // Recap uses the SHORT user instruction (not the 400-char environment
        // paragraph) so the final, highest-weight words are the user's intent.
        var tail = isGrid
            ? $"Exactly {panels} panels, no more and no fewer. Every panel shows this same scene: {instruction} "
              + "No text, no captions, no labels, no logos, no nested images inside a panel."
            : $"{overrideLine}{sheetLine}The whole image is exactly this one undivided moment: {instruction} "
              + "One seamless frame, one moment, one place, everyone sharing the same space from edge to edge.";

        var prompt = $"{frame}{sceneClause} {subjectLead}{subjectList}{tail}";

        var summary =
            $"Medium: {Label(medium)} · scene from: {(scene is not null ? "environment reference" : (hasScene ? "prompt" : "unspecified"))} · "
            + $"arrangement from prompt: {(hasAction ? "yes" : "unspecified")} · "
            + $"{subjects.Count}/{rawCaptions.Count} character description(s) kept · "
            + $"sheet/grid wording removed: {(hasSheetRefs ? "yes" : "no")} · "
            + $"layout: {(isGrid ? $"{panels} panels in one image" : "single image")}";

        return new Composition(prompt, subjects, rawCaptions, summary);
    }

    /// <summary>Plain-language grid shape so the sampler does not invent extra panels.</summary>
    private static string GridShape(int panels) => panels switch
    {
        2 => "in 1 row and 2 columns",
        3 => "in 1 row and 3 columns",
        4 => "in 2 rows and 2 columns",
        5 => "with 3 panels on the top row and 2 on the bottom row",
        _ => "in 2 rows and 3 columns",
    };

    /// <summary>
    /// Keeps the environment caption as the scene definition. When the reference
    /// is a contact sheet (very common: a "reference sheet" of car interior
    /// photos), Florence-2 describes the SHEET, not the place. Injecting that
    /// verbatim made the prompt say both "no grid" and "collage arranged in a
    /// 2x3 grid" — the model followed the more concrete description every time.
    /// Here we strip all sheet/layout wording and keep only real scene content.
    /// </summary>
    private static string? BuildScene(string? environmentCaption, string? environmentNote)
    {
        if (string.IsNullOrWhiteSpace(environmentCaption))
        {
            return null;
        }

        var raw = environmentCaption.Trim();
        var isSheet = SheetLayoutClause.IsMatch(raw) || SheetLeadPhrase.IsMatch(raw);

        var text = isSheet ? CleanSheetCaption(raw) : CaptionBoilerplate.Replace(raw, string.Empty);
        text = Whitespace.Replace(text, " ").Trim().Trim('.', ',', ' ', ';');

        // A caption made up entirely of sheet-layout language is useless as a
        // scene: fall back to whatever scene the user's instruction describes.
        if (text.Length < 12)
        {
            return null;
        }

        if (text.Length > MaxSceneChars)
        {
            var sentenceCut = text.LastIndexOf(". ", MaxSceneChars, StringComparison.Ordinal);
            if (sentenceCut > 120)
            {
                text = text[..(sentenceCut + 1)].Trim();
            }
            else
            {
                var wordCut = text.LastIndexOf(' ', MaxSceneChars);
                text = text[..(wordCut > 120 ? wordCut : MaxSceneChars)];
            }

            text = text.Trim().TrimEnd(',', '.', ' ', ';');
        }

        var prefix = isSheet ? "A single continuous real place: " : string.Empty;

        return prefix
            + text
            + (string.IsNullOrWhiteSpace(environmentNote) ? "" : $". {environmentNote.Trim().TrimEnd('.')}");
    }

    /// <summary>
    /// Removes contact-sheet framing from a caption while preserving the actual
    /// visual content ("the interior of a car", "black leather seats", ...).
    /// </summary>
    private static string CleanSheetCaption(string raw)
    {
        var text = SheetLeadPhrase.Replace(raw, string.Empty);
        text = SheetPanelLeadPhrase.Replace(text, string.Empty);
        text = SheetPhotoCommaPhrase.Replace(text, string.Empty);
        text = CaptionBoilerplate.Replace(text, string.Empty);
        text = Whitespace.Replace(text, " ");

        var fragments = text
            .Split(['.', ';', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(fragment => SceneMetaPrefix.Replace(fragment.Trim(), string.Empty).Trim())
            .Select(fragment => fragment.Trim(' ', '.', '-', ':', ';'))
            .Where(fragment => fragment.Length > 3 && !SheetLayoutClause.IsMatch(fragment))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return string.Join(", ", fragments).Trim();
    }

    /// <summary>
    /// Reduce a caption to an appearance-only subject descriptor. Background,
    /// location and pose clauses are dropped so they cannot fight the user's
    /// requested scene.
    /// </summary>
    /// <param name="caption">Raw (or merged) Florence-2 description.</param>
    /// <param name="maxChars">Maximum length of the returned descriptor.</param>
    public static string ScrubCaption(string caption, int maxChars = MaxSubjectChars)
    {
        // Strip the contact-sheet framing BEFORE clause filtering, otherwise a
        // clause such as "The image is a collage of four photos of a man" passes
        // the appearance test (it contains "man") and smuggles "collage" in.
        var text = SheetLeadPhrase.Replace(caption.Trim(), string.Empty);
        text = SheetPanelLeadPhrase.Replace(text, string.Empty);
        text = SheetPhotoCommaPhrase.Replace(text, string.Empty);
        text = CaptionBoilerplate.Replace(text, string.Empty);

        // Split on sentence boundaries too: Florence-2 paragraphs contain long
        // multi-sentence runs with few commas, and a clause-level location/pose
        // strip used to delete everything after the first "on a stage".
        var clauses = text
            .Split([',', ';', '.', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(clause => PosePhrase.Replace(LocationPhrase.Replace(clause, string.Empty), string.Empty))
            .Select(clause => clause.Trim().Trim('.', ' ', '-', ':'))
            .Where(clause =>
                clause.Length > 0
                && !SheetLayoutClause.IsMatch(clause)
                && !BarePersonClause.IsMatch(clause)
                && PersonOrAppearance.IsMatch(clause)
            )
            .ToList();

        var result = string.Join(", ", clauses);
        result = Whitespace.Replace(result, " ").Trim().Trim(',', '.', ' ', '-');

        if (result.Length == 0)
        {
            // Never throw away a reference entirely: fall back to layout-free,
            // boilerplate-free fragments of the original caption.
            var fallback = PosePhrase.Replace(
                LocationPhrase.Replace(text, string.Empty),
                string.Empty
            );
            fallback = string.Join(
                ", ",
                fallback
                    .Split([',', ';', '.', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(fragment => fragment.Trim())
                    .Where(fragment => fragment.Length > 0 && !SheetLayoutClause.IsMatch(fragment))
            );
            result = Whitespace.Replace(fallback, " ").Trim().Trim(',', '.', ' ', '-');
        }

        var budget = maxChars is < 40 or > 1000 ? MaxSubjectChars : maxChars;

        if (result.Length > budget)
        {
            var cut = result.LastIndexOf(' ', budget);
            result = (cut > 40 ? result[..cut] : result[..budget]).Trim().TrimEnd(',', '.');
        }

        return result;
    }

    private static string CleanInstruction(string? userPrompt)
    {
        var text = AngleTokens.Replace(userPrompt ?? string.Empty, " ");
        text = Whitespace.Replace(text, " ").Trim().TrimEnd('.', ' ', ',');

        if (text.Length == 0)
        {
            text = DefaultInstruction.TrimEnd('.');
        }

        return text + ".";
    }

    private static Medium DetectMedium(string instruction)
    {
        if (ContainsAny(instruction, PaintingWords))
        {
            return Medium.Painting;
        }

        if (ContainsAny(instruction, AnimeWords))
        {
            return Medium.Anime;
        }

        if (ContainsAny(instruction, RenderWords))
        {
            return Medium.Render;
        }

        if (ContainsAny(instruction, PhotoWords))
        {
            return Medium.Photograph;
        }

        return Medium.Unspecified;
    }

    private static string Label(Medium medium) => medium switch
    {
        Medium.Photograph => "photograph",
        Medium.Painting => "painting",
        Medium.Render => "3D render",
        Medium.Anime => "illustration",
        _ => "unspecified",
    };

    private static bool ContainsAny(string text, IEnumerable<string> words) =>
        words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
}
