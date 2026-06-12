namespace WingetTuiSharp.Tests;

public class AppBehaviorTests
{
    [Fact]
    public void DetailPanel_SetDetail_WithLongContentEnablesVerticalScrolling ()
    {
        DetailPanel panel = CreateDetailPanel ();

        panel.SetDetail (CreateLongDetail (), loading: false);

        Assert.True (panel.ViewportSettings.HasFlag (ViewportSettingsFlags.HasVerticalScrollBar));
        Assert.True (panel.GetContentHeight () > panel.Viewport.Height);
        Assert.True (panel.VerticalScrollBar.Visible);
    }

    [Fact]
    public void DetailPanel_OnMouseWheel_ScrollsViewport ()
    {
        DetailPanel panel = CreateDetailPanel ();
        panel.SetDetail (CreateLongDetail (), loading: false);

        InvokeMouse (panel, MouseFlags.WheeledDown);
        int afterWheelDown = panel.Viewport.Y;

        InvokeMouse (panel, MouseFlags.WheeledUp);

        Assert.True (afterWheelDown > 0);
        Assert.Equal (0, panel.Viewport.Y);
    }

    [Fact]
    public void DetailPanel_OnKeyDown_ScrollsAndCanReturnHome ()
    {
        DetailPanel panel = CreateDetailPanel ();
        panel.SetDetail (CreateLongDetail (), loading: false);

        InvokeKeyDown (panel, KeyCode.End);
        int afterEnd = panel.Viewport.Y;

        InvokeKeyDown (panel, KeyCode.Home);

        Assert.True (afterEnd > 0);
        Assert.Equal (0, panel.Viewport.Y);
    }

    [Fact]
    public void DetailPanel_SetDetail_ResetsScrollPositionForNewSelection ()
    {
        DetailPanel panel = CreateDetailPanel ();
        panel.SetDetail (CreateLongDetail (), loading: false);
        InvokeKeyDown (panel, KeyCode.End);

        panel.SetDetail (CreateLongDetail ("Second package"), loading: false);

        Assert.Equal (0, panel.Viewport.Y);
    }

    [Fact]
    public void DetailPanel_SetDetail_CreatesNonFocusableMarkdownViewsForLinkRows ()
    {
        DetailPanel panel = CreateDetailPanel ();

        panel.SetDetail (CreateLongDetail (), loading: false);

        Markdown[] links = panel.SubViews.OfType<Markdown> ().ToArray ();

        Assert.Equal (2, links.Length);
        Assert.All (links, link => Assert.False (link.CanFocus));
        Assert.Contains (links, link => link.Text.Contains ("[https://example.invalid/home](https://example.invalid/home)", StringComparison.Ordinal));
        Assert.Contains (links, link => link.Text.Contains ("[https://example.invalid/releases](https://example.invalid/releases)", StringComparison.Ordinal));
    }

    [Fact]
    public void App_WideWindow_PlacesTabsToTheRightOfTheLogo ()
    {
        App app = new (new MockBackend ())
        {
            Frame = new (0, 0, 120, 40)
        };
        LayoutView (app, new (120, 40));

        Logo logo = GetPrivateField<Logo> (app, "_logo");
        TabBar tabBar = GetPrivateField<TabBar> (app, "_tabBar");
        FrameView listFrame = GetPrivateField<FrameView> (app, "_listFrame");

        Assert.Equal (2, tabBar.Frame.Y);
        Assert.True (tabBar.Frame.X >= logo.Frame.X + logo.Frame.Width);
        // One row of breathing room below the wordmark before the list begins.
        Assert.Equal (Logo.LogoHeight + 1, listFrame.Frame.Y);
    }

    [Fact]
    public void AppState_ApplyFilter_SortsVersionsNumericallyAscending ()
    {
        AppState state = new (new MockBackend ())
        {
            Packages =
            [
                new () { Id = "pkg.one", Name = "One", Version = "2.0.0", Source = "winget" },
                new () { Id = "pkg.two", Name = "Two", Version = "10.0.0", Source = "winget" },
                new () { Id = "pkg.three", Name = "Three", Version = "1.9.0", Source = "winget" }
            ],
            SortField = SortField.Version,
            SortDir = SortDir.Asc
        };

        state.ApplyFilter ();

        Assert.Equal (["1.9.0", "2.0.0", "10.0.0"], state.Filtered.Select (p => p.Version));
    }

