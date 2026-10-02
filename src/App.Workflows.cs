namespace WingetTuiSharp;

public sealed partial class App
{
    private readonly WorkflowStore _workflowStore = new ();
    private readonly Dictionary<string, Package> _searchSelected = new (StringComparer.OrdinalIgnoreCase);
    private UpdateCheckSnapshot? _lastCheck;

    private string CheckContextLabel ()
    {
        _lastCheck = _workflowStore.LatestCheck ();
        if (_lastCheck is null) return "Available upgrades";
        if (_lastCheck.Status != "Succeeded") return "Available upgrades · scheduled check failed";
        return $"Available upgrades · checked {_lastCheck.CheckedAtUtc.ToLocalTime ():g}";
    }

    private void ToggleSearchSelection (Package? package)
    {
        if (package is null) return;
        string key = BatchPlanning.Key (package);
        if (!_searchSelected.Remove (key))
        {
            if (_searchSelected.Count >= 100)
            {
                SetStatus ("Select at most 100 packages per install plan", isError: true);
                RefreshStatusBar ();
                return;
            }
            _searchSelected.Add (key, package);
        }
        UpdateListTitle ();
        _packageTable.SetNeedsDraw ();
        RefreshStatusBar ();
    }

    private void ToggleSearchSelectVisible ()
    {
        List<Package> visible = _state.Filtered;
        if (visible.Count > 0 && visible.All (p => _searchSelected.ContainsKey (BatchPlanning.Key (p))))
        {
            foreach (Package package in visible) _searchSelected.Remove (BatchPlanning.Key (package));
        }
        else
        {
            foreach (Package package in visible)
            {
                if (_searchSelected.Count >= 100) break;
                _searchSelected[BatchPlanning.Key (package)] = package;
            }
            if (visible.Count > 100) SetStatus ("Selected the first 100 visible packages; refine the search for more.");
        }
        UpdateListTitle ();
        _packageTable.SetNeedsDraw ();
        RefreshStatusBar ();
    }

    private void ManagePackageSets ()
    {
        if (App is null) return;
        IReadOnlyList<PackageSet> sets = _workflowStore.Sets ();
        using SetManagerDialog manager = new (sets, _searchSelected.Count);
        App.Run (manager);
        SetManagerChoice? choice = manager.Result;
        if (choice is null) return;

        try
        {
            if (choice.Action == "Save")
            {
                using TextPromptDialog prompt = new ("Save package set", "Set name:");
                App.Run (prompt);
                string? name = prompt.Result;
                if (string.IsNullOrWhiteSpace (name)) return;
                if (sets.Any (x => x.Name.Equals (name, StringComparison.OrdinalIgnoreCase))
                    && !Confirm ("Replace set", $"Replace the saved set “{name}”?")) return;
                _workflowStore.SaveSet (new (name,
                    [.. _searchSelected.Values.Select (p => new PackageChoice (p.Id, p.Name, p.Source))]));
                SetStatus ($"Saved set “{name}” ({_searchSelected.Count} packages).");
            }
            else if (choice.Action == "Load" && choice.Name is { } loadName)
            {
                PackageSet? set = sets.FirstOrDefault (x => x.Name == loadName);
                if (set is null) return;
                if (set.Packages.Count > 100) throw new InvalidOperationException ("This set exceeds the 100-package limit.");
                _searchSelected.Clear ();
                foreach (PackageChoice item in set.Packages)
                {
                    Package? current = _state.Packages.FirstOrDefault (p =>
                        p.Id.Equals (item.Id, StringComparison.OrdinalIgnoreCase)
                        && p.Source.Equals (item.Source, StringComparison.OrdinalIgnoreCase));
                    Package package = current ?? new () { Id = item.Id, Name = item.Name, Source = item.Source };
                    _searchSelected[BatchPlanning.Key (package)] = package;
                }
                RefreshTable ();
                SetStatus ($"Loaded set “{set.Name}” ({_searchSelected.Count} packages). Press B to review.");
            }
            else if (choice.Action == "Delete" && choice.Name is { } deleteName)
            {
                if (!Confirm ("Delete set", $"Delete saved set “{deleteName}”?")) return;
                _workflowStore.DeleteSet (deleteName);
                SetStatus ($"Deleted set “{deleteName}”.");
            }
        }
        catch (Exception ex)
        {
            SetStatus ($"Package set error: {ex.Message}", isError: true);
        }
        RefreshStatusBar ();
    }

