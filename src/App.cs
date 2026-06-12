using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace WingetTuiSharp;

/// <summary>
/// Top-level window. Hosts the header (logo + tabs), search/filter input,
/// 60/40 split between package list and detail panel, and the status bar at the bottom.
/// </summary>
public sealed class App : Runnable
{
    /// <summary>Total rows reserved by the logo/header chrome before search or main content.</summary>
    /// <remarks>One row of breathing room below the wordmark before the list/search start.</remarks>
    private const int HeaderHeight = Logo.LogoHeight + 1;

    // Debounce before an uncached detail fetch fires. Scrolling the list changes the selection
    // rapidly; without this, every row the cursor passes over issues a full backend detail
    // request (for COM: ConnectAsync + FindByIdAsync + GetCatalogPackageMetadata). Bursts of
    // those throttle/wedge the WinGet out-of-proc COM server, causing the detail panel to stall
    // for tens of seconds while it recovers. Holding the fetch until the selection settles for
    // this interval collapses a fast scroll to a single request (the row landed on). Each
    // selection change cancels the pending delay, so passed-over rows never hit the backend.
    private const int DetailLoadDebounceMs = 200;

    private readonly AppState _state;
    private readonly TabBar _tabBar;
    private readonly Logo _logo;
    private readonly TextField _filterInput;
    private readonly TextField _versionInput;
    private readonly FrameView _listFrame;
    private readonly TableView _packageTable;
    private readonly DetailPanel _detailPanel;
    private readonly StatusBar _statusBar;
    private readonly Label _searchHint;
    private readonly Label _backendLabel;
    private CancellationTokenSource _viewCts = new ();
    private CancellationTokenSource _detailCts = new ();

    // Non-null only while an install/upgrade/uninstall (or batch) is in flight. Doubles as the
    // "an operation is running" gate for Esc-to-cancel — distinct from _viewCts/_detailCts, which
    // cover list/detail refreshes that already cancel implicitly on navigation.
    private CancellationTokenSource? _opCts;

    // True while a short preflight fetch (version list / installer preview) is running, so a
    // rapid second trigger can't queue a duplicate modal or race the status line.
    private bool _preflightBusy;
    private object? _spinnerTimer;
    private bool _initialLoadDone;

    public App (IBackend backend)
    {
        _state = new (backend);
        SchemeName = Theme.AppSchemeName;
        Title = "winget-tui (Terminal.Gui port)";

        // --- Header: logo on the left, tabs to the right, vertically centered against the
        // wordmark. Search/filter lives immediately below the logo header and temporarily
        // pushes the list/detail panes down one row while active. ---
        _logo = new () { X = 1, Y = 0 };
        _tabBar = new () { X = Pos.Right (_logo) + 4, Y = (Logo.LogoHeight - 1) / 2, Width = Dim.Fill (1) };

        // Which backend is live + its winget version, dim in the top-right of the header. Empty
        // until DescribeAsync resolves at startup (see OnIsRunningChanged). Anchored to the last
        // logo row so it never collides with the tab row above it.
        _backendLabel = new ()
        {
            X = Pos.AnchorEnd (),
            Y = Logo.LogoHeight - 1,
            Height = 1,
            Width = Dim.Auto (),
            Text = string.Empty,
            SchemeName = Theme.AccentDimSchemeName
        };

        // --- Search / filter input (hidden until needed). Lives immediately below the
        // header chrome; the list shifts down another row when search is shown. ---
        _searchHint = new ()
        {
            X = 1,
            Y = HeaderHeight,
            Width = 2,
            Text = "/ ",
            SchemeName = Theme.AccentSchemeName,
            Visible = false
        };
        _filterInput = new ()
        {
            X = Pos.Right (_searchHint),
            Y = HeaderHeight,
            Width = Dim.Fill (1),
            Title = "to search…",
            Visible = false
        };

        // --- Main content split ---
        _listFrame = new ()
        {
            X = 0,
            Y = HeaderHeight,
            Width = Dim.Percent (60),
            Height = Dim.Fill (1),
            Title = " Installed ",
            BorderStyle = LineStyle.Rounded,
            SchemeName = Theme.FrameFocusedSchemeName
        };

        _packageTable = new ()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill (),
            Height = Dim.Fill (),
            FullRowSelect = true,
            SchemeName = Theme.SurfaceSchemeName
        };
        _packageTable.Style.ShowHorizontalHeaderUnderline = true;
        _packageTable.Style.ExpandLastColumn = true;
        _listFrame.Add (_packageTable);

        _detailPanel = new ()
        {
            X = Pos.Right (_listFrame),
            Y = HeaderHeight,
            Width = Dim.Fill (),
            Height = Dim.Fill (1)
        };

        _statusBar = new ()
        {
            X = 0,
            Y = Pos.AnchorEnd (1),
            Width = Dim.Fill ()
        };

        Add (_logo, _tabBar, _backendLabel, _searchHint, _filterInput, _listFrame, _detailPanel, _statusBar);

        // --- Version input dialog field (lives inside MessageBox-like popover; we use a separate field) ---
        _versionInput = new ();

