namespace WingetTuiSharp;

/// <summary>
/// Right-side package detail panel. Renders the package's metadata with inline styles —
/// label/value pairs in accent vs primary, URLs in info-blue with underline, and the
/// action shortcuts with the key in accent. Implemented via direct drawing because
/// Terminal.Gui v2 doesn't yet expose a rich-text Span/Line primitive (see feature-gaps.md).
/// </summary>
public sealed class DetailPanel : FrameView
{
    private readonly List<List<Span>> _lines = [];
    private readonly List<List<Span>> _wrappedLines = [];
    private readonly List<MarkdownRow> _markdownRows = [];
    private int _wrappedWidth = -1;
    private string? _emptyMessage = "Select a package to view details";

    public DetailPanel ()
    {
        Title = " Package Details ";
        BorderStyle = LineStyle.Rounded;
        SchemeName = Theme.FrameUnfocusedSchemeName;
        CanFocus = true;
        ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        VerticalScrollBar.VisibilityMode = ScrollBarVisibilityMode.Auto;
        VerticalScrollBar.Increment = 3;
    }

    public AppMode Mode { get; set; } = AppMode.Installed;
    public event EventHandler<string>? LinkActivated;

    public void SetDetail (PackageDetail? detail, bool loading)
    {
        ClearMarkdownRows ();
        _lines.Clear ();
        _wrappedLines.Clear ();
        _wrappedWidth = -1;
        ScrollToStart ();

        if (loading)
        {
            Title = " Package Details (loading…) ";
            _emptyMessage = "Loading…";
            SetContentHeight (1);
            SetNeedsDraw ();

            return;
        }

        Title = " Package Details ";

        if (detail is null)
        {
            _emptyMessage = "No package selected";
            SetContentHeight (1);
            SetNeedsDraw ();

            return;
        }

        _emptyMessage = null;

        AddKv ("Name", detail.Name);
        AddKv ("Id", detail.Id, ValueScheme.Info);
        AddKv ("Version", detail.Version);

        if (!string.IsNullOrEmpty (detail.AvailableVersion))
        {
            AddKv ("Available", detail.AvailableVersion, ValueScheme.Success);
        }

        // On the Search tab a row can be one that the user already has installed — make that obvious
        // (the Installed/Upgrades tabs are installed by definition, so the badge is search-only).
        if (Mode == AppMode.Search && !string.IsNullOrEmpty (detail.InstalledVersion))
        {
            AddSingle ($"✓ Installed ({detail.InstalledVersion})", Theme.Success, TextStyle.Bold);
        }

        if (!string.IsNullOrEmpty (detail.Publisher))
        {
            AddKv ("Publisher", detail.Publisher);
        }

        if (!string.IsNullOrEmpty (detail.Author))
        {
            AddKv ("Author", detail.Author);
        }

        // Skip the Source line when both the show output and the list-row context lacked it
        // (rare — typically only ARP/MSIX-only packages with no manifest source).
        if (!string.IsNullOrEmpty (detail.Source))
        {
            AddKv ("Source", detail.Source, detail.Source switch
            {
                "winget" => ValueScheme.Info,
                "msstore" => ValueScheme.Accent,
                _ => ValueScheme.Primary
            });
        }

        // Search-only: explain a non-obvious match (e.g. the package matched on a tag, not its
        // name) so the user understands why it surfaced. Dim so it reads as a footnote.
        if (Mode == AppMode.Search && !string.IsNullOrEmpty (detail.MatchField))
        {
            AddSingle ($"↳ matched on {detail.MatchField}", Theme.TextSecondary, TextStyle.Italic);
        }

        if (!string.IsNullOrEmpty (detail.InstalledScope))
        {
            AddKv ("Scope", detail.InstalledScope);
        }

        if (!string.IsNullOrEmpty (detail.InstalledLocation))
        {
            AddKv ("Installed to", detail.InstalledLocation);
        }

        if (!string.IsNullOrEmpty (detail.License))
        {
            AddKv ("License", detail.License);
        }

        if (!string.IsNullOrEmpty (detail.Copyright))
        {
            AddKv ("Copyright", detail.Copyright);
        }

        if (detail.Tags is { Count: > 0 } tags)
        {
            AddKv ("Tags", string.Join (", ", tags));
        }

        if (detail.ProductCodes is { Count: > 0 } productCodes)
        {
            AddKv ("Product code", string.Join (", ", productCodes));
        }

        if (detail.PackageFamilyNames is { Count: > 0 } familyNames)
        {
            AddKv ("Family name", string.Join (", ", familyNames));
        }

        if (detail.PinState.IsPinned)
        {
            AddSingle ($"\U0001F4CC {detail.PinState.DisplayLabel ()}", Theme.Selection);
        }

        if (!string.IsNullOrEmpty (detail.Homepage))
        {
            AddBlank ();
            AddMarkdownLinkRow ("Homepage", detail.Homepage);
        }

        if (!string.IsNullOrEmpty (detail.ReleaseNotesUrl))
        {
            AddMarkdownLinkRow ("Release notes", detail.ReleaseNotesUrl);
        }

        if (!string.IsNullOrEmpty (detail.SupportUrl))
        {
            AddMarkdownLinkRow ("Support", detail.SupportUrl);
        }

        if (!string.IsNullOrEmpty (detail.PrivacyUrl))
        {
            AddMarkdownLinkRow ("Privacy", detail.PrivacyUrl);
        }

        if (!string.IsNullOrEmpty (detail.PurchaseUrl))
        {
            AddMarkdownLinkRow ("Purchase", detail.PurchaseUrl);
        }

        if (detail.Documentation is { Count: > 0 } docs)
        {
            foreach (DocLink doc in docs)
            {
                AddMarkdownLinkRow (doc.Label, doc.Url);
            }
        }

        if (!string.IsNullOrEmpty (detail.Description))
        {
            AddBlank ();
            AddSingle ("Description:", Theme.Accent, TextStyle.Bold);

            if (detail.IsDescriptionDegraded)
            {
                // Visually distinguish synthesized "no manifest available" notes from real
                // package descriptions: leading ⚠ glyph in accent + body in secondary text.
                AddDegradedParagraph (detail.Description);
            }
            else
            {
                AddParagraph (detail.Description);
            }
        }

        if (!string.IsNullOrEmpty (detail.InstallationNotes))
        {
            AddBlank ();
            AddSingle ("Installation notes:", Theme.Accent, TextStyle.Bold);
            AddParagraph (detail.InstallationNotes);
        }

        AddBlank ();
        AddSingle ("Actions:", Theme.Accent, TextStyle.Bold);

        switch (Mode)
        {
            case AppMode.Search:
                if (!string.IsNullOrEmpty (detail.InstalledVersion))
                {
                    // Already installed: offer manage actions instead of a bare Install. Show
                    // Upgrade only when the catalog's latest differs from what's installed.
                    if (!string.IsNullOrEmpty (detail.AvailableVersion)
                        && !string.Equals (detail.AvailableVersion, detail.InstalledVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        AddAction ("u", "Upgrade");
                    }

                    AddAction ("x", "Uninstall");
                    AddAction ("I", "Install specific version");
                }
                else
                {
                    AddAction ("i", "Install");
                    AddAction ("I", "Install specific version");
                }

                break;
            case AppMode.Installed:
                if (!string.IsNullOrEmpty (detail.AvailableVersion))
                {
                    AddAction ("u", "Upgrade");
                }

                AddAction ("x", "Uninstall");
                AddAction ("p", "Pin/Unpin");
                AddAction ("V", "Verify install");
                AddAction ("R", "Repair install");

                break;
            case AppMode.Upgrades:
                AddAction ("u", "Upgrade");
                AddAction ("x", "Uninstall");
                AddAction ("V", "Verify install");
                AddAction ("R", "Repair install");
                AddAction ("Spc", "Select");
                AddAction ("a", "Toggle All");
                AddAction ("U", "Upgrade selected");

                break;
        }

        if (!string.IsNullOrEmpty (detail.Homepage))
        {
            AddAction ("o", "Open homepage");
        }

        if (!string.IsNullOrEmpty (detail.ReleaseNotesUrl))
        {
            AddAction ("c", "Open changelog");
        }

        UpdateWrappedLines ();
        SetNeedsDraw ();
    }

    /// <inheritdoc />
    protected override bool OnDrawingContent (DrawContext? context)
    {
        UpdateWrappedLines ();

        int height = Viewport.Height;
        int width = Math.Max (1, Viewport.Width - 2);

        if (_emptyMessage is { })
        {
            SetAttribute (new (Theme.TextSecondary, Theme.Surface));
            Move (1, 0);
            AddStr (_emptyMessage);

            return true;
        }

        int start = Math.Clamp (Viewport.Y, 0, Math.Max (0, _wrappedLines.Count - 1));
        int end = Math.Min (start + height, _wrappedLines.Count);

        for (int index = start; index < end; index++)
        {
            DrawWrappedLine (_wrappedLines [index], 1, index - start, width);
        }

        return true;
    }

    /// <inheritdoc />
    protected override bool OnKeyDown (Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.CursorDown:
                ScrollBy (1);
                key.Handled = true;

                return true;
            case KeyCode.CursorUp:
                ScrollBy (-1);
                key.Handled = true;

                return true;
            case KeyCode.PageDown:
                ScrollPage (1);
                key.Handled = true;

                return true;
            case KeyCode.PageUp:
                ScrollPage (-1);
                key.Handled = true;

                return true;
            case KeyCode.Home:
                ScrollToStart ();
                key.Handled = true;

                return true;
            case KeyCode.End:
                ScrollToEnd ();
                key.Handled = true;

                return true;
        }

        if (key.AsRune.Value is var rune and > 0)
        {
            char c = (char)rune;

            switch (c)
            {
                case 'j':
                    ScrollBy (1);
                    key.Handled = true;

                    return true;
                case 'k':
                    ScrollBy (-1);
                    key.Handled = true;

                    return true;
            }
        }

        return base.OnKeyDown (key);
    }

