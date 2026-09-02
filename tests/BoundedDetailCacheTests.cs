using System.Collections;

namespace WingetTuiSharp.Tests;

public sealed class BoundedDetailCacheTests
{
    [Fact]
    public void Set_EvictsLeastRecentEntryAtCountLimit ()
    {
        BoundedDetailCache cache = new (maxEntries: 2, maxRetainedCharacters: 1_000);

        Assert.True (cache.Set ("a", Detail ("a")));
        Assert.True (cache.Set ("b", Detail ("b")));
        Assert.True (cache.Set ("c", Detail ("c")));

        Assert.False (cache.TryGet ("a", out _));
        Assert.True (cache.TryGet ("b", out _));
        Assert.True (cache.TryGet ("c", out _));
        Assert.Equal (2, cache.Count);
    }

    [Fact]
    public void Set_EvictsLeastRecentEntryAtCharacterLimit ()
    {
        BoundedDetailCache cache = new (maxEntries: 10, maxRetainedCharacters: 50);

        Assert.True (cache.Set ("a", Detail ("a", new string ('x', 30))));
        Assert.True (cache.Set ("b", Detail ("b", new string ('y', 30))));

        Assert.False (cache.TryGet ("a", out _));
        Assert.True (cache.TryGet ("b", out _));
        Assert.InRange (cache.RetainedCharacters, 1, cache.MaxRetainedCharacters);
    }

    [Fact]
    public void Set_RejectsOversizeEntryAndRemovesStaleReplacement ()
    {
        BoundedDetailCache cache = new (maxEntries: 10, maxRetainedCharacters: 30);
        Assert.True (cache.Set ("a", Detail ("a")));

        Assert.False (cache.Set ("A", Detail ("a", new string ('x', 100))));

        Assert.Equal (0, cache.Count);
        Assert.Equal (0, cache.RetainedCharacters);
        Assert.False (cache.TryGet ("a", out _));
    }

    [Fact]
    public void Set_RejectsStructurallyOversizeEmptyCollections ()
    {
        BoundedDetailCache cache = new (maxEntries: 10, maxRetainedCharacters: 1_000);
        PackageDetail detail = Detail ("a");
        detail = new ()
        {
            Id = detail.Id,
            Name = detail.Name,
            Tags = Enumerable.Repeat (string.Empty, BoundedDetailCache.MaxCollectionValuesPerEntry + 1).ToArray ()
        };

        Assert.False (cache.Set ("a", detail));
        Assert.Equal (0, cache.Count);
        Assert.Equal (0, cache.RetainedCharacters);
    }

    [Fact]
    public void Set_NeverReadsPastTheCountACollectionReports ()
    {
        BoundedDetailCache cache = new (maxEntries: 10, maxRetainedCharacters: 1_000_000);
        LyingList flood = new (
            reportedCount: 2,
            actual: [.. Enumerable.Repeat ("tag", BoundedDetailCache.MaxCollectionValuesPerEntry * 4)]);

        // Count is the only thing the budget can be checked against, so it is also the only thing
        // that gets read. The oversized tail is never enumerated and never allocated — which is
        // what keeps an endless (or merely huge) backend sequence from escaping the entry ceiling.
        Assert.True (cache.Set ("a", new () { Id = "a", Name = "a", Tags = flood }));
        Assert.Equal (2, cache.RetainedCollectionValues);
        Assert.Equal (2, flood.IndexerReads);
        Assert.Equal (0, flood.EnumerationCount);

        Assert.True (cache.TryGet ("a", out PackageDetail retained));
        Assert.Equal (2, retained.Tags!.Count);
    }

    [Fact]
    public void Set_RejectsCollectionThatGrowsDuringTheCopy ()
    {
        BoundedDetailCache cache = new (maxEntries: 10, maxRetainedCharacters: 1_000_000);

        // Count moves between the budget check and the end of the copy, so the retained size can
        // no longer be reconciled with what was charged against the allowance.
        GrowingList shifting = new (["a", "b", "c"]);

        Assert.False (cache.Set ("a", new () { Id = "a", Name = "a", ProductCodes = shifting }));
        Assert.Equal (0, cache.Count);
        Assert.Equal (0, cache.RetainedCollectionValues);
    }

