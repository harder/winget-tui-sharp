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
    public void Set_ReplacementReaccountsMutatedReturnedCopy ()
    {
        BoundedDetailCache cache = new (maxEntries: 10, maxRetainedCharacters: 500);
        Assert.True (cache.Set ("a", Detail ("a", "short")));
        int before = cache.RetainedCharacters;
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
        int firstCharacters = cache.RetainedCharacters;
        cache.Set ("b", Detail ("b", "two"));
        int bothCharacters = cache.RetainedCharacters;
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
            MatchField = "match"
        };
}
