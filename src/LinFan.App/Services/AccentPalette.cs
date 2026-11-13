// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Media;

namespace LinFan.App.Services;

/// <summary>
/// Derives the accent colours of each theme from the user's choice (<see cref="AccentChoice"/>). Every theme gets a
/// readable variant of the same colour: lines and text reach <see cref="MinContrast"/> against the theme's surfaces
/// (<see cref="Backgrounds"/>), and so does the text on a filled button. Light, Dark and Terminal need 4.5:1, high
/// contrast 7:1.
/// <para>
/// A preset is a colour ladder (300 … 800, Tailwind): each theme takes the first sufficient step from where its own
/// accent sits (light: from 600 darker, dark and terminal: from 400 lighter, high contrast: from 300 lighter) and
/// computes on past the end of the ladder. A colour of the user's own is lightened or darkened until it reads
/// (<see cref="IsAdjusted"/>). Light and dark fill their buttons with a saturated step under white text; terminal and
/// high contrast fill them with the bright accent itself under dark text.
/// </para>
/// <para>
/// The surface and standard colours repeat Theme/SemanticColors.axaml (BgWindow, BgCard, BgCardAlt, OnAccent,
/// AccentColor) so this stays testable without an application; a UI test keeps the two in step.
/// </para>
/// </summary>
public static class AccentPalette
{
    /// <summary>Lightness step past the end of a ladder (HSL), about the distance between two Tailwind steps.</summary>
    private const double LadderStep = 0.07;