    private void AskBatchInstall ()
    {
        if (_searchSelected.Count == 0 || App is null)
        {
            SetStatus ("Select packages with Space before starting a batch install.");
            RefreshStatusBar ();
            return;
        }
        if (!_foreground.TryReserveOperation (out OperationReservation? reservation)) return;
        List<Package> selected = [.. _searchSelected.Values];
        IDisposable loading = _state.AcquireLoading ();
        SetStatus ($"Checking {selected.Count} selected packages…");
        RefreshStatusBar ();

        bool admitted = _background.TryRun (async lifetimeToken =>
        {
            try
            {
                IReadOnlyList<Package> installed = await _state.Backend.ListInstalledAsync (null, lifetimeToken);
                IReadOnlyList<string> sources = await _state.Backend.ListSourcesAsync (lifetimeToken);
                selected = await BatchPlanning.ResolveUnresolvedAsync (selected, async (item, token) =>
                {
                    IReadOnlyList<Package> matches = await _state.Backend.SearchAsync (item.Id, item.Source, token);
                    return matches.FirstOrDefault (p =>
                        p.Id.Equals (item.Id, StringComparison.OrdinalIgnoreCase)
                        && p.Source.Equals (item.Source, StringComparison.OrdinalIgnoreCase));
                }, lifetimeToken);
                BatchPlan plan = BatchPlanning.ForInstalls (selected, installed, sources);
                await DispatchAsync (() =>
                {
                    loading.Dispose ();
                    using BatchPlanDialog review = new (plan);
                    App.Run (review);
                    if (review.Result == true && reservation!.TryTransfer (out ForegroundAdmission admission))
                    {
                        StartBatchPlan (admission, plan);
                    }
                    else
                    {
                        SetStatus ("Install plan cancelled.");
                        RefreshStatusBar ();
                    }
                }, lifetimeToken);
            }
            catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                await DispatchAsync (() =>
                {
                    SetStatus ($"Could not review install plan: {ex.Message}", isError: true);
                    RefreshStatusBar ();
                }, lifetimeToken);
            }
            finally
            {
                loading.Dispose ();
                reservation!.Dispose ();
            }
        });
        if (!admitted)
        {
            loading.Dispose ();
            reservation!.Dispose ();
            ReportRejectedBackgroundAdmission ();
        }
    }

    private void StartBatchPlan (ForegroundAdmission admission, BatchPlan plan)
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
        DateTimeOffset started = DateTimeOffset.UtcNow;
        CancellationToken ct = request.Token;
        IDisposable loading = _state.AcquireLoading ();
        RefreshStatusBar ();
        bool admitted = _background.TryRun (async lifetimeToken =>
        {
            List<RunItem> results = [];
            try
            {
                foreach (BatchPlanItem item in plan.Items)
                {
                    Package package = item.Package;
                    if (!item.CanRun)
                    {
                        results.Add (new (package.Id, package.Name, package.Source, "Skipped", item.Reason));
                        continue;
                    }
                    if (ct.IsCancellationRequested)
                    {
                        results.Add (new (package.Id, package.Name, package.Source, "Skipped", "Cancelled before this package started."));
                        continue;
                    }
                    await DispatchAsync (() =>
                    {
                        SetStatus ($"{plan.Action}ing {package.Name}… · Esc to cancel", owner: StatusOwner.Operation);
                        RefreshStatusBar ();
                    }, ct, () => OperationRequestIsCurrent (request));
                    try
                    {
                        OpResult result = plan.Action == "Install"
                            ? await _state.Backend.InstallAsync (package.Id, null, null, null, ct, package.Source)
                            : await _state.Backend.UpgradeAsync (package.Id, null, ct, package.Source);
                        results.Add (new (package.Id, package.Name, package.Source,
                            result.Success ? "Succeeded" : "Failed", LimitReason (result.Message)));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        results.Add (new (package.Id, package.Name, package.Source, "Skipped", "Cancelled during this package."));
                    }
                    catch (Exception ex)
                    {
                        results.Add (new (package.Id, package.Name, package.Source, "Failed", LimitReason (ex.Message)));
                    }
                }
                RunRecord record = new (started, DateTimeOffset.UtcNow, plan.Action, results);
                await DispatchAsync (() =>
                {
                    loading.Dispose ();
                    string? historyError = null;
                    try { _workflowStore.SaveRun (record); }
                    catch (Exception ex) { historyError = ex.Message; }
                    foreach (RunItem item in results.Where (x => x.Status == "Succeeded"))
                    {
                        _state.InvalidateCachedDetail (item.Id);
                        if (plan.Action == "Install") _searchSelected.Remove ($"{item.Source}\u001f{item.Id}");
                    }
                    _state.BatchSelected.Clear ();
                    (string outcome, bool outcomeIsError) = record.Completion (historyError);
                    CompleteOperationStatus (admission, outcome, outcomeIsError);
                    _foreground.Release (admission);
                    ReleaseOperationRequest (request);
                    using RunRecordDialog dialog = new (record);
                    App?.Run (dialog);
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
            CompleteOperationStatus (admission, "Too many background requests are pending", isError: true);
            _foreground.Release (admission);
            CancelSource (request);
            request.Dispose ();
            RefreshStatusBar ();
        }
    }

    private static string LimitReason (string? reason) =>
        StatusOwnership.TruncateScalarSafe (string.IsNullOrWhiteSpace (reason) ? "Completed." : reason.Trim (), 300);

    private void ShowRunHistory ()
    {
        if (App is null) return;
        IReadOnlyList<RunRecord> runs = _workflowStore.Runs ();
        if (runs.Count == 0)
        {
            SetStatus ("No operations have been recorded yet.");
            RefreshStatusBar ();
            return;
        }
        using RunHistoryDialog picker = new (runs);
        App.Run (picker);
        int? index = picker.Result;
        if (index is not { } i || i < 0 || i >= runs.Count) return;
        using RunRecordDialog detail = new (runs[i]);
        App.Run (detail);
    }

    private void ManageScheduledChecks ()
    {
        if (App is null) return;
        using ScheduleDialog dialog = new (_workflowStore.Schedule (), _workflowStore.LatestCheck ());
        App.Run (dialog);
        ScheduleChoice? choice = dialog.Result;
        if (choice is null) return;
        if (choice.Action == "Check")
        {
            CheckNow ();
            return;
        }
        SetStatus (choice.Action == "Save" ? "Registering daily check…" : "Removing daily check…");
        RefreshStatusBar ();
        bool admitted = _background.TryRun (async ct =>
        {
            string? error;
            if (choice.Action == "Save")
            {
                string? path = UpdateTaskScheduler.ExecutablePath ();
                error = path is null
                    ? "Run a published executable to enable scheduled checks."
                    : await UpdateTaskScheduler.RegisterAsync (path, choice.DailyAt, ct);
            }
            else
            {
                error = await UpdateTaskScheduler.UnregisterAsync (ct);
            }
            await DispatchAsync (() =>
            {
                if (error is null)
                {
                    try
                    {
                        _workflowStore.SaveSchedule (new (choice.Action == "Save", choice.DailyAt, choice.NotifyOnChange));
                        SetStatus (choice.Action == "Save"
                            ? $"Daily update checks scheduled for {choice.DailyAt}."
                            : "Daily update checks disabled.");
                    }
                    catch (Exception ex) { SetStatus ($"Could not save schedule settings: {ex.Message}", isError: true); }
                }
                else SetStatus ($"Schedule error: {error}", isError: true);
                RefreshStatusBar ();
            }, ct);
        });
        if (!admitted) ReportRejectedBackgroundAdmission ();
    }

    private void CheckNow ()
    {
        SetStatus ("Checking for upgrades…");
        RefreshStatusBar ();
        bool admitted = _background.TryRun (async ct =>
        {
            UpdateCheckSnapshot snapshot = await UpdateChecks.CheckAsync (_state.Backend, _workflowStore, ct);
            await DispatchAsync (() =>
            {
                _lastCheck = snapshot;
                SyncTabBar ();
                SetStatus (snapshot.Status == "Succeeded"
                    ? $"Check complete: {snapshot.Actionable} available · {snapshot.Pinned} pinned · {snapshot.NewOrChanged} new or changed."
                    : $"Update check failed: {snapshot.Error}", snapshot.Status != "Succeeded");
                RefreshStatusBar ();
                if (snapshot.Status == "Succeeded" && _state.Mode == AppMode.Upgrades) TriggerRefresh (_state.StatusMessage);
            }, ct);
        });
        if (!admitted) ReportRejectedBackgroundAdmission ();
    }
}
