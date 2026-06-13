// Spike: exercise the smallest end-to-end path through the WinGet COM API
// that touches the types we'd use in a real backend, and — critically — probe
// the AOT failure mode we hit on the first Windows run:
//
//   Unhandled exception. System.InvalidCastException: Specified cast is not valid.
//      at System.Collections.Generic.IReadOnlyListImpl`1.Make_IEnumerableObjRef()
//      at System.Collections.Generic.IReadOnlyListImpl`1.GetEnumerator()
//
// Diagnosis: AOT codegen + COM activation both work (we got "3 catalogs"). The
// failure is enumerating a CsWinRT-projected IReadOnlyList<T> via foreach, which
// goes through IIterable<T> — a generic-instantiation RCW factory that must be
// generated in the *consuming* app. `.Count` (IVectorView.Size) works without it;
// `foreach` (IIterable.First) does not.
//
// This spike now tests TWO independent fixes in a single run so one round-trip on
// Windows tells us exactly what the real backend needs:
//   1. Config: CsWinRTRcwFactoryFallbackGeneratorForceOptIn=true (see .csproj) —
//      should make `foreach` work by generating the RCW factories.
//   2. Code:   indexed access via IVectorView.GetAt (list[i]) — should work
//      regardless, since `.Count` already does.
//
// For each projected collection we touch, Probe() reports whether foreach works
// and whether indexing works, so the output is a decision table, not a crash.

using Microsoft.Management.Deployment;

int Run (string query)
{
    Console.WriteLine ($"[spike] querying winget COM for: {query}");

    PackageManager pm = new ();

    // Step 1: enumerate available catalogs. Verifies COM activation + that the
    // first projected collection (IReadOnlyList<PackageCatalogReference>) can be
    // traversed under AOT — this is exactly where the original run threw.
    IReadOnlyList<PackageCatalogReference> catalogs = pm.GetPackageCatalogs ();
    Probe (
        "catalogs (IReadOnlyList<PackageCatalogReference>)",
        catalogs,
        c => Console.WriteLine ($"      - {c.Info.Name} ({c.Info.Type})"));

    // Step 2: connect to the default winget catalog.
    PackageCatalogReference wingetRef = pm.GetPackageCatalogByName ("winget");
    Console.WriteLine ("[spike] connecting to winget catalog…");
    ConnectResult connect = wingetRef.ConnectAsync ().AsTask ().GetAwaiter ().GetResult ();

    if (connect.Status != ConnectResultStatus.Ok)
    {
        Console.Error.WriteLine ($"[spike] connect failed: {connect.Status}");

        return 1;
    }

    PackageCatalog catalog = connect.PackageCatalog;

    // Step 3: find packages.
    FindPackagesOptions opts = new ();
    PackageMatchFilter filter = new ()
    {
        Field = PackageMatchField.Name,
        Option = PackageFieldMatchOption.ContainsCaseInsensitive,
        Value = query
    };
    opts.Filters.Add (filter);

    Console.WriteLine ($"[spike] running FindPackagesAsync…");
    FindPackagesResult result = catalog.FindPackagesAsync (opts).AsTask ().GetAwaiter ().GetResult ();

    // Step 4: traverse the matches (IReadOnlyList<MatchResult>) and, per package,
    // the AvailableVersions list (IReadOnlyList<PackageVersionId>) — the two other
    // projected-collection instantiations the real backend depends on.
    Probe (
        "matches (IReadOnlyList<MatchResult>)",
        result.Matches,
        m =>
        {
            CatalogPackage pkg = m.CatalogPackage;
            PackageVersionInfo? installed = pkg.InstalledVersion;
            IReadOnlyList<PackageVersionId> available = pkg.AvailableVersions;
            string installedV = installed?.Version ?? "<not installed>";
            string latestV = available.Count > 0 ? GetFirstVersion (available) : "<no remote versions>";
            Console.WriteLine ($"      · {pkg.Id}  name={pkg.Name}  installed={installedV}  latest={latestV}");
        });

    // Step 5: metadata field extraction — the richer-detail-panel fields. The real
    // backend's ShowAsync funnels these through StringVector/DocLinks, which SWALLOW
    // exceptions and return null. So if a richer field silently never appears in the
    // UI, the cause is hidden. Here we probe each field with no swallowing, to see
    // whether the AOT trap extends to IReadOnlyList<string>/<Documentation> and to
    // the PublisherSupportUrl getter.
    Console.WriteLine ();
    Console.WriteLine ("[spike] === metadata probe (richer detail fields) ===");

    if (result.Matches.Count > 0)
    {
        CatalogPackage pkg = result.Matches [0].CatalogPackage;
        Console.WriteLine ($"[spike] probing metadata for: {pkg.Id}");

        PackageVersionInfo? vi = pkg.DefaultInstallVersion;

        if (vi is null)
        {
            Console.WriteLine ("[spike] DefaultInstallVersion is null — cannot fetch metadata");
        }
        else
        {
            CatalogPackageMetadata? meta = null;

            try
            {
                meta = vi.GetCatalogPackageMetadata ();
            }
            catch (Exception ex)
            {
                Console.WriteLine ($"[spike] GetCatalogPackageMetadata threw {ex.GetType ().Name}: {ex.Message}");
            }

            if (meta is not null)
            {
                ProbeString ("meta.Publisher (simple getter, known-good)", () => meta.Publisher);
                ProbeString ("meta.PublisherSupportUrl (simple getter, suspect)", () => meta.PublisherSupportUrl);
                Probe ("meta.Tags (IReadOnlyList<string>)", meta.Tags, t => Console.WriteLine ($"      · tag={t}"));
                Probe ("meta.Documentations (IReadOnlyList<Documentation>)", meta.Documentations, d => Console.WriteLine ($"      · {d.DocumentLabel}={d.DocumentUrl}"));
            }

            Probe ("vi.ProductCodes (IReadOnlyList<string>)", vi.ProductCodes, c => Console.WriteLine ($"      · pc={c}"));
            Probe ("vi.PackageFamilyNames (IReadOnlyList<string>)", vi.PackageFamilyNames, f => Console.WriteLine ($"      · pfn={f}"));
        }
    }

    // NOTE: a composite-catalog metadata probe (mirroring the backend's ShowAsync, which uses
    // CreateCompositePackageCatalog over winget+msstore) was attempted here but threw
    // E_ILLEGAL_STATE_CHANGE because this process already connected the 'winget' catalog
    // DIRECTLY in Step 2 — re-referencing an already-connected source for a composite on the
    // same process is illegal. To test the composite path cleanly, do it in a FRESH process
    // that never does a direct single-catalog connect (or use the app's --comshow diagnostic;
    // see HANDOFF.md). This matters: ShowAsync uses the composite path and is the suspect for
    // the richer-detail-fields-missing bug, even though this single-catalog Step 5 returns them.

    Console.WriteLine ("[spike] done.");

    return 0;
}

