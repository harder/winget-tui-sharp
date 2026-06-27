// Widget views and modal dialogs

namespace WingetTuiSharp;

/// <summary>
/// Custom tab bar widget that renders 3 mutually-exclusive tabs with mouse hit-testing.
/// Mirrors the navbar in src/ui.rs.
/// </summary>
public sealed class TabBar : View
{
    private static readonly (AppMode Mode, string Label) [] _tabs =
    [
        (AppMode.Search, "1 ◇ Search"),
        (AppMode.Installed, "2 ▣ Installed"),
        (AppMode.Upgrades, "3 △ Upgrades")
    ];

    private AppMode _active = AppMode.Installed;

    public TabBar ()
    {
        CanFocus = false;
        Height = 1;
        SchemeName = Theme.NavbarInactiveSchemeName;
    }

    public AppMode Active
    {
        get => _active;
        set
        {
            if (_active == value)
            {
                return;
            }

            _active = value;
            SetNeedsDraw ();
        }
    }

    public event EventHandler<AppMode>? TabClicked;

    /// <inheritdoc />
    protected override bool OnDrawingContent (DrawContext? context)
    {
        SetAttribute (new (Theme.AccentDim, Theme.Bg));

        for (int x = 0; x < Viewport.Width; x++)
        {
            Move (x, 0);
            AddStr (" ");
        }

        int cursor = 0;

        foreach ((AppMode mode, string label) in _tabs)
        {
            string padded = $" {label} ";

            if (cursor + padded.Length > Viewport.Width)
            {
                break;
            }

            Attribute attr = mode == _active
                                 ? new Attribute (Theme.TextOnAccent, Theme.Accent, TextStyle.Bold)
                                 : new Attribute (Theme.AccentDim, Theme.Bg);
            SetAttribute (attr);
            Move (cursor, 0);
            AddStr (padded);
            cursor += padded.Length + 1;
        }

        return true;
    }

    /// <inheritdoc />
    protected override bool OnMouseEvent (Mouse mouse)
    {
        if (!mouse.IsSingleClicked || mouse.Position is null)
        {
            return false;
        }

        int x = mouse.Position.Value.X;
        int cursor = 0;

        foreach ((AppMode mode, string label) in _tabs)
        {
            string padded = $" {label} ";

            if (x >= cursor && x < cursor + padded.Length)
            {
                Active = mode;
                TabClicked?.Invoke (this, mode);
                mouse.Handled = true;

                return true;
            }

            cursor += padded.Length + 1;
        }

        return false;
    }
}

/// <summary>
/// Bottom status bar showing the source filter badge, pin filter badge, status message,
/// and right-aligned context-aware hotkey hints. Replicates ui.rs::render_status_bar.
/// </summary>
public sealed class StatusBar : View
{
    public StatusBar ()
    {
        Height = 1;
        CanFocus = false;
        SchemeName = Theme.StatusSchemeName;
    }

    public AppMode Mode { get; set; } = AppMode.Installed;
    public InputMode InputMode { get; set; } = InputMode.Normal;
    public string? SourceFilter { get; set; }
    public PinFilter PinFilter { get; set; } = PinFilter.All;
    public string Message { get; set; } = string.Empty;
    public bool IsError { get; set; }
    public bool IsLoading { get; set; }
    public int Tick { get; set; }

    /// <summary>When set, a determinate progress bar replaces the spinner (install/upgrade/uninstall).</summary>
    public OpProgress? Op { get; set; }

