namespace WingetTuiSharp;

/// <summary>
/// Thread-safe LRU for manifest details. Entries are copied on ingress and egress so callers
/// cannot mutate retained values without going back through <see cref="Set"/> for re-accounting.
/// </summary>
internal sealed class BoundedDetailCache
{
    internal const int DefaultMaxEntries = 128;

    // Package details are normally only a few KiB. A two-million-character ceiling leaves ample
    // room for rich manifests while keeping the cache's string payload near 4 MiB on modern .NET.
    internal const int DefaultMaxRetainedCharacters = 2 * 1024 * 1024;

    // Character accounting alone cannot bound arrays containing millions of empty strings. This
    // secondary structural ceiling keeps collection reference/storage overhead bounded as well.
    internal const int MaxCollectionValuesPerEntry = 4_096;

    private readonly object _gate = new ();
    private readonly int _maxEntries;
    private readonly long _maxRetainedCharacters;
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new (StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Entry> _recency = new ();
    private long _retainedCharacters;
    private long _retainedCollectionValues;

    internal BoundedDetailCache (
        int maxEntries = DefaultMaxEntries,
        int maxRetainedCharacters = DefaultMaxRetainedCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan (maxEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan (maxRetainedCharacters, 1);
        _maxEntries = maxEntries;
        _maxRetainedCharacters = maxRetainedCharacters;
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    internal long RetainedCharacters
    {
        get
        {
            lock (_gate)
            {
                return _retainedCharacters;
            }
        }
    }

    internal long EstimatedCharacters => RetainedCharacters;

    internal long RetainedCollectionValues
    {
        get
        {
            lock (_gate)
            {
                return _retainedCollectionValues;
            }
        }
    }

    internal int MaxEntries => _maxEntries;
    internal long MaxRetainedCharacters => _maxRetainedCharacters;
    internal long MaxRetainedCollectionValues => (long)_maxEntries * MaxCollectionValuesPerEntry;

    internal CacheMetrics GetMetrics ()
    {
        lock (_gate)
        {
            return new (_entries.Count, _retainedCharacters, _retainedCollectionValues);
        }
    }

    internal bool TryGet (string key, out PackageDetail detail)
    {
        ArgumentNullException.ThrowIfNull (key);

        lock (_gate)
        {
            if (!_entries.TryGetValue (key, out LinkedListNode<Entry>? node))
            {
                detail = null!;

                return false;
            }

            _recency.Remove (node);
            _recency.AddFirst (node);
            detail = Clone (node.Value.Detail);

            return true;
        }
    }

    /// <summary>
    /// Adds or replaces an entry. Returns false when the entry alone exceeds the entire budget;
    /// an earlier value for the same key is removed in that case so it cannot remain stale.
    /// </summary>
    internal bool Set (string key, PackageDetail detail)
    {
        ArgumentNullException.ThrowIfNull (key);
        ArgumentNullException.ThrowIfNull (detail);

        PackageDetail? ownedDetail = TryCloneForRetention (detail);
        long retainedCharacters = ownedDetail is null
                                      ? 0
                                      : MeasureRetainedCharacters (key, ownedDetail, _maxRetainedCharacters);

        lock (_gate)
        {
            RemoveCore (key);

            if (ownedDetail is null || retainedCharacters > _maxRetainedCharacters)
            {
                return false;
            }

            int collectionValues = (int)CountCollectionValues (ownedDetail!);
            Entry entry = new (key, ownedDetail!, retainedCharacters, collectionValues);
            LinkedListNode<Entry> node = _recency.AddFirst (entry);
            _entries.Add (key, node);
            _retainedCharacters += retainedCharacters;
            _retainedCollectionValues += collectionValues;

            while (_entries.Count > _maxEntries || _retainedCharacters > _maxRetainedCharacters)
            {
                LinkedListNode<Entry> oldest = _recency.Last!;
                _recency.RemoveLast ();
                _entries.Remove (oldest.Value.Key);
                _retainedCharacters -= oldest.Value.RetainedCharacters;
                _retainedCollectionValues -= oldest.Value.CollectionValues;
            }

            return true;
        }
    }

    internal bool Remove (string key)
    {
        ArgumentNullException.ThrowIfNull (key);

        lock (_gate)
        {
            return RemoveCore (key);
        }
    }

    internal void Clear ()
    {
        lock (_gate)
        {
            _entries.Clear ();
            _recency.Clear ();
            _retainedCharacters = 0;
            _retainedCollectionValues = 0;
        }
    }

    private bool RemoveCore (string key)
    {
        if (!_entries.Remove (key, out LinkedListNode<Entry>? node))
        {
            return false;
        }

        _recency.Remove (node);
        _retainedCharacters -= node.Value.RetainedCharacters;
        _retainedCollectionValues -= node.Value.CollectionValues;

        return true;
    }

    private static long MeasureRetainedCharacters (string key, PackageDetail detail, long budget)
    {
        long total = 0;

        void Add (string? value)
        {
            if (value is null || total > budget)
            {
                return;
            }

            // Saturate at one past the int-backed configured budget. This is sufficient for
            // rejection and cannot wrap the retained counter.
            long remaining = budget - total;
            total = value.Length > remaining ? checked(budget + 1) : checked(total + value.Length);
        }

        void AddAll (IReadOnlyList<string>? values)
        {
            if (values is null)
            {
                return;
            }

            foreach (string? value in values)
            {
                Add (value);
            }
        }

        Add (key);
        Add (detail.Id);
        Add (detail.Name);
        Add (detail.Version);
        Add (detail.AvailableVersion);
        Add (detail.InstalledVersion);
        Add (detail.Source);
        Add (detail.PinState.GatingVersion);
        Add (detail.Publisher);
        Add (detail.Description);
        Add (detail.Homepage);
        Add (detail.License);
        Add (detail.ReleaseNotesUrl);
        Add (detail.SupportUrl);
        AddAll (detail.Tags);

        if (detail.Documentation is not null)
        {
            foreach (DocLink? link in detail.Documentation)
            {
                Add (link?.Label);
                Add (link?.Url);
            }
        }

        AddAll (detail.ProductCodes);
        AddAll (detail.PackageFamilyNames);
        Add (detail.Author);
        Add (detail.Copyright);
        Add (detail.PrivacyUrl);
        Add (detail.PurchaseUrl);
        Add (detail.InstallationNotes);
        Add (detail.InstalledLocation);
        Add (detail.InstalledScope);
        Add (detail.MatchField);

        return total;
    }

    private static PackageDetail Clone (PackageDetail detail) =>
        new ()
        {
            Id = detail.Id,
            Name = detail.Name,
            Version = detail.Version,
            AvailableVersion = detail.AvailableVersion,
            InstalledVersion = detail.InstalledVersion,
            Source = detail.Source,
            PinState = detail.PinState,
            Publisher = detail.Publisher,
            Description = detail.Description,
            Homepage = detail.Homepage,
            License = detail.License,
            ReleaseNotesUrl = detail.ReleaseNotesUrl,
            SupportUrl = detail.SupportUrl,
            Tags = detail.Tags is null ? null : [.. detail.Tags],
            Documentation = detail.Documentation is null ? null : [.. detail.Documentation],
            ProductCodes = detail.ProductCodes is null ? null : [.. detail.ProductCodes],
            PackageFamilyNames = detail.PackageFamilyNames is null ? null : [.. detail.PackageFamilyNames],
            Author = detail.Author,
            Copyright = detail.Copyright,
            PrivacyUrl = detail.PrivacyUrl,
            PurchaseUrl = detail.PurchaseUrl,
            InstallationNotes = detail.InstallationNotes,
            InstalledLocation = detail.InstalledLocation,
            InstalledScope = detail.InstalledScope,
            MatchField = detail.MatchField,
            IsDescriptionDegraded = detail.IsDescriptionDegraded
        };

    private static PackageDetail? TryCloneForRetention (PackageDetail detail)
    {
        long collectionValues = CountCollectionValues (detail);

        if (collectionValues > MaxCollectionValuesPerEntry)
        {
            return null;
        }

        PackageDetail clone = Clone (detail);
        collectionValues = CountCollectionValues (clone);

        return collectionValues <= MaxCollectionValuesPerEntry ? clone : null;
    }

    private static long CountCollectionValues (PackageDetail detail) =>
        (long)(detail.Tags?.Count ?? 0)
        + (detail.Documentation?.Count ?? 0)
        + (detail.ProductCodes?.Count ?? 0)
        + (detail.PackageFamilyNames?.Count ?? 0);

    internal readonly record struct CacheMetrics (int Count, long EstimatedCharacters, long CollectionValues);

    private sealed record Entry (
        string Key,
        PackageDetail Detail,
        long RetainedCharacters,
        int CollectionValues);
}
