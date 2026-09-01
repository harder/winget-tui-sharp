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
    private readonly int _maxRetainedCharacters;
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new (StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Entry> _recency = new ();
    private int _retainedCharacters;

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

    internal int RetainedCharacters
    {
        get
        {
            lock (_gate)
            {
                return _retainedCharacters;
            }
        }
    }

    internal int MaxEntries => _maxEntries;
    internal int MaxRetainedCharacters => _maxRetainedCharacters;

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
        int retainedCharacters = ownedDetail is null
                                     ? _maxRetainedCharacters + 1
                                     : MeasureRetainedCharacters (key, ownedDetail, _maxRetainedCharacters);

        lock (_gate)
        {
            RemoveCore (key);

            if (retainedCharacters > _maxRetainedCharacters)
            {
                return false;
            }

            Entry entry = new (key, ownedDetail!, retainedCharacters);
            LinkedListNode<Entry> node = _recency.AddFirst (entry);
            _entries.Add (key, node);
            _retainedCharacters += retainedCharacters;

            while (_entries.Count > _maxEntries || _retainedCharacters > _maxRetainedCharacters)
            {
                LinkedListNode<Entry> oldest = _recency.Last!;
                _recency.RemoveLast ();
                _entries.Remove (oldest.Value.Key);
                _retainedCharacters -= oldest.Value.RetainedCharacters;
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

        return true;
    }

    private static int MeasureRetainedCharacters (string key, PackageDetail detail, int budget)
    {
        long total = key.Length;

        void Add (string? value)
        {
            if (value is not null && total <= budget)
            {
                total += value.Length;
            }
        }

        void AddAll (IReadOnlyList<string>? values)
        {
            if (values is null)
            {
                return;
            }

            foreach (string value in values)
            {
                Add (value);
            }
        }

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
            foreach (DocLink link in detail.Documentation)
            {
                Add (link.Label);
                Add (link.Url);
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

        return total > budget ? budget + 1 : (int)total;
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
        long collectionValues = (detail.Tags?.Count ?? 0)
                                + (detail.Documentation?.Count ?? 0)
                                + (detail.ProductCodes?.Count ?? 0)
                                + (detail.PackageFamilyNames?.Count ?? 0);

        if (collectionValues > MaxCollectionValuesPerEntry)
        {
            return null;
        }

        PackageDetail clone = Clone (detail);
        collectionValues = (clone.Tags?.Count ?? 0)
                           + (clone.Documentation?.Count ?? 0)
                           + (clone.ProductCodes?.Count ?? 0)
                           + (clone.PackageFamilyNames?.Count ?? 0);

        return collectionValues <= MaxCollectionValuesPerEntry ? clone : null;
    }

    private sealed record Entry (string Key, PackageDetail Detail, int RetainedCharacters);
}
