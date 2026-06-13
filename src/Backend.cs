
namespace WingetTuiSharp;

public interface IBackend
{
    // `source` is a catalog name (e.g. "winget", "msstore", or a custom REST source) to scope the
    // query to, or null for all configured sources. Discover the available names via ListSourcesAsync.
    Task<IReadOnlyList<Package>> SearchAsync (string query, string? source, CancellationToken ct);
    Task<IReadOnlyList<Package>> ListInstalledAsync (string? source, CancellationToken ct);
    Task<IReadOnlyList<Package>> ListUpgradesAsync (string? source, CancellationToken ct);
    Task<PackageDetail?> ShowAsync (string id, CancellationToken ct);

    // The names of the configured package sources (catalogs), e.g. ["winget", "msstore"], used to
    // build the source-filter cycle dynamically instead of hard-coding the two predefined sources.
    // The COM backend reads them from PackageManager.GetPackageCatalogs(); the CLI parses
    // `winget source list`; the mock returns the two defaults.
    Task<IReadOnlyList<string>> ListSourcesAsync (CancellationToken ct);

    // Available versions for a package, newest first. Drives the version picker. Backends that
    // can't enumerate versions (CLI) return an empty list, in which case the UI falls back to a
    // free-text version prompt.
    Task<IReadOnlyList<string>> ListVersionsAsync (string id, CancellationToken ct);

    // What would be installed (installer type / architecture / scope / elevation) for a package,
    // optionally at a specific version. Shown in the install confirm dialog. Returns null when the
    // backend can't resolve it (CLI), in which case the confirm shows no preview line.
    Task<InstallerPreview?> GetInstallerPreviewAsync (string id, string? version, CancellationToken ct);

    // The install/upgrade/uninstall operations optionally report structured progress through
    // `progress`. Backends that can't (CLI) ignore it; the COM backend maps the WinGet COM
    // progress events onto OpProgress; the mock backend synthesizes a download→install ramp.
    Task<OpResult> InstallAsync (string id, string? version, InstallSettings? settings, IProgress<OpProgress>? progress, CancellationToken ct);
    Task<OpResult> UninstallAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct);
    Task<OpResult> UpgradeAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct);

    // Fetch a package's installer to disk without installing it (winget "download"), reusing the
    // same progress reporting as install. Returns the download location in the OpResult message.
    Task<OpResult> DownloadAsync (string id, string? version, IProgress<OpProgress>? progress, CancellationToken ct);

    // Check whether an installed package's files/registration are intact (COM
    // CheckInstalledStatus). Returns null when the backend has no equivalent (CLI).
    Task<InstallVerification?> VerifyInstalledAsync (string id, CancellationToken ct);

    // True when this backend can repair an installed package (COM only). The UI gates the Repair
    // action on this and shows a neutral "only available on the COM backend" message otherwise,
    // mirroring how Verify degrades. RepairAsync re-runs the installer in repair mode to fix a
    // damaged install, reporting progress like install/upgrade. Only meaningful when CanRepair.
    bool CanRepair { get; }
    Task<OpResult> RepairAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct);

    Task<OpResult> PinAsync (string id, CancellationToken ct);
    Task<OpResult> UnpinAsync (string id, CancellationToken ct);
    Task<IReadOnlyDictionary<string, PinState>> ListPinsAsync (CancellationToken ct);

    // A short, human-readable description of which backend is live and (where available) the
    // winget version behind it — e.g. "COM · winget 1.11.400", "CLI · winget 1.11.400", or
    // "Mock backend". Shown in the help dialog and at startup so it's obvious which backend the
    // app actually selected (the COM build can silently fall back to CLI — see WINDOWS-TESTING.md).
    Task<string> DescribeAsync (CancellationToken ct);
}