    /// <inheritdoc />
    protected override bool OnMouseEvent (Mouse mouse)
    {
        if (mouse.Flags.HasFlag (MouseFlags.WheeledDown))
        {
            SetFocus ();
            ScrollBy (3);
            mouse.Handled = true;

            return true;
        }

        if (mouse.Flags.HasFlag (MouseFlags.WheeledUp))
        {
            SetFocus ();
            ScrollBy (-3);
            mouse.Handled = true;

            return true;
        }

        if (mouse.IsSingleClicked == true)
        {
            SetFocus ();
        }

        return base.OnMouseEvent (mouse);
    }

    public void ScrollBy (int delta)
    {
        UpdateWrappedLines ();

        if (delta != 0 && ScrollVertical (delta) == true)
        {
            SetNeedsDraw ();
        }
    }

    public void ScrollPage (int pages)
    {
        int pageSize = Math.Max (1, Viewport.Height);
        ScrollBy (pages * pageSize);
    }

    public void ScrollToStart ()
    {
        if (Viewport.Y == 0)
        {
            return;
        }

        Viewport = new (Viewport.X, 0, Viewport.Width, Viewport.Height);
        SetNeedsDraw ();
    }

    public void ScrollToEnd ()
    {
        UpdateWrappedLines ();

        int maxScroll = Math.Max (0, GetContentHeight () - Viewport.Height);

        if (Viewport.Y == maxScroll)
        {
            return;
        }

        Viewport = new (Viewport.X, maxScroll, Viewport.Width, Viewport.Height);
        SetNeedsDraw ();
    }

