// The COM backend is Windows-only: it talks to the WinGet COM API
// (Microsoft.Management.Deployment) instead of shelling out to winget.exe and parsing
// stdout. The whole file is gated on WINGET_COM, which the .csproj defines only for the
// net10.0-windows10.0.26100.0 TFM — on net10.0 this file compiles to nothing so the
// cross-platform build stays clean.
//
// === The one AOT rule (see spikes/ComBackendSpike/SPIKE-RESULTS.md) ===
// NEVER `foreach` or LINQ directly over a WinRT-projected collection. Under Native AOT the
// IIterable<T> runtime-callable-wrapper for the generic instantiation isn't generated and
// enumeration throws InvalidCastException at runtime. Indexed access (IVectorView.GetAt,
// i.e. `list[i]`) works fine. Every projected list is funneled through Materialize<T>()
// below, which copies via indexing into a normal List<T>; after that, ordinary
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
///  - A single PackageManager is shared across operations that the UI invokes from background
///    (threadpool/MTA) threads. WinGet's COM objects are expected to be agile, but that hasn't
///    been verified under this app's usage; if RPC_E_WRONG_THREAD or intermittent COM errors
///    surface on Windows, switch to a fresh PackageManager per operation.
///  - Pinning delegates to winget.exe, so pin/unpin/list-pins need winget on PATH even on this
///    backend. If the COM server is registered but winget.exe isn't reachable, pin operations
///    fail (visibly, via the returned OpResult) while everything else keeps working.
/// </summary>
public sealed class ComBackend : IBackend
{
    private readonly PackageManager _pm = new ();

    // Pin operations fall through to the CLI — the COM API has no pinning surface.
    private readonly CliBackend _cliForPins = new ();

    // ------------------------------------------------------------------------
    // Reads
    // ------------------------------------------------------------------------

    public async Task<IReadOnlyList<Package>> SearchAsync (string query, string? source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace (query))
        {
            return [];
        }