        WireEvents ();
        RefreshTable ();
        RefreshStatusBar ();
    }

    private void WireEvents ()
    {
        _tabBar.TabClicked += (_, mode) => SwitchToMode (mode);

        _packageTable.ValueChanged += (_, _) => OnSelectedRowChanged ();

        _filterInput.TextChanged += (_, _) =>
                                    {
                                        if (_state.InputMode == InputMode.LocalFilter)
                                        {
                                            _state.LocalFilter = _filterInput.Text ?? string.Empty;
                                            _state.ApplyFilter ();
                                            RefreshTable ();
                                        }
                                        else if (_state.InputMode == InputMode.Search)
                                        {
                                            _state.SearchQuery = _filterInput.Text ?? string.Empty;
                                        }
                                    };

        _filterInput.Accepted += (_, _) =>
                                 {
                                     if (_state.InputMode == InputMode.Search)
                                     {
                                         TriggerRefresh ();
                                     }

                                     ExitInputMode ();
                                 };

        // Bracketed paste (CSI 2004h) lands in the TextField via the standard Command.Paste
        // pipeline. For Search mode we treat a paste as "intent to search now" — fire the
        // backend immediately rather than waiting for Enter.
        _filterInput.Pasted += (_, _) =>
                               {
                                   if (_state.InputMode == InputMode.Search)
                                   {
                                       _state.SearchQuery = _filterInput.Text ?? string.Empty;
                                       TriggerRefresh ();
                                   }
                               };

        KeyDown += OnKeyDown;
        _packageTable.KeyDown += OnKeyDown;
        _detailPanel.KeyDown += OnKeyDown;
        _detailPanel.LinkActivated += (_, url) => OpenUrl (url);
        _filterInput.KeyDown += OnFilterKeyDown;

        _packageTable.HasFocusChanged += (_, e) => ApplyFocusStyle (_listFrame, e.NewValue);
        _detailPanel.HasFocusChanged += (_, e) => ApplyFocusStyle (_detailPanel, e.NewValue);
    }

    /// <summary>
    /// Swap a frame's scheme AND border line style based on focus. Heavy lines (┏━┓) for the
    /// focused frame, Rounded (╭─╮) for the unfocused one — the same effect upstream gets via
    /// Bold-honoring box drawing in the Ratatui renderer.
    /// </summary>
    private static void ApplyFocusStyle (FrameView frame, bool hasFocus)
    {
        frame.SchemeName = hasFocus ? Theme.FrameFocusedSchemeName : Theme.FrameUnfocusedSchemeName;
        frame.BorderStyle = hasFocus ? LineStyle.Heavy : LineStyle.Rounded;
        frame.SetNeedsDraw ();
    }

    /// <inheritdoc />
    protected override void OnIsRunningChanged (bool newIsRunning)
    {
        base.OnIsRunningChanged (newIsRunning);

        if (newIsRunning && !_initialLoadDone)
        {
            _initialLoadDone = true;
            TriggerRefresh ();
            StartSpinner ();
            LoadBackendDescription ();
            LoadSources ();
        }
        else if (!newIsRunning)
        {
            StopSpinner ();
        }
    }

    /// <summary>
    /// Resolve which backend is live + its winget version once at startup and show it in the
    /// header badge. Best-effort: a failure just leaves the badge empty (the app works regardless).
    /// </summary>
    private void LoadBackendDescription ()
    {
        Task.Run (async () =>
                  {
                      string description;

                      try
                      {
                          description = await _state.Backend.DescribeAsync (CancellationToken.None);
                      }
                      catch
                      {
                          return;
                      }

                      App?.Invoke (() =>
                                   {
                                       _state.BackendDescription = description;
                                       _backendLabel.Text = description;
                                       _backendLabel.SetNeedsLayout ();
                                       _backendLabel.SetNeedsDraw ();
                                   });
                  });
    }

    /// <summary>
    /// Discover the configured package sources once at startup so the <c>f</c> source filter cycles
    /// through the real source list (including custom/enterprise REST sources) instead of just the
    /// two predefined ones. Best-effort: on failure the seeded ["winget","msstore"] defaults stand.
    /// A currently-selected source that's absent from the discovered list is reset to "All".
    /// </summary>
    private void LoadSources ()
    {
        Task.Run (async () =>
                  {
                      IReadOnlyList<string> sources;

                      try
                      {
                          sources = await _state.Backend.ListSourcesAsync (CancellationToken.None);
                      }
                      catch
                      {
                          return;
                      }

                      if (sources.Count == 0)
                      {
                          return;
                      }

                      App?.Invoke (() =>
                                   {
                                       _state.AvailableSources = sources;

                                       if (_state.SourceFilter is { } current
                                           && !sources.Any (s => string.Equals (s, current, StringComparison.OrdinalIgnoreCase)))
                                       {
                                           _state.SourceFilter = null;
                                           RefreshStatusBar ();
                                       }
                                   });
                  });
    }

    private void StartSpinner ()
    {
        if (App is null)
        {
            return;
        }

        _spinnerTimer = App.AddTimeout (TimeSpan.FromMilliseconds (100), () =>
                                                                        {
                                                                            _statusBar.Tick++;

                                                                            if (_statusBar.IsLoading)
                                                                            {
                                                                                _statusBar.SetNeedsDraw ();
                                                                            }

                                                                            return true;
                                                                        });
    }

    private void StopSpinner ()
    {
        if (_spinnerTimer is not null && App is not null)
        {
            App.RemoveTimeout (_spinnerTimer);
            _spinnerTimer = null;
        }
    }

    private void TriggerRefresh ()
    {
        _viewCts.Cancel ();
        _viewCts = new ();
        CancellationToken ct = _viewCts.Token;
        int gen = _state.BumpViewGeneration ();
        AppMode mode = _state.Mode;
        string? src = _state.SourceFilter;
        string query = _state.SearchQuery;

        // Remember the currently-selected package id so we can re-position the cursor on the
        // same package after the refresh, instead of always jumping to row 0. Mirrors
        // upstream's process_messages cursor-anchor behavior.
        string? previousSelectedId = CurrentPackage ()?.Id;

        // Don't hit `winget search` with an empty query — it dumps the entire catalog
        // (~13k packages) which is never what the user wants. Show a placeholder instead.
        if (mode == AppMode.Search && string.IsNullOrWhiteSpace (query))
        {
            _state.Packages = [];
            _state.ApplyFilter ();
            _state.Loading = false;
            _state.StatusMessage = "Press / to search for packages";
            _state.StatusIsError = false;
            RefreshTable ();
            RefreshStatusBar ();
            SyncTabBar ();

            return;
        }

        _state.Loading = true;
        _state.StatusMessage = $"Loading {_state.Mode}…";
        _state.StatusIsError = false;
        RefreshStatusBar ();
        SyncTabBar ();

        Task.Run (async () =>
                  {
                      try
                      {
                          IReadOnlyList<Package> packages = mode switch
                          {
                              AppMode.Search => await _state.Backend.SearchAsync (query, src, ct),
                              AppMode.Upgrades => await _state.Backend.ListUpgradesAsync (src, ct),
                              _ => await _state.Backend.ListInstalledAsync (src, ct)
                          };

                          if (ct.IsCancellationRequested || gen != _state.ViewGeneration)
                          {
                              return;
                          }

                          App?.Invoke (() =>
                                       {
                                           _state.Packages = packages.ToList ();
                                           _state.ApplyFilter ();
                                           _state.Loading = false;
                                           int n = _state.Filtered.Count;
                                           _state.StatusMessage = n == 1 ? "1 package" : $"{n} packages";

                                           // A search that hit the result cap means there's more the user can't see;
                                           // nudge them to narrow it (only the COM backend actually caps).
                                           if (mode == AppMode.Search && packages.Count >= AppState.SearchResultLimit)
                                           {
                                               _state.StatusMessage = $"{AppState.SearchResultLimit}+ matches — refine your search to narrow";
                                           }
                                           RefreshTable ();
                                           RefreshStatusBar ();
                                           RestoreCursorOrSelectFirst (previousSelectedId);
                                       });
                      }
                      catch (OperationCanceledException) { }
                      catch (Exception ex)
                      {
                          string msg = $"Error: {ex.Message}";

                          App?.Invoke (() =>
                                       {
                                           _state.Loading = false;
                                           _state.StatusMessage = msg;
                                           _state.StatusIsError = true;
                                           RefreshStatusBar ();
                                       });
                      }
                  }, ct);
    }

    private void RefreshTable ()
    {
        string title = _state.Mode switch
        {
            AppMode.Search => $" Search ({_state.Filtered.Count}) ",
            AppMode.Upgrades => $" Upgrades ({_state.Filtered.Count} • {_state.BatchSelected.Count} selected) ",
            _ => $" Installed ({_state.Filtered.Count}) "
        };

        if (_state.Mode != AppMode.Search && _state.PinFilter != PinFilter.All)
        {
            title = title.TrimEnd (' ') + $" • {AppState.PinLabel (_state.PinFilter).Trim ()} ";
        }

        _listFrame.Title = title;

        if (_state.Filtered.Count == 0)
        {
            _state.CurrentDetail = null;
            CancelPendingDetailLoad ();
            _detailPanel.SetDetail (null, false);

            _packageTable.Table = new EnumerableTableSource<EmptyRow> ([], new ()
            {
                { _state.Mode == AppMode.Upgrades ? "Name" : "Name", _ => string.Empty }
            });

            RefreshStatusBar ();

            return;
        }

        Dictionary<string, Func<Package, object>> cols;

        if (_state.Mode == AppMode.Upgrades)
        {
            cols = new ()
            {
                [HeaderWithSort ("Name", SortField.Name)] = p =>
                {
                    string marker = _state.BatchSelected.Contains (p.Id) ? "[x] " : "    ";
                    string pin = p.PinState.IsPinned ? "📌 " : string.Empty;

                    return marker + pin + p.Name;
                },
                [HeaderWithSort ("Id", SortField.Id)] = p => FormatIdForDisplay (p.Id),
                [HeaderWithSort ("Version", SortField.Version)] = p => p.Version,
                ["Available"] = p => p.AvailableVersion ?? string.Empty,
                ["Source"] = p => p.Source
            };
        }
        else
        {
            cols = new ()
            {
                [HeaderWithSort ("Name", SortField.Name)] = p =>
                {
                    string pin = p.PinState.IsPinned ? "📌 " : string.Empty;

                    return pin + p.Name;
                },
                [HeaderWithSort ("Id", SortField.Id)] = p => FormatIdForDisplay (p.Id),
                [HeaderWithSort ("Version", SortField.Version)] = p => p.Version,
                ["Source"] = p => p.Source
            };
        }

        EnumerableTableSource<Package> raw = new (_state.Filtered, cols);
        MarkedTableSource marked = new (raw);
        _packageTable.Table = marked;

        ApplyColumnStyles (marked);
        OnSelectedRowChanged ();
    }

    /// <summary>
    /// Sets per-column MaxWidth (so long names/ids truncate with a `…` indicator), accent-bold
    /// header coloring, and a per-cell scheme on the Source column to color-code winget vs msstore.
    /// </summary>
    private void ApplyColumnStyles (MarkedTableSource marked)
    {
        _packageTable.Style.ColumnStyles.Clear ();

        // Column 0: the cursor marker — exactly 1 cell wide.
        ColumnStyle markerStyle = _packageTable.Style.GetOrCreateColumnStyle (0);
        markerStyle.MinWidth = 1;
        markerStyle.MaxWidth = 1;
        markerStyle.HeaderColorGetter = _ => new Scheme (_packageTable.GetScheme ())
        {
            Normal = new (Theme.Accent, Theme.Surface)
        };

        for (int i = 1; i < marked.Columns; i++)
        {
            ColumnStyle s = _packageTable.Style.GetOrCreateColumnStyle (i);
            s.HeaderColorGetter = _ => new Scheme (_packageTable.GetScheme ())
            {
                Normal = new (Theme.Accent, Theme.Surface, TextStyle.Bold),
                Focus = new (Theme.Accent, Theme.Surface, TextStyle.Bold)
            };

            // Pin column widths by setting MinWidth = MaxWidth. Otherwise TableView's
            // CalculateMaxCellWidth scans the visible viewport and recomputes widths every
            // frame from the max content width — so when the user presses Down arrow and
            // a new row enters the viewport with different content widths, all columns
            // shift. Fixed widths avoid that visual jump.
            string name = marked.ColumnNames [i];

            if (name.StartsWith ("Name", StringComparison.Ordinal))
            {
                s.MinWidth = 24;
                s.MaxWidth = 24;
            }
            else if (name.StartsWith ("Id", StringComparison.Ordinal))
            {
                s.MinWidth = 28;
                s.MaxWidth = 28;
            }
            else if (name.StartsWith ("Version", StringComparison.Ordinal))
            {
                s.MinWidth = 14;
                s.MaxWidth = 14;
            }
            else if (name == "Available")
            {
                s.MinWidth = 14;
                s.MaxWidth = 14;
            }
            else if (name == "Source")
            {
                s.MinWidth = 8;
                s.MaxWidth = 8;
                s.ColorGetter = args =>
                {
                    string val = args.CellValue?.ToString () ?? string.Empty;
                    Color fg = val switch
                    {
                        "winget" => Theme.Info,
                        "msstore" => Theme.Accent,
                        _ => Theme.TextSecondary
                    };

                    // Color-code only the unselected (Normal) cells. The selected row's
                    // background is Theme.Accent, so an Accent foreground (msstore) would be
                    // invisible (Accent-on-Accent); leaving Focus/Active as the row defaults
                    // keeps the highlighted Source cell readable (dark-on-gold).
                    return new Scheme (args.RowScheme)
                    {
                        Normal = new (fg, args.RowScheme.Normal.Background)
                    };
                };
            }
        }
    }

    private string HeaderWithSort (string label, SortField field)
    {
        if (_state.SortField != field)
        {
            return label;
        }

        return label + (_state.SortDir == SortDir.Asc ? " ↑" : " ↓");
    }

    private void SyncTabBar () => _tabBar.Active = _state.Mode;

    /// <summary>
    /// Try to position the cursor on the same package the user had selected before the
    /// refresh (by id). If that package is no longer in the filtered list, fall back to
    /// row 0. If the list is empty, clear the detail panel.
    /// </summary>
    private void RestoreCursorOrSelectFirst (string? previousId)
    {
        if (_state.Filtered.Count == 0)
        {
            _state.CurrentDetail = null;
            CancelPendingDetailLoad ();
            _detailPanel.SetDetail (null, false);
            RefreshStatusBar ();

            return;
        }

        int row = 0;

        if (!string.IsNullOrEmpty (previousId))
        {
            int found = _state.Filtered.FindIndex (p => p.Id.Equals (previousId, StringComparison.OrdinalIgnoreCase));

            if (found >= 0)
            {
                row = found;
            }
        }

        _packageTable.Value = new (new (0, row));
    }

    private void RefreshStatusBar ()
    {
        _statusBar.Mode = _state.Mode;
        _statusBar.InputMode = _state.InputMode;
        _statusBar.SourceFilter = _state.SourceFilter;
        _statusBar.PinFilter = _state.PinFilter;
        _statusBar.Message = _state.StatusMessage;
        _statusBar.IsError = _state.StatusIsError;
        _statusBar.IsLoading = _state.Loading || _state.DetailLoading;
        _statusBar.Op = _state.OpProgress;
        _statusBar.SetNeedsDraw ();
        _detailPanel.Mode = _state.Mode;
    }

    private void OnSelectedRowChanged ()
    {
        CancelPendingDetailLoad ();

        int row = _packageTable.Value?.SelectedCell.Y ?? -1;

        if (_packageTable.Table is MarkedTableSource ms && ms.CursorRow != row)
        {
            ms.CursorRow = row;
            _packageTable.SetNeedsDraw ();
        }

        Package? p = _state.SelectedPackage (row);

        if (p is null)
        {
            _state.CurrentDetail = null;
            _detailPanel.SetDetail (null, false);
            RefreshStatusBar ();

            return;
        }

        if (_state.DetailCache.TryGetValue (p.Id, out PackageDetail? cached))
        {
            cached.MergeContext (p);
            cached.EnsureDetailHint ();
            _state.CurrentDetail = cached;
            _detailPanel.SetDetail (cached, false);
            RefreshStatusBar ();

            return;
        }

        _detailPanel.SetDetail (null, true);
        CancellationToken ct = _detailCts.Token;
        int gen = _state.BumpDetailGeneration ();
        _state.DetailLoading = true;
        RefreshStatusBar ();

        Task.Run (async () =>
                  {
                      try
                      {
                          // Debounce: a fast scroll cancels `ct` (via CancelPendingDetailLoad on
                          // the next selection change) before this delay elapses, so we only fetch
                          // for the row the cursor settles on — not every row it passes over.
                          await Task.Delay (DetailLoadDebounceMs, ct);

                          PackageDetail? detail = await _state.Backend.ShowAsync (p.Id, ct);

                          if (ct.IsCancellationRequested || gen != _state.DetailGeneration)
                          {
                              return;
                          }

                          App?.Invoke (() =>
                                       {
                                           _state.DetailLoading = false;

                                           // Fall back to a stub detail built from the list-row context when winget show
                                           // can't resolve the package (truncated id, store-only entries with no manifest,
                                           // packages with unusual characters in id like ".115Chrome"). Mirrors upstream's
                                           // "sparse detail fallback" pattern so the panel never goes blank.
                                           PackageDetail final = detail ?? BuildStubDetail (p);
                                           final.MergeContext (p);
                                           final.EnsureDetailHint ();
                                           _state.DetailCache [p.Id] = final;
                                           _state.CurrentDetail = final;
                                           _detailPanel.SetDetail (final, false);
                                           RefreshStatusBar ();
                                       });
                      }
                      catch (OperationCanceledException) { }
                      catch (Exception ex)
                      {
                          App?.Invoke (() =>
                                       {
                                           _state.DetailLoading = false;
                                           _state.StatusMessage = $"Detail error: {ex.Message}";
                                           _state.StatusIsError = true;
                                           RefreshStatusBar ();
                                       });
                       }
                   }, ct);
    }

    private void CancelPendingDetailLoad ()
    {
        _detailCts.Cancel ();
        _detailCts.Dispose ();
        _detailCts = new ();
        _state.DetailLoading = false;
    }

    /// <summary>
    /// Move the package-list cursor by <paramref name="delta"/> rows, clamped to the table.
    /// Used for vim-style j/k navigation and for forwarding navigation keys from the filter
    /// input mode (so the user can scroll through filtered results while typing).
    /// </summary>
    private void MoveListCursor (int delta)
    {
        if (_state.Filtered.Count == 0)
        {
            return;
        }

        int current = _packageTable.Value?.SelectedCell.Y ?? 0;
        int next = Math.Clamp (current + delta, 0, _state.Filtered.Count - 1);

        if (next != current)
        {
            _packageTable.Value = new (new (0, next));
        }
    }

    /// <summary>
    /// Cosmetic transform for the Id column: ARP-derived ids look like
    /// <c>ARP\Machine\X64\{registry-key}</c>. The first three segments are identical noise
    /// across hundreds of installed-only rows, so strip them and show just the trailing
    /// registry key, which is the part that actually identifies the package. The original
    /// id on <see cref="Package.Id"/> is preserved for backend operations.
    /// </summary>
    internal static string FormatIdForDisplay (string id)
    {
        if (!id.StartsWith ("ARP\\", StringComparison.Ordinal))
        {
            return id;
        }

        string [] parts = id.Split ('\\');

        // Expected shape: ARP \ {Machine|User} \ {X64|Arm64|X86|Arm} \ {key…}.
        // If anything shorter, fall back to the raw id rather than misrepresent it.
        return parts.Length >= 4 ? string.Join ('\\', parts [3..]) : id;
    }

    private static PackageDetail BuildStubDetail (Package p) =>
        new ()
        {
            Id = p.Id,
            Name = p.Name,
            Version = p.Version,
            AvailableVersion = p.AvailableVersion,
            Source = p.Source,
            PinState = p.PinState,
            Description = "winget could not retrieve manifest details for this package. Showing list-view information only.",
            IsDescriptionDegraded = true
        };

    private Package? CurrentPackage ()
    {
        int row = _packageTable.Value?.SelectedCell.Y ?? -1;

        return _state.SelectedPackage (row);
    }

    // ------------------------------------------------------------------------
    // Keyboard handling — mirrors src/handler.rs from shanselman/winget-tui.
    // ------------------------------------------------------------------------

    private void OnFilterKeyDown (object? sender, Key key)
    {
        if (key.KeyCode == KeyCode.Esc)
        {
            if (_state.InputMode == InputMode.LocalFilter)
            {
                _state.LocalFilter = string.Empty;
                _filterInput.Text = string.Empty;
                _state.ApplyFilter ();
                RefreshTable ();
            }

            ExitInputMode ();
            key.Handled = true;

            return;
        }

        // Let the user navigate the filtered list while the filter input has focus.
        // Mirrors upstream src/handler.rs:182-212 which forwards Up/Down/PgUp/PgDn/Home/End
        // through to move_selection without closing the input box.
        switch (key.KeyCode)
        {
            case KeyCode.CursorDown:
                MoveListCursor (1);
                key.Handled = true;

                break;
            case KeyCode.CursorUp:
                MoveListCursor (-1);
                key.Handled = true;

                break;
            case KeyCode.PageDown:
                MoveListCursor (10);
                key.Handled = true;

                break;
            case KeyCode.PageUp:
                MoveListCursor (-10);
                key.Handled = true;

                break;
            case KeyCode.Home:
                if (_state.Filtered.Count > 0)
                {
                    _packageTable.Value = new (new (0, 0));
                }

                key.Handled = true;

                break;
            case KeyCode.End:
                if (_state.Filtered.Count > 0)
                {
                    _packageTable.Value = new (new (0, _state.Filtered.Count - 1));
                }

                key.Handled = true;

                break;
        }
    }

    private void OnKeyDown (object? sender, Key key)
    {
        if (_state.InputMode != InputMode.Normal)
        {
            return;
        }

        switch (key.KeyCode)
        {
            case KeyCode.Esc:

                // While an operation is in flight, Esc cancels it (COM aborts cooperatively)
                // rather than quitting. With nothing running, Esc quits as before.
                if (_opCts is { } opCts)
                {
                    opCts.Cancel ();
                    _state.StatusMessage = "Cancelling…";
                    RefreshStatusBar ();
                    key.Handled = true;

                    return;
                }

                RequestStop ();
                key.Handled = true;

                return;
            case KeyCode.Q:
                RequestStop ();
                key.Handled = true;

                return;
            case KeyCode.C | KeyCode.CtrlMask:
                RequestStop ();
                key.Handled = true;

                return;
            case KeyCode.D1:
                JumpToTab (AppMode.Search);
                key.Handled = true;

                return;
            case KeyCode.D2:
                JumpToTab (AppMode.Installed);
                key.Handled = true;

                return;
            case KeyCode.D3:
                JumpToTab (AppMode.Upgrades);
                key.Handled = true;

                return;
        }

        // Left/Right arrows cycle modes (Search ↔ Installed ↔ Upgrades) only when focus is
        // not on the list — otherwise the list's column-aware navigation handles them.
        if (key.KeyCode == KeyCode.CursorRight && _packageTable.HasFocus == false)
        {
            _state.CycleMode (true);
            SwitchToMode (_state.Mode);
            key.Handled = true;

            return;
        }

        if (key.KeyCode == KeyCode.CursorLeft && _packageTable.HasFocus == false)
        {
            _state.CycleMode (false);
            SwitchToMode (_state.Mode);
            key.Handled = true;

            return;
        }

        // Tab and Shift+Tab both toggle focus between the package list and the detail panel.
        // (Upstream binds it this way; previously Shift+Tab cycled mode backward which
        // conflicted with the Left-arrow binding.)
        if (key.KeyCode == KeyCode.Tab || key.KeyCode == (KeyCode.Tab | KeyCode.ShiftMask))
        {
            if (_packageTable.HasFocus)
            {
                _detailPanel.SetFocus ();
            }
            else
            {
                _packageTable.SetFocus ();
            }

            key.Handled = true;

            return;
        }

        // Explicit Home/End/PgUp/PgDn for parity (TableView handles them by default but
        // we want to forward them through our handler so generation-counter cancellation
        // wraps any incidental detail loads triggered by selection changes).
        if (key.KeyCode is KeyCode.Home or KeyCode.End or KeyCode.PageUp or KeyCode.PageDown && _packageTable.HasFocus)
        {
            // Let TableView's default command bindings handle the move; do not mark handled.
            return;
        }

        // Character keys
        if (key.AsRune.Value is var rune and > 0)
        {
            char c = (char)rune;

            switch (c)
            {
                case 'j':

                    // Vim-style down. Forward to the table by simulating CursorDown.
                    MoveListCursor (1);
                    key.Handled = true;

                    return;
                case 'k':

                    // Vim-style up.
                    MoveListCursor (-1);
                    key.Handled = true;

                    return;
                case '/':
                case 's':
                    EnterFilterMode ();
                    key.Handled = true;

                    return;
                case 'f':
                    _state.CycleSourceFilter ();
                    TriggerRefresh ();
                    key.Handled = true;

                    return;
                case 'r':
                    TriggerRefresh ();
                    key.Handled = true;

                    return;
                case 'S':
                    _state.CycleSort ();
                    _state.ApplyFilter ();
                    RefreshTable ();
                    key.Handled = true;

                    return;
                case 'P':
                    if (_state.Mode != AppMode.Search)
                    {
                        _state.CyclePinFilter ();
                        _state.ApplyFilter ();
                        RefreshTable ();
                        RefreshStatusBar ();
                    }

                    key.Handled = true;

                    return;
                case '?':
                    ShowHelp ();
                    key.Handled = true;

                    return;
                case 'e':
                    ExportCsv ();
                    key.Handled = true;

                    return;
                case 'o':
                    OpenUrl (_state.CurrentDetail?.Homepage);
                    key.Handled = true;

                    return;
                case 'c':
                    OpenUrl (_state.CurrentDetail?.ReleaseNotesUrl);
                    key.Handled = true;

                    return;
                case 'i':
                    AskInstall (CurrentPackage (), specificVersion: false);
                    key.Handled = true;

                    return;
                case 'I':
                    AskInstall (CurrentPackage (), specificVersion: true);
                    key.Handled = true;

                    return;
                case 'd':
                    AskDownload (CurrentPackage ());
                    key.Handled = true;

                    return;
                case 'A':
                    AskAdvancedInstall (CurrentPackage ());
                    key.Handled = true;

                    return;
                case 'V':
                    AskVerify (CurrentPackage ());
                    key.Handled = true;

                    return;
                case 'R':
                    if (_state.Mode != AppMode.Search)
                    {
                        AskRepair (CurrentPackage ());
                        key.Handled = true;
                    }

                    return;
                case 'u':
                    AskUpgrade (CurrentPackage ());
                    key.Handled = true;

                    return;
                case 'x':
                    AskUninstall (CurrentPackage ());
                    key.Handled = true;

                    return;
                case 'p':
                    if (_state.Mode != AppMode.Search)
                    {
                        TogglePin (CurrentPackage ());
                        key.Handled = true;
                    }

                    return;
                case ' ':
                    if (_state.Mode == AppMode.Upgrades)
                    {
                        ToggleBatchSelect (CurrentPackage ());
                        key.Handled = true;
                    }

                    return;
                case 'a':
                    if (_state.Mode == AppMode.Upgrades)
                    {
                        ToggleSelectAll ();
                        key.Handled = true;
                    }

                    return;
                case 'U':
                    if (_state.Mode == AppMode.Upgrades)
                    {
                        AskBatchUpgrade ();
                        key.Handled = true;
                    }

                    return;
            }
        }
    }

    private void JumpToTab (AppMode mode)
    {
        if (_state.Mode == mode)
        {
            return;
        }

        SwitchToMode (mode);
    }

    /// <summary>
    /// Centralized view-switch: clears the local filter, batch selection, and any input
    /// mode. Without this, a `/` filter typed in Installed would carry into Search/Upgrades
    /// and silently filter rows the user thinks they're seeing whole.
    /// </summary>
    private void SwitchToMode (AppMode mode)
    {
        _state.Mode = mode;
        _state.LocalFilter = string.Empty;
        _state.BatchSelected.Clear ();

        if (_state.InputMode != InputMode.Normal)
        {
            ExitInputMode ();
        }

        TriggerRefresh ();
    }

    private void EnterFilterMode ()
    {
        _state.InputMode = _state.Mode == AppMode.Search ? InputMode.Search : InputMode.LocalFilter;
        _searchHint.Visible = true;
        _filterInput.Visible = true;
        _filterInput.Title = _state.Mode == AppMode.Search ? "to search…" : "to filter…";
        _filterInput.Text = _state.Mode == AppMode.Search ? _state.SearchQuery : _state.LocalFilter;
        _listFrame.Y = HeaderHeight + 1;
        _detailPanel.Y = HeaderHeight + 1;
        _filterInput.SetFocus ();
        RefreshStatusBar ();
    }

    private void ExitInputMode ()
    {
        _state.InputMode = InputMode.Normal;
        _searchHint.Visible = false;
        _filterInput.Visible = false;
        _listFrame.Y = HeaderHeight;
        _detailPanel.Y = HeaderHeight;
        _packageTable.SetFocus ();
        RefreshStatusBar ();
    }

    // ------------------------------------------------------------------------
    // Confirm + execute helpers
    // ------------------------------------------------------------------------

    /// <summary>
    /// winget sometimes truncates package ids in its tabular output with `…`. Operating on
    /// such an id will fail because winget can't match the literal `…` against the catalog.
    /// Surface a clear message instead of letting winget fail opaquely.
    /// </summary>
    private bool GuardTruncatedId (Package? p, string verb)
    {
        if (p is null || !p.IsTruncated)
        {
            return false;
        }

        _state.StatusMessage = $"Cannot {verb}: id was truncated by winget — pick the same package from another view (e.g. Installed) for the full id.";
        _state.StatusIsError = true;
        RefreshStatusBar ();

        return true;
    }

    private void AskInstall (Package? p, bool specificVersion)
    {
        if (p is null || App is null || GuardTruncatedId (p, "install"))
        {
            return;
        }

        if (specificVersion)
        {
            BeginVersionPick (p);
        }
        else
        {
            ConfirmAndInstall (p, null);
        }
    }

    /// <summary>
    /// Fetch the available versions, then let the user pick one (real list from the backend) and
    /// continue to the install confirm. Falls back to the free-text prompt when the backend can't
    /// enumerate versions (e.g. the CLI backend returns an empty list).
    /// </summary>
    private void BeginVersionPick (Package p)
    {
        FetchThen (
            "Loading versions…",
            ct => _state.Backend.ListVersionsAsync (p.Id, ct),
            versions =>
            {
                string? chosen = versions.Count > 0 ? PickVersion (p, versions) : PromptForVersion (p);

                if (!string.IsNullOrEmpty (chosen))
                {
                    ConfirmAndInstall (p, chosen);
                }
            });
    }

    /// <summary>
    /// Fetch the applicable-installer preview, show it in the confirm dialog
    /// (e.g. "Install X? \n MSI · x64 · machine · admin"), then install on confirm.
    /// </summary>
    private void ConfirmAndInstall (Package p, string? version, InstallSettings? settings = null)
    {
        FetchThen (
            "Checking installer…",
            ct => _state.Backend.GetInstallerPreviewAsync (p.Id, version, ct),
            preview =>
            {
                string title = version is null ? $"Install {p.Name}?" : $"Install {p.Name} {version}?";
                List<string> lines = [];

                if (!string.IsNullOrEmpty (preview?.Summary))
                {
                    lines.Add (preview!.Summary);
                }

                string optionsLine = settings is null ? string.Empty : DescribeSettings (settings);

                if (optionsLine.Length > 0)
                {
                    lines.Add (optionsLine);
                }

                string body = lines.Count == 0 ? title : $"{title}\n\n{string.Join ("\n", lines)}";

                if (Confirm ("Install", body))
                {
                    string activity = version is null ? $"Installing {p.Name}" : $"Installing {p.Name} {version}";
                    RunOperation (activity, (prog, ct) => _state.Backend.InstallAsync (p.Id, version, settings, prog, ct));
                }
            });
    }

    private static string DescribeSettings (InstallSettings s)
    {
        List<string> parts = [];

        if (s.Scope != InstallScopePref.Default)
        {
            parts.Add (s.Scope == InstallScopePref.Machine ? "machine" : "user");
        }

        if (s.Mode != InstallModePref.Default)
        {
            parts.Add (s.Mode.ToString ().ToLowerInvariant ());
        }

        if (s.Architecture != InstallArchPref.Default)
        {
            parts.Add (s.Architecture.ToString ().ToLowerInvariant ());
        }

        if (!string.IsNullOrWhiteSpace (s.CustomArgs))
        {
            parts.Add ($"custom: {s.CustomArgs}");
        }

        return parts.Count == 0 ? string.Empty : "Options: " + string.Join (" · ", parts);
    }

    /// <summary>Fetch the installer to disk without installing, reusing the operation progress bar.</summary>
    private void AskDownload (Package? p)
    {
        if (p is null || App is null || GuardTruncatedId (p, "download"))
        {
            return;
        }

        if (!Confirm ("Download", $"Download the installer for {p.Name} without installing it?"))
        {
            return;
        }

        RunOperation ($"Downloading {p.Name}", (prog, ct) => _state.Backend.DownloadAsync (p.Id, null, prog, ct));
    }

    /// <summary>Open the advanced-options panel, then install the latest version with those options.</summary>
    private void AskAdvancedInstall (Package? p)
    {
        if (p is null || App is null || GuardTruncatedId (p, "install"))
        {
            return;
        }

        InstallSettings? settings = PromptAdvancedOptions (p);

        if (settings is null)
        {
            return; // cancelled
        }

        // All-default selection means "backend defaults" — normalize to null so it behaves
        // identically to a plain install on every backend (no per-backend "Default" ambiguity).
        ConfirmAndInstall (p, null, settings.IsDefault ? null : settings);
    }

    /// <summary>Run CheckInstalledStatus on a package and report whether its install is intact.</summary>
    private void AskVerify (Package? p)
    {
        if (p is null || App is null || GuardTruncatedId (p, "verify"))
        {
            return;
        }

        FetchThen (
            $"Verifying {p.Name}…",
            ct => _state.Backend.VerifyInstalledAsync (p.Id, ct),
            verification =>
            {
                if (verification is null)
                {
                    _state.StatusMessage = "Verify is only available on the COM backend.";
                    _state.StatusIsError = false;
                    RefreshStatusBar ();

                    return;
                }

                ShowVerifyResult (p, verification);
            });
    }

    private void ShowVerifyResult (Package p, InstallVerification v)
    {
        if (App is null)
        {
            return;
        }

        StringBuilder sb = new ();
        sb.AppendLine (v.Summary);

        int shown = 0;

        foreach (VerifyCheck c in v.Checks)
        {
            if (shown++ >= 12)
            {
                sb.AppendLine ("…");

                break;
            }

            string detail = string.IsNullOrEmpty (c.Detail) ? string.Empty : $" — {c.Detail}";
            sb.AppendLine ($"{(c.Ok ? "✓" : "✗")} {c.Label}{detail}");
        }

        string body = sb.ToString ().TrimEnd ();

        // When the install is damaged, offer to repair it right from the result dialog. Choosing
        // Repair runs it directly (no second confirm — clicking Repair here is the confirmation,
        // and this path is COM-only since Verify is). Other outcomes are informational only.
        if (v.Outcome == VerifyOutcome.Issues)
        {
            if (MessageBox.Query (App, $"Verify: {p.Name}", body, "_Repair", "_Close") == 0)
            {
                RunRepair (p);
            }

            return;
        }

        MessageBox.Query (App, $"Verify: {p.Name}", body, "_OK");
    }

    /// <summary>
    /// Repair an installed package (re-run the installer in repair mode). Gated on the backend
    /// supporting repair — degrades to a neutral message like Verify does on the CLI backend.
    /// </summary>
    private void AskRepair (Package? p)
    {
        if (p is null || App is null)
        {
            return;
        }

        if (!_state.Backend.CanRepair)
        {
            _state.StatusMessage = "Repair is only available on the COM backend.";
            _state.StatusIsError = false;
            RefreshStatusBar ();

            return;
        }

        if (!Confirm ("Repair", $"Repair {p.Name}? This re-runs the installer's repair to fix a damaged install."))
        {
            return;
        }

        RunRepair (p);
    }

    /// <summary>
    /// Execute the repair (guard + run). Shared by the standalone action (which confirms first via
    /// <see cref="AskRepair"/>) and the Verify→Repair offer (which treats the button click as the confirm).
    /// </summary>
    private void RunRepair (Package p)
    {
        if (App is null || GuardTruncatedId (p, "repair"))
        {
            return;
        }

        RunOperation ($"Repairing {p.Name}", (prog, ct) => _state.Backend.RepairAsync (p.Id, prog, ct));
    }

    private InstallSettings? PromptAdvancedOptions (Package p)
    {
        if (App is null)
        {
            return null;
        }

        AdvancedInstallDialog dlg = new (p.Name);
        App.Run (dlg);
        InstallSettings? result = dlg.Result;
        dlg.Dispose ();

        return result;
    }

    /// <summary>
    /// Run a short async fetch on a background thread (with a transient status), then invoke the
    /// continuation on the UI thread. Used to gather version/installer info before showing a modal
    /// dialog without blocking the UI. Skipped if an operation is already in flight.
    /// </summary>
    private void FetchThen<T> (string activity, Func<CancellationToken, Task<T>> fetch, Action<T> onResult)
    {
        // Serialize preflight fetches and don't start one atop a running operation: prevents a
        // rapid double-trigger from queuing a second modal behind the first.
        if (_opCts is not null || _preflightBusy)
        {
            return;
        }

        _preflightBusy = true;
        _state.StatusMessage = activity;
        _state.Loading = true;
        _state.StatusIsError = false;
        RefreshStatusBar ();
        CancellationToken ct = _detailCts.Token;

        Task.Run (async () =>
                  {
                      T result;

                      try
                      {
                          result = await fetch (ct);
                      }
                      catch (OperationCanceledException) when (ct.IsCancellationRequested)
                      {
                          App?.Invoke (() =>
                                       {
                                           _preflightBusy = false;
                                           _state.Loading = false;
                                           _state.StatusMessage = string.Empty;
                                           _state.StatusIsError = false;
                                           RefreshStatusBar ();
                                       });

                          return;
                      }
                      catch (Exception ex)
                      {
                          App?.Invoke (() =>
                                       {
                                           _preflightBusy = false;
                                           _state.Loading = false;
                                           _state.StatusMessage = $"Error: {ex.Message}";
                                           _state.StatusIsError = true;
                                           RefreshStatusBar ();
                                       });

                          return;
                      }

                      if (ct.IsCancellationRequested)
                      {
                          App?.Invoke (() =>
                                       {
                                           _preflightBusy = false;
                                           _state.Loading = false;
                                           _state.StatusMessage = string.Empty;
                                           _state.StatusIsError = false;
                                           RefreshStatusBar ();
                                       });

                          return;
                      }

                      App?.Invoke (() =>
                                   {
                                       if (ct.IsCancellationRequested)
                                       {
                                           _preflightBusy = false;
                                           _state.Loading = false;
                                           _state.StatusMessage = string.Empty;
                                           _state.StatusIsError = false;
                                           RefreshStatusBar ();

                                           return;
                                       }

                                       // Clear the gate before onResult so its (modal) flow — and any
                                       // RunOperation it starts — isn't blocked by this guard.
                                       _preflightBusy = false;
                                       _state.Loading = false;
                                       _state.StatusMessage = string.Empty;
                                       RefreshStatusBar ();
                                       onResult (result);
                                   });
                  });
    }

    private string? PickVersion (Package p, IReadOnlyList<string> versions)
    {
        if (App is null)
        {
            return null;
        }

        VersionPickerDialog dlg = new (p.Name, versions);
        App.Run (dlg);
        string? value = dlg.Result;
        dlg.Dispose ();

        return value;
    }

    private void AskUpgrade (Package? p)
    {
        if (p is null || App is null || GuardTruncatedId (p, "upgrade"))
        {
            return;
        }

        if (!Confirm ("Upgrade", $"Upgrade {p.Name}?"))
        {
            return;
        }

        RunOperation ($"Upgrading {p.Name}", (prog, ct) => _state.Backend.UpgradeAsync (p.Id, prog, ct));
    }

    private void AskUninstall (Package? p)
    {
        if (p is null || App is null || GuardTruncatedId (p, "uninstall"))
        {
            return;
        }

        if (!Confirm ("Uninstall", $"Uninstall {p.Name}? This cannot be undone."))
        {
            return;
        }

        RunOperation ($"Uninstalling {p.Name}", (prog, ct) => _state.Backend.UninstallAsync (p.Id, prog, ct));
    }

    private void TogglePin (Package? p)
    {
        if (p is null || App is null || GuardTruncatedId (p, "pin"))
        {
            return;
        }

        bool pinned = p.PinState.IsPinned;
        string label = pinned ? "Unpin" : "Pin";

        if (!Confirm (label, $"{label} {p.Name}?"))
        {
            return;
        }

        RunOperation ($"{label}ning {p.Name}", (_, ct) => pinned
                                                              ? _state.Backend.UnpinAsync (p.Id, ct)
                                                              : _state.Backend.PinAsync (p.Id, ct));
    }

    private void ToggleBatchSelect (Package? p)
    {
        if (p is null)
        {
            return;
        }

        if (!_state.BatchSelected.Add (p.Id))
        {
            _state.BatchSelected.Remove (p.Id);
        }

        RefreshTable ();
    }

    private void ToggleSelectAll ()
    {
        if (_state.BatchSelected.Count == _state.Filtered.Count)
        {
            _state.BatchSelected.Clear ();
        }
        else
        {
            foreach (Package p in _state.Filtered)
            {
                _state.BatchSelected.Add (p.Id);
            }
        }

        RefreshTable ();
    }

    private void AskBatchUpgrade ()
    {
        if (_state.BatchSelected.Count == 0 || App is null)
        {
            return;
        }

        if (!Confirm ("Batch Upgrade", $"Upgrade {_state.BatchSelected.Count} selected packages?"))
        {
            return;
        }

        // Share the single-operation gate so Esc cancels the batch (the in-flight item aborts via
        // its token; the loop then stops on the next iteration). Refuse to start atop another op.
        if (_opCts is not null)
        {
            return;
        }

        _opCts = new ();
        CancellationToken ct = _opCts.Token;
        string [] ids = [.. _state.BatchSelected];

        Task.Run (async () =>
                  {
                      bool cancelled = false;

                      foreach (string id in ids)
                      {
                          if (ct.IsCancellationRequested)
                          {
                              cancelled = true;

                              break;
                          }

                          App?.Invoke (() =>
                                       {
                                           _state.StatusMessage = $"Upgrading {id}… · Esc to cancel";
                                           _state.Loading = true;
                                           RefreshStatusBar ();
                                       });

                          OpResult result;

                          try
                          {
                              // Per-item progress would fight the batch loop's own status line; the
                              // loop reports "Upgrading {id}…" per package instead.
                              result = await _state.Backend.UpgradeAsync (id, null, ct);
                          }
                          catch (OperationCanceledException)
                          {
                              cancelled = true;

                              break;
                          }
                          catch (Exception ex)
                          {
                              result = new ()
                              {
                                  Operation = new () { Kind = OperationKind.Upgrade, PackageId = id },
                                  Success = false,
                                  Message = ex.Message
                              };
                          }

                          App?.Invoke (() =>
                                       {
                                           if (result.Success)
                                           {
                                               _state.DetailCache.Remove (id);
                                           }

                                           _state.StatusMessage = result.Success
                                                                      ? $"Upgraded {id}"
                                                                      : $"Failed: {id}";
                                           _state.StatusIsError = !result.Success;
                                           RefreshStatusBar ();
                                       });
                      }

                      App?.Invoke (() =>
                                   {
                                       _opCts?.Dispose ();
                                       _opCts = null;
                                       _state.BatchSelected.Clear ();
                                       _state.Loading = false;

                                       if (cancelled)
                                       {
                                           _state.StatusMessage = "Cancelled";
                                           _state.StatusIsError = false;
                                       }

                                       TriggerRefresh ();
                                   });
                  });
    }

    private void RunOperation (string activity, Func<IProgress<OpProgress>, CancellationToken, Task<OpResult>> op)
    {
        // One operation at a time: a second request while one is in flight is ignored, which
        // keeps _opCts (and the Esc-cancel target) unambiguous and avoids leaking a CTS.
        if (_opCts is not null)
        {
            return;
        }

        _opCts = new ();
        CancellationToken ct = _opCts.Token;

        _state.StatusMessage = $"{activity} · Esc to cancel";
        _state.Loading = true;
        _state.StatusIsError = false;
        _state.OpProgress = null;
        RefreshStatusBar ();

        IProgress<OpProgress> progress = new UiProgress (this);

        Task.Run (async () =>
                  {
                      OpResult? result = null;
                      bool cancelled = false;

                      try
                      {
                          result = await op (progress, ct);
                      }
                      catch (OperationCanceledException)
                      {
                          cancelled = true;
                      }
                      catch (Exception ex)
                      {
                          result = new ()
                          {
                              Operation = new () { Kind = OperationKind.Install },
                              Success = false,
                              Message = ex.Message
                          };
                      }

                      App?.Invoke (() =>
                                   {
                                       _opCts?.Dispose ();
                                       _opCts = null;
                                       _state.Loading = false;
                                       _state.OpProgress = null;

                                       if (cancelled)
                                       {
                                           _state.StatusMessage = "Cancelled";
                                           _state.StatusIsError = false;
                                       }
                                       else
                                       {
                                           _state.StatusMessage = result!.Success ? "Done" : result.Message;
                                           _state.StatusIsError = !result.Success;

                                           if (result.Operation.PackageId is { } id)
                                           {
                                               _state.DetailCache.Remove (id);
                                           }
                                       }

                                       TriggerRefresh ();
                                   });
                  });
    }

    /// <summary>
    /// Apply a backend progress sample to the status bar. Runs on the UI thread (marshaled by
    /// <see cref="UiProgress"/>). Ignored once the operation has settled so a late report can't
    /// resurrect the progress bar after the final "Done".
    /// </summary>
    private void OnOpProgress (OpProgress value)
    {
        // Gate on the operation CTS, not _state.Loading: Loading is also toggled by ordinary
        // list/detail refreshes, so a concurrent refresh could otherwise drop op samples or let
        // a late report through after the op settled. _opCts is non-null iff an op is in flight
        // and is cleared before the final refresh.
        if (_opCts is null)
        {
            return;
        }

        _state.OpProgress = value;
        RefreshStatusBar ();
    }

    private void ReportProgress (OpProgress value) => App?.Invoke (() => OnOpProgress (value));

    /// <summary>
    /// <see cref="IProgress{T}"/> bridge that marshals backend progress (raised on a background
    /// or COM thread) onto the Terminal.Gui UI thread before touching view state.
    /// </summary>
    private sealed class UiProgress (App owner) : IProgress<OpProgress>
    {
        public void Report (OpProgress value) => owner.ReportProgress (value);
    }

    private bool Confirm (string title, string message)
    {
        if (App is null)
        {
            return false;
        }

        int? result = MessageBox.Query (App, title, message, "_Yes", "_No");

        return result == 0;
    }

    private string? PromptForVersion (Package p)
    {
        if (App is null)
        {
            return null;
        }

        VersionInputDialog dlg = new (p.Name);
        App.Run (dlg);
        string? value = dlg.Result as string;
        dlg.Dispose ();

        return value;
    }

    private void ShowHelp ()
    {
        if (App is null)
        {
            return;
        }

        HelpDialog dlg = new (_state.BackendDescription);
        App.Run (dlg);
        dlg.Dispose ();
    }

    private void ExportCsv ()
    {
        try
        {
            string path = Path.Combine (Environment.CurrentDirectory, "winget-tui-export.csv");
            using StreamWriter sw = new (path);
            sw.WriteLine ("Name,Id,Version,Available,Source");

            foreach (Package p in _state.Filtered)
            {
                sw.WriteLine ($"\"{EscapeCsvCell (p.Name)}\",\"{EscapeCsvCell (p.Id)}\",\"{EscapeCsvCell (p.Version)}\",\"{EscapeCsvCell (p.AvailableVersion ?? string.Empty)}\",\"{EscapeCsvCell (p.Source)}\"");
            }

            _state.StatusMessage = $"Exported {_state.Filtered.Count} rows to {path}";
            _state.StatusIsError = false;
        }
        catch (Exception ex)
        {
            _state.StatusMessage = $"Export failed: {ex.Message}";
            _state.StatusIsError = true;
        }

        RefreshStatusBar ();
    }

    private void OpenUrl (string? url)
    {
        if (string.IsNullOrWhiteSpace (url))
        {
            _state.StatusMessage = "No URL available";
            _state.StatusIsError = false;
            RefreshStatusBar ();

            return;
        }

        if (!TryNormalizeOpenableUrl (url, out string normalizedUrl))
        {
            _state.StatusMessage = "Blocked non-http(s) URL";
            _state.StatusIsError = true;
            RefreshStatusBar ();

            return;
        }

        try
        {
            ProcessStartInfo psi = new (normalizedUrl) { UseShellExecute = true };
            Process.Start (psi);
            _state.StatusMessage = $"Opened {normalizedUrl}";
            _state.StatusIsError = false;
        }
        catch (Exception ex)
        {
            _state.StatusMessage = $"Open failed: {ex.Message}";
            _state.StatusIsError = true;
        }

        RefreshStatusBar ();
    }

    internal static string EscapeCsvCell (string value)
    {
        string escaped = LooksLikeCsvFormula (value) ? "'" + value : value;

        return escaped.Replace ("\"", "\"\"");
    }

    internal static bool TryNormalizeOpenableUrl (string? url, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;

        if (string.IsNullOrWhiteSpace (url))
        {
            return false;
        }

        if (!Uri.TryCreate (url.Trim (), UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        normalizedUrl = parsed.AbsoluteUri;

        return true;
    }

    private static bool LooksLikeCsvFormula (string value)
    {
        if (string.IsNullOrEmpty (value))
        {
            return false;
        }

        string trimmed = value.TrimStart ();

        return trimmed.Length > 0 && trimmed [0] is '=' or '+' or '-' or '@';
    }

    private sealed record EmptyRow ();

    /// <summary>
    /// Wraps an <see cref="EnumerableTableSource{Package}"/> and prepends a column-0 cursor
    /// marker (<c>●</c> on the cursor row, blank otherwise). A redundant visual cue alongside
    /// the row-highlight colors — useful when terminal themes mute the highlight, and for
    /// color-blind accessibility.
    ///
    /// Nested here because it has no consumer outside <see cref="App"/>; pulling
    /// it out as a public top-level type would just clutter the public surface.
    /// </summary>
    private sealed class MarkedTableSource : IEnumerableTableSource<Package>
    {
        private readonly EnumerableTableSource<Package> _inner;
        private readonly string [] _columns;

        public MarkedTableSource (EnumerableTableSource<Package> inner)
        {
            _inner = inner;
            _columns = new [] { " " }.Concat (inner.ColumnNames).ToArray ();
        }

        public int CursorRow { get; set; } = -1;

        public object this [int row, int col]
            => col == 0
                   ? (row == CursorRow ? "●" : " ")
                   : _inner [row, col - 1];

        public int Rows => _inner.Rows;
        public int Columns => _columns.Length;
        public string [] ColumnNames => _columns;
        public IEnumerable<Package> GetAllObjects () => _inner.GetAllObjects ();
        public Package GetObjectOnRow (int row) => _inner.GetObjectOnRow (row);
    }
}
