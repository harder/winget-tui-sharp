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
    private readonly FrameView _listFrame;
    private readonly SortableTableView _packageTable;
    private readonly DetailPanel _detailPanel;
    private readonly StatusBar _statusBar;
    private readonly Label _searchHint;
    private readonly Label _backendLabel;
    private readonly BackgroundTaskTracker _background = new ();
    private readonly ExportWorkflowState _exportWorkflow = new ();
    private readonly StatusOwnership _statusOwnership = new ();
    private readonly ForegroundWorkflowCoordinator _foreground = new ();
    private readonly object _progressGate = new ();
    private readonly TimeSpan? _smokeDelay;
    private CancellationTokenSource _viewCts;
    private CancellationTokenSource _detailCts;

    // Non-null only while an install/upgrade/uninstall (or batch) is in flight. Doubles as the
    // "an operation is running" gate for Esc-to-cancel — distinct from _viewCts/_detailCts, which
    // cover list/detail refreshes that already cancel implicitly on navigation.
    private CancellationTokenSource? _opCts;
    private CancellationTokenSource? _preflightCts;

    private object? _spinnerTimer;
    private object? _smokeTimer;
    private bool _initialLoadDone;
    private int _shutdownStarted;
    private int _uiAccepting = 1;
    private PendingProgress? _pendingProgress;
    private bool _progressDispatcherRunning;

    public App (IBackend backend, TimeSpan? smokeDelay = null)
    {
        _state = new (backend);
        _smokeDelay = smokeDelay;
        _viewCts = CreateLifetimeLinkedSource ();
        _detailCts = CreateLifetimeLinkedSource ();
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

        // Reflow column widths when the table is resized (e.g. terminal resize) so the Available
        // column stays visible on a narrow window instead of being pushed off the right edge.
        _packageTable.ViewportChanged += (_, _) => ApplyColumnWidths ();
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

        WireEvents ();
        RefreshTable ();
        RefreshStatusBar ();
    }

    private void WireEvents ()
    {
        _tabBar.TabClicked += (_, mode) => SwitchToMode (mode);

        _packageTable.ValueChanged += (_, _) => OnSelectedRowChanged ();
        _packageTable.HeaderClicked += OnHeaderClicked;

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

            if (_smokeDelay is { } delay && App is { } app)
            {
                _smokeTimer = app.AddTimeout (delay, () =>
                                                       {
                                                           _smokeTimer = null;
                                                           RequestGracefulStop ();

                                                           return false;
                                                       });
            }
        }
        else if (!newIsRunning)
        {
            BeginShutdown ();
            StopSpinner ();
        }
    }

    /// <summary>
    /// Stops admission, cancels every application-owned lifetime, and waits a bounded amount of
    /// time for admitted work. Safe to call repeatedly.
    /// </summary>
    public async Task<bool> ShutdownAsync (TimeSpan timeout)
    {
        BeginShutdown ();
        bool drained = await _background.DrainAsync (timeout).ConfigureAwait (false);

        if (drained)
        {
            DisposeCancellationSources ();
            _background.Dispose ();
        }

        return drained;
    }

    public IReadOnlyList<Exception> BackgroundFailures => _background.Failures;
    public int DroppedBackgroundFailureCount => _background.DroppedFailureCount;

    private void BeginShutdown ()
    {
        if (Interlocked.Exchange (ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        Volatile.Write (ref _uiAccepting, 0);
        _background.BeginStop ();
        _foreground.Stop ();
        _statusOwnership.Clear ();
        CancelSource (_viewCts);
        CancelSource (_detailCts);
        CancelSource (Volatile.Read (ref _preflightCts));
        CancelSource (Volatile.Read (ref _opCts));
        _exportWorkflow.CancelActive ();

        if (_smokeTimer is not null && App is { } app)
        {
            app.RemoveTimeout (_smokeTimer);
            _smokeTimer = null;
        }
    }

    private void RequestGracefulStop ()
    {
        BeginShutdown ();
        RequestStop ();
    }

    private CancellationTokenSource CreateLifetimeLinkedSource () =>
        CancellationTokenSource.CreateLinkedTokenSource (_background.LifetimeToken);

    private bool TryOwnOperationRequest (CancellationTokenSource request) =>
        Interlocked.CompareExchange (ref _opCts, request, null) is null;

    private bool ReleaseOperationRequest (CancellationTokenSource request) =>
        ReferenceEquals (Interlocked.CompareExchange (ref _opCts, null, request), request);

    private bool OperationRequestIsCurrent (CancellationTokenSource request) =>
        ReferenceEquals (Volatile.Read (ref _opCts), request);

    private CancellationTokenSource ReplaceLifetimeLinkedSource (ref CancellationTokenSource source)
    {
        CancellationTokenSource replacement = CreateLifetimeLinkedSource ();
        CancellationTokenSource previous = source;
        source = replacement;
        CancelSource (previous);
        previous.Dispose ();

        return replacement;
    }

    private static void CancelSource (CancellationTokenSource? source)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel ();
        }
        catch (ObjectDisposedException)
        {
            // A worker may have completed and disposed its request source concurrently.
        }
        catch
        {
            // A third-party cancellation callback must not prevent the remaining shutdown work.
        }
    }

    private void DisposeCancellationSources ()
    {
        _viewCts.Dispose ();
        _detailCts.Dispose ();
        Interlocked.Exchange (ref _preflightCts, null)?.Dispose ();
        Interlocked.Exchange (ref _opCts, null)?.Dispose ();
        _exportWorkflow.Dispose ();
    }

    /// <summary>
    /// Queues a UI callback only while both the application lifetime and request are current.
    /// Cancellation settles the returned task even if Terminal.Gui never runs its queued timeout.
    /// </summary>
    private async Task<bool> DispatchAsync (
        Action action,
        CancellationToken requestToken,
        Func<bool>? isCurrent = null)
    {
        bool CanQueue () => UiCallbackCanQueue (
            Volatile.Read (ref _uiAccepting) != 0,
            _background.LifetimeToken,
            requestToken);
        bool CanExecute () => UiCallbackCanRun (
            Volatile.Read (ref _uiAccepting) != 0,
            _background.LifetimeToken,
            requestToken,
            isCurrent);

        if (!CanQueue () || App is not { } app)
        {
            return false;
        }

        TaskCompletionSource<bool> completion = new (TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource (
            _background.LifetimeToken,
            requestToken);
        using CancellationTokenRegistration registration = cancellation.Token.Register (
            () => completion.TrySetResult (false));

        try
        {
            app.Invoke (() =>
                        {
                            if (!CanExecute ())
                            {
                                completion.TrySetResult (false);

                                return;
                            }

                            try
                            {
                                action ();
                                completion.TrySetResult (true);
                            }
                            catch (Exception ex)
                            {
                                completion.TrySetException (ex);
                            }
                        });
        }
        catch (Exception) when (!CanQueue ())
        {
            completion.TrySetResult (false);
        }

        return await completion.Task.ConfigureAwait (false);
    }

    internal static bool UiCallbackCanRun (
        bool accepting,
        CancellationToken lifetimeToken,
        CancellationToken requestToken,
        Func<bool>? isCurrent = null) =>
        UiCallbackCanQueue (accepting, lifetimeToken, requestToken)
        && (isCurrent?.Invoke () ?? true);

    internal static bool UiCallbackCanQueue (
        bool accepting,
        CancellationToken lifetimeToken,
        CancellationToken requestToken) =>
        accepting
        && !lifetimeToken.IsCancellationRequested
        && !requestToken.IsCancellationRequested;

    internal static bool PreflightIdentityMatches (object? currentRequest, object request) =>
        ReferenceEquals (currentRequest, request);

    internal static bool PreflightOwnsActivity (string currentStatus, string activity) =>
        string.Equals (currentStatus, activity, StringComparison.Ordinal);

    private bool SetStatus (string message, bool isError = false, StatusOwner owner = StatusOwner.Ambient) =>
        _statusOwnership.TryWrite (owner, message, isError, WriteStatus);

    private void WriteStatus (string message, bool isError)
    {
        _state.StatusMessage = message;
        _state.StatusIsError = isError;
    }

    private bool CompleteOperationStatus (
        ForegroundAdmission admission,
        string outcome,
        bool isError) =>
        _statusOwnership.CompleteOperation (admission.Id, outcome, isError, WriteStatus);

    private void ReportRejectedBackgroundAdmission ()
    {
        if (Volatile.Read (ref _uiAccepting) == 0 || _background.LifetimeToken.IsCancellationRequested)
        {
            return;
        }

        SetStatus ("Too many background requests are still pending; wait and try again", isError: true);
        RefreshStatusBar ();
    }

    /// <summary>
    /// Resolve which backend is live + its winget version once at startup and show it in the
    /// header badge. Best-effort: a failure just leaves the badge empty (the app works regardless).
    /// </summary>
    private void LoadBackendDescription ()
    {
        _background.TryRun (async lifetimeToken =>
                            {
                                string description;

                                try
                                {
                                    description = await _state.Backend.DescribeAsync (lifetimeToken);
                                }
                                catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
                                {
                                    return;
                                }
                                catch
                                {
                                    return;
                                }

                                await DispatchAsync (() =>
                                                     {
                                                         _state.BackendDescription = description;
                                                         _backendLabel.Text = description;
                                                         _backendLabel.SetNeedsLayout ();
                                                         _backendLabel.SetNeedsDraw ();
                                                     }, lifetimeToken);
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
        _background.TryRun (async lifetimeToken =>
                            {
                                IReadOnlyList<string> sources;

                                try
                                {
                                    sources = await _state.Backend.ListSourcesAsync (lifetimeToken);
                                }
                                catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
                                {
                                    return;
                                }
                                catch
                                {
                                    return;
                                }

                                if (sources.Count == 0)
                                {
                                    return;
                                }

                                await DispatchAsync (() =>
                                                     {
                                                         _state.AvailableSources = sources;

                                                         if (_state.SourceFilter is { } current
                                                             && !sources.Any (s => string.Equals (s, current, StringComparison.OrdinalIgnoreCase)))
                                                         {
                                                             _state.SourceFilter = null;
                                                             RefreshStatusBar ();
                                                         }
                                                     }, lifetimeToken);
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
                                                                            if (Volatile.Read (ref _uiAccepting) == 0)
                                                                            {
                                                                                _spinnerTimer = null;

                                                                                return false;
                                                                            }

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

    // keepMessage: when a list reload is triggered right after an operation, pass the op's result
    // line (e.g. "Done", "Uninstalled X") so the reload keeps showing it — with the spinner as a
    // "refreshing" cue — instead of overwriting it with "Loading Installed…" (the slow reload would
    // otherwise mask the brief result). Null on a normal refresh, which shows the usual messages.
    private void TriggerRefresh (string? keepMessage = null)
    {
        CancellationTokenSource request = ReplaceLifetimeLinkedSource (ref _viewCts);
        CancellationToken ct = request.Token;
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
            SetStatus ("Press / to search for packages");
            RefreshTable ();
            RefreshStatusBar ();
            SyncTabBar ();

            return;
        }

        IDisposable loading = _state.AcquireLoading ();
        SetStatus (
            keepMessage ?? $"Loading {_state.Mode}…",
            keepMessage is not null && _state.StatusIsError);
        RefreshStatusBar ();
        SyncTabBar ();

        bool admitted = _background.TryRun (async lifetimeToken =>
                                             {
                                                 try
                                                 {
                                                     IReadOnlyList<Package> packages = mode switch
                                                     {
                                                         AppMode.Search => await _state.Backend.SearchAsync (query, src, ct),
                                                         AppMode.Upgrades => await _state.Backend.ListUpgradesAsync (src, ct),
                                                         _ => await _state.Backend.ListInstalledAsync (src, ct)
                                                     };

                                                     IReadOnlyDictionary<string, PinState>? pins = null;
                                                     Exception? pinFailure = null;

                                                     try
                                                     {
                                                         pins = await _state.Backend.ListPinsAsync (ct);
                                                     }
                                                     catch (OperationCanceledException) when (ct.IsCancellationRequested)
                                                     {
                                                         throw;
                                                     }
                                                     catch (Exception ex)
                                                     {
                                                         pinFailure = ex;
                                                     }

                                                     await DispatchAsync (() =>
                                                                          {
                                                                              _state.Packages = packages.ToList ();

                                                                              bool pinsComplete = pins is not null
                                                                                                  && _state.RecordPinSnapshot (pins);

                                                                              if (pinsComplete)
                                                                              {
                                                                                  _state.ApplyPinSnapshot (_state.Packages);
                                                                              }
                                                                              else
                                                                              {
                                                                                  // Unknown is not unpinned. Preserve each backend row's state
                                                                                  // and retain (but stale-mark) the last successful snapshot.
                                                                                  _state.MarkPinsStale ();
                                                                              }

                                                                              _state.ApplyFilter ();
                                                                              loading.Dispose ();

                                                                              // Keep the op's result line visible after the reload rather than replacing it
                                                                              // with a package count, so the user sees what just happened.
                                                                              if (keepMessage is null)
                                                                              {
                                                                                  int n = _state.Filtered.Count;
                                                                                  string status = n == 1 ? "1 package" : $"{n} packages";

                                                                                  if (mode == AppMode.Search && packages.Count >= AppState.SearchResultLimit)
                                                                                  {
                                                                                      status = $"{AppState.SearchResultLimit}+ matches — refine your search to narrow";
                                                                                  }

                                                                                  SetStatus (status);
                                                                              }

                                                                              if (!pinsComplete)
                                                                              {
                                                                                  string reason = pinFailure?.Message
                                                                                                  ?? "the pin snapshot exceeded its safety limits";
                                                                                  string warning =
                                                                                      $"Pin status unavailable; pin actions and filtering are disabled: {reason}";
                                                                                  SetStatus (
                                                                                      keepMessage is null ? warning : $"{keepMessage} · {warning}",
                                                                                      isError: true);
                                                                              }

                                                                              RefreshTable ();
                                                                              RefreshStatusBar ();
                                                                              RestoreCursorOrSelectFirst (previousSelectedId);
                                                                          }, ct, () => gen == _state.ViewGeneration);
                                                 }
                                                 catch (OperationCanceledException) when (ct.IsCancellationRequested)
                                                 {
                                                     // Superseded view or application shutdown.
                                                 }
                                                 catch (Exception ex)
                                                 {
                                                     string msg = $"Error: {ex.Message}";
                                                     await DispatchAsync (() =>
                                                                          {
                                                                              loading.Dispose ();
                                                                              SetStatus (msg, isError: true);
                                                                              RefreshStatusBar ();
                                                                          }, ct, () => gen == _state.ViewGeneration);
                                                 }
                                                 finally
                                                 {
                                                     loading.Dispose ();
                                                     await DispatchAsync (RefreshStatusBar, lifetimeToken);
                                                 }
                                             });

        if (!admitted)
        {
            loading.Dispose ();
            ReportRejectedBackgroundAdmission ();
        }
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

            // Render a single message row explaining *why* the list is empty, instead of a bare
            // headered table. The message is contextual: "All packages are up to date!" vs. a
            // filter/pin-specific note. Mirrors upstream winget-tui's empty-state messages (#228).
            _packageTable.Table = new EnumerableTableSource<string> ([EmptyStateMessage (_state)], new ()
            {
                { " ", message => message }
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

            if (marked.ColumnNames [i] == "Source")
            {
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

        // Pin per-column widths (MinWidth = MaxWidth). Otherwise TableView's CalculateMaxCellWidth
        // recomputes widths every frame from the visible rows' content, so scrolling jumps columns
        // around. Width depends on the table's current size, so re-run on resize (ViewportChanged).
        ApplyColumnWidths (force: true);
    }

    private int _lastColumnLayoutWidth = -1;

    /// <summary>
    /// Sizes the data columns to the table's current width. Name/Id/Version shrink toward minimums
    /// when the terminal is narrow so the <b>Available</b> column stays visible — it sits just
    /// before the expanding Source column and is otherwise the first to be pushed off-screen, so a
    /// user on a small window can't tell it exists. Wired to ViewportChanged to reflow on resize.
    /// </summary>
    private void ApplyColumnWidths (bool force = false)
    {
        ITableSource? table = _packageTable.Table;

        if (table is null)
        {
            return;
        }

        int avail = _packageTable.Viewport.Width;

        if (avail <= 0 || (!force && avail == _lastColumnLayoutWidth))
        {
            return;
        }

        _lastColumnLayoutWidth = avail;

        string [] names = table.ColumnNames;

        // Preferred widths, and how far each may shrink. Available/Source are not shrunk so they
        // survive; Source is the ExpandLastColumn target and fills whatever remains.
        int nameW = 24, idW = 28, verW = 14;
        const int availW = 14, sourceW = 8, srcReserve = 6;
        const int nameMin = 14, idMin = 16, verMin = 9;

        bool hasAvailable = names.Contains ("Available");
        int dataCols = Math.Max (0, names.Length - 1); // exclude the 1-wide marker column

        // Reserve the marker, rough inter-column padding, and a minimum for the expanding Source
        // column, then shrink Id → Name → Version (in that order) to fit Name+Id+Version+Available.
        int budget = avail - 1 - (dataCols + 1) - srcReserve;
        int deficit = nameW + idW + verW + (hasAvailable ? availW : 0) - budget;

        if (deficit > 0) { int c = Math.Min (deficit, idW - idMin); idW -= c; deficit -= c; }
        if (deficit > 0) { int c = Math.Min (deficit, nameW - nameMin); nameW -= c; deficit -= c; }
        if (deficit > 0) { int c = Math.Min (deficit, verW - verMin); verW -= c; deficit -= c; }

        for (int i = 1; i < names.Length; i++)
        {
            string name = names [i];

            int? w = name.StartsWith ("Name", StringComparison.Ordinal) ? nameW
                   : name.StartsWith ("Id", StringComparison.Ordinal) ? idW
                   : name.StartsWith ("Version", StringComparison.Ordinal) ? verW
                   : name == "Available" ? availW
                   : name == "Source" ? sourceW
                   : null;

            if (w is null)
            {
                continue;
            }

            ColumnStyle s = _packageTable.Style.GetOrCreateColumnStyle (i);
            s.MinWidth = w.Value;
            s.MaxWidth = w.Value;
        }

        _packageTable.SetNeedsDraw ();
    }

    private string HeaderWithSort (string label, SortField field)
    {
        if (_state.SortField != field)
        {
            return label;
        }

        return label + (_state.SortDir == SortDir.Asc ? " ↑" : " ↓");
    }

    /// <summary>
    /// Contextual message shown in the list when nothing matches. Distinguishes "up to date" from
    /// a filter/pin that's hiding rows, so the user isn't misled. Mirrors upstream winget-tui's
    /// draw_package_list empty-state arms (#228), plus a local-filter case the port adds.
    /// </summary>
    internal static string EmptyStateMessage (AppState state)
    {
        if (!string.IsNullOrEmpty (state.LocalFilter))
        {
            return $"No packages match “{state.LocalFilter}”.";
        }

        return state.Mode switch
        {
            AppMode.Search => string.IsNullOrEmpty (state.SearchQuery)
                                  ? "Type to search for packages."
                                  : "No packages found.",
            AppMode.Upgrades when state.PinFilter == PinFilter.PinnedOnly => "No pinned packages with upgrades found.",
            AppMode.Upgrades when state.PinFilter == PinFilter.UnpinnedOnly => "No unpinned packages with upgrades found.",
            AppMode.Upgrades => "All packages are up to date!",
            _ => "No packages found."
        };
    }

    /// <summary>
    /// Maps a clicked column header to the field it sorts by, or null for non-sortable columns
    /// (the marker, Available, Source). The header text may carry a trailing sort arrow.
    /// </summary>
    internal static SortField? SortFieldForHeader (string columnName)
    {
        if (columnName.StartsWith ("Name", StringComparison.Ordinal))
        {
            return SortField.Name;
        }

        if (columnName.StartsWith ("Id", StringComparison.Ordinal))
        {
            return SortField.Id;
        }

        if (columnName.StartsWith ("Version", StringComparison.Ordinal))
        {
            return SortField.Version;
        }

        return null;
    }

    /// <summary>
    /// Sort the list when a sortable column header is clicked: first click sorts ascending, a
    /// click on the already-active column toggles direction. Mirrors upstream winget-tui's
    /// click-to-sort (commit 66d464c4). Clicks on non-sortable headers are a no-op.
    /// </summary>
    private void OnHeaderClicked (int column)
    {
        ITableSource? source = _packageTable.Table;

        if (source is null || column < 0 || column >= source.Columns)
        {
            return;
        }

        if (SortFieldForHeader (source.ColumnNames [column]) is not { } field)
        {
            return;
        }

        if (_state.SortField == field)
        {
            _state.SortDir = _state.SortDir == SortDir.Asc ? SortDir.Desc : SortDir.Asc;
        }
        else
        {
            _state.SortField = field;
            _state.SortDir = SortDir.Asc;
        }

        _state.ApplyFilter ();
        RefreshTable ();
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

        if (_state.TryGetCachedDetail (p, out PackageDetail cached))
        {
            _state.CurrentDetail = cached;
            _detailPanel.SetDetail (cached, false);
            RefreshStatusBar ();

            return;
        }

        _detailPanel.SetDetail (null, true);
        CancellationToken ct = _detailCts.Token;
        int gen = _state.BumpDetailGeneration ();
        IDisposable loading = _state.AcquireLoading (detail: true);
        RefreshStatusBar ();

        bool admitted = _background.TryRun (async lifetimeToken =>
                                             {
                                                 try
                                                 {
                                                     // Debounce: a fast scroll cancels `ct` (via CancelPendingDetailLoad on
                                                     // the next selection change) before this delay elapses, so we only fetch
                                                     // for the row the cursor settles on — not every row it passes over.
                                                     await Task.Delay (DetailLoadDebounceMs, ct);

                                                     PackageDetail? detail = await _state.Backend.ShowAsync (p.Id, ct);
                                                     await DispatchAsync (() =>
                                                                          {
                                                                              loading.Dispose ();

                                                                              // Fall back to a stub detail built from the list-row context when winget show
                                                                              // can't resolve the package (truncated id, store-only entries with no manifest,
                                                                              // packages with unusual characters in id like ".115Chrome").
                                                                              PackageDetail final = detail ?? BuildStubDetail (p);
                                                                              final.MergeContext (p);
                                                                              final.EnsureDetailHint ();
                                                                              _state.CacheDetail (p.Id, final);
                                                                              _state.CurrentDetail = final;
                                                                              _detailPanel.SetDetail (final, false);
                                                                              RefreshStatusBar ();
                                                                          }, ct,
                                                                          () => gen == _state.DetailGeneration
                                                                                && string.Equals (CurrentPackage ()?.Id, p.Id, StringComparison.OrdinalIgnoreCase));
                                                 }
                                                 catch (OperationCanceledException) when (ct.IsCancellationRequested)
                                                 {
                                                     // Superseded selection or application shutdown.
                                                 }
                                                 catch (Exception ex)
                                                 {
                                                     await DispatchAsync (() =>
                                                                          {
                                                                              loading.Dispose ();
                                                                              SetStatus ($"Detail error: {ex.Message}", isError: true);
                                                                              RefreshStatusBar ();
                                                                          }, ct,
                                                                          () => gen == _state.DetailGeneration
                                                                                && string.Equals (CurrentPackage ()?.Id, p.Id, StringComparison.OrdinalIgnoreCase));
                                                 }
                                                 finally
                                                 {
                                                     loading.Dispose ();
                                                     await DispatchAsync (RefreshStatusBar, lifetimeToken);
                                                 }
                                             });

        if (!admitted)
        {
            loading.Dispose ();
            _state.CurrentDetail = null;
            _detailPanel.SetDetail (null, false);
            ReportRejectedBackgroundAdmission ();
        }
    }

    private void CancelPendingDetailLoad ()
    {
        ReplaceLifetimeLinkedSource (ref _detailCts);
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
                if (Volatile.Read (ref _opCts) is { } opCts)
                {
                    opCts.Cancel ();
                    SetStatus ("Cancelling…", owner: StatusOwner.Operation);
                    RefreshStatusBar ();
                    key.Handled = true;

                    return;
                }

                RequestGracefulStop ();
                key.Handled = true;

                return;
            case KeyCode.Q:
                RequestGracefulStop ();
                key.Handled = true;

                return;
            case KeyCode.C | KeyCode.CtrlMask:
                RequestGracefulStop ();
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
                        if (_state.CyclePinFilter ())
                        {
                            _state.ApplyFilter ();
                            RefreshTable ();
                            RefreshStatusBar ();
                        }
                        else
                        {
                            SetStatus (
                                "Pin status is unavailable; refresh successfully before filtering pins",
                                isError: true);
                            RefreshStatusBar ();
                        }
                    }

                    key.Handled = true;

                    return;
                case '?':
                    ShowHelp ();
                    key.Handled = true;

                    return;
                case 't':
                    ShowThemePicker ();
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

        SetStatus (
            $"Cannot {verb}: id was truncated by winget — pick the same package from another view (e.g. Installed) for the full id.",
            isError: true);
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
                string? chosen = null;
                TryUseOperationReservation (
                    _foreground,
                    _ =>
                    {
                        chosen = versions.Count > 0 ? PickVersion (p, versions) : PromptForVersion (p);

                        return !string.IsNullOrEmpty (chosen);
                    });

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

                TryUseOperationReservation (
                    _foreground,
                    reservation =>
                    {
                        if (!Confirm ("Install", body))
                        {
                            return false;
                        }

                        string activity = version is null ? $"Installing {p.Name}" : $"Installing {p.Name} {version}";
                        RunOperation (
                            reservation,
                            activity,
                            (prog, ct) => _state.Backend.InstallAsync (p.Id, version, settings, prog, ct));

                        return true;
                    });
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

        TryUseOperationReservation (
            _foreground,
            reservation =>
            {
                if (!Confirm ("Download", $"Download the installer for {p.Name} without installing it?"))
                {
                    return false;
                }

                RunOperation (
                    reservation,
                    $"Downloading {p.Name}",
                    (prog, ct) => _state.Backend.DownloadAsync (p.Id, null, prog, ct));

                return true;
            });
    }

    /// <summary>Open the advanced-options panel, then install the latest version with those options.</summary>
    private void AskAdvancedInstall (Package? p)
    {
        if (p is null || App is null || GuardTruncatedId (p, "install"))
        {
            return;
        }

        InstallSettings? settings = null;
        TryUseOperationReservation (
            _foreground,
            _ =>
            {
                settings = PromptAdvancedOptions (p);

                return settings is not null;
            });

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
                    SetStatus ("Verify is only available on the COM backend.");
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
            TryUseOperationReservation (
                _foreground,
                reservation =>
                {
                    if (MessageBox.Query (App, $"Verify: {p.Name}", body, "_Repair", "_Close") != 0)
                    {
                        return false;
                    }

                    RunRepair (p, reservation);

                    return true;
                });

            return;
        }

        TryShowReservedModal (
            _foreground,
            () => MessageBox.Query (App, $"Verify: {p.Name}", body, "_OK"));
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
            SetStatus ("Repair is only available on the COM backend.");
            RefreshStatusBar ();

            return;
        }

        TryUseOperationReservation (
            _foreground,
            reservation =>
            {
                if (!Confirm ("Repair", $"Repair {p.Name}? This re-runs the installer's repair to fix a damaged install."))
                {
                    return false;
                }

                RunRepair (p, reservation);

                return true;
            });
    }

    /// <summary>
    /// Execute the repair (guard + run). Shared by the standalone action (which confirms first via
    /// <see cref="AskRepair"/>) and the Verify→Repair offer (which treats the button click as the confirm).
    /// </summary>
    private void RunRepair (Package p, OperationReservation reservation)
    {
        if (App is null || GuardTruncatedId (p, "repair"))
        {
            return;
        }

        RunOperation (
            reservation,
            $"Repairing {p.Name}",
            (prog, ct) => _state.Backend.RepairAsync (p.Id, prog, ct));
    }

    private InstallSettings? PromptAdvancedOptions (Package p)
    {
        if (App is null)
        {
            return null;
        }

        using AdvancedInstallDialog dlg = new (p.Name);
        App.Run (dlg);

        return dlg.Result;
    }

    /// <summary>
    /// Run a short async fetch on a background thread (with a transient status), then invoke the
    /// continuation on the UI thread. Used to gather version/installer info before showing a modal
    /// dialog without blocking the UI. Skipped if an operation is already in flight.
    /// </summary>
    private void FetchThen<T> (string activity, Func<CancellationToken, Task<T>> fetch, Action<T> onResult)
    {
        if (!_foreground.TryBegin (ForegroundWorkflow.Preflight, out ForegroundAdmission admission))
        {
            return;
        }

        SetStatus (activity);
        IDisposable loading = _state.AcquireLoading ();
        int loadingReleased = 0;
        void ReleaseLoading ()
        {
            if (Interlocked.Exchange (ref loadingReleased, 1) == 0)
            {
                loading.Dispose ();
            }
        }

        RefreshStatusBar ();
        CancellationTokenSource request = CreatePreflightSource (_background.LifetimeToken);

        if (Interlocked.CompareExchange (ref _preflightCts, request, null) is not null)
        {
            ReleaseLoading ();
            _foreground.Release (admission);
            request.Dispose ();
            SetStatus ("A package preflight is already active", isError: true);
            RefreshStatusBar ();

            return;
        }

        CancellationToken ct = request.Token;
        int viewGeneration = _state.ViewGeneration;
        int detailGeneration = _state.DetailGeneration;
        AppMode mode = _state.Mode;
        string? packageId = CurrentPackage ()?.Id;

        void ReleasePreflight ()
        {
            ReleaseLoading ();

            if (ReferenceEquals (Interlocked.CompareExchange (ref _preflightCts, null, request), request))
            {
                _foreground.Release (admission);
            }
        }

        bool admitted = _background.TryRun (async lifetimeToken =>
                                             {
                                                 try
                                                 {
                                                     T result = await fetch (ct);
                                                     await DispatchAsync (() =>
                                                                          {
                                                                              CompletePreflight (
                                                                                  () =>
                                                                                  {
                                                                                      ReleasePreflight ();
                                                                                      SetStatus (string.Empty);
                                                                                      RefreshStatusBar ();
                                                                                  },
                                                                                  () => onResult (result),
                                                                                  ex =>
                                                                                  {
                                                                                      SetStatus ($"Error: {ex.Message}", isError: true);
                                                                                      RefreshStatusBar ();
                                                                                  });
                                                                          }, ct,
                                                                          () => viewGeneration == _state.ViewGeneration
                                                                                && detailGeneration == _state.DetailGeneration
                                                                                && mode == _state.Mode
                                                                                && string.Equals (packageId, CurrentPackage ()?.Id, StringComparison.OrdinalIgnoreCase));
                                                 }
                                                 catch (OperationCanceledException) when (ct.IsCancellationRequested)
                                                 {
                                                     // Cleanup below uses the application lifetime rather than this
                                                     // cancelled request, so it can repaint a still-running UI.
                                                 }
                                                 catch (Exception ex)
                                                 {
                                                     await DispatchAsync (() =>
                                                                          {
                                                                              ReleasePreflight ();
                                                                              SetStatus ($"Error: {ex.Message}", isError: true);
                                                                              RefreshStatusBar ();
                                                                          }, ct,
                                                                          () => PreflightIdentityMatches (Volatile.Read (ref _preflightCts), request)
                                                                                && viewGeneration == _state.ViewGeneration
                                                                                && detailGeneration == _state.DetailGeneration);
                                                 }
                                                 finally
                                                 {
                                                     await CleanupPreflightAsync (
                                                         () => DispatchAsync (() =>
                                                                              {
                                                                                  ReleasePreflight ();

                                                                                  if (PreflightOwnsActivity (_state.StatusMessage, activity))
                                                                                  {
                                                                                      SetStatus (string.Empty);
                                                                                  }

                                                                                  RefreshStatusBar ();
                                                                              }, lifetimeToken,
                                                                              () => PreflightIdentityMatches (Volatile.Read (ref _preflightCts), request)),
                                                         ReleasePreflight,
                                                         () =>
                                                         {
                                                             CancelSource (request);
                                                             request.Dispose ();
                                                         });
                                                 }
                                             });

        if (!admitted)
        {
            ReleasePreflight ();
            CancelSource (request);
            request.Dispose ();
            ReportRejectedBackgroundAdmission ();
        }
    }

    internal static void CompletePreflight (
        Action release,
        Action onResult,
        Action<Exception> reportError)
    {
        release ();

        try
        {
            onResult ();
        }
        catch (Exception ex)
        {
            reportError (ex);
        }
    }

    internal static CancellationTokenSource CreatePreflightSource (CancellationToken lifetimeToken) =>
        CancellationTokenSource.CreateLinkedTokenSource (lifetimeToken);

    internal static async Task CleanupPreflightAsync (
        Func<Task> dispatchCleanup,
        Action release,
        Action dispose)
    {
        try
        {
            await dispatchCleanup ().ConfigureAwait (false);
        }
        finally
        {
            try
            {
                release ();
            }
            finally
            {
                dispose ();
            }
        }
    }

    internal static bool TryUseOperationReservation (
        ForegroundWorkflowCoordinator coordinator,
        Func<OperationReservation, bool> action)
    {
        if (!coordinator.TryReserveOperation (out OperationReservation? reservation))
        {
            return false;
        }

        using OperationReservation activeReservation = reservation!;

        return action (activeReservation);
    }

    internal static bool TryShowReservedModal (
        ForegroundWorkflowCoordinator coordinator,
        Action showModal) =>
        TryUseOperationReservation (
            coordinator,
            _ =>
            {
                showModal ();

                return true;
            });

    private string? PickVersion (Package p, IReadOnlyList<string> versions)
    {
        if (App is null)
        {
            return null;
        }

        using VersionPickerDialog dlg = new (p.Name, versions);
        App.Run (dlg);

        return dlg.Result;
    }

    private void AskUpgrade (Package? p)
    {
        if (p is null || App is null)
        {
            return;
        }

        // Unlike install/uninstall/pin, a truncated id doesn't block an upgrade: the CLI backend's
        // UpgradeAsync tries `--id` then falls back to `--name --exact`, so handing it the name
        // resolves the row winget truncated. Mirrors upstream winget-tui (commit fd9e9dbe).
        // Truncation only arises from the CLI tabular parse; the COM backend always has full ids.
        string query = UpgradeQueryFor (p);
        string prompt = p.IsTruncated
                            ? $"Upgrade {p.Name}? (id was truncated by winget — matching by name)"
                            : $"Upgrade {p.Name}?";

        TryUseOperationReservation (
            _foreground,
            reservation =>
            {
                if (!Confirm ("Upgrade", prompt))
                {
                    return false;
                }

                RunOperation (
                    reservation,
                    $"Upgrading {p.Name}",
                    (prog, ct) => _state.Backend.UpgradeAsync (query, prog, ct));

                return true;
            });
    }

    /// <summary>
    /// The query to hand <see cref="IBackend.UpgradeAsync"/> for a row: its id normally, but its
    /// exact name when winget truncated the id (an `--id` match against the literal `…` can't
    /// succeed; the CLI backend then resolves it via `--name --exact`).
    /// </summary>
    internal static string UpgradeQueryFor (Package p) => p.IsTruncated ? p.Name : p.Id;

    private void AskUninstall (Package? p)
    {
        if (p is null || App is null || GuardTruncatedId (p, "uninstall"))
        {
            return;
        }

        TryUseOperationReservation (
            _foreground,
            reservation =>
            {
                if (!Confirm ("Uninstall", $"Uninstall {p.Name}? This cannot be undone."))
                {
                    return false;
                }

                RunOperation (
                    reservation,
                    $"Uninstalling {p.Name}",
                    (prog, ct) => _state.Backend.UninstallAsync (p.Id, prog, ct));

                return true;
            });
    }

    private void TogglePin (Package? p)
    {
        if (p is null || App is null || GuardTruncatedId (p, "pin"))
        {
            return;
        }

        if (!_state.PinDataFresh)
        {
            SetStatus (
                "Pin status is unavailable; refresh successfully before changing this pin",
                isError: true);
            RefreshStatusBar ();

            return;
        }

        bool pinned = p.PinState.IsPinned;
        string label = pinned ? "Unpin" : "Pin";

        TryUseOperationReservation (
            _foreground,
            reservation =>
            {
                if (!Confirm (label, $"{label} {p.Name}?"))
                {
                    return false;
                }

                RunOperation (
                    reservation,
                    $"{label}ning {p.Name}",
                    (_, ct) => pinned
                                   ? _state.Backend.UnpinAsync (p.Id, ct)
                                   : _state.Backend.PinAsync (p.Id, ct));

                return true;
            });
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

        if (!_foreground.TryReserveOperation (out OperationReservation? reservation))
        {
            return;
        }

        using (OperationReservation activeReservation = reservation!)
        {
            if (!Confirm ("Batch Upgrade", $"Upgrade {_state.BatchSelected.Count} selected packages?"))
            {
                return;
            }

            if (!activeReservation.TryTransfer (out ForegroundAdmission admission))
            {
                return;
            }

            StartBatchUpgrade (admission);
        }
    }

    private void StartBatchUpgrade (ForegroundAdmission admission)
    {
        if (!_statusOwnership.BeginOperation (admission.Id))
        {
            _foreground.Release (admission);

            return;
        }

        CancellationTokenSource request = CreateLifetimeLinkedSource ();

        if (!TryOwnOperationRequest (request))
        {
            request.Dispose ();
            _statusOwnership.AbortOperation (admission.Id);
            _foreground.Release (admission);

            return;
        }

        CancellationToken ct = request.Token;
        string [] ids = [.. _state.BatchSelected];
        IDisposable loading = _state.AcquireLoading ();
        RefreshStatusBar ();

        bool admitted = _background.TryRun (async lifetimeToken =>
                                             {
                                                 try
                                                 {
                                                     bool cancelled = false;

                                                 foreach (string id in ids)
                                                 {
                                                     if (ct.IsCancellationRequested)
                                                     {
                                                         cancelled = true;

                                                         break;
                                                     }

                                                     await DispatchAsync (() =>
                                                                          {
                                                                              SetStatus (
                                                                                  $"Upgrading {id}… · Esc to cancel",
                                                                                  owner: StatusOwner.Operation);
                                                                              RefreshStatusBar ();
                                                                          }, ct, () => OperationRequestIsCurrent (request));

                                                     OpResult result;

                                                     try
                                                     {
                                                         // Per-item progress would fight the batch loop's own status line; the
                                                         // loop reports "Upgrading {id}…" per package instead.
                                                         result = await _state.Backend.UpgradeAsync (id, null, ct);
                                                     }
                                                     catch (OperationCanceledException) when (ct.IsCancellationRequested)
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

                                                     await DispatchAsync (() =>
                                                                          {
                                                                              if (result.Success)
                                                                              {
                                                                                  _state.InvalidateCachedDetail (id);
                                                                              }

                                                                              SetStatus (
                                                                                  result.Success ? $"Upgraded {id}" : $"Failed: {id}",
                                                                                  !result.Success,
                                                                                  StatusOwner.Operation);
                                                                              RefreshStatusBar ();
                                                                          }, ct, () => OperationRequestIsCurrent (request));
                                                 }

                                                     await DispatchAsync (() =>
                                                                      {
                                                                          loading.Dispose ();
                                                                          _state.BatchSelected.Clear ();
                                                                          string outcome = cancelled ? "Cancelled" : _state.StatusMessage;
                                                                          bool outcomeIsError = !cancelled && _state.StatusIsError;
                                                                          CompleteOperationStatus (admission, outcome, outcomeIsError);
                                                                          _foreground.Release (admission);
                                                                          ReleaseOperationRequest (request);

                                                                          TriggerRefresh (_state.StatusMessage);
                                                                          }, lifetimeToken, () => OperationRequestIsCurrent (request));
                                                 }
                                                 finally
                                                 {
                                                     loading.Dispose ();

                                                     ReleaseOperationRequest (request);

                                                     _statusOwnership.AbortOperation (admission.Id);
                                                     _foreground.Release (admission);

                                                     CancelSource (request);
                                                     request.Dispose ();
                                                 }
                                             });

        if (!admitted)
        {
            loading.Dispose ();
            ReleaseOperationRequest (request);
            CompleteOperationStatus (
                admission,
                "Too many background requests are still pending; wait and try again",
                isError: true);
            _foreground.Release (admission);
            CancelSource (request);
            request.Dispose ();
            RefreshStatusBar ();
        }
    }

    private void RunOperation (
        OperationReservation reservation,
        string activity,
        Func<IProgress<OpProgress>, CancellationToken, Task<OpResult>> op)
    {
        if (!reservation.TryTransfer (out ForegroundAdmission admission))
        {
            return;
        }

        if (!_statusOwnership.BeginOperation (admission.Id))
        {
            _foreground.Release (admission);

            return;
        }

        CancellationTokenSource request = CreateLifetimeLinkedSource ();

        if (!TryOwnOperationRequest (request))
        {
            request.Dispose ();
            _statusOwnership.AbortOperation (admission.Id);
            _foreground.Release (admission);

            return;
        }

        CancellationToken ct = request.Token;

        SetStatus ($"{activity} · Esc to cancel", owner: StatusOwner.Operation);
        IDisposable loading = _state.AcquireLoading ();
        _state.OpProgress = null;
        RefreshStatusBar ();

        IProgress<OpProgress> progress = new UiProgress (this, request);

        bool admitted = _background.TryRun (async lifetimeToken =>
                                             {
                                                 try
                                                 {
                                                     OpResult? result = null;
                                                     bool cancelled = false;

                                                 try
                                                 {
                                                     result = await op (progress, ct);
                                                 }
                                                 catch (OperationCanceledException) when (ct.IsCancellationRequested)
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

                                                     await DispatchAsync (() =>
                                                                      {
                                                                          loading.Dispose ();
                                                                          _state.OpProgress = null;
                                                                          string outcome;
                                                                          bool outcomeIsError;

                                                                          if (cancelled)
                                                                          {
                                                                              outcome = "Cancelled";
                                                                              outcomeIsError = false;
                                                                          }
                                                                          else
                                                                          {
                                                                              outcome = result!.Success ? "Done" : result.Message;
                                                                              outcomeIsError = !result.Success;

                                                                              if (MarkPinsStaleAfterSuccessfulMutation (_state, result))
                                                                              {
                                                                                  _state.ApplyFilter ();
                                                                                  RefreshTable ();
                                                                              }

                                                                              if (result.Operation.PackageId is { } id)
                                                                              {
                                                                                  _state.InvalidateCachedDetail (id);
                                                                              }
                                                                          }

                                                                          CompleteOperationStatus (admission, outcome, outcomeIsError);
                                                                          _foreground.Release (admission);
                                                                          ReleaseOperationRequest (request);

                                                                          TriggerRefresh (_state.StatusMessage);
                                                                          }, lifetimeToken, () => OperationRequestIsCurrent (request));
                                                 }
                                                 finally
                                                 {
                                                     loading.Dispose ();

                                                     ReleaseOperationRequest (request);

                                                     _statusOwnership.AbortOperation (admission.Id);
                                                     _foreground.Release (admission);

                                                     CancelSource (request);
                                                     request.Dispose ();
                                                 }
                                             });

        if (!admitted)
        {
            loading.Dispose ();
            ReleaseOperationRequest (request);
            CompleteOperationStatus (
                admission,
                "Too many background requests are still pending; wait and try again",
                isError: true);
            _foreground.Release (admission);
            CancelSource (request);
            request.Dispose ();
            RefreshStatusBar ();
        }
    }

    internal static bool MarkPinsStaleAfterSuccessfulMutation (AppState state, OpResult result)
    {
        if (!result.Success || result.Operation.Kind is not (OperationKind.Pin or OperationKind.Unpin))
        {
            return false;
        }

        state.MarkPinsStale ();

        return true;
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
        if (Volatile.Read (ref _opCts) is null)
        {
            return;
        }

        _state.OpProgress = value;
        RefreshStatusBar ();
    }

    private void ReportProgress (OpProgress value, CancellationTokenSource request, CancellationToken requestToken)
    {
        if (requestToken.IsCancellationRequested || !OperationRequestIsCurrent (request))
        {
            return;
        }

        lock (_progressGate)
        {
            _pendingProgress = new (value, request, requestToken);

            if (_progressDispatcherRunning)
            {
                return;
            }

            _progressDispatcherRunning = true;
        }

        if (!_background.TryRun (DrainProgressAsync))
        {
            lock (_progressGate)
            {
                _pendingProgress = null;
                _progressDispatcherRunning = false;
            }
        }
    }

    private async Task DrainProgressAsync (CancellationToken lifetimeToken)
    {
        while (!lifetimeToken.IsCancellationRequested)
        {
            PendingProgress? pending;

            lock (_progressGate)
            {
                pending = _pendingProgress;
                _pendingProgress = null;

                if (pending is null)
                {
                    _progressDispatcherRunning = false;

                    return;
                }
            }

            await DispatchAsync (() => OnOpProgress (pending.Value), pending.RequestToken,
                                 () => OperationRequestIsCurrent (pending.Request));
        }

        lock (_progressGate)
        {
            _pendingProgress = null;
            _progressDispatcherRunning = false;
        }
    }

    /// <summary>
    /// <see cref="IProgress{T}"/> bridge that marshals backend progress (raised on a background
    /// or COM thread) onto the Terminal.Gui UI thread before touching view state.
    /// </summary>
    private sealed class UiProgress (App owner, CancellationTokenSource request) : IProgress<OpProgress>
    {
        private readonly CancellationToken _requestToken = request.Token;

        public void Report (OpProgress value) => owner.ReportProgress (value, request, _requestToken);
    }

    private sealed record PendingProgress (
        OpProgress Value,
        CancellationTokenSource Request,
        CancellationToken RequestToken);

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

        using VersionInputDialog dlg = new (p.Name);
        App.Run (dlg);

        return dlg.Result as string;
    }

    private void ShowHelp ()
    {
        if (App is null)
        {
            return;
        }

        using HelpDialog dlg = new (_state.BackendDescription, StartupDiagnostics.ComFallbackReason);
        App.Run (dlg);
    }

    private void ShowThemePicker ()
    {
        if (App is null)
        {
            return;
        }

        using ThemePickerDialog dlg = new ();
        App.Run (dlg);
        string? chosen = dlg.Result;

        if (chosen is not null && chosen != Theme.CurrentPaletteName)
        {
            Theme.TryApply (chosen);
            RefreshTheme ();
        }
    }

    /// <summary>Forces a full-app repaint after a live theme swap. Scheme-named views and the
    /// direct-draw Attribute calls in DetailPanel/Ui/Logo already re-read Theme.* fields at draw
    /// time - this just needs to trigger that redraw.</summary>
    private void RefreshTheme ()
    {
        RefreshTable ();
        RefreshStatusBar ();
        App?.TopRunnableView?.SetNeedsLayout ();
        App?.TopRunnableView?.SetNeedsDraw ();
    }

    private void ExportCsv ()
    {
        if (!_foreground.TryBegin (ForegroundWorkflow.Export, out ForegroundAdmission admission))
        {
            return;
        }

        CsvSnapshot snapshot;

        try
        {
            // Copy immutable scalar data on the UI thread. CsvExporter enforces row, cell, and
            // aggregate character ceilings before this snapshot crosses into background work.
            snapshot = CsvExporter.CreateSnapshot (_state.Filtered);
        }
        catch (Exception ex)
        {
            _foreground.Release (admission);
            SetStatus ($"Export preparation failed: {ex.Message}", isError: true);
            RefreshStatusBar ();

            return;
        }

        string path = Path.Combine (Environment.CurrentDirectory, "winget-tui-export.csv");
        string activity = $"Exporting {snapshot.Rows.Count} rows…";
        if (!_exportWorkflow.TryBegin (
                _background.LifetimeToken,
                activity,
                () => _state.AcquireLoading (),
                out ExportOperation operation))
        {
            _foreground.Release (admission);

            return;
        }

        CancellationToken ct = operation.Token;
        SetStatus (activity);
        RefreshStatusBar ();

        bool admitted = _background.TryRun (async lifetimeToken =>
                                             {
                                                 try
                                                 {
                                                     await CsvExporter.WriteAtomicAsync (path, snapshot, ct);
                                                     await DispatchAsync (() => CompleteExport (
                                                                                  operation,
                                                                                  FormatExportSuccess (snapshot, path),
                                                                                  isError: false),
                                                                          ct,
                                                                          () => _exportWorkflow.IsCurrent (operation));
                                                 }
                                                 catch (OperationCanceledException) when (ct.IsCancellationRequested)
                                                 {
                                                     // Application shutdown owns cancellation; no stopped UI callback.
                                                 }
                                                 catch (Exception ex)
                                                 {
                                                     await DispatchAsync (() => CompleteExport (
                                                                                  operation,
                                                                                  $"Export failed: {ex.Message}",
                                                                                  isError: true),
                                                                          lifetimeToken,
                                                                          () => _exportWorkflow.IsCurrent (operation));
                                                 }
                                                 finally
                                                 {
                                                     _exportWorkflow.Release (operation);
                                                     _foreground.Release (admission);
                                                 }
                                             });

        if (!admitted)
        {
            if (_exportWorkflow.RejectAdmission (operation, out string message))
            {
                _foreground.Release (admission);
                SetStatus (message, isError: true);
                RefreshStatusBar ();
            }
            else
            {
                _foreground.Release (admission);
            }
        }
    }

    private void CompleteExport (
        ExportOperation operation,
        string message,
        bool isError)
    {
        ExportCompletion completion = _exportWorkflow.Complete (operation, _state.StatusMessage);

        if (!completion.WasCurrent)
        {
            return;
        }

        // A newer operation may own the status line even though this export still owns its task
        // identity. In that case completion must not erase or replace the newer message.
        if (completion.OwnedStatus)
        {
            SetStatus (message, isError);
        }

        RefreshStatusBar ();
    }

    private static string FormatExportSuccess (CsvSnapshot snapshot, string path)
    {
        if (!snapshot.WasTruncated)
        {
            return $"Exported {snapshot.Rows.Count} rows to {path}";
        }

        return $"Exported {snapshot.Rows.Count} of {snapshot.SourceRowCount} rows to {path}; "
               + $"bounded export omitted {snapshot.OmittedRowCount} rows and truncated {snapshot.TruncatedCellCount} cells "
               + $"(limits: {CsvExporter.MaxRows:N0} rows, {CsvExporter.MaxCellCharacters:N0} chars/cell, "
               + $"{CsvExporter.MaxSnapshotCharacters:N0} chars total)";
    }

    private void OpenUrl (string? url)
    {
        if (string.IsNullOrWhiteSpace (url))
        {
            SetStatus ("No URL available");
            RefreshStatusBar ();

            return;
        }

        if (!TryNormalizeOpenableUrl (url, out string normalizedUrl))
        {
            SetStatus ("Blocked non-http(s) URL", isError: true);
            RefreshStatusBar ();

            return;
        }

        try
        {
            LaunchUrl (normalizedUrl, psi => Process.Start (psi));
            SetStatus ($"Opened {normalizedUrl}");
        }
        catch (Exception ex)
        {
            SetStatus ($"Open failed: {ex.Message}", isError: true);
        }

        RefreshStatusBar ();
    }

    internal static string EscapeCsvCell (string value)
        => CsvExporter.EscapeCell (value);

    internal static void LaunchUrl (
        string normalizedUrl,
        Func<ProcessStartInfo, IDisposable?> launcher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace (normalizedUrl);
        ArgumentNullException.ThrowIfNull (launcher);
        ProcessStartInfo psi = new (normalizedUrl) { UseShellExecute = true };
        using IDisposable? launched = launcher (psi);
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

    /// <summary>
    /// A <see cref="TableView"/> that reports clicks on a column header (raising
    /// <see cref="HeaderClicked"/> with the column index) so the app can sort by that column,
    /// matching upstream winget-tui's click-to-sort. Clicks on body rows keep the base behaviour.
    /// </summary>
    private sealed class SortableTableView : TableView
    {
        /// <summary>Raised with the clicked header's column index (the marker column is 0).</summary>
        public event Action<int>? HeaderClicked;

        /// <inheritdoc />
        protected override bool OnMouseEvent (Mouse mouse)
        {
            if (mouse.IsSingleClicked == true && mouse.Position is { } pos)
            {
                _ = ScreenToCell (pos.X, pos.Y, out int? headerColumn);

                if (headerColumn is { } column)
                {
                    HeaderClicked?.Invoke (column);
                    mouse.Handled = true;

                    return true;
                }
            }

            return base.OnMouseEvent (mouse);
        }
    }

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