    private static readonly char [] _spinner = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];

    /// <summary>Render a compact fixed-width progress bar like <c>▕████░░░░░░▏  42%</c>.</summary>
    private static string RenderBar (double fraction)
    {
        const int width = 10;
        fraction = Math.Clamp (fraction, 0, 1);
        int filled = (int)Math.Round (fraction * width);

        return $"▕{new string ('█', filled)}{new string ('░', width - filled)}▏ {fraction * 100,3:0}%";
    }

    /// <inheritdoc />
    protected override bool OnDrawingContent (DrawContext? context)
    {
        SetAttribute (new (Theme.TextPrimary, Theme.Surface));

        for (int x = 0; x < Viewport.Width; x++)
        {
            Move (x, 0);
            AddStr (" ");
        }

        int x0 = 0;

        // Source filter badge
        string srcLabel = AppState.SourceLabel (SourceFilter);
        Attribute srcAttr = SourceFilter switch
        {
            "winget" => new (Theme.TextOnAccent, Theme.Info, TextStyle.Bold),
            "msstore" => new (Theme.TextOnAccent, Theme.Accent, TextStyle.Bold),

            // null = "All", and any custom source, share the neutral badge.
            _ => new (Theme.TextOnAccent, Theme.TextSecondary, TextStyle.Bold)
        };
        SetAttribute (srcAttr);
        Move (x0, 0);
        AddStr (srcLabel);
        x0 += srcLabel.Length + 1;

        // Pin filter badge (skip in Search mode)
        if (Mode != AppMode.Search)
        {
            string pinLabel = AppState.PinLabel (PinFilter);
            Attribute pinAttr = PinFilter switch
            {
                PinFilter.PinnedOnly => new (Theme.TextOnAccent, Theme.Selection, TextStyle.Bold),
                PinFilter.UnpinnedOnly => new (Theme.TextOnAccent, Theme.TextSecondary, TextStyle.Bold),
                _ => new (Theme.TextOnAccent, Theme.AccentDim, TextStyle.Bold)
            };
            SetAttribute (pinAttr);
            Move (x0, 0);
            AddStr (pinLabel);
            x0 += pinLabel.Length + 1;
        }

        // Hotkey hints right-aligned. Reserve at least 8 columns for the status message; if the
        // hints don't fit in the remaining width, drop lowest-priority pairs from the left first.
        // Pairs are separated by a dim "│" glyph for visual grouping.
        string [] hintPairs = ComposeHintPairs ();
        int hintsAvailable = Viewport.Width - x0 - 8;
        (string [] visiblePairs, bool elided) = TruncateHintPairs (hintPairs, hintsAvailable);
        int hintsWidth = HintsWidth (visiblePairs, elided);
        int hintsStart = Math.Max (x0 + 1, Viewport.Width - hintsWidth);

        // Status message between filters and hints. A running operation shows a determinate
        // progress bar with its phase; an indeterminate load shows the braille spinner.
        string msg = Message;

        if (Op is { } op)
        {
            msg = $"{RenderBar (op.Fraction)} {op.Label}  {msg}";
        }
        else if (IsLoading)
        {
            char spin = _spinner [Tick % _spinner.Length];
            msg = $"{spin} {msg}";
        }

        int msgMax = hintsStart - x0 - 1;

        if (msg.Length > msgMax)
        {
            msg = msgMax > 1 ? msg [..(msgMax - 1)] + "…" : string.Empty;
        }

        Attribute msgAttr = IsError
                                ? new Attribute (Theme.Danger, Theme.Surface, TextStyle.Bold)
                                : IsLoading || Op is not null
                                    ? new Attribute (Theme.Accent, Theme.Surface)
                                    : new Attribute (Theme.TextPrimary, Theme.Surface);
        SetAttribute (msgAttr);
        Move (x0, 0);
        AddStr (msg);

        DrawHintPairs (hintsStart, visiblePairs, elided);

        return true;
    }

    private void DrawHintPairs (int xStart, string [] pairs, bool elided)
    {
        Attribute primary = new (Theme.TextPrimary, Theme.Surface);
        Attribute dim = new (Theme.TextSecondary, Theme.Surface);
        int x = xStart;

        if (elided)
        {
            SetAttribute (dim);
            Move (x, 0);
            AddStr ("… ");
            x += 2;
        }

        for (int i = 0; i < pairs.Length; i++)
        {
            SetAttribute (primary);
            Move (x, 0);
            AddStr (pairs [i]);
            x += pairs [i].Length;

            if (i < pairs.Length - 1)
            {
                SetAttribute (dim);
                Move (x, 0);
                AddStr (" │ ");
                x += 3;
            }
        }
    }

    private static int HintsWidth (string [] pairs, bool elided)
    {
        if (pairs.Length == 0)
        {
            return elided ? 1 : 0;
        }

        int width = (elided ? 2 : 0) + pairs.Sum (p => p.Length) + 3 * (pairs.Length - 1);

        return width;
    }

    /// <summary>
    /// Drop hint pairs from the left (lowest priority first; the right end has q Quit / ? Help)
    /// until what's left fits in <paramref name="available"/> columns including the " │ "
    /// separators between pairs and a leading "… " when anything was elided.
    /// </summary>
    private static (string [] Pairs, bool Elided) TruncateHintPairs (string [] pairs, int available)
    {
        if (pairs.Length == 0 || available <= 0)
        {
            return (Array.Empty<string> (), false);
        }

        if (HintsWidth (pairs, false) <= available)
        {
            return (pairs, false);
        }

        for (int start = 1; start < pairs.Length; start++)
        {
            string [] candidate = pairs [start..];

            if (HintsWidth (candidate, true) <= available)
            {
                return (candidate, true);
            }
        }

        return (Array.Empty<string> (), true);
    }

    private string [] ComposeHintPairs ()
    {
        return InputMode switch
        {
            InputMode.Search => ["Esc Cancel", "Enter Search"],
            InputMode.LocalFilter => ["Esc Clear", "Enter Done", "Bksp Del"],
            InputMode.VersionInput => ["Esc Cancel", "Enter Confirm", "Bksp Del"],
            _ => Mode switch
            {
                AppMode.Search => ["/ Search", "f Source", "r Refresh", "e Export", "? Help", "q Quit"],

                // Upgrades adds the multi-select hints. They sit just before Help/Quit so the
                // left-dropping truncation sheds the lower-value pairs (Filter/Source/Pin) first,
                // keeping "Spc Select / U Upgrade sel" — the non-obvious batch flow — visible.
                AppMode.Upgrades => ["/ Filter", "f Source", "p Pin", "P Pins", "r Refresh", "Spc Select", "U Upgrade sel", "? Help", "q Quit"],
                _ => ["/ Filter", "f Source", "p Pin", "P Pins", "r Refresh", "e Export", "? Help", "q Quit"]
            }
        };
    }
}
/// <summary>
/// Modal dialog asking for a specific version string before install.
/// Returns the string via <see cref="Runnable{TResult}.Result"/>; null if cancelled.
/// </summary>
public sealed class VersionInputDialog : Runnable<string?>
{
    private readonly TextField _input;