    private static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);

    /// <param name="Surfaces">Where accent text and lines are read: BgWindow, BgCard, BgCardAlt.</param>
    /// <param name="LadderStart">The ladder step the theme's own accent sits on (0 = 300 … 5 = 800).</param>
    /// <param name="BrightFill">Buttons are filled with the accent itself under dark <paramref name="Ink"/>.</param>
    /// <param name="Ink">Text on filled buttons (OnAccent).</param>
    private sealed record Rule(
        Color[] Surfaces,
        double Min,
        int LadderStart,
        bool BrightFill,
        Color Ink,
        byte TintAlpha,
        Color Standard)
    {
        public bool LightSurfaces => ColorContrast.Luminance(Surfaces[0]) > 0.5;

        /// <summary>BgCard - what the clamp line is mixed into on dark themes.</summary>
        public Color Card => Surfaces[1];
    }

    private static readonly Rule LightRule = new(
        [Hex("#F7F7FA"), Hex("#FFFFFF"), Hex("#EFEFF3")], 4.5, 3, false, White, 0x14, Hex("#0284C7"));

    private static readonly Rule DarkRule = new(
        [Hex("#0E0E11"), Hex("#16161A"), Hex("#1E1E24")], 4.5, 1, false, White, 0x14, Hex("#38BDF8"));

    private static readonly Rule TerminalRule = new(
        [Hex("#050805"), Hex("#0A100B"), Hex("#0F1A11")], 4.5, 1, true, Hex("#050805"), 0x14, Hex("#39FF6A"));

    private static readonly Rule HighContrastRule = new(
        [Hex("#000000"), Hex("#000000"), Hex("#121212")], 7, 0, true, Hex("#000000"), 0x26, Hex("#3FE0FF"));

    /// <summary>The presets as ladders 300, 400, 500, 600, 700, 800 (Tailwind).</summary>
    private static readonly IReadOnlyDictionary<AccentChoice, Color[]> Ladders = new Dictionary<AccentChoice, Color[]>
    {
        [AccentChoice.Indigo] = Ladder("#A5B4FC", "#818CF8", "#6366F1", "#4F46E5", "#4338CA", "#3730A3"),
        [AccentChoice.Violet] = Ladder("#C4B5FD", "#A78BFA", "#8B5CF6", "#7C3AED", "#6D28D9", "#5B21B6"),
        [AccentChoice.Pink] = Ladder("#F9A8D4", "#F472B6", "#EC4899", "#DB2777", "#BE185D", "#9D174D"),
        [AccentChoice.Orange] = Ladder("#FDBA74", "#FB923C", "#F97316", "#EA580C", "#C2410C", "#9A3412"),
        [AccentChoice.Emerald] = Ladder("#6EE7B7", "#34D399", "#10B981", "#059669", "#047857", "#065F46"),
        [AccentChoice.Gray] = Ladder("#D4D4D8", "#A1A1AA", "#71717A", "#52525B", "#3F3F46", "#27272A"),
    };

    /// <summary>The presets in the order the appearance page shows them.</summary>
    public static IReadOnlyList<AccentChoice> Presets { get; } =
        [AccentChoice.Indigo, AccentChoice.Violet, AccentChoice.Pink, AccentChoice.Orange, AccentChoice.Emerald, AccentChoice.Gray];

    /// <summary>The colour ladder of a preset (300 … 800).</summary>
    public static IReadOnlyList<Color> LadderOf(AccentChoice preset) =>
        Ladders.TryGetValue(preset, out Color[]? ladder)
            ? ladder
            : throw new ArgumentOutOfRangeException(nameof(preset), preset, "Not a preset.");

    /// <summary>The surfaces accent text and lines must read on in a theme.</summary>
    public static IReadOnlyList<Color> Backgrounds(ThemeChoice theme) => RuleOf(theme).Surfaces;

    /// <summary>The contrast a theme's accent text and button text must reach (4.5, high contrast 7).</summary>
    public static double MinContrast(ThemeChoice theme) => RuleOf(theme).Min;

    /// <summary>The theme's own accent - what <see cref="AccentChoice.Standard"/> shows.</summary>
    public static Color StandardAccent(ThemeChoice theme) => RuleOf(theme).Standard;

    /// <summary>
    /// The accent colours of <paramref name="theme"/> for a choice; <c>null</c> for <see cref="AccentChoice.Standard"/>
    /// - the theme's own tokens apply then. <paramref name="custom"/> is only read for <see cref="AccentChoice.Custom"/>.
    /// </summary>
    public static AccentTokens? Derive(ThemeChoice theme, AccentChoice choice, Color custom = default)
    {
        Rule rule = RuleOf(theme);
        return choice switch
        {
            AccentChoice.Standard => null,
            AccentChoice.Custom => FromColor(rule, Color.FromRgb(custom.R, custom.G, custom.B)),
            _ => FromLadder(rule, LadderOf(choice)),
        };
    }

    /// <summary>The accent line colour a choice gives in a theme - what its swatch shows there.</summary>
    public static Color AccentOf(ThemeChoice theme, AccentChoice choice, Color custom = default) =>
        Derive(theme, choice, custom)?.Accent ?? StandardAccent(theme);

    /// <summary>
    /// Whether a colour of the user's own had to be lightened or darkened to read as a line in the theme. The button
    /// fill does not count: where the line reads, so does the text on it (light, terminal, high contrast), and the dark
    /// theme fills with a deeper variant under white text by design - no line colour could carry white text there.
    /// </summary>
    public static bool IsAdjusted(ThemeChoice theme, Color custom)
    {
        Color own = Color.FromRgb(custom.R, custom.G, custom.B);
        return Derive(theme, AccentChoice.Custom, own)!.Accent != own;
    }

    /// <summary>
    /// Which status colour the chosen accent comes close to. Only for saturated colours of a middle lightness; greys,
    /// near-black and near-white clash with nothing. A preset is judged by its middle step (500).
    /// </summary>
    public static AccentClash ClashOf(AccentChoice choice, Color custom = default)
    {
        if (choice == AccentChoice.Standard)
            return AccentClash.None;
        Color basis = choice == AccentChoice.Custom ? custom : LadderOf(choice)[2];
        HslColor hsl = basis.ToHsl();
        if (hsl.S < 0.35 || hsl.L < 0.15 || hsl.L > 0.9)
            return AccentClash.None;
        return hsl.H switch
        {
            >= 15 and <= 52 => AccentClash.Warning,
            >= 340 or < 15 => AccentClash.Danger,
            >= 90 and <= 165 => AccentClash.Success,
            _ => AccentClash.None,
        };
    }

    private static AccentTokens FromLadder(Rule rule, IReadOnlyList<Color> ladder)
    {
        bool light = rule.LightSurfaces;
        Color[] ink = [rule.Ink];

        // Past either end of the ladder the steps go on computed: lighter below 300, darker beyond 800.
        Color Step(int i) => i < 0
            ? ColorContrast.ShiftLightness(ladder[0], -LadderStep * i)
            : i >= ladder.Count
                ? ColorContrast.ShiftLightness(ladder[^1], -LadderStep * (i - ladder.Count + 1))
                : ladder[i];

        // Light surfaces need darker steps (up the ladder), dark ones lighter steps (down it).
        int a = rule.LadderStart;
        while (ColorContrast.MinRatio(Step(a), rule.Surfaces) < rule.Min && (light ? a < ladder.Count - 1 : a > 0))
            a += light ? 1 : -1;
        Color accent = ColorContrast.Fit(Step(a), rule.Surfaces, rule.Min, lighten: !light);

        Color fill, hover, pressed;
        if (rule.BrightFill)
        {
            fill = ColorContrast.Fit(accent, ink, rule.Min, lighten: true);
            hover = ColorContrast.Fit(Step(a - 1), ink, rule.Min, lighten: true);
            pressed = ColorContrast.Fit(Step(a + 1), ink, rule.Min, lighten: true);
        }
        else
        {
            // White text: the first step from 600 on that carries it.
            int f = 3;
            while (ColorContrast.Ratio(Step(f), rule.Ink) < rule.Min && f < ladder.Count - 1)
                f++;
            fill = ColorContrast.Fit(Step(f), ink, rule.Min, lighten: false);
            hover = ColorContrast.Fit(Step(f + 1), ink, rule.Min, lighten: false);
            pressed = ColorContrast.Fit(Step(f + 2), ink, rule.Min, lighten: false);
        }

        Color clamp = light ? ladder[0] : ColorContrast.Mix(accent, rule.Card, 0.45);
        return new AccentTokens(accent, fill, hover, pressed, Step(a + 1), clamp, WithAlpha(accent, rule.TintAlpha), rule.Ink);
    }

    private static AccentTokens FromColor(Rule rule, Color own)
    {
        bool light = rule.LightSurfaces;
        Color[] ink = [rule.Ink];
        Color accent = ColorContrast.Fit(own, rule.Surfaces, rule.Min, lighten: !light);

        Color fill, hover, pressed;
        if (rule.BrightFill)
        {
            fill = ColorContrast.Fit(accent, ink, rule.Min, lighten: true);
            hover = ColorContrast.ShiftLightness(fill, 0.08);
            pressed = ColorContrast.Fit(ColorContrast.ShiftLightness(fill, -0.08), ink, rule.Min, lighten: true);
        }
        else
        {
            fill = ColorContrast.Fit(own, ink, rule.Min, lighten: false);
            hover = ColorContrast.ShiftLightness(fill, -LadderStep);
            pressed = ColorContrast.ShiftLightness(fill, -2 * LadderStep);
        }

        Color clamp = light ? ColorContrast.Mix(own, White, 0.45) : ColorContrast.Mix(accent, rule.Card, 0.45);
        return new AccentTokens(accent, fill, hover, pressed, ColorContrast.ShiftLightness(accent, -LadderStep), clamp,
            WithAlpha(accent, rule.TintAlpha), rule.Ink);
    }

    private static Rule RuleOf(ThemeChoice theme) => theme switch
    {
        ThemeChoice.Light => LightRule,
        ThemeChoice.Dark => DarkRule,
        ThemeChoice.Terminal => TerminalRule,
        ThemeChoice.HighContrast => HighContrastRule,
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "A concrete theme is needed, not System."),
    };

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static Color Hex(string hex) => ColorContrast.TryParseHex(hex, out Color c)
        ? c
        : throw new ArgumentException($"Not a colour: {hex}", nameof(hex));

    private static Color[] Ladder(params string[] hex) => hex.Select(Hex).ToArray();
}
