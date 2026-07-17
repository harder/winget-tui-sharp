namespace WingetTuiSharp;

/// <summary>
/// Switchable color palettes and the registered <see cref="Scheme"/>s for the app, plus the
/// pixel-art "winget" wordart rendered as a <see cref="Logo"/> view. The default (Sage) and
/// the "Amber" alternative mirror the constants and shapes in shanselman/winget-tui's
/// src/theme.rs; Amber is the exact upstream-matching palette.
/// </summary>
public static class Theme
{
    /// <summary>A named set of the 11 color roles the app draws with.</summary>
    public readonly record struct Palette (
        Color Accent,
        Color AccentDim,
        Color TextPrimary,
        Color TextSecondary,
        Color TextOnAccent,
        Color Surface,
        Color Bg,
        Color Success,
        Color Danger,
        Color Info,
        Color Selection);

    public static readonly Palette AmberPalette = new (
        Accent: new (238, 201, 141),
        AccentDim: new (137, 130, 112),
        TextPrimary: new (232, 220, 183),
        TextSecondary: new (158, 158, 158),
        TextOnAccent: new (30, 30, 30),
        Surface: new (45, 45, 45),
        Bg: new (30, 30, 30),
        Success: new (86, 185, 127),
        Danger: new (231, 72, 86),
        Info: new (97, 175, 239),
        Selection: new (198, 120, 221));

    public static readonly Palette SagePalette = AmberPalette with
    {
        Accent = new (196, 214, 150),
        AccentDim = new (128, 138, 110),
        TextPrimary = new (222, 227, 196)
    };

    public static readonly Palette MossPalette = AmberPalette with
    {
        Accent = new (216, 206, 124),
        AccentDim = new (140, 132, 80),
        TextPrimary = new (228, 220, 168)
    };

    public static readonly Palette RosePalette = AmberPalette with
    {
        Accent = new (236, 194, 194),
        AccentDim = new (156, 124, 124),
        TextPrimary = new (236, 212, 212)
    };

    /// <summary>All selectable palettes, in picker/display order. <c>Id</c> is the short
    /// lowercase token accepted by <c>--theme=</c> and <see cref="TryApply"/>; <c>DisplayName</c>
    /// is what the in-app theme picker shows.</summary>
    public static readonly IReadOnlyList<(string Id, string DisplayName, Palette Value)> Palettes =
    [
        ("amber", "Amber", AmberPalette),
        ("sage", "Sage", SagePalette),
        ("moss", "Moss & Olive", MossPalette),
        ("rose", "Dusty Rose", RosePalette)
    ];

    public static string CurrentPaletteName { get; private set; } = "sage";

    public static Color Accent = SagePalette.Accent;
    public static Color AccentDim = SagePalette.AccentDim;
    public static Color TextPrimary = SagePalette.TextPrimary;
    public static Color TextSecondary = SagePalette.TextSecondary;
    public static Color TextOnAccent = SagePalette.TextOnAccent;
    public static Color Surface = SagePalette.Surface;
    public static Color Bg = SagePalette.Bg;
    public static Color Success = SagePalette.Success;
    public static Color Danger = SagePalette.Danger;
    public static Color Info = SagePalette.Info;
    public static Color Selection = SagePalette.Selection;

    /// <summary>Switches to the palette whose <c>Id</c> matches <paramref name="id"/>
    /// (case-insensitive), re-registers all schemes, and returns <see langword="true"/>.
    /// Returns <see langword="false"/> without changing anything if <paramref name="id"/>
    /// doesn't match any entry in <see cref="Palettes"/>.</summary>
    public static bool TryApply (string id)
    {
        foreach ((string paletteId, string _, Palette value) in Palettes)
        {
            if (string.Equals (paletteId, id, StringComparison.OrdinalIgnoreCase))
            {
                Accent = value.Accent;
                AccentDim = value.AccentDim;
                TextPrimary = value.TextPrimary;
                TextSecondary = value.TextSecondary;
                TextOnAccent = value.TextOnAccent;
                Surface = value.Surface;
                Bg = value.Bg;
                Success = value.Success;
                Danger = value.Danger;
                Info = value.Info;
                Selection = value.Selection;
                CurrentPaletteName = paletteId;
                Register ();

                return true;
            }
        }

        return false;
    }

    public const string AppSchemeName = "WingetTuiSharp.App";
    public const string SurfaceSchemeName = "WingetTuiSharp.Surface";
    public const string FrameFocusedSchemeName = "WingetTuiSharp.FrameFocused";
    public const string FrameUnfocusedSchemeName = "WingetTuiSharp.FrameUnfocused";
    public const string NavbarActiveSchemeName = "WingetTuiSharp.NavbarActive";
    public const string NavbarInactiveSchemeName = "WingetTuiSharp.NavbarInactive";
    public const string StatusSchemeName = "WingetTuiSharp.Status";
    public const string AccentSchemeName = "WingetTuiSharp.Accent";
    public const string AccentDimSchemeName = "WingetTuiSharp.AccentDim";
    public const string InfoSchemeName = "WingetTuiSharp.Info";
    public const string DangerSchemeName = "WingetTuiSharp.Danger";
    public const string SuccessSchemeName = "WingetTuiSharp.Success";

