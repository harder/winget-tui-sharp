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
    public void App_WideWindow_UsesSkillViewStyleCompactHeader ()
    {
        App app = new (new MockBackend ())
        {
            Frame = new (0, 0, 120, 40)
        };
        LayoutView (app, new (120, 40));

        TabBar tabBar = GetPrivateField<TabBar> (app, "_tabBar");
        Label context = GetPrivateField<Label> (app, "_contextLabel");
        FrameView listFrame = GetPrivateField<FrameView> (app, "_listFrame");

        Assert.Equal ("WinGet TUI — winget-tui", app.Title);
        Assert.Equal (0, tabBar.Frame.Y);
        Assert.Equal (app.Viewport.Width, tabBar.Frame.Width);
        Assert.Equal (1, context.Frame.Y);
        Assert.Equal ("Installed packages", context.Text);
        Assert.Equal (2, listFrame.Frame.Y);
    }

    [Fact]
    public void App_SmallWindow_ShowsResizeGuard ()
    {
        App app = new (new MockBackend ()) { Frame = new (0, 0, 70, 20) };
        LayoutView (app, new (70, 20));

        TerminalSizeGuardView guard = GetPrivateField<TerminalSizeGuardView> (app, "_sizeGuard");

        Assert.True (guard.Visible);
        Assert.Contains ("70×20", TerminalSizeGuardView.BuildMessage (70, 20));
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
    [InlineData (SortDir.Asc, "2.0", "3.0", "10.0")]
    [InlineData (SortDir.Desc, "10.0", "3.0", "2.0")]
    public void AppState_ApplyFilter_SortsAvailableVersionsInUpgrades (
        SortDir direction, string first, string second, string third)
    {
        AppState state = new (new MockBackend ())
        {
            Mode = AppMode.Upgrades,
            Packages =
            [
                new () { Id = "a", Name = "A", Version = "1.0", AvailableVersion = "3.0" },
                new () { Id = "b", Name = "B", Version = "1.0", AvailableVersion = "10.0" },
                new () { Id = "c", Name = "C", Version = "1.0", AvailableVersion = "2.0" }
            ],
            SortField = SortField.AvailableVersion,
            SortDir = direction
        };

        state.ApplyFilter ();

        Assert.Equal ([first, second, third], state.Filtered.Select (p => p.AvailableVersion));
    }

    [Theory]
    [InlineData (AppMode.Installed, SortField.None)]
    [InlineData (AppMode.Upgrades, SortField.AvailableVersion)]
    public void AppState_CycleSort_IncludesAvailableOnlyForUpgrades (AppMode mode, SortField expected)
    {
        AppState state = new (new MockBackend ())
        {
            Mode = mode,
            SortField = SortField.Version,
            SortDir = SortDir.Desc
        };

        state.CycleSort ();

        Assert.Equal (expected, state.SortField);
        Assert.Equal (SortDir.Asc, state.SortDir);
    }

    [Fact]
    public void App_LocalFilter_KeepsSelectedPackageWhenCleared ()
    {
        App app = new (new MockBackend ());
        AppState state = GetPrivateField<AppState> (app, "_state");
        TextField input = GetPrivateField<TextField> (app, "_filterInput");
        TableView table = (TableView)typeof (App).GetField (
            "_packageTable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue (app)!;

        state.Packages =
        [
            new () { Id = "one", Name = "One", Version = "1" },
            new () { Id = "two", Name = "Two", Version = "1" },
            new () { Id = "three", Name = "Three", Version = "1" }
        ];
        state.ApplyFilter ();
        InvokePrivate (app, "RefreshTable");
        table.Value = new (new (0, 2));
        state.InputMode = InputMode.LocalFilter;

        input.Text = "Three";
        Assert.Equal ("three", state.SelectedPackage (table.Value!.SelectedCell.Y)?.Id);

        input.Text = string.Empty;
        Assert.Equal ("three", state.SelectedPackage (table.Value!.SelectedCell.Y)?.Id);
    }

    [Fact]
    public void App_LocalFilter_KeepsSelectedSourceWhenIdsMatch ()
    {
        App app = new (new MockBackend ());
        AppState state = GetPrivateField<AppState> (app, "_state");
        TextField input = GetPrivateField<TextField> (app, "_filterInput");
        TableView table = (TableView)typeof (App).GetField (
            "_packageTable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue (app)!;

        state.Packages =
        [
            new () { Id = "shared.app", Name = "Desktop App", Version = "1", Source = "winget" },
            new () { Id = "shared.app", Name = "Store App", Version = "2", Source = "msstore" }
        ];
        state.ApplyFilter ();
        InvokePrivate (app, "RefreshTable");
        table.Value = new (new (0, 1));
        state.InputMode = InputMode.LocalFilter;

        input.Text = "Store";
        Assert.Equal ("msstore", state.SelectedPackage (table.Value!.SelectedCell.Y)?.Source);

        input.Text = string.Empty;
        Assert.Equal ("msstore", state.SelectedPackage (table.Value!.SelectedCell.Y)?.Source);
    }

    [Fact]
    public void App_LocalFilter_KeepsSelectedPackageWhenIdsDifferOnlyByCase ()
    {
        App app = new (new MockBackend ());
        AppState state = GetPrivateField<AppState> (app, "_state");
        TextField input = GetPrivateField<TextField> (app, "_filterInput");
        TableView table = (TableView)typeof (App).GetField (
            "_packageTable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue (app)!;

        state.Packages =
        [
            new () { Id = "Example.App", Name = "Upper", Version = "1", Source = "winget" },
            new () { Id = "example.app", Name = "Lower", Version = "1", Source = "winget" }
        ];
        state.ApplyFilter ();
        InvokePrivate (app, "RefreshTable");
        table.Value = new (new (0, 1));
        state.InputMode = InputMode.LocalFilter;

        input.Text = "Lower";
        Assert.Equal ("example.app", state.SelectedPackage (table.Value!.SelectedCell.Y)?.Id);

        input.Text = string.Empty;
        Assert.Equal ("example.app", state.SelectedPackage (table.Value!.SelectedCell.Y)?.Id);
    }

    [Fact]
    public void App_SortedAvailableColumn_KeepsItsReservedWidth ()
    {
        App app = new (new MockBackend ()) { Frame = new (0, 0, 140, 40) };
        AppState state = GetPrivateField<AppState> (app, "_state");
        TableView table = (TableView)typeof (App).GetField (
            "_packageTable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue (app)!;
        LayoutView (app, new (140, 40));
        state.Mode = AppMode.Upgrades;
        state.Packages = [new () { Id = "test.app", Name = "Test App", Version = "1", AvailableVersion = "2" }];
        state.ApplyFilter ();
        InvokePrivate (app, "RefreshTable");
        int availableColumn = Array.FindIndex (table.Table!.ColumnNames,
            name => name.StartsWith ("Available", StringComparison.Ordinal));
        int idColumn = Array.FindIndex (table.Table.ColumnNames,
            name => name.StartsWith ("Id", StringComparison.Ordinal));
        int idWidth = table.Style.GetOrCreateColumnStyle (idColumn).MinWidth;

        state.SortField = SortField.AvailableVersion;
        state.ApplyFilter ();
        InvokePrivate (app, "RefreshTable");

        Assert.Equal ("Available ↑", table.Table!.ColumnNames [availableColumn]);
        Assert.Equal (14, table.Style.GetOrCreateColumnStyle (availableColumn).MinWidth);
        Assert.Equal (14, table.Style.GetOrCreateColumnStyle (availableColumn).MaxWidth);
        Assert.Equal (idWidth, table.Style.GetOrCreateColumnStyle (idColumn).MinWidth);
    }

    [Fact]
    public void App_FilterShortcuts_ClearTextAndExitOnEmptyBackspace ()
    {
        App app = new (new MockBackend ());
        AppState state = GetPrivateField<AppState> (app, "_state");
        TextField input = GetPrivateField<TextField> (app, "_filterInput");
        state.InputMode = InputMode.LocalFilter;
        input.Text = "sample";

        Key clear = new (KeyCode.U | KeyCode.CtrlMask);
        InvokePrivate (app, "OnFilterKeyDown", input, clear);

        Assert.True (clear.Handled);
        Assert.Equal (string.Empty, state.LocalFilter);
        Assert.Equal (InputMode.LocalFilter, state.InputMode);

        Key backspace = new (KeyCode.Backspace);
        InvokePrivate (app, "OnFilterKeyDown", input, backspace);

        Assert.True (backspace.Handled);
        Assert.Equal (InputMode.Normal, state.InputMode);
    }

    [Fact]
    public void App_FilterCtrlC_RequestsShutdown ()
    {
        App app = new (new MockBackend ());
        AppState state = GetPrivateField<AppState> (app, "_state");
        TextField input = GetPrivateField<TextField> (app, "_filterInput");
        state.InputMode = InputMode.Search;

        Key quit = new (KeyCode.C | KeyCode.CtrlMask);
        InvokePrivate (app, "OnFilterKeyDown", input, quit);

        Assert.True (quit.Handled);
        Assert.Equal (0, GetPrivateInt (app, "_uiAccepting"));
    }

    [Fact]
    public void UpgradeQueryFor_TruncatedId_FallsBackToName ()
    {
        // winget truncates long ids in tabular output with `…`; an --id match can't succeed, so
        // the upgrade query must be the exact name instead. Mirrors upstream winget-tui fd9e9dbe.
        Package p = new () { Id = "Microsoft.Azure.Function…", Name = "Azure Functions Core Tools", Source = "winget" };

        Assert.Equal ("Azure Functions Core Tools", App.UpgradeQueryFor (p));
    }

    [Fact]
    public void UpgradeQueryFor_FullId_UsesId ()
    {
        Package p = new () { Id = "7zip.7zip", Name = "7-Zip", Source = "winget" };

        Assert.Equal ("7zip.7zip", App.UpgradeQueryFor (p));
    }

    [Theory]
    [InlineData (AppMode.Upgrades, PinFilter.All, "", "All packages are up to date!")]
    [InlineData (AppMode.Upgrades, PinFilter.PinnedOnly, "", "No pinned packages with upgrades found.")]
    [InlineData (AppMode.Upgrades, PinFilter.UnpinnedOnly, "", "No unpinned packages with upgrades found.")]
    [InlineData (AppMode.Installed, PinFilter.PinnedOnly, "", "No pinned packages found.")]
    [InlineData (AppMode.Installed, PinFilter.UnpinnedOnly, "", "No unpinned packages found.")]
    [InlineData (AppMode.Installed, PinFilter.All, "", "No packages found.")]
    public void EmptyStateMessage_ReflectsModeAndPinFilter (AppMode mode, PinFilter pin, string filter, string expected)
    {
        AppState state = new (new MockBackend ())
        {
            Mode = mode,
            PinFilter = pin,
            LocalFilter = filter
        };

        Assert.Equal (expected, App.EmptyStateMessage (state));
    }

    [Fact]
    public void EmptyStateMessage_WithActiveLocalFilter_ExplainsTheFilter ()
    {
        // An active filter that hides everything must not read "All packages are up to date!".
        AppState state = new (new MockBackend ())
        {
            Mode = AppMode.Upgrades,
            PinFilter = PinFilter.All,
            LocalFilter = "nonesuch"
        };

        Assert.Contains ("nonesuch", App.EmptyStateMessage (state), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData ("Name", SortField.Name)]
    [InlineData ("Name ↑", SortField.Name)]
    [InlineData ("Id", SortField.Id)]
    [InlineData ("Version ↓", SortField.Version)]
    [InlineData ("Available", SortField.AvailableVersion)]
    [InlineData ("Available ↑", SortField.AvailableVersion)]
    public void SortFieldForHeader_MapsSortableColumns (string header, SortField expected)
    {
        Assert.Equal (expected, App.SortFieldForHeader (header));
    }

    [Theory]
    [InlineData (" ")]
    [InlineData ("Source")]
    public void SortFieldForHeader_ReturnsNullForNonSortableColumns (string header)
    {
        Assert.Null (App.SortFieldForHeader (header));
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

    private static int GetPrivateInt (object instance, string fieldName)
    {
        System.Reflection.FieldInfo field = instance.GetType ().GetField (
            fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        return Assert.IsType<int> (field.GetValue (instance));
    }

    private static void InvokePrivate (object instance, string methodName)
    {
        System.Reflection.MethodInfo method = instance.GetType ().GetMethod (
            methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        Assert.NotNull (method);
        _ = method.Invoke (instance, []);
    }

    private static void InvokePrivate (object instance, string methodName, params object[] arguments)
    {
        System.Reflection.MethodInfo method = instance.GetType ().GetMethod (
            methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        Assert.NotNull (method);
        _ = method.Invoke (instance, arguments);
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
