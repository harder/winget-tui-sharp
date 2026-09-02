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
///  - Mutating COM work is serialized through a 32-waiter bounded, cancellation-aware gate.
///    Reads use independent PackageManager instances so a long install cannot freeze list/detail
///    refreshes. Every call retains its manager through the complete asynchronous operation; no
///    projected COM object crosses calls. All projected collections and strings are bounded before
///    managed retention. This is compile- and unit-tested from macOS, but activation, apartment
///    behavior, and cancellation still require Windows runtime testing.
///  - Pinning delegates to winget.exe, so pin/unpin/list-pins need winget on PATH even on this
///    backend. If the COM server is registered but winget.exe isn't reachable, pin operations
///    fail (visibly, via the returned OpResult) while everything else keeps working.
/// </summary>
public sealed class ComBackend : IBackend
{
    private readonly BoundedAsyncGate _mutationGate = new (maxQueuedWaiters: 32);

    // Pin operations fall through to the CLI — the COM API has no pinning surface.
    private readonly CliBackend _cliForPins = new ();

    public ComBackend ()
    {
        // Preserve eager activation as the backend-selection probe. Calls use a fresh manager so
        // a projected COM instance is never shared across concurrent reads or mutations.
        _ = new PackageManager ();
    }

    private async Task<T> WithPackageManagerAsync<T> (
        CancellationToken ct,
        Func<PackageManager, CancellationToken, Task<T>> operation)
    {
        PackageManager pm = new ();

        try
        {
            return await operation (pm, ct);
        }
        finally
        {
            // Keep the operation's COM activation context alive until every projected async
            // operation/callback has completed, faulted, or observed cancellation.
            GC.KeepAlive (pm);
        }
    }

    private async Task<T> WithMutationPackageManagerAsync<T> (
        CancellationToken ct,
        Func<PackageManager, CancellationToken, Task<T>> operation)
    {
        using IDisposable lease = await _mutationGate.AcquireAsync (ct);

        return await WithPackageManagerAsync (ct, operation);
    }

    // ------------------------------------------------------------------------
    // Reads
    // ------------------------------------------------------------------------

    public Task<IReadOnlyList<Package>> SearchAsync (string query, string? source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace (query))
        {
            return Task.FromResult<IReadOnlyList<Package>> ([]);
        }