    [Fact]
    public void Set_SharesOneItemAllowanceAcrossEveryCollection ()
    {
        BoundedDetailCache cache = new (maxEntries: 10, maxRetainedCharacters: 1_000_000);
        int half = BoundedDetailCache.MaxCollectionValuesPerEntry / 2;
        string [] chunk = [.. Enumerable.Repeat (string.Empty, half + 1)];

        // Neither list exceeds the per-entry ceiling alone, but together they do.
        Assert.False (cache.Set ("a", new () { Id = "a", Name = "a", Tags = chunk, ProductCodes = chunk }));
        Assert.Equal (0, cache.Count);

        Assert.True (cache.Set ("b", new () { Id = "b", Name = "b", Tags = chunk }));
        Assert.Equal (half + 1, cache.RetainedCollectionValues);
    }

    [Fact]
    public void Set_ReplacementReaccountsMutatedReturnedCopy ()
    {
        BoundedDetailCache cache = new (maxEntries: 10, maxRetainedCharacters: 500);
        Assert.True (cache.Set ("a", Detail ("a", "short")));
        long before = cache.RetainedCharacters;
        Assert.True (cache.TryGet ("a", out PackageDetail returned));

        returned.Description = new string ('z', 100);

        // Get returns a snapshot, so mutation cannot silently change retained cache accounting.
        Assert.Equal (before, cache.RetainedCharacters);
        Assert.True (cache.Set ("a", returned));
        BoundedDetailCache expected = new (maxEntries: 10, maxRetainedCharacters: 500);
        expected.Set ("a", returned);
        Assert.Equal (expected.RetainedCharacters, cache.RetainedCharacters);
        Assert.True (cache.RetainedCharacters > before);
        Assert.Equal (1, cache.Count);
    }

    [Fact]
    public void TryGet_RefreshesRecency ()
    {
        BoundedDetailCache cache = new (maxEntries: 2, maxRetainedCharacters: 1_000);
        cache.Set ("a", Detail ("a"));
        cache.Set ("b", Detail ("b"));

        Assert.True (cache.TryGet ("a", out _));
        cache.Set ("c", Detail ("c"));

        Assert.True (cache.TryGet ("a", out _));
        Assert.False (cache.TryGet ("b", out _));
        Assert.True (cache.TryGet ("c", out _));
    }

    [Fact]
    public void Keys_AreCaseInsensitive ()
    {
        BoundedDetailCache cache = new (maxEntries: 10, maxRetainedCharacters: 1_000);
        cache.Set ("Acme.Foo", Detail ("Acme.Foo", "first"));
        cache.Set ("ACME.FOO", Detail ("Acme.Foo", "second"));

        Assert.Equal (1, cache.Count);
        Assert.True (cache.TryGet ("acme.foo", out PackageDetail detail));
        Assert.Equal ("second", detail.Description);
    }

    [Fact]
    public void RemoveAndClear_ReleaseExactAccounting ()
    {
        BoundedDetailCache cache = new (maxEntries: 10, maxRetainedCharacters: 1_000);
        cache.Set ("a", Detail ("a", "one"));
        long firstCharacters = cache.RetainedCharacters;
        cache.Set ("b", Detail ("b", "two"));
        long bothCharacters = cache.RetainedCharacters;
        Assert.True (cache.Remove ("A"));
        Assert.False (cache.Remove ("missing"));
        Assert.Equal (1, cache.Count);
        Assert.Equal (bothCharacters - firstCharacters, cache.RetainedCharacters);

        cache.Clear ();

        Assert.Equal (0, cache.Count);
        Assert.Equal (0, cache.RetainedCharacters);
    }

    [Fact]
    public void EveryOperation_PreservesHardLimits ()
    {
        BoundedDetailCache cache = new (maxEntries: 7, maxRetainedCharacters: 200);

        for (int index = 0; index < 100; index++)
        {
            string key = $"package-{index % 13}";
            PackageDetail detail = FullDetail (key, new string ((char)('a' + index % 26), index % 80));
            cache.Set (key, detail);
            cache.TryGet ($"PACKAGE-{index % 13}", out _);

            Assert.InRange (cache.Count, 0, cache.MaxEntries);
            Assert.InRange (cache.RetainedCharacters, 0, cache.MaxRetainedCharacters);
        }
    }