    [Fact]
    public void AppState_ApplyFilter_SortsVersionsNumericallyDescending ()
    {
        AppState state = new (new MockBackend ())
        {
            Packages =
            [
                new () { Id = "pkg.one", Name = "One", Version = "2.0.0", Source = "winget" },
                new () { Id = "pkg.two", Name = "Two", Version = "10.0.0", Source = "winget" },
                new () { Id = "pkg.three", Name = "Three", Version = "1.9.0", Source = "winget" }
            ],
            SortField = SortField.Version,
            SortDir = SortDir.Desc
        };

        state.ApplyFilter ();

        Assert.Equal (["10.0.0", "2.0.0", "1.9.0"], state.Filtered.Select (p => p.Version));
    }

    [Theory]
    [InlineData ("=2+5", "'=2+5")]
    [InlineData (" -hidden formula", "' -hidden formula")]
    [InlineData ("@cmd", "'@cmd")]
    [InlineData ("plain text", "plain text")]
    public void EscapeCsvCell_PrefixesSpreadsheetFormulaVectors (string input, string expected)
    {
        Assert.Equal (expected, App.EscapeCsvCell (input));
    }

    [Theory]
    [InlineData ("https://example.invalid/foo", true)]
    [InlineData ("http://example.invalid/foo", true)]
    [InlineData ("file:///etc/passwd", false)]
    [InlineData ("javascript:alert(1)", false)]
    [InlineData ("not a url", false)]
    public void TryNormalizeOpenableUrl_AllowsOnlyHttpSchemes (string input, bool expected)
    {
        bool result = App.TryNormalizeOpenableUrl (input, out string normalized);

        Assert.Equal (expected, result);

        if (expected)
        {
            Assert.NotEmpty (normalized);
        }
        else
        {
            Assert.Equal (string.Empty, normalized);
        }
    }

    [Fact]
    public async Task MockBackend_Repair_ReportsRepairingProgressAndSucceeds ()
    {
        CollectingProgress progress = new ();

        OpResult result = await new MockBackend ().RepairAsync ("Git.Git", progress, CancellationToken.None);

        Assert.True (result.Success);
        Assert.Equal (OperationKind.Repair, result.Operation.Kind);
        Assert.Contains (progress.Samples, s => s.Phase == OpPhase.Repairing);
        Assert.Contains (progress.Samples, s => s.Phase == OpPhase.Done);
    }

    [Fact]
    public void CanRepair_IsTrueForMock_AndFalseForCli ()
    {
        // Repair is COM-only: the mock fakes it for dev iteration; the CLI reports it unavailable.
        Assert.True (new MockBackend ().CanRepair);
        Assert.False (new CliBackend ().CanRepair);
    }

    private sealed class CollectingProgress : IProgress<OpProgress>
    {
        public List<OpProgress> Samples { get; } = [];

        public void Report (OpProgress value) => Samples.Add (value);
    }

    private static DetailPanel CreateDetailPanel ()
    {
        DetailPanel panel = new ()
        {
            Frame = new (0, 0, 28, 8),
            Viewport = new (0, 0, 26, 6)
        };

        return panel;
    }

    private static PackageDetail CreateLongDetail (string name = "Long package")
        => new ()
        {
            Id = $"pkg.{name.Replace (" ", string.Empty, StringComparison.OrdinalIgnoreCase)}",
            Name = name,
            Version = "1.0.0",
            Source = "winget",
            Description = string.Join (
                ' ',
                Enumerable.Range (1, 80).Select (i => $"detail-line-{i:00} wraps through the panel to force scrolling")),
            Homepage = "https://example.invalid/home",
            ReleaseNotesUrl = "https://example.invalid/releases"
        };

    private static void InvokeMouse (DetailPanel panel, MouseFlags flags)
    {
        Mouse mouse = new ()
        {
            Position = new (1, 1),
            Flags = flags
        };

        System.Reflection.MethodInfo onMouse = typeof (DetailPanel).GetMethod (
            "OnMouseEvent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        Assert.NotNull (onMouse);
        _ = onMouse.Invoke (panel, [mouse]);
    }

    private static void InvokeKeyDown (DetailPanel panel, KeyCode keyCode)
    {
        Key key = new (keyCode);

        System.Reflection.MethodInfo onKeyDown = typeof (DetailPanel).GetMethod (
            "OnKeyDown",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        Assert.NotNull (onKeyDown);
        _ = onKeyDown.Invoke (panel, [key]);
    }

    private static T GetPrivateField<T> (object instance, string fieldName) where T : class
    {
        System.Reflection.FieldInfo field = instance.GetType ().GetField (
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        return Assert.IsType<T> (field.GetValue (instance));
    }

    private static void LayoutView (View view, System.Drawing.Size size)
    {
        view.SetRelativeLayout (size);

        System.Reflection.MethodInfo layoutSubViews = typeof (View).GetMethod (
            "LayoutSubViews",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        Assert.NotNull (layoutSubViews);
        _ = layoutSubViews.Invoke (view, []);
    }
}
