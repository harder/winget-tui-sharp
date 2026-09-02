
using System.Text;

namespace WingetTuiSharp;

/// <summary>
/// In-memory state for the running app.
/// </summary>
public sealed class AppState
{
    /// <summary>
    /// Upper bound on rows a single search returns, so a pathologically broad query can't flood
    /// the table. Lives here (not in the WINGET_COM-gated ComBackend) so the cross-platform App
    /// build can reference it too. The COM backend applies it via FindPackagesOptions.ResultLimit.
    /// </summary>
    public const int SearchResultLimit = 1000;

    public AppState (IBackend backend) => Backend = backend;

    public IBackend Backend { get; }

    public AppMode Mode { get; set; } = AppMode.Installed;
    public InputMode InputMode { get; set; } = InputMode.Normal;
    public Focus Focus { get; set; } = Focus.PackageList;

    public List<Package> Packages { get; set; } = [];
    public List<Package> Filtered { get; private set; } = [];
    public HashSet<string> BatchSelected { get; } = new (StringComparer.OrdinalIgnoreCase);
    public PackageDetail? CurrentDetail { get; set; }
    private readonly BoundedDetailCache _detailCache = new ();
    private readonly BoundedPinSnapshot _pinSnapshot = new ();

    public string SearchQuery { get; set; } = string.Empty;
    public string LocalFilter { get; set; } = string.Empty;

    /// <summary>The catalog the source filter is scoped to, or null for "All". Cycled by <see cref="CycleSourceFilter"/>.</summary>
    public string? SourceFilter { get; set; }

    /// <summary>
    /// Configured source names the filter cycles through (besides "All"). Seeded with the two
    /// predefined sources and replaced once <see cref="IBackend.ListSourcesAsync"/> resolves at
    /// startup, so custom/enterprise REST sources become filterable too.
    /// </summary>
    public IReadOnlyList<string> AvailableSources { get; set; } = ["winget", "msstore"];

    public PinFilter PinFilter { get; set; } = PinFilter.All;
    public SortField SortField { get; set; } = SortField.None;
    public SortDir SortDir { get; set; } = SortDir.Asc;
    private int _loadingOwners;
    private int _detailLoadingOwners;

    public bool Loading => Volatile.Read (ref _loadingOwners) > 0;
    public bool DetailLoading => Volatile.Read (ref _detailLoadingOwners) > 0;
    public string StatusMessage { get; set; } = string.Empty;
    public bool StatusIsError { get; set; }
    internal bool PinDataFresh => _pinSnapshot.IsFresh;
    internal bool HasPinSnapshot => _pinSnapshot.HasSnapshot;
    internal int PinSnapshotCount => _pinSnapshot.Count;

    /// <summary>Which backend is live + its winget version (e.g. "COM · winget 1.11.400"), for the header badge and help. Empty until resolved at startup.</summary>
    public string BackendDescription { get; set; } = string.Empty;

    /// <summary>Progress of the in-flight install/upgrade/uninstall, or null when none is running.</summary>
    public OpProgress? OpProgress { get; set; }

    public int ViewGeneration { get; private set; }
    public int DetailGeneration { get; private set; }

    public int BumpViewGeneration () => ++ViewGeneration;
    public int BumpDetailGeneration () => ++DetailGeneration;

    internal bool TryGetCachedDetail (Package context, out PackageDetail detail)
    {
        if (!_detailCache.TryGet (context.Id, out detail))
        {
            return false;
        }

        detail.MergeContext (context);

        // MergeContext deliberately never downgrades a pin, because a list row that predates a
        // pin must not erase it. A fresh snapshot is the opposite case: it is authoritative, so
        // an external unpin (or a changed kind/gating version) has to overwrite the cached value
        // rather than being re-cached as pinned for the rest of the entry's lifetime.
        if (_pinSnapshot.IsFresh)
        {
            detail.PinState = _pinSnapshot.TryGet (context.Id, out PinState snapshotPin)
                                  ? snapshotPin
                                  : PinState.Unpinned;
        }

        detail.EnsureDetailHint ();
        _detailCache.Set (context.Id, detail);

        return true;
    }