        // Composite over the remote catalog(s), returning remote packages correlated with
        // installed status. CatalogDefault searches the catalog's default field set
        // (Id/Name/Moniker/Tags) — the free-text-search field.
        PackageCatalog catalog = await ConnectAsync (
            CompositeRef (RemoteRefs (source), CompositeSearchBehavior.RemotePackagesFromRemoteCatalogs),
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
        opts.ResultLimit = AppState.SearchResultLimit;

        FindPackagesResult result = await catalog.FindPackagesAsync (opts).AsTask (ct);

        List<Package> packages = [];

        foreach (MatchResult m in Materialize (result.Matches))
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
                    Id = pkg.Id,
                    Name = pkg.Name,
                    Version = version,
                    Source = SourceOf (pkg),
                    MatchField = NotableMatchField (m),
                    InstalledVersion = installedVersion,
                    AvailableVersion = updateAvailable ? LatestAvailableVersion (pkg) : null
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

    public Task<IReadOnlyList<Package>> ListInstalledAsync (string? source, CancellationToken ct)
        => ListLocalAsync (source, upgradesOnly: false, ct);

    public Task<IReadOnlyList<Package>> ListUpgradesAsync (string? source, CancellationToken ct)
        => ListLocalAsync (source, upgradesOnly: true, ct);

    /// <summary>
    /// Installed packages, optionally filtered to those with an available upgrade. Uses a
    /// composite catalog with <see cref="CompositeSearchBehavior.LocalCatalogs"/>: results come
    /// from the implicit local "installed" catalog, correlated against the supplied remote
    /// catalog(s) so each row knows its available version / update status.
    /// </summary>
    private async Task<IReadOnlyList<Package>> ListLocalAsync (string? source, bool upgradesOnly, CancellationToken ct)
    {
        PackageCatalog catalog = await ConnectAsync (
            CompositeRef (RemoteRefs (source), CompositeSearchBehavior.LocalCatalogs),
            ct);

        // An empty filter set returns every installed package.
        FindPackagesResult result = await catalog.FindPackagesAsync (new ()).AsTask (ct);

        List<Package> packages = [];

        foreach (MatchResult m in Materialize (result.Matches))
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
                    Id = pkg.Id,
                    Name = pkg.Name,
                    Version = installed,
                    Source = SourceOf (pkg),
                    AvailableVersion = updateAvailable ? LatestAvailableVersion (pkg) : null
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
        CatalogPackage? pkg = await FindByIdAsync (id, null, installedContext: false, ct);

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

        string? description = Coalesce (meta?.Description, meta?.ShortDescription);

        // Installed-only metadata (location/scope) comes from the installed version's metadata
        // bag, not the manifest — so resolve it from the installed version specifically.
        PackageVersionInfo? installed = SafeInstalledVersion (pkg);

        try
        {
            return new ()
            {
                Id = pkg.Id,
                Name = Coalesce (meta?.PackageName, pkg.Name) ?? pkg.Id,
                Version = SafeVersion (SafeInstalledVersion (pkg)) ?? SafeVersion (versionInfo) ?? string.Empty,
                AvailableVersion = LatestAvailableVersion (pkg),
                InstalledVersion = SafeVersion (installed),
                Source = SourceOf (pkg),
                Publisher = NullIfEmpty (meta?.Publisher),
                Author = NullIfEmpty (meta?.Author),
                Copyright = NullIfEmpty (meta?.Copyright),
                Description = description,
                Homepage = NullIfEmpty (meta?.PackageUrl),
                License = NullIfEmpty (meta?.License),
                ReleaseNotesUrl = NullIfEmpty (meta?.ReleaseNotesUrl),
                SupportUrl = NullIfEmpty (meta?.PublisherSupportUrl),
                PrivacyUrl = NullIfEmpty (meta?.PrivacyUrl),
                PurchaseUrl = NullIfEmpty (meta?.PurchaseUrl),
                InstallationNotes = NullIfEmpty (meta?.InstallationNotes),
                InstalledLocation = SafeMetadata (installed, PackageVersionMetadataField.InstalledLocation),
                InstalledScope = SafeMetadata (installed, PackageVersionMetadataField.InstalledScope),
                Tags = meta is null ? null : StringVector (() => meta.Tags),
                Documentation = DocLinks (meta),
                ProductCodes = StringVector (() => versionInfo.ProductCodes),
                PackageFamilyNames = StringVector (() => versionInfo.PackageFamilyNames)
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
        CatalogPackage? pkg = await FindByIdAsync (id, null, installedContext: false, ct);

        if (pkg is null)
        {
            return [];
        }

        List<string> versions = [];
        HashSet<string> seen = new (StringComparer.OrdinalIgnoreCase);

        try
        {
            // AvailableVersions is newest-first. Indexed access via Materialize (AOT rule).
            foreach (PackageVersionId vid in Materialize (pkg.AvailableVersions))
            {
                string v = vid.Version;

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
        CatalogPackage? pkg = await FindByIdAsync (id, null, installedContext: false, ct);

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
                Version = SafeVersion (versionInfo)
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
        Operation op = new () { Kind = OperationKind.Install, PackageId = id, Version = version };
        CatalogPackage? pkg = await FindByIdAsync (id, null, installedContext: false, ct);

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
                return Fail (op, $"Version '{version}' is not available for {pkg.Name}.");
            }

            options.PackageVersionId = versionId;
        }

        // Set the progress handler on the WinRT op before awaiting; it fires on a COM thread,
        // so the IProgress<> the caller supplies is responsible for marshaling to the UI.
        var asyncOp = _pm.InstallPackageAsync (pkg, options);
        asyncOp.Progress = (_, p) => progress?.Report (MapInstall (p));
        InstallResult result = await asyncOp.AsTask (ct);

        return result.Status == InstallResultStatus.Ok
                   ? Ok (op, $"Installed {pkg.Name}{(result.RebootRequired ? " (reboot required)" : string.Empty)}")
                   : Fail (op, DescribeInstall ("Install", result));
    }

    public async Task<OpResult> UpgradeAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        Operation op = new () { Kind = OperationKind.Upgrade, PackageId = id };

        // Installed context so the package carries both its installed version and the
        // correlated remote available versions that the upgrade resolves against.
        CatalogPackage? pkg = await FindByIdAsync (id, null, installedContext: true, ct);

        if (pkg is null)
        {
            return Fail (op, $"Installed package '{id}' not found.");
        }

        InstallOptions options = new ()
        {
            PackageInstallMode = PackageInstallMode.Silent,
            AcceptPackageAgreements = true
        };

        var asyncOp = _pm.UpgradePackageAsync (pkg, options);
        asyncOp.Progress = (_, p) => progress?.Report (MapInstall (p));
        InstallResult result = await asyncOp.AsTask (ct);

        return result.Status == InstallResultStatus.Ok
                   ? Ok (op, $"Upgraded {pkg.Name}{(result.RebootRequired ? " (reboot required)" : string.Empty)}")
                   : Fail (op, DescribeInstall ("Upgrade", result));
    }

    public async Task<OpResult> UninstallAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        Operation op = new () { Kind = OperationKind.Uninstall, PackageId = id };
        CatalogPackage? pkg = await FindByIdAsync (id, null, installedContext: true, ct);

        if (pkg is null)
        {
            return Fail (op, $"Installed package '{id}' not found.");
        }

        UninstallOptions options = new () { PackageUninstallMode = PackageUninstallMode.Silent };
        var asyncOp = _pm.UninstallPackageAsync (pkg, options);
        asyncOp.Progress = (_, p) => progress?.Report (MapUninstall (p));
        UninstallResult result = await asyncOp.AsTask (ct);

        return result.Status == UninstallResultStatus.Ok
                   ? Ok (op, $"Uninstalled {pkg.Name}{(result.RebootRequired ? " (reboot required)" : string.Empty)}")
                   : Fail (op, $"Uninstall failed: {result.Status} (installer 0x{result.UninstallerErrorCode:X}, hr 0x{HResultOf (result.ExtendedErrorCode):X8})");
    }

    public async Task<OpResult> DownloadAsync (string id, string? version, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        Operation op = new () { Kind = OperationKind.Download, PackageId = id, Version = version };
        CatalogPackage? pkg = await FindByIdAsync (id, null, installedContext: false, ct);

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
                return Fail (op, $"Version '{version}' is not available for {pkg.Name}.");
            }

            options.PackageVersionId = versionId;
        }