    private void UpdateWrappedLines ()
    {
        int maxWidth = Math.Max (1, Viewport.Width - 2);

        if (_wrappedWidth == maxWidth)
        {
            return;
        }

        _wrappedLines.Clear ();

        if (_emptyMessage is not null)
        {
            foreach (MarkdownRow markdownRow in _markdownRows)
            {
                markdownRow.View.Visible = false;
            }

            _wrappedLines.Add ([]);
            SetContentHeight (1);
            _wrappedWidth = maxWidth;

            return;
        }

        for (int sourceLineIndex = 0; sourceLineIndex < _lines.Count; sourceLineIndex++)
        {
            List<List<Span>> wrapped = WrapLine (_lines [sourceLineIndex], maxWidth);
            int startLine = _wrappedLines.Count;
            _wrappedLines.AddRange (wrapped);

            MarkdownRow? markdownRow = _markdownRows.FirstOrDefault (row => row.SourceLineIndex == sourceLineIndex);

            if (markdownRow is not null)
            {
                LayoutMarkdownRow (markdownRow, startLine, wrapped.Count, maxWidth);
            }
        }

        if (_wrappedLines.Count == 0)
        {
            _wrappedLines.Add ([]);
        }

        SetContentHeight (_wrappedLines.Count);
        _wrappedWidth = maxWidth;
    }