    internal bool CacheDetail (string id, PackageDetail detail) => _detailCache.Set (id, detail);
    internal bool InvalidateCachedDetail (string id) => _detailCache.Remove (id);
    internal void ClearCachedDetails () => _detailCache.Clear ();

    internal bool RecordPinSnapshot (IReadOnlyDictionary<string, PinState> pins) =>
        _pinSnapshot.TryRecord (pins);

    internal bool ApplyPinSnapshot (IEnumerable<Package> packages) =>
        _pinSnapshot.TryApply (packages);

    internal bool TryGetSnapshotPin (string id, out PinState state) =>
        _pinSnapshot.TryGet (id, out state);

    internal void MarkPinsStale ()
    {
        _pinSnapshot.MarkStale ();
        PinFilter = PinFilter.All;
    }

    /// <summary>
    /// Acquires independent loading ownership. Disposing one lease cannot clear another owner's
    /// spinner, and repeated disposal is harmless.
    /// </summary>
    public IDisposable AcquireLoading (bool detail = false)
    {
        if (detail)
        {
            Interlocked.Increment (ref _detailLoadingOwners);
        }
        else
        {
            Interlocked.Increment (ref _loadingOwners);
        }

        return new LoadingLease (this, detail);
    }

    private void ReleaseLoading (bool detail)
    {
        ref int owners = ref (detail ? ref _detailLoadingOwners : ref _loadingOwners);
        int remaining = Interlocked.Decrement (ref owners);

        if (remaining < 0)
        {
            Interlocked.Exchange (ref owners, 0);
            throw new InvalidOperationException ("Loading ownership was released more than once.");
        }
    }

    private sealed class LoadingLease (AppState owner, bool detail) : IDisposable
    {
        private AppState? _owner = owner;

        public void Dispose () => Interlocked.Exchange (ref _owner, null)?.ReleaseLoading (detail);
    }

    /// <summary>
    /// Recomputes Filtered based on LocalFilter, PinFilter, sort. Preserves selection by id when possible.
    /// </summary>
    public void ApplyFilter ()
    {
        IEnumerable<Package> q = Packages;

        if (!string.IsNullOrEmpty (LocalFilter))
        {
            string filter = LocalFilter;

            q = q.Where (p => p.Name.Contains (filter, StringComparison.OrdinalIgnoreCase)
                              || p.Id.Contains (filter, StringComparison.OrdinalIgnoreCase));
        }

        if (Mode != AppMode.Search && PinDataFresh)
        {
            q = PinFilter switch
            {
                PinFilter.PinnedOnly => q.Where (p => p.PinState.IsPinned),
                PinFilter.UnpinnedOnly => q.Where (p => !p.PinState.IsPinned),
                _ => q
            };
        }

        List<Package> filtered = q.ToList ();

        switch (SortField)
        {
            case SortField.Name:
                filtered.Sort ((left, right) => StringComparer.OrdinalIgnoreCase.Compare (left.Name, right.Name));

                break;
            case SortField.Id:
                filtered.Sort ((left, right) => StringComparer.OrdinalIgnoreCase.Compare (left.Id, right.Id));

                break;
            case SortField.Version:
                filtered.Sort ((left, right) => CliBackend.CompareVersionsLike (left.Version, right.Version));

                break;
        }

        if (SortDir == SortDir.Desc && SortField != SortField.None)
        {
            filtered.Reverse ();
        }

        Filtered = filtered;
    }

    public Package? SelectedPackage (int selected)
    {
        if (selected < 0 || selected >= Filtered.Count)
        {
            return null;
        }

        return Filtered [selected];
    }