    public VersionInputDialog (string packageName)
    {
        Title = $" Install specific version of {packageName} ";
        BorderStyle = LineStyle.Rounded;
        Width = 60;
        Height = 8;
        X = Pos.Center ();
        Y = Pos.Center ();
        SchemeName = Theme.SurfaceSchemeName;
        Arrangement = ViewArrangement.Movable;

        Label prompt = new () { X = 1, Y = 1, Text = "Version:" };
        _input = new () { X = Pos.Right (prompt) + 1, Y = 1, Width = Dim.Fill (1) };

        Button confirm = new () { X = Pos.Center () - 8, Y = 4, Text = "_Confirm", IsDefault = true };
        Button cancel = new () { X = Pos.Center () + 2, Y = 4, Text = "Cancel" };

        confirm.Accepting += (_, e) =>
                             {
                                 Result = _input.Text;
                                 RequestStop ();
                                 e.Handled = true;
                             };

        cancel.Accepting += (_, e) =>
                            {
                                Result = null;
                                RequestStop ();
                                e.Handled = true;
                            };

        Add (prompt, _input, confirm, cancel);
        _input.SetFocus ();
    }
}

/// <summary>
/// Modal that lets the user pick a real version from a list (newest first), used when the backend
/// can enumerate available versions (the COM backend). Falls back to <see cref="VersionInputDialog"/>
/// when no versions are available (e.g. the CLI backend). Result is the chosen version, or null on cancel.
/// </summary>
public sealed class VersionPickerDialog : Runnable<string?>
{
    public VersionPickerDialog (string packageName, IReadOnlyList<string> versions)
    {
        Title = $" Select version of {packageName} ";
        BorderStyle = LineStyle.Rounded;
        Width = 60;
        Height = Math.Clamp (versions.Count + 6, 10, 22);
        X = Pos.Center ();
        Y = Pos.Center ();
        SchemeName = Theme.SurfaceSchemeName;
        Arrangement = ViewArrangement.Movable;

        Label prompt = new () { X = 1, Y = 0, Text = "Pick a version (newest first):" };

        ListView list = new ()
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill (1),
            Height = Dim.Fill (2),
            SchemeName = Theme.SurfaceSchemeName
        };
        list.SetSource (new ObservableCollection<string> (versions));
        list.SelectedItem = 0;

