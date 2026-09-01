// The COM backend is Windows-only: it talks to the WinGet COM API
// (Microsoft.Management.Deployment) instead of shelling out to winget.exe and parsing
// stdout. The whole file is gated on WINGET_COM, which the .csproj defines only for the
// net10.0-windows10.0.26100.0 TFM — on net10.0 this file compiles to nothing so the
// cross-platform build stays clean.
//
// === The one AOT rule ===
// NEVER `foreach` or LINQ directly over a WinRT-projected collection. Under Native AOT the
// IIterable<T> runtime-callable-wrapper for the generic instantiation isn't generated and
// enumeration throws InvalidCastException at runtime. Indexed access (IVectorView.GetAt,
// i.e. `list[i]`) works fine. Every projected list is funneled through Materialize<T>()
// below, which copies a bounded number via indexing into a normal List<T>; after that, ordinary
// foreach/LINQ on the managed copy is safe.

#if WINGET_COM
using Microsoft.Management.Deployment;

namespace WingetTuiSharp;

/// <summary>
/// <see cref="IBackend"/> implementation over the WinGet COM API. Returns structured objects
/// directly from the package manager rather than parsing CLI tabular output.
///
/// Pinning has no COM surface (the API exposes no pin/unpin/list-pins), so those three
/// operations are delegated to an internal <see cref="CliBackend"/> — winget.exe is always
/// present on a machine where the COM server is registered, so this keeps full feature parity.
///
/// Known limitations (from code review, deferred deliberately):
///  - Composite connect is all-or-nothing: a configured-but-unhealthy source (e.g. a broken
///    msstore) can fail a SourceFilter.All query even when winget alone is fine. Mitigated by
///    the in-app source filter ('f') which lets the user narrow to a single working source.
///  - Operations resolve a package by id alone (FindByIdAsync over SourceFilter.All takes the
///    first exact match). If the same id existed in multiple catalogs the wrong source could be
///    chosen. Rare in practice (winget vs msstore ids differ), and matches CliBackend's by-id
///    behavior; carrying source identity through IBackend would be a separate change.
///  - COM work is serialized through a 32-waiter bounded, cancellation-aware gate. Each admitted
///    operation creates its own PackageManager and retains it through the complete asynchronous
///    operation; no projected COM object crosses operations. All projected collections and
///    strings are bounded before managed retention. This is compile- and unit-tested from macOS,
///    but activation, apartment behavior, and cancellation still require Windows runtime testing.
///  - Pinning delegates to winget.exe, so pin/unpin/list-pins need winget on PATH even on this
///    backend. If the COM server is registered but winget.exe isn't reachable, pin operations
///    fail (visibly, via the returned OpResult) while everything else keeps working.
/// </summary>
public sealed class ComBackend : IBackend
{
    private readonly BoundedAsyncGate _comGate = new (maxQueuedWaiters: 32);

    // Pin operations fall through to the CLI — the COM API has no pinning surface.
    private readonly CliBackend _cliForPins = new ();

    public ComBackend ()
    {
        // Preserve eager activation as the backend-selection probe. Calls use a fresh manager
        // after gate admission so a projected COM instance is never shared across operations.
        _ = new PackageManager ();
    }

    // ------------------------------------------------------------------------
    // Reads
    // ------------------------------------------------------------------------

    public async Task<IReadOnlyList<Package>> SearchAsync (string query, string? source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace (query))
        {
            return [];
        }

        using IDisposable lease = await _comGate.AcquireAsync (ct);
        PackageManager pm = new ();

        // Composite over the remote catalog(s), returning remote packages correlated with
        // installed status. CatalogDefault searches the catalog's default field set
        // (Id/Name/Moniker/Tags) — the free-text-search field.
        PackageCatalog catalog = await ConnectAsync (
            CompositeRef (pm, RemoteRefs (pm, source), CompositeSearchBehavior.RemotePackagesFromRemoteCatalogs),
            ct);

        FindPackagesOptions opts = new ();
        opts.Selectors.Add (new ()
        {
            Field = PackageMatchField.CatalogDefault,
            Option = PackageFieldMatchOption.ContainsCaseInsensitive,
            Value = query
        });

        // Cap a pathologically broad query (e.g. a one-letter term) so it can't materialize tens
        // of thousands of rows. The app already blocks empty queries; this guards the merely-broad
        // ones. result.WasLimitExceeded below tells the UI to nudge the user to refine.
        opts.ResultLimit = BackendLimits.SearchMatches;

        FindPackagesResult result = await catalog.FindPackagesAsync (opts).AsTask (ct);

        List<Package> packages = [];

        foreach (MatchResult m in Materialize (result.Matches, BackendLimits.SearchMatches))
        {
            try
            {
                CatalogPackage pkg = m.CatalogPackage;
                string version = SafeVersion (SafeDefaultInstallVersion (pkg)) ?? LatestAvailableVersion (pkg) ?? string.Empty;

                // The search composite (RemotePackagesFromRemoteCatalogs) correlates installed
                // status, so a search row knows whether it's installed and whether an upgrade is
                // available — surfaced so the UI can offer Uninstall/Upgrade rather than Install.
                string? installedVersion = SafeVersion (SafeInstalledVersion (pkg));
                bool updateAvailable = installedVersion is not null && SafeIsUpdateAvailable (pkg);

                packages.Add (new ()
                {
                    Id = SimpleText (pkg.Id) ?? string.Empty,
                    Name = SimpleText (pkg.Name) ?? string.Empty,
                    Version = SimpleText (version) ?? string.Empty,
                    Source = SimpleText (SourceOf (pkg)) ?? string.Empty,
                    MatchField = SimpleText (NotableMatchField (m)),
                    InstalledVersion = SimpleText (installedVersion),
                    AvailableVersion = updateAvailable ? SimpleText (LatestAvailableVersion (pkg)) : null
                });
            }
            catch
            {
                // A bad HRESULT on Id/Name surfaces as an exception here; skip the malformed
                // row rather than failing the entire search.
            }
        }