    public void CycleSort ()
    {
        // None -> Name asc -> Name desc -> Id asc -> Id desc -> Version asc -> Version desc -> None
        (SortField, SortDir) = (SortField, SortDir) switch
        {
            (SortField.None, _) => (SortField.Name, SortDir.Asc),
            (SortField.Name, SortDir.Asc) => (SortField.Name, SortDir.Desc),
            (SortField.Name, SortDir.Desc) => (SortField.Id, SortDir.Asc),
            (SortField.Id, SortDir.Asc) => (SortField.Id, SortDir.Desc),
            (SortField.Id, SortDir.Desc) => (SortField.Version, SortDir.Asc),
            (SortField.Version, SortDir.Asc) => (SortField.Version, SortDir.Desc),
            _ => (SortField.None, SortDir.Asc)
        };
    }

    /// <summary>
    /// Cycle All → each configured source in order → All. Resilient to the available-source list
    /// changing under it (a now-missing current source just restarts the cycle at All).
    /// </summary>
    public void CycleSourceFilter ()
    {
        if (AvailableSources.Count == 0)
        {
            SourceFilter = null;

            return;
        }

        if (SourceFilter is null)
        {
            SourceFilter = AvailableSources [0];

            return;
        }

        int idx = -1;

        for (int i = 0; i < AvailableSources.Count; i++)
        {
            if (string.Equals (AvailableSources [i], SourceFilter, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;

                break;
            }
        }

        // null = "All" once we step past the last source (or if the current one vanished).
        SourceFilter = idx >= 0 && idx + 1 < AvailableSources.Count ? AvailableSources [idx + 1] : null;
    }

    public bool CyclePinFilter ()
    {
        if (!PinDataFresh)
        {
            return false;
        }

        PinFilter = PinFilter switch
        {
            PinFilter.All => PinFilter.PinnedOnly,
            PinFilter.PinnedOnly => PinFilter.UnpinnedOnly,
            _ => PinFilter.All
        };

        return true;
    }

    public void CycleMode (bool forward)
    {
        Mode = forward
                   ? Mode switch
                   {
                       AppMode.Search => AppMode.Installed,
                       AppMode.Installed => AppMode.Upgrades,
                       _ => AppMode.Search
                   }
                   : Mode switch
                   {
                       AppMode.Search => AppMode.Upgrades,
                       AppMode.Installed => AppMode.Search,
                       _ => AppMode.Installed
                   };
    }

    /// <summary>Status-bar badge text for a source filter: null → "All", else the source name (capitalized for the two predefined ones).</summary>
    public static string SourceLabel (string? f)
        => f switch
        {
            null => " All ",
            "winget" => " Winget ",
            "msstore" => " MsStore ",
            _ => $" {f} "
        };

    public static string PinLabel (PinFilter f)
        => f switch
        {
            PinFilter.PinnedOnly => " \U0001F4CC only ",
            PinFilter.UnpinnedOnly => " \U0001F4CC hide ",
            _ => " \U0001F4CC all "
        };
}

internal enum StatusOwner
{
    Ambient,
    Operation
}

/// <summary>
/// Gives the in-flight package operation exclusive ownership of the shared status line. Ambient
/// workflows may continue to run, but their feedback cannot hide operation progress or results.
/// </summary>
internal sealed class StatusOwnership
{
    internal const int MaxDeferredErrorCharacters = 512;
    internal const int MaxPublishedStatusCharacters = 1024;

    private readonly object _gate = new ();
    private long? _activeOperationId;
    private string? _deferredError;

    internal bool BeginOperation (long operationId)
    {
        lock (_gate)
        {
            if (_activeOperationId is not null)
            {
                return false;
            }

            _activeOperationId = operationId;
            _deferredError = null;

            return true;
        }
    }

    internal bool TryWrite (
        StatusOwner owner,
        string message,
        bool isError,
        Action<string, bool> write)
    {
        ArgumentNullException.ThrowIfNull (write);

        lock (_gate)
        {
            if (owner == StatusOwner.Ambient && _activeOperationId is not null)
            {
                // Latest error wins. This is one bounded scalar-safe slot, never a queue.
                if (isError)
                {
                    _deferredError = TruncateScalarSafe (message, MaxDeferredErrorCharacters);
                }

                return false;
            }

            write (TruncateScalarSafe (message, MaxPublishedStatusCharacters), isError);

            return true;
        }
    }