        Button install = new () { X = Pos.Center () - 8, Y = Pos.AnchorEnd (1), Text = "_Install", IsDefault = true };
        Button cancel = new () { X = Pos.Center () + 2, Y = Pos.AnchorEnd (1), Text = "Cancel" };

        install.Accepting += (_, e) =>
                             {
                                 int idx = list.SelectedItem ?? -1;
                                 Result = idx >= 0 && idx < versions.Count ? versions [idx] : null;
                                 RequestStop ();
                                 e.Handled = true;
                             };

        cancel.Accepting += (_, e) =>
                            {
                                Result = null;
                                RequestStop ();
                                e.Handled = true;
                            };

        Add (prompt, list, install, cancel);
        list.SetFocus ();
    }
}

/// <summary>
/// Advanced install options panel: install scope, mode, architecture, and custom installer args.
/// Result is the chosen <see cref="InstallSettings"/>, or null on cancel. The COM backend maps
/// these onto InstallOptions; the CLI backend onto winget flags. Each OptionSelector's index lines
/// up with the corresponding enum's member order.
/// </summary>
public sealed class AdvancedInstallDialog : Runnable<InstallSettings?>
{
    public AdvancedInstallDialog (string packageName)
    {
        Title = $" Advanced install: {packageName} ";
        BorderStyle = LineStyle.Rounded;
        Width = 66;
        Height = 15;
        X = Pos.Center ();
        Y = Pos.Center ();
        SchemeName = Theme.SurfaceSchemeName;
        Arrangement = ViewArrangement.Movable;

        Label scopeLabel = new () { X = 1, Y = 1, Text = "Scope: " };
        OptionSelector scope = new ()
        {
            X = Pos.Right (scopeLabel), Y = 1, Width = Dim.Fill (1),
            Labels = ["Default", "User", "Machine"]
        };
        scope.Value = 0;

        Label modeLabel = new () { X = 1, Y = 3, Text = "Mode:  " };
        OptionSelector mode = new ()
        {
            X = Pos.Right (modeLabel), Y = 3, Width = Dim.Fill (1),
            Labels = ["Default", "Silent", "Interactive"]
        };
        mode.Value = 0;

        Label archLabel = new () { X = 1, Y = 5, Text = "Arch:  " };
        OptionSelector arch = new ()
        {
            X = Pos.Right (archLabel), Y = 5, Width = Dim.Fill (1),
            Labels = ["Default", "x64", "x86", "arm64"]
        };
        arch.Value = 0;

        Label argsLabel = new () { X = 1, Y = 7, Text = "Custom installer args (optional):" };
        TextField argsField = new () { X = 1, Y = 8, Width = Dim.Fill (1) };

        Button install = new () { X = Pos.Center () - 8, Y = Pos.AnchorEnd (1), Text = "_Install", IsDefault = true };
        Button cancel = new () { X = Pos.Center () + 2, Y = Pos.AnchorEnd (1), Text = "Cancel" };

        install.Accepting += (_, e) =>
                             {
                                 Result = new InstallSettings
                                 {
                                     Scope = (InstallScopePref)(scope.Value ?? 0),
                                     Mode = (InstallModePref)(mode.Value ?? 0),
                                     Architecture = (InstallArchPref)(arch.Value ?? 0),
                                     CustomArgs = string.IsNullOrWhiteSpace (argsField.Text) ? null : argsField.Text
                                 };
                                 RequestStop ();
                                 e.Handled = true;
                             };

        cancel.Accepting += (_, e) =>
                            {
                                Result = null;
                                RequestStop ();
                                e.Handled = true;
                            };

        Add (scopeLabel, scope, modeLabel, mode, archLabel, arch, argsLabel, argsField, install, cancel);
        scope.SetFocus ();
    }
}