        return WithPackageManagerAsync (ct, (pm, token) => SearchCoreAsync (pm, query, source, token));
    }

    private static async Task<IReadOnlyList<Package>> SearchCoreAsync (
        PackageManager pm,
        string query,
        string? source,
        CancellationToken ct)
    {
        // Composite over the remote catalog(s), returning remote packages correlated with
        // installed status. CatalogDefault searches the catalog's default field set
        // (Id/Name/Moniker/Tags) — the free-text-search field.
        PackageCatalog catalog = await ConnectAsync (
            CompositeRef (pm, RemoteRefs (pm, source, ct), CompositeSearchBehavior.RemotePackagesFromRemoteCatalogs, ct),
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
        // ones. The UI infers truncation when the returned count reaches this shared cap.
        opts.ResultLimit = BackendLimits.SearchMatches;

        FindPackagesResult result = await catalog.FindPackagesAsync (opts).AsTask (ct);

        List<Package> packages = [];
        CharacterBudget characters = new (BackendLimits.PackageResultCharacters);

        foreach (MatchResult m in Materialize (result.Matches, BackendLimits.SearchMatches, ct))
        {
            ct.ThrowIfCancellationRequested ();
            try
            {
                CatalogPackage pkg = m.CatalogPackage;
                string? packageId = ExactIdentity (pkg.Id);
                string? rawVersion = SafeVersion (SafeDefaultInstallVersion (pkg)) ?? LatestAvailableVersion (pkg);
                string? rawSource = SourceOf (pkg);

                // The search composite (RemotePackagesFromRemoteCatalogs) correlates installed
                // status, so a search row knows whether it's installed and whether an upgrade is
                // available — surfaced so the UI can offer Uninstall/Upgrade rather than Install.
                string? installedVersion = SafeVersion (SafeInstalledVersion (pkg));
                bool updateAvailable = installedVersion is not null && SafeIsUpdateAvailable (pkg);
                string? availableVersion = updateAvailable ? LatestAvailableVersion (pkg) : null;

                if (packageId is null
                    || !TryExactIdentity (rawVersion, out string? version)
                    || !TryExactIdentity (installedVersion, out installedVersion)
                    || !TryExactIdentity (availableVersion, out availableVersion)
                    || !TryExactIdentity (rawSource, out string? packageSource))
                {
                    continue;
                }

                if (!characters.TryReserveExact (
                        packageId,
                        version,
                        packageSource,
                        installedVersion,
                        availableVersion))
                {
                    break;
                }

                packages.Add (new ()
                {
                    Id = packageId,
                    Name = characters.TakeDisplay (pkg.Name, BackendLimits.SimpleTextCharacters) ?? string.Empty,
                    Version = version ?? string.Empty,
                    Source = packageSource ?? string.Empty,
                    MatchField = characters.TakeDisplay (NotableMatchField (m), BackendLimits.SimpleTextCharacters),
                    InstalledVersion = installedVersion,
                    AvailableVersion = availableVersion
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
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
        => WithPackageManagerAsync (ct, (pm, token) => ListLocalAsync (pm, source, upgradesOnly: false, token));

    public Task<IReadOnlyList<Package>> ListUpgradesAsync (string? source, CancellationToken ct)
        => WithPackageManagerAsync (ct, (pm, token) => ListLocalAsync (pm, source, upgradesOnly: true, token));

    /// <summary>
    /// Installed packages, optionally filtered to those with an available upgrade. Uses a
    /// composite catalog with <see cref="CompositeSearchBehavior.LocalCatalogs"/>: results come
    /// from the implicit local "installed" catalog, correlated against the supplied remote
    /// catalog(s) so each row knows its available version / update status.
    /// </summary>
    private static async Task<IReadOnlyList<Package>> ListLocalAsync (PackageManager pm, string? source, bool upgradesOnly, CancellationToken ct)
    {
        PackageCatalog catalog = await ConnectAsync (
            CompositeRef (pm, RemoteRefs (pm, source, ct), CompositeSearchBehavior.LocalCatalogs, ct),
            ct);

        // An empty filter set returns every installed package.
        FindPackagesOptions options = new () { ResultLimit = BackendLimits.LocalMatches };
        FindPackagesResult result = await catalog.FindPackagesAsync (options).AsTask (ct);

        List<Package> packages = [];
        CharacterBudget characters = new (BackendLimits.PackageResultCharacters);

        foreach (MatchResult m in Materialize (result.Matches, BackendLimits.LocalMatches, ct))
        {
            ct.ThrowIfCancellationRequested ();
            try
            {
                CatalogPackage pkg = m.CatalogPackage;
                bool updateAvailable = SafeIsUpdateAvailable (pkg);

                if (upgradesOnly && !updateAvailable)
                {
                    continue;
                }

                string installed = SafeVersion (SafeInstalledVersion (pkg)) ?? string.Empty;
                string? packageId = ExactIdentity (pkg.Id);
                string? availableVersion = updateAvailable ? LatestAvailableVersion (pkg) : null;
                string? rawSource = SourceOf (pkg);

                if (packageId is null
                    || !TryExactIdentity (installed, out string? exactInstalled)
                    || !TryExactIdentity (availableVersion, out availableVersion)
                    || !TryExactIdentity (rawSource, out string? packageSource))
                {
                    continue;
                }

                if (!characters.TryReserveExact (
                        packageId,
                        exactInstalled,
                        packageSource,
                        availableVersion))
                {
                    break;
                }

                packages.Add (new ()
                {
                    Id = packageId,
                    Name = characters.TakeDisplay (pkg.Name, BackendLimits.SimpleTextCharacters) ?? string.Empty,
                    Version = exactInstalled ?? string.Empty,
                    Source = packageSource ?? string.Empty,
                    AvailableVersion = availableVersion
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Skip a malformed row (bad HRESULT on a property read) rather than failing
                // the entire listing.
            }
        }

        return packages;
    }

    public Task<PackageDetail?> ShowAsync (string id, CancellationToken ct)
        => WithPackageManagerAsync (ct, (pm, token) => ShowCoreAsync (pm, id, token));

    private static async Task<PackageDetail?> ShowCoreAsync (PackageManager pm, string id, CancellationToken ct)
    {
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
            ct.ThrowIfCancellationRequested ();
            meta = versionInfo.GetCatalogPackageMetadata ();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // No localized manifest metadata available; fall back to the bare fields below.
        }

        string? description = Coalesce (meta?.Description, meta?.ShortDescription);

        // Installed-only metadata (location/scope) comes from the installed version's metadata
        // bag, not the manifest — so resolve it from the installed version specifically.
        PackageVersionInfo? installed = SafeInstalledVersion (pkg);
        CollectionBudget metadataBudget = new (BackendLimits.MetadataItems);
        CharacterBudget detailCharacters = new (BackendLimits.PackageDetailCharacters);

        try
        {
            string? packageId = ExactIdentity (pkg.Id);
            string? rawVersion = SafeVersion (SafeInstalledVersion (pkg)) ?? SafeVersion (versionInfo);
            string? rawAvailableVersion = LatestAvailableVersion (pkg);
            string? rawInstalledVersion = SafeVersion (installed);
            string? rawSource = SourceOf (pkg);

            if (packageId is null
                || !TryExactIdentity (rawVersion, out string? version)
                || !TryExactIdentity (rawAvailableVersion, out string? availableVersion)
                || !TryExactIdentity (rawInstalledVersion, out string? installedVersion)
                || !TryExactIdentity (rawSource, out string? packageSource))
            {
                return null;
            }

            if (!detailCharacters.TryReserveExact (
                    packageId,
                    version,
                    availableVersion,
                    installedVersion,
                    packageSource))
            {
                return null;
            }

            string? displayName = detailCharacters.TakeDisplay (
                Coalesce (meta?.PackageName, pkg.Name),
                BackendLimits.SimpleTextCharacters);

            return new ()
            {
                Id = packageId,
                Name = string.IsNullOrEmpty (displayName) ? packageId : displayName,
                Version = version ?? string.Empty,
                AvailableVersion = availableVersion,
                InstalledVersion = installedVersion,
                Source = packageSource ?? string.Empty,
                Publisher = detailCharacters.TakeDisplay (NullIfEmpty (meta?.Publisher), BackendLimits.SimpleTextCharacters),
                Author = detailCharacters.TakeDisplay (NullIfEmpty (meta?.Author), BackendLimits.SimpleTextCharacters),
                Copyright = detailCharacters.TakeDisplay (NullIfEmpty (meta?.Copyright), BackendLimits.SimpleTextCharacters),
                Description = detailCharacters.TakeDisplay (description, BackendLimits.RichTextCharacters),
                Homepage = detailCharacters.TakeDisplay (NullIfEmpty (meta?.PackageUrl), BackendLimits.SimpleTextCharacters),
                License = detailCharacters.TakeDisplay (NullIfEmpty (meta?.License), BackendLimits.SimpleTextCharacters),
                ReleaseNotesUrl = detailCharacters.TakeDisplay (NullIfEmpty (meta?.ReleaseNotesUrl), BackendLimits.SimpleTextCharacters),
                SupportUrl = detailCharacters.TakeDisplay (NullIfEmpty (meta?.PublisherSupportUrl), BackendLimits.SimpleTextCharacters),
                PrivacyUrl = detailCharacters.TakeDisplay (NullIfEmpty (meta?.PrivacyUrl), BackendLimits.SimpleTextCharacters),
                PurchaseUrl = detailCharacters.TakeDisplay (NullIfEmpty (meta?.PurchaseUrl), BackendLimits.SimpleTextCharacters),
                InstallationNotes = detailCharacters.TakeDisplay (NullIfEmpty (meta?.InstallationNotes), BackendLimits.RichTextCharacters),
                InstalledLocation = detailCharacters.TakeDisplay (
                    SafeMetadata (installed, PackageVersionMetadataField.InstalledLocation),
                    BackendLimits.SimpleTextCharacters),
                InstalledScope = detailCharacters.TakeDisplay (
                    SafeMetadata (installed, PackageVersionMetadataField.InstalledScope),
                    BackendLimits.SimpleTextCharacters),
                Tags = meta is null ? null : StringVector (() => meta.Tags, metadataBudget, detailCharacters, ct),
                Documentation = DocLinks (meta, metadataBudget, detailCharacters, ct),
                ProductCodes = StringVector (() => versionInfo.ProductCodes, metadataBudget, detailCharacters, ct),
                PackageFamilyNames = StringVector (() => versionInfo.PackageFamilyNames, metadataBudget, detailCharacters, ct)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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

    public Task<IReadOnlyList<string>> ListVersionsAsync (string id, CancellationToken ct)
        => WithPackageManagerAsync (ct, (pm, token) => ListVersionsCoreAsync (pm, id, token));

    private static async Task<IReadOnlyList<string>> ListVersionsCoreAsync (PackageManager pm, string id, CancellationToken ct)
    {
        CatalogPackage? pkg = await FindByIdAsync (pm, id, null, installedContext: false, ct);

        if (pkg is null)
        {
            return [];
        }

        List<string> versions = [];
        HashSet<string> seen = new (StringComparer.OrdinalIgnoreCase);
        CharacterBudget characters = new (BackendLimits.VersionResultCharacters);

        try
        {
            // AvailableVersions is newest-first. Indexed access via Materialize (AOT rule).
            foreach (PackageVersionId vid in Materialize (pkg.AvailableVersions, BackendLimits.Versions, ct))
            {
                ct.ThrowIfCancellationRequested ();
                string? v = ExactIdentity (vid.Version);

                if (v is not null && !seen.Contains (v))
                {
                    if (!characters.TryTakeExact (v, out string? accepted))
                    {
                        break;
                    }

                    seen.Add (accepted!);
                    versions.Add (accepted!);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Return whatever we collected before the version list became unreadable.
        }

        return versions;
    }

    public Task<InstallerPreview?> GetInstallerPreviewAsync (string id, string? version, CancellationToken ct)
        => WithPackageManagerAsync (ct, (pm, token) => GetInstallerPreviewCoreAsync (pm, id, version, token));

    private static async Task<InstallerPreview?> GetInstallerPreviewCoreAsync (
        PackageManager pm,
        string id,
        string? version,
        CancellationToken ct)
    {
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
            PackageVersionId? vid = FindVersionId (pkg, version, ct);
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
            ct.ThrowIfCancellationRequested ();
            // Resolve the installer that *would* be chosen for default options on this machine.
            PackageInstallerInfo installer = versionInfo.GetApplicableInstaller (new InstallOptions ());

            if (installer is null)
            {
                return null;
            }

            string? rawVersion = SafeVersion (versionInfo);

            if (!TryExactIdentity (rawVersion, out string? exactVersion))
            {
                return null;
            }

            return new InstallerPreview
            {
                InstallerType = TypeName (installer.InstallerType),
                Architecture = ArchName (installer.Architecture),
                Scope = ScopeName (installer.Scope),
                RequiresElevation = RequiresElevation (installer),
                Version = exactVersion
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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

    public Task<OpResult> InstallAsync (string id, string? version, InstallSettings? settings, IProgress<OpProgress>? progress, CancellationToken ct)
        => WithMutationPackageManagerAsync (ct, (pm, token) => InstallCoreAsync (pm, id, version, settings, progress, token));

    private static async Task<OpResult> InstallCoreAsync (
        PackageManager pm,
        string id,
        string? version,
        InstallSettings? settings,
        IProgress<OpProgress>? progress,
        CancellationToken ct)
    {
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
            PackageVersionId? versionId = FindVersionId (pkg, version, ct);

            if (versionId is null)
            {
                return Fail (op, $"Version '{version}' is not available for {SimpleText (pkg.Name) ?? id}.");
            }

            options.PackageVersionId = versionId;
        }

        // Set the progress handler on the WinRT op before awaiting; it fires on a COM thread,
        // so the IProgress<> the caller supplies is responsible for marshaling to the UI.
        var asyncOp = pm.InstallPackageAsync (pkg, options);
        InstallResult result;

        try
        {
            asyncOp.Progress = (_, p) => progress?.Report (MapInstall (p));
            result = await asyncOp.AsTask (ct);
        }
        finally
        {
            BestEffortCleanup.Run (
                () => asyncOp.Progress = null,
                () => GC.KeepAlive (asyncOp));
        }

        return result.Status == InstallResultStatus.Ok
                   ? Ok (op, $"Installed {SimpleText (pkg.Name) ?? id}{(result.RebootRequired ? " (reboot required)" : string.Empty)}")
                   : Fail (op, DescribeInstall ("Install", result));
    }

    public Task<OpResult> UpgradeAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct)
        => WithMutationPackageManagerAsync (ct, (pm, token) => UpgradeCoreAsync (pm, id, progress, token));

    private static async Task<OpResult> UpgradeCoreAsync (
        PackageManager pm,
        string id,
        IProgress<OpProgress>? progress,
        CancellationToken ct)
    {
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
        InstallResult result;

        try
        {
            asyncOp.Progress = (_, p) => progress?.Report (MapInstall (p));
            result = await asyncOp.AsTask (ct);
        }
        finally
        {
            BestEffortCleanup.Run (
                () => asyncOp.Progress = null,
                () => GC.KeepAlive (asyncOp));
        }

        return result.Status == InstallResultStatus.Ok
                   ? Ok (op, $"Upgraded {SimpleText (pkg.Name) ?? id}{(result.RebootRequired ? " (reboot required)" : string.Empty)}")
                   : Fail (op, DescribeInstall ("Upgrade", result));
    }

    public Task<OpResult> UninstallAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct)
        => WithMutationPackageManagerAsync (ct, (pm, token) => UninstallCoreAsync (pm, id, progress, token));

    private static async Task<OpResult> UninstallCoreAsync (
        PackageManager pm,
        string id,
        IProgress<OpProgress>? progress,
        CancellationToken ct)
    {
        Operation op = new () { Kind = OperationKind.Uninstall, PackageId = id };
        CatalogPackage? pkg = await FindByIdAsync (pm, id, null, installedContext: true, ct);

        if (pkg is null)
        {
            return Fail (op, $"Installed package '{id}' not found.");
        }

        UninstallOptions options = new () { PackageUninstallMode = PackageUninstallMode.Silent };
        var asyncOp = pm.UninstallPackageAsync (pkg, options);
        UninstallResult result;

        try
        {
            asyncOp.Progress = (_, p) => progress?.Report (MapUninstall (p));
            result = await asyncOp.AsTask (ct);
        }
        finally
        {
            BestEffortCleanup.Run (
                () => asyncOp.Progress = null,
                () => GC.KeepAlive (asyncOp));
        }

        return result.Status == UninstallResultStatus.Ok
                   ? Ok (op, $"Uninstalled {SimpleText (pkg.Name) ?? id}{(result.RebootRequired ? " (reboot required)" : string.Empty)}")
                   : Fail (op, $"Uninstall failed: {result.Status} (installer 0x{result.UninstallerErrorCode:X}, hr 0x{HResultOf (result.ExtendedErrorCode):X8})");
    }

    public Task<OpResult> DownloadAsync (string id, string? version, IProgress<OpProgress>? progress, CancellationToken ct)
        => WithMutationPackageManagerAsync (ct, (pm, token) => DownloadCoreAsync (pm, id, version, progress, token));

    private static async Task<OpResult> DownloadCoreAsync (
        PackageManager pm,
        string id,
        string? version,
        IProgress<OpProgress>? progress,
        CancellationToken ct)
    {
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
            PackageVersionId? versionId = FindVersionId (pkg, version, ct);

            if (versionId is null)
            {
                return Fail (op, $"Version '{version}' is not available for {SimpleText (pkg.Name) ?? id}.");
            }

            options.PackageVersionId = versionId;
        }

        var asyncOp = pm.DownloadPackageAsync (pkg, options);
        DownloadResult result;

        try
        {
            asyncOp.Progress = (_, p) => progress?.Report (MapDownload (p));
            result = await asyncOp.AsTask (ct);
        }
        finally
        {
            BestEffortCleanup.Run (
                () => asyncOp.Progress = null,
                () => GC.KeepAlive (asyncOp));
        }

        return result.Status == DownloadResultStatus.Ok
                   ? Ok (op, $"Downloaded {SimpleText (pkg.Name) ?? id} to {dir}")
                   : Fail (op, $"Download failed: {result.Status} (hr 0x{HResultOf (result.ExtendedErrorCode):X8})");
    }

    public bool CanRepair => true;

    public Task<OpResult> RepairAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct)
        => WithMutationPackageManagerAsync (ct, (pm, token) => RepairCoreAsync (pm, id, progress, token));

    private static async Task<OpResult> RepairCoreAsync (
        PackageManager pm,
        string id,
        IProgress<OpProgress>? progress,
        CancellationToken ct)
    {
        Operation op = new () { Kind = OperationKind.Repair, PackageId = id };

        // Installed context so the package carries the installed version that repair targets.
        CatalogPackage? pkg = await FindByIdAsync (pm, id, null, installedContext: true, ct);

        if (pkg is null)
        {
            return Fail (op, $"Installed package '{id}' not found.");
        }

        RepairOptions options = new () { PackageRepairMode = PackageRepairMode.Silent };

        var asyncOp = pm.RepairPackageAsync (pkg, options);
        RepairResult result;

        try
        {
            asyncOp.Progress = (_, p) => progress?.Report (MapRepair (p));
            result = await asyncOp.AsTask (ct);
        }
        finally
        {
            BestEffortCleanup.Run (
                () => asyncOp.Progress = null,
                () => GC.KeepAlive (asyncOp));
        }

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

    public Task<InstallVerification?> VerifyInstalledAsync (string id, CancellationToken ct)
        => WithPackageManagerAsync (ct, (pm, token) => VerifyInstalledCoreAsync (pm, id, token));

    private static async Task<InstallVerification?> VerifyInstalledCoreAsync (PackageManager pm, string id, CancellationToken ct)
    {
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
            // Track whether every installer and status entry was observed. Partial data cannot
            // choose a trustworthy "best" installer unless another fully observed installer has
            // already proved the package healthy.
            List<VerificationCandidate> perInstaller = [];
            CollectionBudget verificationBudget = new (BackendLimits.VerificationItems);
            CharacterBudget verificationCharacters = new (BackendLimits.VerificationCharacters);
            int projectedInstallerCount = result.PackageInstalledStatus.Count;
            int requestedInstallerCount = Math.Min (
                projectedInstallerCount,
                BackendLimits.VerificationInstallers);
            CollectionTake installerTake = verificationBudget.TakeBounded (requestedInstallerCount);
            bool externalIncomplete = projectedInstallerCount > requestedInstallerCount || !installerTake.Complete;

            // Two nested projected vectors — indexed via Materialize (AOT rule).
            foreach (PackageInstallerInstalledStatus installer in Materialize (
                         result.PackageInstalledStatus,
                         installerTake.Count,
                         ct))
            {
                ct.ThrowIfCancellationRequested ();
                IReadOnlyList<InstalledStatus> entries;
                CollectionTake entryTake;

                try
                {
                    entryTake = verificationBudget.TakeBounded (installer.InstallerInstalledStatus.Count);
                    entries = Materialize (
                        installer.InstallerInstalledStatus,
                        entryTake.Count,
                        ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    externalIncomplete = true;

                    continue;
                }

                List<VerifyCheck> checks = [];
                bool complete = entryTake.Complete;

                foreach (InstalledStatus entry in entries)
                {
                    ct.ThrowIfCancellationRequested ();

                    if (verificationCharacters.Remaining == 0)
                    {
                        complete = false;
                        break;
                    }

                    try
                    {
                        // HRESULT projects to an Exception: null means S_OK (the check passed).
                        bool ok = entry.Status is null;
                        string? path = NullIfEmpty (entry.Path);
                        string label = verificationCharacters.TakeDisplay (
                                           StatusTypeName (entry.Type),
                                           BackendLimits.SimpleTextCharacters)
                                       ?? string.Empty;
                        string? detail = verificationCharacters.TakeDisplay (
                            ok ? path : Coalesce (path, $"hr 0x{HResultOf (entry.Status):X8}"),
                            BackendLimits.SimpleTextCharacters);
                        checks.Add (new (
                            label,
                            ok,
                            detail));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // Couldn't read this check (bad HRESULT projecting the entry). Record it as a
                        // failing display row and mark the installer incomplete. The evaluator then
                        // returns Error unless another fully observed installer independently passes.
                        complete = false;
                        string label = verificationCharacters.TakeDisplay (
                                           "Status check",
                                           BackendLimits.SimpleTextCharacters)
                                       ?? string.Empty;
                        string? detail = verificationCharacters.TakeDisplay (
                            "could not read installed-status entry",
                            BackendLimits.SimpleTextCharacters);
                        checks.Add (new (label, false, detail));
                    }
                }

                if (checks.Count > 0 || !complete)
                {
                    perInstaller.Add (new (checks, complete));
                }
            }

            // A complete passing installer still proves health under WinGet's any-installer
            // semantics. Without one, omitted/read-failed/truncated data could change which
            // installer is the best match and therefore must not produce a definitive result.
            VerificationDecision decision = VerificationEvaluator.Decide (perInstaller, externalIncomplete);
            return new () { Outcome = decision.Outcome, Checks = decision.Checks };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
    private static IReadOnlyList<string>? StringVector (
        Func<IReadOnlyList<string>?> get,
        CollectionBudget itemBudget,
        CharacterBudget characterBudget,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested ();
            IReadOnlyList<string>? projected = get ();

            if (projected is null)
            {
                return null;
            }

            List<string> list = [];

            foreach (string s in Materialize (projected, itemBudget.Take (projected.Count), ct))
            {
                ct.ThrowIfCancellationRequested ();

                if (characterBudget.Remaining == 0)
                {
                    break;
                }

                string? bounded = characterBudget.TakeDisplay (
                    NullIfEmpty (s),
                    BackendLimits.SimpleTextCharacters);

                if (bounded is not null)
                {
                    list.Add (bounded);
                }
            }

            return list.Count > 0 ? list : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<DocLink>? DocLinks (
        CatalogPackageMetadata? meta,
        CollectionBudget itemBudget,
        CharacterBudget characterBudget,
        CancellationToken ct)
    {
        if (meta is null)
        {
            return null;
        }

        try
        {
            ct.ThrowIfCancellationRequested ();
            List<DocLink> links = [];

            foreach (Documentation d in Materialize (
                         meta.Documentations,
                         itemBudget.Take (meta.Documentations.Count),
                         ct))
            {
                ct.ThrowIfCancellationRequested ();

                if (characterBudget.Remaining == 0)
                {
                    break;
                }

                try
                {
                    string? url = characterBudget.TakeDisplay (
                        d.DocumentUrl,
                        BackendLimits.SimpleTextCharacters);

                    if (!string.IsNullOrWhiteSpace (url))
                    {
                        string label = characterBudget.TakeDisplay (
                                           string.IsNullOrWhiteSpace (d.DocumentLabel)
                                               ? "Documentation"
                                               : d.DocumentLabel,
                                           BackendLimits.SimpleTextCharacters)
                                       ?? string.Empty;
                        links.Add (new (label, url));
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Skip a malformed documentation entry rather than dropping the whole list.
                }
            }

            return links.Count > 0 ? links : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
        => WithPackageManagerAsync (ct, DescribeCoreAsync);

    private static Task<string> DescribeCoreAsync (PackageManager pm, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested ();
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
    private static List<PackageCatalogReference> RemoteRefs (PackageManager pm, string? source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested ();
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
            foreach (PackageCatalogReference r in Materialize (pm.GetPackageCatalogs (), BackendLimits.Catalogs, ct))
            {
                ct.ThrowIfCancellationRequested ();
                r.AcceptSourceAgreements = true;
                refs.Add (r);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // GetPackageCatalogs failed (unusual); fall back to the two predefined sources so the
            // common case still works.
            foreach (string name in (string [])["winget", "msstore"])
            {
                ct.ThrowIfCancellationRequested ();
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

    public Task<IReadOnlyList<string>> ListSourcesAsync (CancellationToken ct)
        => WithPackageManagerAsync (ct, ListSourcesCoreAsync);

    private static Task<IReadOnlyList<string>> ListSourcesCoreAsync (PackageManager pm, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested ();
        List<string> names = [];
        CharacterBudget characters = new (BackendLimits.SourceResultCharacters);

        try
        {
            foreach (PackageCatalogReference r in Materialize (pm.GetPackageCatalogs (), BackendLimits.Catalogs, ct))
            {
                ct.ThrowIfCancellationRequested ();
                try
                {
                    string? name = ExactIdentity (r.Info?.Name);

                    if (name is not null)
                    {
                        if (!characters.TryTakeExact (name, out string? accepted))
                        {
                            break;
                        }

                        names.Add (accepted!);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Skip a catalog whose Info/Name read threw rather than dropping the whole list.
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
    private static PackageCatalogReference CompositeRef (
        PackageManager pm,
        List<PackageCatalogReference> refs,
        CompositeSearchBehavior behavior,
        CancellationToken ct)
    {
        CreateCompositePackageCatalogOptions opts = new () { CompositeSearchBehavior = behavior };

        foreach (PackageCatalogReference r in refs)
        {
            ct.ThrowIfCancellationRequested ();
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
                RemoteRefs (pm, source, ct),
                installedContext ? CompositeSearchBehavior.LocalCatalogs : CompositeSearchBehavior.RemotePackagesFromRemoteCatalogs,
                ct),
            ct);

        FindPackagesOptions opts = new () { ResultLimit = 1 };
        opts.Filters.Add (new ()
        {
            Field = PackageMatchField.Id,
            Option = PackageFieldMatchOption.EqualsCaseInsensitive,
            Value = id
        });

        FindPackagesResult result = await catalog.FindPackagesAsync (opts).AsTask (ct);
        List<MatchResult> matches = Materialize (result.Matches, 1, ct);

        return matches.Count > 0 ? matches [0].CatalogPackage : null;
    }

    private static PackageVersionId? FindVersionId (CatalogPackage pkg, string version, CancellationToken ct)
    {
        try
        {
            foreach (PackageVersionId vid in Materialize (pkg.AvailableVersions, BackendLimits.Versions, ct))
            {
                ct.ThrowIfCancellationRequested ();
                if (string.Equals (vid.Version, version, StringComparison.OrdinalIgnoreCase))
                {
                    return vid;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
    private static List<T> Materialize<T> (
        IReadOnlyList<T> projected,
        int maximum,
        CancellationToken ct)
        => BackendLimits.Materialize (projected, maximum, ct);

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

    private static string? ExactIdentity (string? value) => BackendLimits.ExactIdentity (value);

    private static bool TryExactIdentity (string? value, out string? exact)
        => BackendLimits.TryExactIdentity (value, out exact);
}
#endif