    internal bool CompleteOperation (
        long operationId,
        string outcome,
        bool outcomeIsError,
        Action<string, bool> write)
    {
        ArgumentNullException.ThrowIfNull (write);

        lock (_gate)
        {
            if (_activeOperationId != operationId)
            {
                return false;
            }

            const string DeferredLabel = " · Background error: ";
            string published;

            if (string.IsNullOrEmpty (_deferredError))
            {
                published = outcome;
            }
            else
            {
                int outcomeLimit = MaxPublishedStatusCharacters
                                   - DeferredLabel.Length
                                   - MaxDeferredErrorCharacters;
                published = $"{TruncateScalarSafe (outcome, outcomeLimit)}{DeferredLabel}{_deferredError}";
            }
            bool publishedIsError = outcomeIsError || _deferredError is not null;
            _activeOperationId = null;
            _deferredError = null;
            write (TruncateScalarSafe (published, MaxPublishedStatusCharacters), publishedIsError);

            return true;
        }
    }

    internal bool AbortOperation (long operationId)
    {
        lock (_gate)
        {
            if (_activeOperationId != operationId)
            {
                return false;
            }

            _activeOperationId = null;
            _deferredError = null;

            return true;
        }
    }

    internal int DeferredErrorCount
    {
        get
        {
            lock (_gate)
            {
                return _deferredError is null ? 0 : 1;
            }
        }
    }

    internal void Clear ()
    {
        lock (_gate)
        {
            _activeOperationId = null;
            _deferredError = null;
        }
    }

    internal static string TruncateScalarSafe (string value, int maxCharacters)
    {
        if (value.Length <= maxCharacters)
        {
            return value;
        }

        StringBuilder bounded = new (maxCharacters);
        int contentLimit = Math.Max (0, maxCharacters - 1);

        foreach (Rune rune in value.EnumerateRunes ())
        {
            if (bounded.Length + rune.Utf16SequenceLength > contentLimit)
            {
                break;
            }

            bounded.Append (rune);
        }

        bounded.Append ('…');

        return bounded.ToString ();
    }
}

internal enum ForegroundWorkflow
{
    Operation,
    Preflight,
    Export
}

internal readonly record struct ForegroundAdmission (ForegroundWorkflow Workflow, long Id);

internal sealed class OperationReservation : IDisposable
{
    private readonly ForegroundWorkflowCoordinator _owner;
    private int _state;

    internal OperationReservation (ForegroundWorkflowCoordinator owner, ForegroundAdmission admission)
    {
        _owner = owner;
        Admission = admission;
    }

    internal ForegroundAdmission Admission { get; }

    /// <summary>Transfers release responsibility to the operation runner exactly once.</summary>
    internal bool TryTransfer (out ForegroundAdmission admission)
    {
        admission = Admission;

        return Interlocked.CompareExchange (ref _state, 1, 0) == 0;
    }

    public void Dispose ()
    {
        if (Interlocked.CompareExchange (ref _state, 2, 0) == 0)
        {
            _owner.Release (Admission);
        }
    }
}

/// <summary>Serializes user-visible foreground workflows with idempotent identity-based release.</summary>
internal sealed class ForegroundWorkflowCoordinator
{
    private readonly object _gate = new ();
    private ForegroundAdmission? _active;
    private long _nextId;
    private bool _stopped;

    internal bool TryBegin (ForegroundWorkflow workflow, out ForegroundAdmission admission)
    {
        lock (_gate)
        {
            if (_stopped || _active is not null)
            {
                admission = default;

                return false;
            }

            admission = new (workflow, ++_nextId);
            _active = admission;

            return true;
        }
    }