// Probe a simple (non-collection) string getter: does it throw, or return empty?
void ProbeString (string label, Func<string?> get)
{
    try
    {
        string? v = get ();
        Console.WriteLine ($"[spike] {label}: {(string.IsNullOrEmpty (v) ? "<empty>" : v)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine ($"[spike] {label}: ✗ threw {ex.GetType ().Name}: {ex.Message}");
    }
}

// Read the first element of a projected list via indexing only (IVectorView.GetAt),
// deliberately avoiding foreach so this never trips the IIterable<T> path.
string GetFirstVersion (IReadOnlyList<PackageVersionId> versions)
    => versions [0].Version;

// Traverse a projected IReadOnlyList<T> both ways and report which paths survive AOT.
// `.Count` is assumed to work (it did in the original run); the question is foreach
// (IIterable<T>) vs indexing (IVectorView.GetAt).
void Probe<T> (string label, IReadOnlyList<T> list, Action<T> print)
{
    int count;

    try
    {
        count = list.Count;
    }
    catch (Exception ex)
    {
        Console.WriteLine ($"[spike] {label}: ✗ even .Count threw {ex.GetType ().Name}: {ex.Message}");

        return;
    }

    Console.WriteLine ($"[spike] {label}: {count} item(s)");

    // Path A — foreach, i.e. IIterable<T>.First/IIterator<T>. This is the path that
    // threw on the first AOT run. If the RCW factory fallback fix worked, it now runs.
    bool foreachOk;

    try
    {
        int shown = 0;

        foreach (T item in list)
        {
            print (item);

            if (++shown >= 5)
            {
                break;
            }
        }

        foreachOk = true;
        Console.WriteLine ($"[spike]   ✓ foreach (IIterable<{typeof (T).Name}>) WORKS under AOT");
    }
    catch (Exception ex)
    {
        foreachOk = false;
        Console.WriteLine ($"[spike]   ✗ foreach threw {ex.GetType ().Name}: {ex.Message}");
    }

    if (foreachOk)
    {
        return;
    }

    // Path B — indexed access via IVectorView.GetAt. Same ABI surface as .Count,
    // so this is the robust fallback for the real backend if config can't cover
    // every instantiation.
    try
    {
        for (int i = 0; i < Math.Min (count, 5); i++)
        {
            print (list [i]);
        }

        Console.WriteLine ($"[spike]   ✓ indexed for-loop (IVectorView.GetAt) WORKS — use this pattern in the backend");
    }
    catch (Exception ex)
    {
        Console.WriteLine ($"[spike]   ✗ indexed access ALSO threw {ex.GetType ().Name}: {ex.Message} — problem is deeper than enumeration");
    }
}

return Run (args.Length > 0 ? args [0] : "powertoys");