    private void ClearMarkdownRows ()
    {
        Exception? firstFailure = null;

        while (_markdownRows.Count > 0)
        {
            int index = _markdownRows.Count - 1;
            MarkdownRow markdownRow = _markdownRows [index];
            _markdownRows.RemoveAt (index);

            try
            {
                RemoveAndDisposeDynamicView (markdownRow.View, () => Remove (markdownRow.View));
            }
            catch (Exception ex)
            {
                // Continue releasing every detached dynamic child, then preserve the first error.
                firstFailure ??= ex;
            }
        }

        if (firstFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture (firstFailure).Throw ();
        }
    }

    internal static void RemoveAndDisposeDynamicView (IDisposable view, Action remove)
    {
        try
        {
            remove ();
        }
        finally
        {
            view.Dispose ();
        }
    }

    private void LayoutMarkdownRow (MarkdownRow markdownRow, int startLine, int lineCount, int width)
    {
        markdownRow.View.X = 1;
        markdownRow.View.Y = startLine;
        markdownRow.View.Width = width;
        markdownRow.View.Height = Math.Max (1, lineCount);
        markdownRow.View.Visible = true;
        markdownRow.View.SetNeedsLayout ();
        markdownRow.View.SetNeedsDraw ();
    }

    private static List<List<Span>> WrapLine (IReadOnlyList<Span> spans, int maxWidth)
    {
        List<List<Span>> wrapped = [];
        List<Span> current = [];
        int currentWidth = 0;

        foreach (Span span in spans)
        {
            if (string.IsNullOrEmpty (span.Text))
            {
                continue;
            }

            string [] words = span.Text.Split (' ');

            for (int i = 0; i < words.Length; i++)
            {
                string word = words [i];
                string token = i == 0 ? word : " " + word;

                if (string.IsNullOrEmpty (token))
                {
                    continue;
                }

                int tokenWidth = token.GetColumns ();

                if (currentWidth + tokenWidth > maxWidth && currentWidth > 0)
                {
                    wrapped.Add (current);
                    current = [];
                    currentWidth = 0;

                    if (token.StartsWith (' '))
                    {
                        token = token.TrimStart ();
                        tokenWidth = token.GetColumns ();
                    }
                }

                if (string.IsNullOrEmpty (token))
                {
                    continue;
                }

                current.Add (new (token, span.Attr));
                currentWidth += tokenWidth;
            }
        }

        wrapped.Add (current);

        return wrapped;
    }