        var asyncOp = _pm.DownloadPackageAsync (pkg, options);
        asyncOp.Progress = (_, p) => progress?.Report (MapDownload (p));
        DownloadResult result = await asyncOp.AsTask (ct);

        return result.Status == DownloadResultStatus.Ok
                   ? Ok (op, $"Downloaded {pkg.Name} to {dir}")
                   : Fail (op, $"Download failed: {result.Status} (hr 0x{HResultOf (result.ExtendedErrorCode):X8})");
    }

    public bool CanRepair => true;

    public async Task<OpResult> RepairAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        Operation op = new () { Kind = OperationKind.Repair, PackageId = id };

        // Installed context so the package carries the installed version that repair targets.
        CatalogPackage? pkg = await FindByIdAsync (id, null, installedContext: true, ct);

        if (pkg is null)
        {
            return Fail (op, $"Installed package '{id}' not found.");
        }

        RepairOptions options = new () { PackageRepairMode = PackageRepairMode.Silent };

        var asyncOp = _pm.RepairPackageAsync (pkg, options);
        asyncOp.Progress = (_, p) => progress?.Report (MapRepair (p));
        RepairResult result = await asyncOp.AsTask (ct);

        return result.Status switch
        {
            RepairResultStatus.Ok => Ok (op, $"Repaired {pkg.Name}{(result.RebootRequired ? " (reboot required)" : string.Empty)}"),
            RepairResultStatus.NoApplicableRepairer => Fail (op, $"{pkg.Name} doesn't support repair."),
            _ => Fail (op, $"Repair failed: {result.Status} (repairer {result.RepairerErrorCode}, hr 0x{HResultOf (result.ExtendedErrorCode):X8})")
        };
    }

    public async Task<InstallVerification?> VerifyInstalledAsync (string id, CancellationToken ct)
    {
        CatalogPackage? pkg = await FindByIdAsync (id, null, installedContext: true, ct);

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

            // Two nested projected vectors — indexed via Materialize (AOT rule).
            foreach (PackageInstallerInstalledStatus installer in Materialize (result.PackageInstalledStatus))
            {
                IReadOnlyList<InstalledStatus> entries;

                try
                {
                    entries = Materialize (installer.InstallerInstalledStatus);
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
                        string? path = NullIfEmpty (entry.Path);
                        checks.Add (new (StatusTypeName (entry.Type), ok, ok ? path : Coalesce (path, $"hr 0x{HResultOf (entry.Status):X8}")));
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
    private static IReadOnlyList<string>? StringVector (Func<IReadOnlyList<string>?> get)
    {
        try
        {
            IReadOnlyList<string>? projected = get ();

            if (projected is null)
            {
                return null;
            }

            List<string> list = [];

            foreach (string s in Materialize (projected))
            {
                if (!string.IsNullOrWhiteSpace (s))
                {
                    list.Add (s);
                }
            }

            return list.Count > 0 ? list : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<DocLink>? DocLinks (CatalogPackageMetadata? meta)
    {
        if (meta is null)
        {
            return null;
        }

        try
        {
            List<DocLink> links = [];

            foreach (Documentation d in Materialize (meta.Documentations))
            {
                try
                {
                    string url = d.DocumentUrl;

                    if (!string.IsNullOrWhiteSpace (url))
                    {
                        links.Add (new (string.IsNullOrWhiteSpace (d.DocumentLabel) ? "Documentation" : d.DocumentLabel, url));
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

    public Task<string> DescribeAsync (CancellationToken ct)
    {
        string version;

        try
        {
            // PackageManager.Version (contract 13) — the running WinGet COM server version.
            version = NullIfEmpty (_pm.Version) ?? "unknown version";
        }
        catch
        {
            // Older COM servers don't expose Version; the backend still works.
            version = "unknown version";
        }

        return Task.FromResult ($"COM · winget {version}");
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
    private List<PackageCatalogReference> RemoteRefs (string? source)
    {
        List<PackageCatalogReference> refs = [];

        if (!string.IsNullOrEmpty (source))
        {
            PackageCatalogReference? r = _pm.GetPackageCatalogByName (source);

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
            foreach (PackageCatalogReference r in Materialize (_pm.GetPackageCatalogs ()))
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
                PackageCatalogReference? r = _pm.GetPackageCatalogByName (name);

                if (r is not null)
                {
                    r.AcceptSourceAgreements = true;
                    refs.Add (r);
                }
            }
        }

        return refs;
    }

    public Task<IReadOnlyList<string>> ListSourcesAsync (CancellationToken ct)
    {
        List<string> names = [];

        try
        {
            foreach (PackageCatalogReference r in Materialize (_pm.GetPackageCatalogs ()))
            {
                try
                {
                    string? name = NullIfEmpty (r.Info?.Name);

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

        return Task.FromResult<IReadOnlyList<string>> (names);
    }

    /// <summary>
    /// Wrap one-or-more remote references into a composite catalog. The local "installed"
    /// catalog is implicit in every composite; <paramref name="behavior"/> selects which side
    /// queries return.
    /// </summary>
    private PackageCatalogReference CompositeRef (List<PackageCatalogReference> refs, CompositeSearchBehavior behavior)
    {
        CreateCompositePackageCatalogOptions opts = new () { CompositeSearchBehavior = behavior };

        foreach (PackageCatalogReference r in refs)
        {
            opts.Catalogs.Add (r);
        }

        return _pm.CreateCompositePackageCatalog (opts);
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
    private async Task<CatalogPackage?> FindByIdAsync (string id, string? source, bool installedContext, CancellationToken ct)
    {
        PackageCatalog catalog = await ConnectAsync (
            CompositeRef (
                RemoteRefs (source),
                installedContext ? CompositeSearchBehavior.LocalCatalogs : CompositeSearchBehavior.RemotePackagesFromRemoteCatalogs),
            ct);

        FindPackagesOptions opts = new ();
        opts.Filters.Add (new ()
        {
            Field = PackageMatchField.Id,
            Option = PackageFieldMatchOption.EqualsCaseInsensitive,
            Value = id
        });

        FindPackagesResult result = await catalog.FindPackagesAsync (opts).AsTask (ct);
        List<MatchResult> matches = Materialize (result.Matches);

        return matches.Count > 0 ? matches [0].CatalogPackage : null;
    }

    private static PackageVersionId? FindVersionId (CatalogPackage pkg, string version)
    {
        try
        {
            foreach (PackageVersionId vid in Materialize (pkg.AvailableVersions))
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
    private static List<T> Materialize<T> (IReadOnlyList<T> projected)
    {
        int count = projected.Count;
        List<T> copy = new (count);

        for (int i = 0; i < count; i++)
        {
            copy.Add (projected [i]);
        }

        return copy;
    }

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
}
#endif