        return packages;
    }

    /// <summary>
    /// The field this result matched on, but only when it's a non-obvious one. A match on Name,
    /// Id, or the catalog default (free-text) is expected and needs no annotation; a match on a
    /// Moniker, Tag, Command, family name, or product code explains why an otherwise-unexpected
    /// package surfaced, so we surface those as a "Matched on" hint. Returns null otherwise.
    /// </summary>
    private static string? NotableMatchField (MatchResult m)
    {
        try
        {
            return m.MatchCriteria?.Field switch
            {
                PackageMatchField.Moniker => "moniker",
                PackageMatchField.Tag => "tag",
                PackageMatchField.Command => "command",
                PackageMatchField.PackageFamilyName => "family name",
                PackageMatchField.ProductCode => "product code",
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<Package>> ListInstalledAsync (string? source, CancellationToken ct)
    {
        using IDisposable lease = await _comGate.AcquireAsync (ct);
        return await ListLocalAsync (new PackageManager (), source, upgradesOnly: false, ct);
    }

    public async Task<IReadOnlyList<Package>> ListUpgradesAsync (string? source, CancellationToken ct)
    {
        using IDisposable lease = await _comGate.AcquireAsync (ct);
        return await ListLocalAsync (new PackageManager (), source, upgradesOnly: true, ct);
    }

    /// <summary>
    /// Installed packages, optionally filtered to those with an available upgrade. Uses a
    /// composite catalog with <see cref="CompositeSearchBehavior.LocalCatalogs"/>: results come
    /// from the implicit local "installed" catalog, correlated against the supplied remote
    /// catalog(s) so each row knows its available version / update status.
    /// </summary>
    private static async Task<IReadOnlyList<Package>> ListLocalAsync (PackageManager pm, string? source, bool upgradesOnly, CancellationToken ct)
    {
        PackageCatalog catalog = await ConnectAsync (
            CompositeRef (pm, RemoteRefs (pm, source), CompositeSearchBehavior.LocalCatalogs),
            ct);

        // An empty filter set returns every installed package.
        FindPackagesOptions options = new () { ResultLimit = BackendLimits.LocalMatches };
        FindPackagesResult result = await catalog.FindPackagesAsync (options).AsTask (ct);

        List<Package> packages = [];

        foreach (MatchResult m in Materialize (result.Matches, BackendLimits.LocalMatches))
        {
            try
            {
                CatalogPackage pkg = m.CatalogPackage;
                bool updateAvailable = SafeIsUpdateAvailable (pkg);

                if (upgradesOnly && !updateAvailable)
                {
                    continue;
                }

                string installed = SafeVersion (SafeInstalledVersion (pkg)) ?? string.Empty;

                packages.Add (new ()
                {
                    Id = SimpleText (pkg.Id) ?? string.Empty,
                    Name = SimpleText (pkg.Name) ?? string.Empty,
                    Version = SimpleText (installed) ?? string.Empty,
                    Source = SimpleText (SourceOf (pkg)) ?? string.Empty,
                    AvailableVersion = updateAvailable ? SimpleText (LatestAvailableVersion (pkg)) : null
                });
            }
            catch
            {
                // Skip a malformed row (bad HRESULT on a property read) rather than failing
                // the entire listing.
            }
        }

        return packages;
    }

    public async Task<PackageDetail?> ShowAsync (string id, CancellationToken ct)
    {
        using IDisposable lease = await _comGate.AcquireAsync (ct);
        PackageManager pm = new ();
        CatalogPackage? pkg = await FindByIdAsync (pm, id, null, installedContext: false, ct);

        if (pkg is null)
        {
            return null;
        }

        // Prefer the default-install (latest) version's manifest; fall back to installed.
        PackageVersionInfo? versionInfo = SafeDefaultInstallVersion (pkg) ?? SafeInstalledVersion (pkg);

        if (versionInfo is null)
        {
            return null;
        }

        CatalogPackageMetadata? meta = null;

        try
        {
            meta = versionInfo.GetCatalogPackageMetadata ();
        }
        catch
        {
            // No localized manifest metadata available; fall back to the bare fields below.
        }

        string? description = RichText (Coalesce (meta?.Description, meta?.ShortDescription));

        // Installed-only metadata (location/scope) comes from the installed version's metadata
        // bag, not the manifest — so resolve it from the installed version specifically.
        PackageVersionInfo? installed = SafeInstalledVersion (pkg);
        CollectionBudget metadataBudget = new (BackendLimits.MetadataItems);

        try
        {
            return new ()
            {
                Id = SimpleText (pkg.Id) ?? string.Empty,
                Name = SimpleText (Coalesce (meta?.PackageName, pkg.Name)) ?? SimpleText (pkg.Id) ?? string.Empty,
                Version = SimpleText (SafeVersion (SafeInstalledVersion (pkg)) ?? SafeVersion (versionInfo)) ?? string.Empty,
                AvailableVersion = SimpleText (LatestAvailableVersion (pkg)),
                InstalledVersion = SimpleText (SafeVersion (installed)),
                Source = SimpleText (SourceOf (pkg)) ?? string.Empty,
                Publisher = SimpleText (NullIfEmpty (meta?.Publisher)),
                Author = SimpleText (NullIfEmpty (meta?.Author)),
                Copyright = SimpleText (NullIfEmpty (meta?.Copyright)),
                Description = description,
                Homepage = SimpleText (NullIfEmpty (meta?.PackageUrl)),
                License = SimpleText (NullIfEmpty (meta?.License)),
                ReleaseNotesUrl = SimpleText (NullIfEmpty (meta?.ReleaseNotesUrl)),
                SupportUrl = SimpleText (NullIfEmpty (meta?.PublisherSupportUrl)),
                PrivacyUrl = SimpleText (NullIfEmpty (meta?.PrivacyUrl)),
                PurchaseUrl = SimpleText (NullIfEmpty (meta?.PurchaseUrl)),
                InstallationNotes = RichText (NullIfEmpty (meta?.InstallationNotes)),
                InstalledLocation = SimpleText (SafeMetadata (installed, PackageVersionMetadataField.InstalledLocation)),
                InstalledScope = SimpleText (SafeMetadata (installed, PackageVersionMetadataField.InstalledScope)),
                Tags = meta is null ? null : StringVector (() => meta.Tags, metadataBudget),
                Documentation = DocLinks (meta, metadataBudget),
                ProductCodes = StringVector (() => versionInfo.ProductCodes, metadataBudget),
                PackageFamilyNames = StringVector (() => versionInfo.PackageFamilyNames, metadataBudget)
            };
        }
        catch
        {
            // Core id/name getters threw (bad HRESULT). Return null so the app falls back to its
            // stub detail rather than surfacing a "Detail error", matching the list path's
            // skip-the-bad-row behavior.
            return null;
        }
    }

    // ------------------------------------------------------------------------
    // Version list + install preview
    // ------------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> ListVersionsAsync (string id, CancellationToken ct)
    {
        using IDisposable lease = await _comGate.AcquireAsync (ct);
        PackageManager pm = new ();
        CatalogPackage? pkg = await FindByIdAsync (pm, id, null, installedContext: false, ct);

        if (pkg is null)
        {
            return [];
        }

        List<string> versions = [];
        HashSet<string> seen = new (StringComparer.OrdinalIgnoreCase);

        try
        {
            // AvailableVersions is newest-first. Indexed access via Materialize (AOT rule).
            foreach (PackageVersionId vid in Materialize (pkg.AvailableVersions, BackendLimits.Versions))
            {
                string v = SimpleText (vid.Version) ?? string.Empty;

                if (!string.IsNullOrWhiteSpace (v) && seen.Add (v))
                {
                    versions.Add (v);
                }
            }
        }
        catch
        {
            // Return whatever we collected before the version list became unreadable.
        }

        return versions;
    }

    public async Task<InstallerPreview?> GetInstallerPreviewAsync (string id, string? version, CancellationToken ct)
    {
        using IDisposable lease = await _comGate.AcquireAsync (ct);
        PackageManager pm = new ();
        CatalogPackage? pkg = await FindByIdAsync (pm, id, null, installedContext: false, ct);

        if (pkg is null)
        {
            return null;
        }

        PackageVersionInfo? versionInfo;

        if (!string.IsNullOrEmpty (version))
        {
            // Explicit version: resolve exactly that. Do NOT fall back to a different version —
            // a fallback would compute the preview from the wrong installer while the confirm
            // dialog still says "Install X <version>".
            PackageVersionId? vid = FindVersionId (pkg, version);
            versionInfo = vid is null ? null : SafeGetVersionInfo (pkg, vid);
        }
        else
        {
            // Latest: the default-install version, else the installed version.
            versionInfo = SafeDefaultInstallVersion (pkg) ?? SafeInstalledVersion (pkg);
        }

        if (versionInfo is null)
        {
            return null;
        }

        try
        {
            // Resolve the installer that *would* be chosen for default options on this machine.
            PackageInstallerInfo installer = versionInfo.GetApplicableInstaller (new InstallOptions ());

            if (installer is null)
            {
                return null;
            }

            return new InstallerPreview
            {
                InstallerType = TypeName (installer.InstallerType),
                Architecture = ArchName (installer.Architecture),
                Scope = ScopeName (installer.Scope),
                RequiresElevation = RequiresElevation (installer),
                Version = SimpleText (SafeVersion (versionInfo))
            };
        }
        catch
        {
            // No applicable installer (e.g. arch mismatch) or the API isn't available — no preview.
            return null;
        }
    }

    private static PackageVersionInfo? SafeGetVersionInfo (CatalogPackage pkg, PackageVersionId vid)
    {
        try
        {
            return pkg.GetPackageVersionInfo (vid);
        }
        catch
        {
            return null;
        }
    }

    private static bool RequiresElevation (PackageInstallerInfo installer)
    {
        try
        {
            return installer.ElevationRequirement == ElevationRequirement.ElevationRequired;
        }
        catch
        {
            // ElevationRequirement is a newer contract member; absent on older COM servers.
            return false;
        }
    }

    private static string? TypeName (PackageInstallerType t)
        => t switch
        {
            PackageInstallerType.Msi => "MSI",
            PackageInstallerType.Msix => "MSIX",
            PackageInstallerType.Exe => "EXE",
            PackageInstallerType.MSStore => "Store",
            PackageInstallerType.Inno => "Inno",
            PackageInstallerType.Nullsoft => "Nullsoft",
            PackageInstallerType.Wix => "WiX",
            PackageInstallerType.Burn => "Burn",
            PackageInstallerType.Zip => "Zip",
            PackageInstallerType.Portable => "Portable",
            PackageInstallerType.Font => "Font",
            _ => null
        };

    private static string? ArchName (Windows.System.ProcessorArchitecture a)
        => a switch
        {
            Windows.System.ProcessorArchitecture.X64 => "x64",
            Windows.System.ProcessorArchitecture.X86 => "x86",
            Windows.System.ProcessorArchitecture.Arm64 => "arm64",
            Windows.System.ProcessorArchitecture.Arm => "arm",
            Windows.System.ProcessorArchitecture.Neutral => "neutral",
            _ => null
        };

    private static string? ScopeName (PackageInstallerScope s)
        => s switch
        {
            PackageInstallerScope.System => "machine",
            PackageInstallerScope.User => "user",
            _ => null
        };

    // ------------------------------------------------------------------------
    // Writes
    // ------------------------------------------------------------------------

    public async Task<OpResult> InstallAsync (string id, string? version, InstallSettings? settings, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        using IDisposable lease = await _comGate.AcquireAsync (ct);
        PackageManager pm = new ();
        Operation op = new () { Kind = OperationKind.Install, PackageId = id, Version = version };
        CatalogPackage? pkg = await FindByIdAsync (pm, id, null, installedContext: false, ct);

        if (pkg is null)
        {
            return Fail (op, $"Package '{id}' not found in any configured source.");
        }

        // Default to a silent install; advanced settings may override mode/scope/arch/args below.
        InstallOptions options = new ()
        {
            PackageInstallMode = PackageInstallMode.Silent,
            AcceptPackageAgreements = true
        };

        ApplyInstallSettings (options, settings);

        if (!string.IsNullOrEmpty (version))
        {
            PackageVersionId? versionId = FindVersionId (pkg, version);

            if (versionId is null)
            {
                return Fail (op, $"Version '{version}' is not available for {SimpleText (pkg.Name) ?? id}.");
            }

            options.PackageVersionId = versionId;
        }

        // Set the progress handler on the WinRT op before awaiting; it fires on a COM thread,
        // so the IProgress<> the caller supplies is responsible for marshaling to the UI.
        var asyncOp = pm.InstallPackageAsync (pkg, options);
        asyncOp.Progress = (_, p) => progress?.Report (MapInstall (p));
        InstallResult result = await asyncOp.AsTask (ct);

        return result.Status == InstallResultStatus.Ok
                   ? Ok (op, $"Installed {SimpleText (pkg.Name) ?? id}{(result.RebootRequired ? " (reboot required)" : string.Empty)}")
                   : Fail (op, DescribeInstall ("Install", result));
    }

    public async Task<OpResult> UpgradeAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        using IDisposable lease = await _comGate.AcquireAsync (ct);
        PackageManager pm = new ();
        Operation op = new () { Kind = OperationKind.Upgrade, PackageId = id };

        // Installed context so the package carries both its installed version and the
        // correlated remote available versions that the upgrade resolves against.
        CatalogPackage? pkg = await FindByIdAsync (pm, id, null, installedContext: true, ct);

        if (pkg is null)
        {
            return Fail (op, $"Installed package '{id}' not found.");
        }

        InstallOptions options = new ()
        {
            PackageInstallMode = PackageInstallMode.Silent,
            AcceptPackageAgreements = true
        };

        var asyncOp = pm.UpgradePackageAsync (pkg, options);
        asyncOp.Progress = (_, p) => progress?.Report (MapInstall (p));
        InstallResult result = await asyncOp.AsTask (ct);

        return result.Status == InstallResultStatus.Ok
                   ? Ok (op, $"Upgraded {SimpleText (pkg.Name) ?? id}{(result.RebootRequired ? " (reboot required)" : string.Empty)}")
                   : Fail (op, DescribeInstall ("Upgrade", result));
    }

    public async Task<OpResult> UninstallAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        using IDisposable lease = await _comGate.AcquireAsync (ct);
        PackageManager pm = new ();
        Operation op = new () { Kind = OperationKind.Uninstall, PackageId = id };
        CatalogPackage? pkg = await FindByIdAsync (pm, id, null, installedContext: true, ct);

        if (pkg is null)
        {
            return Fail (op, $"Installed package '{id}' not found.");
        }

        UninstallOptions options = new () { PackageUninstallMode = PackageUninstallMode.Silent };
        var asyncOp = pm.UninstallPackageAsync (pkg, options);
        asyncOp.Progress = (_, p) => progress?.Report (MapUninstall (p));
        UninstallResult result = await asyncOp.AsTask (ct);

        return result.Status == UninstallResultStatus.Ok
                   ? Ok (op, $"Uninstalled {SimpleText (pkg.Name) ?? id}{(result.RebootRequired ? " (reboot required)" : string.Empty)}")
                   : Fail (op, $"Uninstall failed: {result.Status} (installer 0x{result.UninstallerErrorCode:X}, hr 0x{HResultOf (result.ExtendedErrorCode):X8})");
    }

    public async Task<OpResult> DownloadAsync (string id, string? version, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        using IDisposable lease = await _comGate.AcquireAsync (ct);
        PackageManager pm = new ();
        Operation op = new () { Kind = OperationKind.Download, PackageId = id, Version = version };
        CatalogPackage? pkg = await FindByIdAsync (pm, id, null, installedContext: false, ct);

        if (pkg is null)
        {
            return Fail (op, $"Package '{id}' not found in any configured source.");
        }

        string dir = DownloadDirectory ();

        try
        {
            Directory.CreateDirectory (dir);
        }
        catch (Exception ex)
        {
            return Fail (op, $"Could not prepare download folder '{dir}': {ex.Message}");
        }

        DownloadOptions options = new ()
        {
            DownloadDirectory = dir,
            AcceptPackageAgreements = true
        };

        if (!string.IsNullOrEmpty (version))
        {
            PackageVersionId? versionId = FindVersionId (pkg, version);

            if (versionId is null)
            {
                return Fail (op, $"Version '{version}' is not available for {SimpleText (pkg.Name) ?? id}.");
            }

            options.PackageVersionId = versionId;
        }

        var asyncOp = pm.DownloadPackageAsync (pkg, options);
        asyncOp.Progress = (_, p) => progress?.Report (MapDownload (p));
        DownloadResult result = await asyncOp.AsTask (ct);

        return result.Status == DownloadResultStatus.Ok
                   ? Ok (op, $"Downloaded {SimpleText (pkg.Name) ?? id} to {dir}")
                   : Fail (op, $"Download failed: {result.Status} (hr 0x{HResultOf (result.ExtendedErrorCode):X8})");
    }

    public bool CanRepair => true;

    public async Task<OpResult> RepairAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        using IDisposable lease = await _comGate.AcquireAsync (ct);
        PackageManager pm = new ();
        Operation op = new () { Kind = OperationKind.Repair, PackageId = id };

        // Installed context so the package carries the installed version that repair targets.
        CatalogPackage? pkg = await FindByIdAsync (pm, id, null, installedContext: true, ct);

        if (pkg is null)
        {
            return Fail (op, $"Installed package '{id}' not found.");
        }

        RepairOptions options = new () { PackageRepairMode = PackageRepairMode.Silent };

        var asyncOp = pm.RepairPackageAsync (pkg, options);
        asyncOp.Progress = (_, p) => progress?.Report (MapRepair (p));
        RepairResult result = await asyncOp.AsTask (ct);

        if (result.Status == RepairResultStatus.Ok)
        {
            return Ok (op, $"Repaired {SimpleText (pkg.Name) ?? id}{(result.RebootRequired ? " (reboot required)" : string.Empty)}");
        }

        return Fail (op, RepairFailureMessage (SimpleText (pkg.Name) ?? id, result));
    }

    // The COM API reports "this package can't be repaired" in two ways: as the dedicated
    // RepairResultStatus.NoApplicableRepairer status, or — for portable/zip and similar installer
    // technologies — as a generic RepairError whose ExtendedErrorCode carries one of the winget
    // "not supported / not applicable" HRESULTs. Both mean the same thing to the user, so map them
    // to one friendly line; genuine repair failures keep the detailed status + HRESULT.
    private static string RepairFailureMessage (string name, RepairResult result)
    {
        if (result.Status == RepairResultStatus.NoApplicableRepairer)
        {
            return $"{name} doesn't support repair.";
        }

        return HResultOf (result.ExtendedErrorCode) switch
        {
            0x8A150079u or // NO_REPAIR_INFO_FOUND — "Repair command not found."
            0x8A15007Au or // REPAIR_NOT_APPLICABLE
            0x8A15007Cu    // REPAIR_NOT_SUPPORTED — installer technology has no repair (e.g. portable .zip)
                => $"{name} doesn't support repair.",
            0x8A15007Du    // ADMIN_CONTEXT_REPAIR_PROHIBITED
                => $"Repairing {name} requires elevation it can't get (it's installed in user scope).",
            var hr => $"Repair failed: {result.Status} (repairer {result.RepairerErrorCode}, hr 0x{hr:X8})"
        };
    }

    public async Task<InstallVerification?> VerifyInstalledAsync (string id, CancellationToken ct)
    {
        using IDisposable lease = await _comGate.AcquireAsync (ct);
        PackageManager pm = new ();
        CatalogPackage? pkg = await FindByIdAsync (pm, id, null, installedContext: true, ct);

        if (pkg is null)
        {
            // Not "can't verify" (null is reserved for the CLI backend) — the package just
            // isn't found / installed, so there's nothing to check.
            return new () { Outcome = VerifyOutcome.NotApplicable };
        }

        CheckInstalledStatusResult result;

        try
        {
            result = await pkg.CheckInstalledStatusAsync (InstalledStatusType.AllChecks).AsTask (ct);
        }
        catch
        {
            return new () { Outcome = VerifyOutcome.Error };
        }

        try
        {
            if (result.Status != CheckInstalledStatusResultStatus.Ok)
            {
                return new () { Outcome = VerifyOutcome.Error };
            }

            // CheckInstalledStatus returns one status block PER installer in the package's manifest
            // (x64/arm64/x86 × user/machine, the portable variant, etc.). Only the installer that's
            // actually present passes its checks; the others legitimately report "Apps & Features
            // entry not found" (0x8A150201) and the like. So evaluate each installer independently
            // and treat the package as installed correctly when ANY single installer's checks all
            // pass — rather than flagging the package because some *other* manifest installer (which
            // was never installed) didn't match. (The old code flattened all installers and reported
            // Issues if any one check failed, so multi-installer packages always looked corrupt.)
            // Track each installer's checks plus whether any of its entries couldn't be read, so a
            // "best" installer whose only failure is an unreadable entry reports Error ("couldn't
            // verify"), not Issues ("may be corrupt").
            List<(List<VerifyCheck> Checks, bool ReadError)> perInstaller = [];
            bool hadReadError = false;
            CollectionBudget verificationBudget = new (BackendLimits.VerificationItems);
            int installerCount = Math.Min (
                result.PackageInstalledStatus.Count,
                BackendLimits.VerificationInstallers);

            // Two nested projected vectors — indexed via Materialize (AOT rule).
            foreach (PackageInstallerInstalledStatus installer in Materialize (
                         result.PackageInstalledStatus,
                         verificationBudget.Take (installerCount)))
            {
                IReadOnlyList<InstalledStatus> entries;

                try
                {
                    entries = Materialize (
                        installer.InstallerInstalledStatus,
                        verificationBudget.Take (installer.InstallerInstalledStatus.Count));
                }
                catch
                {
                    hadReadError = true;

                    continue;
                }

                List<VerifyCheck> checks = [];
                bool readError = false;

                foreach (InstalledStatus entry in entries)
                {
                    try
                    {
                        // HRESULT projects to an Exception: null means S_OK (the check passed).
                        bool ok = entry.Status is null;
                        string? path = SimpleText (NullIfEmpty (entry.Path));
                        checks.Add (new (
                            SimpleText (StatusTypeName (entry.Type)) ?? "Status check",
                            ok,
                            SimpleText (ok ? path : Coalesce (path, $"hr 0x{HResultOf (entry.Status):X8}"))));
                    }
                    catch
                    {
                        // Couldn't read this check (bad HRESULT projecting the entry). Record it as a
                        // FAILING check AND flag the installer's data as incomplete — so the package
                        // isn't reported Ok on partial data, and a best installer whose failures are
                        // *only* read errors reports Error ("couldn't verify"), not Issues.
                        readError = true;
                        hadReadError = true;
                        checks.Add (new ("Status check", false, "could not read installed-status entry"));
                    }
                }

                if (checks.Count > 0)
                {
                    perInstaller.Add ((checks, readError));
                }
            }

            if (perInstaller.Count == 0)
            {
                // No installer yielded a readable check: can't honestly verify if a read errored.
                return new () { Outcome = hadReadError ? VerifyOutcome.Error : VerifyOutcome.NotApplicable };
            }

            // Best-matching installer = the one with the fewest failing checks. All pass → installed
            // correctly (show its clean checks). Otherwise, if its data was incomplete (a read error)
            // we can't honestly call it corrupt → Error; a genuine failing check → Issues.
            (List<VerifyCheck> Checks, bool ReadError) best = perInstaller.OrderBy (x => x.Checks.Count (c => !c.Ok)).First ();

            VerifyOutcome outcome = best.Checks.TrueForAll (c => c.Ok) ? VerifyOutcome.Ok
                                    : best.ReadError ? VerifyOutcome.Error
                                    : VerifyOutcome.Issues;

            return new () { Outcome = outcome, Checks = best.Checks };
        }
        catch
        {
            // result.Status / PackageInstalledStatus materialization threw.
            return new () { Outcome = VerifyOutcome.Error };
        }
    }

    private static string StatusTypeName (InstalledStatusType t)
        => t switch
        {
            InstalledStatusType.AppsAndFeaturesEntry => "Registry entry",
            InstalledStatusType.AppsAndFeaturesEntryInstallLocation => "Install location",
            InstalledStatusType.AppsAndFeaturesEntryInstallLocationFile => "Install-location file",
            InstalledStatusType.DefaultInstallLocation => "Default install location",
            InstalledStatusType.DefaultInstallLocationFile => "Default-location file",
            _ => t.ToString ()
        };

    /// <summary>Read a projected string vector into a managed list (indexed, guarded), or null if empty/unreadable.</summary>
    private static IReadOnlyList<string>? StringVector (Func<IReadOnlyList<string>?> get, CollectionBudget budget)
    {
        try
        {
            IReadOnlyList<string>? projected = get ();

            if (projected is null)
            {
                return null;
            }

            List<string> list = [];

            foreach (string s in Materialize (projected, budget.Take (projected.Count)))
            {
                string? bounded = SimpleText (NullIfEmpty (s));

                if (bounded is not null)
                {
                    list.Add (bounded);
                }
            }

            return list.Count > 0 ? list : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<DocLink>? DocLinks (CatalogPackageMetadata? meta, CollectionBudget budget)
    {
        if (meta is null)
        {
            return null;
        }

        try
        {
            List<DocLink> links = [];

            foreach (Documentation d in Materialize (meta.Documentations, budget.Take (meta.Documentations.Count)))
            {
                try
                {
                    string? url = SimpleText (d.DocumentUrl);

                    if (!string.IsNullOrWhiteSpace (url))
                    {
                        string label = SimpleText (d.DocumentLabel) ?? "Documentation";
                        links.Add (new (label, url));
                    }
                }
                catch
                {
                    // Skip a malformed documentation entry rather than dropping the whole list.
                }
            }

            return links.Count > 0 ? links : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Where DownloadPackageAsync drops installers: a stable folder under the user's Downloads.</summary>
    private static string DownloadDirectory ()
        => Path.Combine (
            Environment.GetFolderPath (Environment.SpecialFolder.UserProfile),
            "Downloads",
            "winget-tui");

    /// <summary>Map the user's advanced-install choices onto the WinGet InstallOptions.</summary>
    private static void ApplyInstallSettings (InstallOptions options, InstallSettings? settings)
    {
        if (settings is null)
        {
            return;
        }

        options.PackageInstallScope = settings.Scope switch
        {
            InstallScopePref.User => PackageInstallScope.User,
            InstallScopePref.Machine => PackageInstallScope.System,
            _ => options.PackageInstallScope
        };

        if (settings.Mode != InstallModePref.Default)
        {
            options.PackageInstallMode = settings.Mode == InstallModePref.Interactive
                                             ? PackageInstallMode.Interactive
                                             : PackageInstallMode.Silent;
        }

        Windows.System.ProcessorArchitecture? arch = settings.Architecture switch
        {
            InstallArchPref.X64 => Windows.System.ProcessorArchitecture.X64,
            InstallArchPref.X86 => Windows.System.ProcessorArchitecture.X86,
            InstallArchPref.Arm64 => Windows.System.ProcessorArchitecture.Arm64,
            _ => null
        };

        if (arch is { } a)
        {
            // .Add on the projected IVector is a method call (not enumeration) — AOT-safe.
            options.AllowedArchitectures.Add (a);
        }

        if (!string.IsNullOrWhiteSpace (settings.CustomArgs))
        {
            options.AdditionalInstallerArguments = settings.CustomArgs;
        }
    }

    // ------------------------------------------------------------------------
    // Pinning — delegated to the CLI (no COM surface for pins).
    // ------------------------------------------------------------------------

    public Task<OpResult> PinAsync (string id, CancellationToken ct) => _cliForPins.PinAsync (id, ct);

    public Task<OpResult> UnpinAsync (string id, CancellationToken ct) => _cliForPins.UnpinAsync (id, ct);

    public Task<IReadOnlyDictionary<string, PinState>> ListPinsAsync (CancellationToken ct) => _cliForPins.ListPinsAsync (ct);

    public async Task<string> DescribeAsync (CancellationToken ct)
    {
        using IDisposable lease = await _comGate.AcquireAsync (ct);
        PackageManager pm = new ();
        string version;

        try
        {
            // PackageManager.Version (contract 13) — the running WinGet COM server version.
            version = SimpleText (NullIfEmpty (pm.Version)) ?? "unknown version";
        }
        catch
        {
            // Older COM servers don't expose Version; the backend still works.
            version = "unknown version";
        }

        return $"COM · winget {version}";
    }

    // ------------------------------------------------------------------------
    // Catalog plumbing
    // ------------------------------------------------------------------------

    /// <summary>
    /// Resolve the configured remote catalog reference(s) for a source filter. A specific
    /// <paramref name="source"/> name resolves just that catalog; null ("All") expands to every
    /// configured source (winget, msstore, and any custom REST sources) via GetPackageCatalogs,
    /// rather than the hard-coded pair — so enterprise/custom sources are included automatically.
    /// </summary>
    private static List<PackageCatalogReference> RemoteRefs (PackageManager pm, string? source)
    {
        List<PackageCatalogReference> refs = [];

        if (!string.IsNullOrEmpty (source))
        {
            PackageCatalogReference? r = pm.GetPackageCatalogByName (source);

            if (r is not null)
            {
                // Accept source agreements up front so ConnectAsync doesn't fail with
                // SourceAgreementsNotAccepted on a fresh machine.
                r.AcceptSourceAgreements = true;
                refs.Add (r);
            }

            return refs;
        }

        // All configured sources. GetPackageCatalogs() returns the source list (excludes the
        // implicit local "installed" catalog, which the composite adds on its own).
        try
        {
            foreach (PackageCatalogReference r in Materialize (pm.GetPackageCatalogs (), BackendLimits.Catalogs))
            {
                r.AcceptSourceAgreements = true;
                refs.Add (r);
            }
        }
        catch
        {
            // GetPackageCatalogs failed (unusual); fall back to the two predefined sources so the
            // common case still works.
            foreach (string name in (string [])["winget", "msstore"])
            {
                PackageCatalogReference? r = pm.GetPackageCatalogByName (name);

                if (r is not null)
                {
                    r.AcceptSourceAgreements = true;
                    refs.Add (r);
                }
            }
        }

        return refs;
    }

    public async Task<IReadOnlyList<string>> ListSourcesAsync (CancellationToken ct)
    {
        using IDisposable lease = await _comGate.AcquireAsync (ct);
        PackageManager pm = new ();
        List<string> names = [];

        try
        {
            foreach (PackageCatalogReference r in Materialize (pm.GetPackageCatalogs (), BackendLimits.Catalogs))
            {
                try
                {
                    string? name = SimpleText (NullIfEmpty (r.Info?.Name));

                    if (name is not null)
                    {
                        names.Add (name);
                    }
                }
                catch
                {
                    // Skip a catalog whose Info/Name read threw rather than dropping the whole list.
                }
            }
        }
        catch
        {
            // GetPackageCatalogs threw — return empty; the app keeps its seeded defaults.
        }

        return names;
    }

    /// <summary>
    /// Wrap one-or-more remote references into a composite catalog. The local "installed"
    /// catalog is implicit in every composite; <paramref name="behavior"/> selects which side
    /// queries return.
    /// </summary>
    private static PackageCatalogReference CompositeRef (PackageManager pm, List<PackageCatalogReference> refs, CompositeSearchBehavior behavior)
    {
        CreateCompositePackageCatalogOptions opts = new () { CompositeSearchBehavior = behavior };

        foreach (PackageCatalogReference r in refs)
        {
            opts.Catalogs.Add (r);
        }

        return pm.CreateCompositePackageCatalog (opts);
    }

    private static async Task<PackageCatalog> ConnectAsync (PackageCatalogReference reference, CancellationToken ct)
    {
        // NOTE: do NOT set AcceptSourceAgreements here. Every reference passed in is a *composite*
        // (from CompositeRef), and setting AcceptSourceAgreements on a composite reference throws
        // E_ILLEGAL_STATE_CHANGE. The API-correct place is each *source* reference before it's
        // composited — RemoteRefs already does that. (This only surfaced once COM actually
        // activated; under AOT the backend silently fell back to CLI, so the path was never run.)
        ConnectResult result = await reference.ConnectAsync ().AsTask (ct);

        if (result.Status != ConnectResultStatus.Ok || result.PackageCatalog is null)
        {
            throw new InvalidOperationException ($"Could not connect to package catalog: {result.Status}");
        }

        return result.PackageCatalog;
    }

    /// <summary>Find a single package by exact (case-insensitive) id.</summary>
    private static async Task<CatalogPackage?> FindByIdAsync (PackageManager pm, string id, string? source, bool installedContext, CancellationToken ct)
    {
        PackageCatalog catalog = await ConnectAsync (
            CompositeRef (
                pm,
                RemoteRefs (pm, source),
                installedContext ? CompositeSearchBehavior.LocalCatalogs : CompositeSearchBehavior.RemotePackagesFromRemoteCatalogs),
            ct);

        FindPackagesOptions opts = new () { ResultLimit = 1 };
        opts.Filters.Add (new ()
        {
            Field = PackageMatchField.Id,
            Option = PackageFieldMatchOption.EqualsCaseInsensitive,
            Value = id
        });

        FindPackagesResult result = await catalog.FindPackagesAsync (opts).AsTask (ct);
        List<MatchResult> matches = Materialize (result.Matches, 1);

        return matches.Count > 0 ? matches [0].CatalogPackage : null;
    }

    private static PackageVersionId? FindVersionId (CatalogPackage pkg, string version)
    {
        try
        {
            foreach (PackageVersionId vid in Materialize (pkg.AvailableVersions, BackendLimits.Versions))
            {
                if (string.Equals (vid.Version, version, StringComparison.OrdinalIgnoreCase))
                {
                    return vid;
                }
            }
        }
        catch
        {
            // Version list unreadable (bad HRESULT) — treat as "version not found" so the caller
            // returns a clean OpResult instead of throwing.
        }

        return null;
    }

    // ------------------------------------------------------------------------
    // Field extraction — every WinRT property access that can throw on an odd
    // package is wrapped so one bad row never sinks the whole listing.
    // ------------------------------------------------------------------------

    private static string SourceOf (CatalogPackage pkg)
    {
        // The remote source the package is available from (installed-only rows fall back to
        // the local "InstalledPackages" catalog name, which we'd rather not show — prefer remote).
        PackageVersionInfo? v = SafeDefaultInstallVersion (pkg) ?? SafeInstalledVersion (pkg);

        try
        {
            return v?.PackageCatalog?.Info?.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static PackageVersionInfo? SafeInstalledVersion (CatalogPackage pkg)
    {
        try
        {
            return pkg.InstalledVersion;
        }
        catch
        {
            return null;
        }
    }

    private static PackageVersionInfo? SafeDefaultInstallVersion (CatalogPackage pkg)
    {
        try
        {
            return pkg.DefaultInstallVersion;
        }
        catch
        {
            return null;
        }
    }

    private static bool SafeIsUpdateAvailable (CatalogPackage pkg)
    {
        try
        {
            return pkg.IsUpdateAvailable;
        }
        catch
        {
            return false;
        }
    }

    private static string? SafeVersion (PackageVersionInfo? info)
    {
        if (info is null)
        {
            return null;
        }

        try
        {
            return NullIfEmpty (info.Version);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Read a single PackageVersionMetadata field, returning null if absent/empty/unreadable.</summary>
    private static string? SafeMetadata (PackageVersionInfo? info, PackageVersionMetadataField field)
    {
        if (info is null)
        {
            return null;
        }

        try
        {
            return NullIfEmpty (info.GetMetadata (field));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Latest available version string (AvailableVersions is newest-first), else the default-install version.</summary>
    private static string? LatestAvailableVersion (CatalogPackage pkg)
    {
        try
        {
            // Indexed access only — never enumerate the projected view (AOT).
            if (pkg.AvailableVersions is { Count: > 0 } versions)
            {
                return NullIfEmpty (versions [0].Version);
            }
        }
        catch
        {
            // fall through
        }

        return SafeVersion (SafeDefaultInstallVersion (pkg));
    }

    // ------------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------------

    /// <summary>
    /// Copy a WinRT-projected list into a managed <see cref="List{T}"/> using indexed access.
    /// This is the AOT-safe substitute for enumerating the projection directly (see the file
    /// header). Callers may then foreach/LINQ the returned managed copy freely.
    /// </summary>
    private static List<T> Materialize<T> (IReadOnlyList<T> projected, int maximum)
        => BackendLimits.Materialize (projected, maximum);

    /// <summary>Map the WinGet install/upgrade progress struct onto the backend-agnostic model.</summary>
    private static OpProgress MapInstall (InstallProgress p)
    {
        OpPhase phase = p.State switch
        {
            PackageInstallProgressState.Queued => OpPhase.Queued,
            PackageInstallProgressState.Downloading => OpPhase.Downloading,
            PackageInstallProgressState.Installing => OpPhase.Installing,
            PackageInstallProgressState.PostInstall => OpPhase.Finalizing,
            PackageInstallProgressState.Finished => OpPhase.Done,
            _ => OpPhase.Installing
        };

        double fraction = p.State switch
        {
            PackageInstallProgressState.Downloading => p.DownloadProgress,
            PackageInstallProgressState.Installing => p.InstallationProgress,
            PackageInstallProgressState.Finished => 1.0,
            _ => 0.0
        };

        return new (phase, fraction);
    }

    private static OpProgress MapUninstall (UninstallProgress p)
    {
        OpPhase phase = p.State switch
        {
            PackageUninstallProgressState.Queued => OpPhase.Queued,
            PackageUninstallProgressState.Uninstalling => OpPhase.Uninstalling,
            PackageUninstallProgressState.PostUninstall => OpPhase.Finalizing,
            PackageUninstallProgressState.Finished => OpPhase.Done,
            _ => OpPhase.Uninstalling
        };

        return new (phase, p.UninstallationProgress);
    }

    private static OpProgress MapDownload (PackageDownloadProgress p)
    {
        OpPhase phase = p.State switch
        {
            PackageDownloadProgressState.Queued => OpPhase.Queued,
            PackageDownloadProgressState.Downloading => OpPhase.Downloading,
            PackageDownloadProgressState.Finished => OpPhase.Done,
            _ => OpPhase.Downloading
        };

        return new (phase, p.State == PackageDownloadProgressState.Finished ? 1.0 : p.DownloadProgress);
    }

    private static OpProgress MapRepair (RepairProgress p)
    {
        OpPhase phase = p.State switch
        {
            PackageRepairProgressState.Queued => OpPhase.Queued,
            PackageRepairProgressState.Repairing => OpPhase.Repairing,
            PackageRepairProgressState.PostRepair => OpPhase.Finalizing,
            PackageRepairProgressState.Finished => OpPhase.Done,
            _ => OpPhase.Repairing
        };

        return new (phase, p.State == PackageRepairProgressState.Finished ? 1.0 : p.RepairCompletionProgress);
    }

    private static string DescribeInstall (string verb, InstallResult result)
        => $"{verb} failed: {result.Status} (installer {result.InstallerErrorCode}, hr 0x{HResultOf (result.ExtendedErrorCode):X8})";

    // In this projection, the IDL's `HRESULT ExtendedErrorCode` surfaces as a System.Exception
    // (CsWinRT maps a failed HRESULT to its exception). Pull the numeric HRESULT back out.
    private static uint HResultOf (Exception? error) => (uint)(error?.HResult ?? 0);

    private static OpResult Ok (Operation op, string message) => new () { Operation = op, Success = true, Message = message };

    private static OpResult Fail (Operation op, string message) => new () { Operation = op, Success = false, Message = message };

    private static string? NullIfEmpty (string? value) => string.IsNullOrWhiteSpace (value) ? null : value;

    private static string? Coalesce (string? a, string? b) => NullIfEmpty (a) ?? NullIfEmpty (b);

    private static string? SimpleText (string? value) => BackendLimits.SimpleText (value);

    private static string? RichText (string? value) => BackendLimits.RichText (value);
}
#endif