    [Fact]
    public void IntMaxBudget_StructuralOversizeRejectsWithoutOverflowOrNullDereference ()
    {
        BoundedDetailCache cache = new (
            maxEntries: int.MaxValue,
            maxRetainedCharacters: int.MaxValue);
        PackageDetail detail = new ()
        {
            Id = "oversize",
            Name = "oversize",
            Tags = Enumerable.Repeat (string.Empty, BoundedDetailCache.MaxCollectionValuesPerEntry + 1).ToArray ()
        };

        Assert.False (cache.Set ("oversize", detail));
        BoundedDetailCache.CacheMetrics metrics = cache.GetMetrics ();
        Assert.Equal (0, metrics.Count);
        Assert.Equal (0, metrics.EstimatedCharacters);
        Assert.Equal (0, metrics.CollectionValues);
        Assert.True (cache.MaxRetainedCollectionValues > int.MaxValue);
    }

    [Fact]
    public void ExactBudgetAddAndOversizeReplacement_CannotWrapOrBypassEviction ()
    {
        const int budget = 64;
        BoundedDetailCache cache = new (maxEntries: 4, maxRetainedCharacters: budget);
        PackageDetail exact = Detail ("a", new string ('x', budget - 3));
        Assert.True (cache.Set ("a", exact));
        Assert.Equal (budget, cache.EstimatedCharacters);

        Assert.True (cache.Set ("b", Detail ("b")));
        Assert.False (cache.TryGet ("a", out _));
        Assert.True (cache.TryGet ("b", out _));
        Assert.InRange (cache.EstimatedCharacters, 0, cache.MaxRetainedCharacters);

        Assert.False (cache.Set ("B", Detail ("b", new string ('y', budget))));
        Assert.False (cache.TryGet ("b", out _));
        BoundedDetailCache.CacheMetrics metrics = cache.GetMetrics ();
        Assert.Equal (0, metrics.Count);
        Assert.Equal (0, metrics.EstimatedCharacters);
        Assert.Equal (0, metrics.CollectionValues);
    }

    [Fact]
    public void SetGetReplacement_RoundTripsEveryDetailFieldAndAccountsExactly ()
    {
        BoundedDetailCache cache = new (maxEntries: 4, maxRetainedCharacters: 10_000);
        PackageDetail original = FullDetail ("full-id", "full-description");
        const string key = "cache-key";

        Assert.True (cache.Set (key, original));
        Assert.True (cache.TryGet (key, out PackageDetail returned));

        AssertDetailEqual (original, returned);
        Assert.NotSame (original.Tags, returned.Tags);
        Assert.NotSame (original.Documentation, returned.Documentation);
        Assert.NotSame (original.ProductCodes, returned.ProductCodes);
        Assert.NotSame (original.PackageFamilyNames, returned.PackageFamilyNames);
        Assert.Equal (ExpectedCharacters (key, original), cache.EstimatedCharacters);
        Assert.Equal (5, cache.RetainedCollectionValues);

        returned.Version = "replacement-version";
        returned.AvailableVersion = "replacement-available";
        returned.InstalledVersion = "replacement-installed";
        returned.Source = "replacement-source";
        returned.PinState = new (PinStateKind.Gating, "replacement-gate");
        returned.Description = "replacement-description";
        returned.MatchField = "replacement-match";
        returned.IsDescriptionDegraded = false;

        Assert.True (cache.Set (key, returned));
        Assert.Equal (ExpectedCharacters (key, returned), cache.EstimatedCharacters);
        Assert.True (cache.TryGet (key, out PackageDetail replacement));
        AssertDetailEqual (returned, replacement);
    }