/// <summary>
/// Help overlay shown by pressing <c>?</c>. Mirrors the contents from src/ui.rs::render_help.
/// </summary>
public sealed class HelpDialog : Runnable
{
    public HelpDialog (string backendDescription = "", string? comFallbackReason = null)
    {
        Title = " Help ";
        BorderStyle = LineStyle.Rounded;
        Width = Dim.Percent (60);
        Height = Dim.Percent (70);
        X = Pos.Center ();
        Y = Pos.Center ();
        SchemeName = Theme.SurfaceSchemeName;
        Arrangement = ViewArrangement.Movable;

        // Lead with which backend is live so it's discoverable that the COM build can fall back
        // to the CLI (see WINDOWS-TESTING.md). Empty until resolved → omit the line entirely.
        string header = string.IsNullOrEmpty (backendDescription)
                            ? string.Empty
                            : $"Backend: {backendDescription}\n\n";

        // If a COM backend was requested but activation failed, the app silently fell back to CLI
        // (the stderr note is overdrawn by the TUI). Surface the reason here so it's discoverable.
        if (!string.IsNullOrEmpty (comFallbackReason))
        {
            header += $"COM unavailable: {comFallbackReason} — using CLI.\n\n";
        }

        Code content = new ()
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill (1),
            Height = Dim.Fill (1),
            Text = header + HelpText
        };
        Add (content);

        Button close = new ()
        {
            X = Pos.Center (),
            Y = Pos.AnchorEnd (1),
            Text = "_Close",
            IsDefault = true
        };
        close.Accepting += (_, _) => RequestStop ();
        Add (close);

        KeyDown += (_, key) =>
                   {
                       if (key.KeyCode is KeyCode.Esc or KeyCode.Q)
                       {
                           RequestStop ();
                           key.Handled = true;
                       }

                       if (key.AsRune.Value == '?')
                       {
                           RequestStop ();
                           key.Handled = true;
                       }
                   };
    }

    private const string HelpText =
        """
        Navigation
          up / k        Move up
          dn / j        Move down
          PgUp / PgDn   Page navigation
          Home / End    Jump to start / end
          lt / rt       Switch tabs (Search/Installed/Upgrades)
          Tab / S-Tab   Toggle focus between list and detail
          /  or  s      Search (Search tab) / local filter
          f             Cycle source filter
          r             Refresh

        Actions
          i             Install
          I             Install specific version (pick from list)
          A             Advanced install (scope / mode / arch / args)
          d             Download installer only (no install)
          u             Upgrade
          x             Uninstall
          V             Verify install (check files / registration)
          R             Repair install (re-run the installer's repair)
          p             Pin / Unpin
          Space         Toggle batch select (Upgrades only)
          a             Select / deselect all (Upgrades only)
          U             Batch upgrade selected
          e             Export visible list to CSV
          P             Cycle pin filter
          S             Cycle sort columns (Name / Id / Version, ↑/↓)
          o             Open package homepage
          c             Open changelog / release notes

        Mouse
          Click tab to switch view
          Click row to select
          Scroll wheel to navigate

        General
          ?             Toggle this help
          Esc           Cancel a running operation, else quit
          q / Ctrl+C    Quit
        """;
}