    internal bool TryReserveOperation (out OperationReservation? reservation)
    {
        if (!TryBegin (ForegroundWorkflow.Operation, out ForegroundAdmission admission))
        {
            reservation = null;

            return false;
        }

        reservation = new (this, admission);

        return true;
    }

    internal bool Release (ForegroundAdmission admission)
    {
        lock (_gate)
        {
            if (_active != admission)
            {
                return false;
            }

            _active = null;

            return true;
        }
    }

    internal bool IsCurrent (ForegroundAdmission admission)
    {
        lock (_gate)
        {
            return _active == admission;
        }
    }

    internal void Stop ()
    {
        lock (_gate)
        {
            _stopped = true;
            _active = null;
        }
    }
}

internal sealed class BoundedPinSnapshot
{
    internal const int MaxEntries = 4096;
    internal const int MaxAggregateCharacters = 256 * 1024;
    internal const int MaxKeyCharacters = 4096;
    internal const int MaxGatingVersionCharacters = 256;

    private readonly Dictionary<string, PinState> _states = new (StringComparer.OrdinalIgnoreCase);

    internal bool IsFresh { get; private set; }
    internal bool HasSnapshot { get; private set; }
    internal int Count => _states.Count;

    internal bool TryRecord (IReadOnlyDictionary<string, PinState> pins)
    {
        ArgumentNullException.ThrowIfNull (pins);
        int reportedCount;

        try
        {
            reportedCount = pins.Count;
        }
        catch
        {
            IsFresh = false;

            return false;
        }

        // A trustworthy Count lets a huge source fail without even asking it for an enumerator.
        if (reportedCount < 0 || reportedCount > MaxEntries)
        {
            IsFresh = false;

            return false;
        }

        Dictionary<string, PinState> candidate = new (
            Math.Min (reportedCount, MaxEntries),
            StringComparer.OrdinalIgnoreCase);
        int aggregateCharacters = 0;
        int inspected = 0;

        try
        {
            foreach ((string id, PinState sourceState) in pins)
            {
                // Over-enumeration is rejected at the first extra entry rather than at MaxEntries:
                // a source that yields more than it reported cannot be reconciled with its Count.
                if (inspected++ >= reportedCount
                    || inspected > MaxEntries
                    || string.IsNullOrEmpty (id)
                    || id.Length > MaxKeyCharacters)
                {
                    IsFresh = false;

                    return false;
                }

                string? gatingVersion = sourceState.GatingVersion is null
                                            ? null
                                            : StatusOwnership.TruncateScalarSafe (
                                                sourceState.GatingVersion,
                                                MaxGatingVersionCharacters);
                int entryCharacters = id.Length + (gatingVersion?.Length ?? 0);

                if (entryCharacters > MaxAggregateCharacters - aggregateCharacters)
                {
                    IsFresh = false;

                    return false;
                }

                aggregateCharacters += entryCharacters;
                candidate [id] = sourceState with { GatingVersion = gatingVersion };
            }
        }
        catch
        {
            IsFresh = false;

            return false;
        }

        // Under-enumeration is just as untrustworthy as over-enumeration: accepting a short
        // enumeration as complete would silently report every unlisted package as unpinned.
        if (inspected != reportedCount)
        {
            IsFresh = false;

            return false;
        }

        _states.Clear ();

        foreach ((string id, PinState state) in candidate)
        {
            _states.Add (id, state);
        }

        HasSnapshot = true;
        IsFresh = true;

        return true;
    }

    internal void MarkStale () => IsFresh = false;

    internal bool TryGet (string id, out PinState state)
    {
        if (!IsFresh)
        {
            state = PinState.Unpinned;

            return false;
        }

        return _states.TryGetValue (id, out state);
    }

    internal bool TryApply (IEnumerable<Package> packages)
    {
        if (!IsFresh)
        {
            return false;
        }

        foreach (Package package in packages)
        {
            package.PinState = _states.TryGetValue (package.Id, out PinState state)
                                   ? state
                                   : PinState.Unpinned;
        }

        return true;
    }
}