    public static void Register ()
    {
        SchemeManager.AddScheme (AppSchemeName, new ()
        {
            Normal = new (TextPrimary, Bg),
            Focus = new (TextPrimary, Bg),
            Active = new (TextOnAccent, Accent, TextStyle.Bold),
            HotNormal = new (Accent, Bg),
            HotFocus = new (Accent, Bg),
            Disabled = new (TextSecondary, Bg)
        });

        SchemeManager.AddScheme (SurfaceSchemeName, new ()
        {
            Normal = new (TextPrimary, Surface),

            // TableView uses Focus for the selected row when focused, Active when not.
            // Focus must be the brightest (selected + focused). Active must still be
            // visible (selected without focus) so the highlight persists.
            Focus = new (TextOnAccent, Accent, TextStyle.Bold),
            Active = new (TextPrimary, AccentDim, TextStyle.Bold),
            HotNormal = new (Accent, Surface),
            HotFocus = new (TextOnAccent, Accent, TextStyle.Bold),
            Disabled = new (TextSecondary, Surface)
        });

        SchemeManager.AddScheme (NavbarActiveSchemeName, new ()
        {
            Normal = new (TextOnAccent, Accent, TextStyle.Bold),
            Focus = new (TextOnAccent, Accent, TextStyle.Bold),
            HotNormal = new (TextOnAccent, Accent, TextStyle.Bold),
            HotFocus = new (TextOnAccent, Accent, TextStyle.Bold)
        });

        SchemeManager.AddScheme (NavbarInactiveSchemeName, new ()
        {
            Normal = new (AccentDim, Bg),
            Focus = new (TextPrimary, Bg),
            HotNormal = new (AccentDim, Bg),
            HotFocus = new (TextPrimary, Bg)
        });

        SchemeManager.AddScheme (StatusSchemeName, new ()
        {
            Normal = new (TextPrimary, Surface),
            Focus = new (TextPrimary, Surface),
            HotNormal = new (Accent, Surface),
            HotFocus = new (Accent, Surface)
        });

        SchemeManager.AddScheme (AccentSchemeName, new ()
        {
            Normal = new (Accent, Bg, TextStyle.Bold),
            Focus = new (Accent, Bg, TextStyle.Bold),
            HotNormal = new (Accent, Bg, TextStyle.Bold),
            HotFocus = new (Accent, Bg, TextStyle.Bold)
        });

        SchemeManager.AddScheme (AccentDimSchemeName, new ()
        {
            Normal = new (AccentDim, Bg),
            Focus = new (AccentDim, Bg),
            HotNormal = new (AccentDim, Bg),
            HotFocus = new (AccentDim, Bg)
        });

        // Schemes used by FrameView containers ONLY (not by their inner content). The Border
        // renders its lines and title using VisualRole.Normal, so we swap these schemes on
        // the frame based on whether its content has focus. Inner content keeps its own
        // SchemeName so data colors are unaffected.
        SchemeManager.AddScheme (FrameFocusedSchemeName, new ()
        {
            Normal = new (Accent, Surface, TextStyle.Bold),
            Focus = new (Accent, Surface, TextStyle.Bold)
        });

        SchemeManager.AddScheme (FrameUnfocusedSchemeName, new ()
        {
            Normal = new (AccentDim, Surface),
            Focus = new (AccentDim, Surface)
        });

        SchemeManager.AddScheme (InfoSchemeName, new ()
        {
            Normal = new (Info, Bg, TextStyle.Underline),
            Focus = new (Info, Bg, TextStyle.Underline),
            HotNormal = new (Info, Bg, TextStyle.Underline),
            HotFocus = new (Info, Bg, TextStyle.Underline)
        });

        SchemeManager.AddScheme (DangerSchemeName, new ()
        {
            Normal = new (Danger, Bg),
            Focus = new (Danger, Bg),
            HotNormal = new (Danger, Bg),
            HotFocus = new (Danger, Bg)
        });

        SchemeManager.AddScheme (SuccessSchemeName, new ()
        {
            Normal = new (Success, Bg),
            Focus = new (Success, Bg),
            HotNormal = new (Success, Bg),
            HotFocus = new (Success, Bg)
        });
    }
}

/// <summary>
/// Block-art "WINGET TUI #" wordmark rendered directly in 5 text rows for better legibility.
/// </summary>
public sealed class Logo : View
{
    // "WINGET TUI #" rendered as a compact 51×5 block wordmark with a 1-column gap
    // between letters and a 2-column gap between words.
    private static readonly string [] _lines =
    [
        "█   █ ███ █  █  ██  ████ ████  ████ █  █ ███   █ █ ",
        "█   █  █  ██ █ █    █     █     █   █  █  █   █████",
        "█ █ █  █  █ ██ █ ██ ███   █     █   █  █  █    █ █ ",
        "██ ██  █  █  █ █  █ █     █     █   █  █  █   █████",
        "█   █ ███ █  █  ███ ████  █     █    ██  ███   █ █ "
    ];

    public const int LogoWidth = 51;
    public const int LogoHeight = 5;

    public Logo ()
    {
        Width = LogoWidth;
        Height = LogoHeight;
        CanFocus = false;
        SchemeName = Theme.AccentSchemeName;
    }

    /// <inheritdoc />
    protected override bool OnDrawingContent (DrawContext? context)
    {
        SetAttribute (new (Theme.Accent, Theme.Bg, TextStyle.Bold));

        for (int y = 0; y < _lines.Length && y < Viewport.Height; y++)
        {
            Move (0, y);
            AddStr (_lines [y]);
        }

        return true;
    }
}
