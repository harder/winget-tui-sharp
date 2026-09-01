
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
    public Dictionary<string, PinState> Pins { get; } = new (StringComparer.OrdinalIgnoreCase);
    public HashSet<string> BatchSelected { get; } = new (StringComparer.OrdinalIgnoreCase);
    public PackageDetail? CurrentDetail { get; set; }
    public Dictionary<string, PackageDetail> DetailCache { get; } = new (StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Which backend is live + its winget version (e.g. "COM · winget 1.11.400"), for the header badge and help. Empty until resolved at startup.</summary>
    public string BackendDescription { get; set; } = string.Empty;

    /// <summary>Progress of the in-flight install/upgrade/uninstall, or null when none is running.</summary>
    public OpProgress? OpProgress { get; set; }

    public int ViewGeneration { get; private set; }
    public int DetailGeneration { get; private set; }

    public int BumpViewGeneration () => ++ViewGeneration;
    public int BumpDetailGeneration () => ++DetailGeneration;

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

        if (Mode != AppMode.Search)
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

    public void CyclePinFilter ()
    {
        PinFilter = PinFilter switch
        {
            PinFilter.All => PinFilter.PinnedOnly,
            PinFilter.PinnedOnly => PinFilter.UnpinnedOnly,
            _ => PinFilter.All
        };
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