    private void DrawWrappedLine (IReadOnlyList<Span> spans, int x0, int y, int maxWidth)
    {
        int x = x0;

        foreach (Span span in spans)
        {
            if (string.IsNullOrEmpty (span.Text))
            {
                continue;
            }

            string text = span.Text;
            int width = text.GetColumns ();

            if (x + width > x0 + maxWidth)
            {
                text = TrimToWidth (text, Math.Max (0, x0 + maxWidth - x));
                width = text.GetColumns ();
            }

            if (string.IsNullOrEmpty (text))
            {
                continue;
            }

            SetAttribute (span.Attr);
            Move (x, y);
            AddStr (text);
            x += width;
        }
    }

    private static string TrimToWidth (string text, int maxWidth)
    {
        if (maxWidth <= 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new ();
        int width = 0;

        foreach (Rune rune in text.EnumerateRunes ())
        {
            int runeWidth = rune.GetColumns ();

            if (width + runeWidth > maxWidth)
            {
                break;
            }

            builder.Append (rune.ToString ());
            width += runeWidth;
        }

        return builder.ToString ();
    }

    private enum ValueScheme
    {
        Primary,
        Info,
        Success,
        Accent,
        Link
    }

    private void AddKv (string label, string value, ValueScheme scheme = ValueScheme.Primary)
    {
        _lines.Add ([
            new Span ($"{label}: ", new (Theme.Accent, Theme.Surface, TextStyle.Bold)),
            new Span (value, scheme switch
            {
                ValueScheme.Info => new (Theme.Info, Theme.Surface),
                ValueScheme.Success => new (Theme.Success, Theme.Surface),
                ValueScheme.Accent => new (Theme.Accent, Theme.Surface),
                ValueScheme.Link => new (Theme.Info, Theme.Surface, TextStyle.Underline),
                _ => new (Theme.TextPrimary, Theme.Surface)
            })
        ]);
    }

    private void AddMarkdownLinkRow (string label, string url)
    {
        string visibleText = $"{label}: {url}";
        AddSingle (visibleText, Theme.Info, TextStyle.Underline);

        Markdown markdown = new ()
        {
            CanFocus = false,
            X = 1,
            Y = 0,
            Width = 1,
            Height = 1,
            Text = $"**{label}:** [{url}]({url})",
            UseThemeBackground = false
        };
        markdown.LinkClicked += (_, e) =>
                                {
                                    e.Handled = true;
                                    LinkActivated?.Invoke (this, e.Url);
                                };

        Add (markdown);
        _markdownRows.Add (new (markdown, _lines.Count - 1));
    }

    private void AddSingle (string text, Color fg, TextStyle style = TextStyle.None) =>
        _lines.Add ([new Span (text, new (fg, Theme.Surface, style))]);

    private void AddBlank () => _lines.Add ([]);

    private void AddParagraph (string text)
    {
        foreach (string paragraph in text.Split ('\n'))
        {
            _lines.Add ([new Span (paragraph, new (Theme.TextPrimary, Theme.Surface))]);
        }
    }

    private void AddDegradedParagraph (string text)
    {
        bool first = true;

        foreach (string paragraph in text.Split ('\n'))
        {
            if (first)
            {
                _lines.Add ([
                    new Span ("⚠ ", new (Theme.Accent, Theme.Surface)),
                    new Span (paragraph, new (Theme.TextSecondary, Theme.Surface, TextStyle.Italic))
                ]);
                first = false;
            }
            else
            {
                _lines.Add ([new Span (paragraph, new (Theme.TextSecondary, Theme.Surface, TextStyle.Italic))]);
            }
        }
    }

    private void AddAction (string key, string description) =>
        _lines.Add ([
            new Span (" ", new (Theme.TextPrimary, Theme.Surface)),

            // Key rendered as a colored chip: text-on-accent foreground on accent background.
            // Mirrors the [i] Install style in upstream src/ui.rs.
            new Span ($" {key} ", new (Theme.TextOnAccent, Theme.Accent, TextStyle.Bold)),
            new Span ($"  {description}", new (Theme.TextPrimary, Theme.Surface))
        ]);

    private sealed record Span (string Text, Attribute Attr);
    private sealed record MarkdownRow (Markdown View, int SourceLineIndex);
}