    [Fact]
    public void ConcurrentOperations_PreserveAllHardInvariants ()
    {
        BoundedDetailCache cache = new (maxEntries: 17, maxRetainedCharacters: 2_000);
        System.Collections.Concurrent.ConcurrentQueue<Exception> failures = new ();

        Parallel.For (
            0,
            8,
            new ParallelOptions { CancellationToken = TestContext.Current.CancellationToken },
            worker =>
            {
                Random random = new (0x51A7 + worker);

                for (int iteration = 0; iteration < 2_000; iteration++)
                {
                    try
                    {
                        string key = $"package-{random.Next (40)}";

                        switch (random.Next (4))
                        {
                            case 0:
                                cache.Set (
                                    key,
                                    new ()
                                    {
                                        Id = key,
                                        Name = $"name-{worker}",
                                        Description = new string ((char)('a' + worker), random.Next (0, 250)),
                                        Tags = Enumerable.Repeat ("tag", random.Next (0, 12)).ToArray ()
                                    });
                                break;
                            case 1:
                                cache.TryGet (key.ToUpperInvariant (), out _);
                                break;
                            case 2:
                                cache.Remove (key);
                                break;
                            default:
                                cache.Clear ();
                                break;
                        }

                        BoundedDetailCache.CacheMetrics metrics = cache.GetMetrics ();

                        if (metrics.Count < 0 || metrics.Count > cache.MaxEntries
                            || metrics.EstimatedCharacters < 0
                            || metrics.EstimatedCharacters > cache.MaxRetainedCharacters
                            || metrics.CollectionValues < 0
                            || metrics.CollectionValues > cache.MaxRetainedCollectionValues)
                        {
                            failures.Enqueue (new InvalidOperationException ($"Invalid cache metrics: {metrics}"));
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue (ex);
                    }
                }
            });

        Assert.Empty (failures);
        BoundedDetailCache.CacheMetrics final = cache.GetMetrics ();
        Assert.InRange (final.Count, 0, cache.MaxEntries);
        Assert.InRange (final.EstimatedCharacters, 0, cache.MaxRetainedCharacters);
        Assert.InRange (final.CollectionValues, 0, cache.MaxRetainedCollectionValues);
    }

    [Fact]
    public void AppState_CacheRefreshReaccountsAndInvalidationRemovesEntry ()
    {
        AppState state = new (new MockBackend ());
        PackageDetail detail = new () { Id = "Acme.Foo", Name = "Foo" };
        Assert.True (state.CacheDetail (detail.Id, detail));
        Package context = new ()
        {
            Id = detail.Id,
            Name = detail.Name,
            Version = "1.2.3",
            AvailableVersion = "2.0.0",
            InstalledVersion = "1.2.3",
            Source = "winget",
            MatchField = "Tag"
        };

        Assert.True (state.TryGetCachedDetail (context, out PackageDetail refreshed));
        Assert.Equal ("1.2.3", refreshed.Version);
        Assert.Equal ("2.0.0", refreshed.AvailableVersion);
        Assert.Equal ("1.2.3", refreshed.InstalledVersion);
        Assert.Equal ("winget", refreshed.Source);
        Assert.Equal ("Tag", refreshed.MatchField);

        Assert.True (state.TryGetCachedDetail (context, out PackageDetail persisted));
        AssertDetailEqual (refreshed, persisted);
        Assert.True (state.InvalidateCachedDetail (context.Id));
        Assert.False (state.TryGetCachedDetail (context, out _));
    }

    private static PackageDetail Detail (string id, string? description = null) =>
        new () { Id = id, Name = id, Description = description };

    private static PackageDetail FullDetail (string id, string description) =>
        new ()
        {
            Id = id,
            Name = "name",
            Version = "version",
            AvailableVersion = "available",
            InstalledVersion = "installed",
            Source = "source",
            PinState = new (PinStateKind.Gating, "gate"),
            Publisher = "publisher",
            Description = description,
            Homepage = "homepage",
            License = "license",
            ReleaseNotesUrl = "release",
            SupportUrl = "support",
            Tags = ["tag1", "tag2"],
            Documentation = [new ("doc", "url")],
            ProductCodes = ["product"],
            PackageFamilyNames = ["family"],
            Author = "author",
            Copyright = "copyright",
            PrivacyUrl = "privacy",
            PurchaseUrl = "purchase",
            InstallationNotes = "notes",
            InstalledLocation = "location",
            InstalledScope = "scope",
            MatchField = "match",
            IsDescriptionDegraded = true
        };

    private static int ExpectedCharacters (string key, PackageDetail detail)
    {
        IEnumerable<string?> scalars =
        [
            key,
            detail.Id,
            detail.Name,
            detail.Version,
            detail.AvailableVersion,
            detail.InstalledVersion,
            detail.Source,
            detail.PinState.GatingVersion,
            detail.Publisher,
            detail.Description,
            detail.Homepage,
            detail.License,
            detail.ReleaseNotesUrl,
            detail.SupportUrl,
            detail.Author,
            detail.Copyright,
            detail.PrivacyUrl,
            detail.PurchaseUrl,
            detail.InstallationNotes,
            detail.InstalledLocation,
            detail.InstalledScope,
            detail.MatchField
        ];

        return scalars.Sum (value => value?.Length ?? 0)
               + (detail.Tags?.Sum (value => value.Length) ?? 0)
               + (detail.Documentation?.Sum (link => link.Label.Length + link.Url.Length) ?? 0)
               + (detail.ProductCodes?.Sum (value => value.Length) ?? 0)
               + (detail.PackageFamilyNames?.Sum (value => value.Length) ?? 0);
    }

    private static void AssertDetailEqual (PackageDetail expected, PackageDetail actual)
    {
        Assert.Equal (expected.Id, actual.Id);
        Assert.Equal (expected.Name, actual.Name);
        Assert.Equal (expected.Version, actual.Version);
        Assert.Equal (expected.AvailableVersion, actual.AvailableVersion);
        Assert.Equal (expected.InstalledVersion, actual.InstalledVersion);
        Assert.Equal (expected.Source, actual.Source);
        Assert.Equal (expected.PinState, actual.PinState);
        Assert.Equal (expected.Publisher, actual.Publisher);
        Assert.Equal (expected.Description, actual.Description);
        Assert.Equal (expected.Homepage, actual.Homepage);
        Assert.Equal (expected.License, actual.License);
        Assert.Equal (expected.ReleaseNotesUrl, actual.ReleaseNotesUrl);
        Assert.Equal (expected.SupportUrl, actual.SupportUrl);
        Assert.Equal (expected.Tags, actual.Tags);
        Assert.Equal (expected.Documentation, actual.Documentation);
        Assert.Equal (expected.ProductCodes, actual.ProductCodes);
        Assert.Equal (expected.PackageFamilyNames, actual.PackageFamilyNames);
        Assert.Equal (expected.Author, actual.Author);
        Assert.Equal (expected.Copyright, actual.Copyright);
        Assert.Equal (expected.PrivacyUrl, actual.PrivacyUrl);
        Assert.Equal (expected.PurchaseUrl, actual.PurchaseUrl);
        Assert.Equal (expected.InstallationNotes, actual.InstallationNotes);
        Assert.Equal (expected.InstalledLocation, actual.InstalledLocation);
        Assert.Equal (expected.InstalledScope, actual.InstalledScope);
        Assert.Equal (expected.MatchField, actual.MatchField);
        Assert.Equal (expected.IsDescriptionDegraded, actual.IsDescriptionDegraded);
    }

    /// <summary>
    /// A backend list whose <see cref="Count"/> disagrees with what it will actually yield —
    /// the shape the cache has to reject rather than allocate first and measure afterwards.
    /// </summary>
    private sealed class LyingList (int reportedCount, IReadOnlyList<string> actual) : IReadOnlyList<string>
    {
        internal int IndexerReads { get; private set; }
        internal int EnumerationCount { get; private set; }

        public int Count => reportedCount;

        public string this [int index]
        {
            get
            {
                IndexerReads++;

                return actual [index];
            }
        }

        public IEnumerator<string> GetEnumerator ()
        {
            EnumerationCount++;

            return actual.GetEnumerator ();
        }

        IEnumerator IEnumerable.GetEnumerator () => GetEnumerator ();
    }

    /// <summary>A list that reports one more item every time its <see cref="Count"/> is read.</summary>
    private sealed class GrowingList (IReadOnlyList<string> actual) : IReadOnlyList<string>
    {
        private int _reads;

        public int Count => Math.Min (actual.Count, ++_reads);
        public string this [int index] => actual [index];
        public IEnumerator<string> GetEnumerator () => actual.GetEnumerator ();
        IEnumerator IEnumerable.GetEnumerator () => GetEnumerator ();
    }
}
