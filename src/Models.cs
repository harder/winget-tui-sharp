namespace WingetTuiSharp;

public enum AppMode
{
    Search,
    Installed,
    Upgrades
}

public enum InputMode
{
    Normal,
    Search,
    LocalFilter,
    VersionInput
}

public enum Focus
{
    PackageList,
    DetailPanel
}

public enum SortField
{
    None,
    Name,
    Id,
    Version
}

public enum SortDir
{
    Asc,
    Desc
}

public enum PinFilter
{
    All,
    PinnedOnly,
    UnpinnedOnly
}

public enum PinStateKind
{
    None,
    Pinned,
    Blocking,
    Gating
}

public readonly record struct PinState (PinStateKind Kind, string? GatingVersion = null)
{
    public bool IsPinned => Kind != PinStateKind.None;

    public string DisplayLabel ()
        => Kind switch
        {
            PinStateKind.Pinned => "Pinned",
            PinStateKind.Blocking => "Blocking",
            PinStateKind.Gating => $"Gating {GatingVersion}",
            _ => string.Empty
        };

    public static readonly PinState Unpinned = new (PinStateKind.None);
}

public sealed class Package
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string? AvailableVersion { get; init; }
    public PinState PinState { get; set; } = PinState.Unpinned;

    /// <summary>
    /// The installed version of this package, when it's installed. On the Installed/Upgrades tabs
    /// every row is installed (so this mirrors <see cref="Version"/>); the value matters most for
    /// <b>search</b> rows, where it's the COM composite's correlated installed version (null when
    /// the package isn't installed) — the signal that lets the UI offer Uninstall/Upgrade instead
    /// of a bare Install. Null on the CLI backend (no cheap correlation there).
    /// </summary>
    public string? InstalledVersion { get; init; }

    /// <summary>
    /// For search results only: the manifest field the package matched on when it's a
    /// non-obvious one (Tag / Moniker / Command / family name / product code) — i.e. not Name/Id.
    /// Null for normal name/id matches and for non-search rows. Surfaced as a "Matched on" hint
    /// in the detail panel so the user understands why an unexpected package showed up.
    /// </summary>
    public string? MatchField { get; init; }

    public bool IsTruncated => Id.EndsWith ('…') || Id.EndsWith ("...", StringComparison.Ordinal);
}

public sealed class PackageDetail
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; set; } = string.Empty;
    public string? AvailableVersion { get; set; }
    public string? InstalledVersion { get; set; }
    public string Source { get; set; } = string.Empty;
    public PinState PinState { get; set; } = PinState.Unpinned;
    public string? Publisher { get; init; }
    public string? Description { get; set; }
    public string? Homepage { get; init; }
    public string? License { get; init; }
    public string? ReleaseNotesUrl { get; init; }

    // Richer manifest fields (populated by the COM backend; null elsewhere).
    public string? SupportUrl { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyList<DocLink>? Documentation { get; init; }
    public IReadOnlyList<string>? ProductCodes { get; init; }
    public IReadOnlyList<string>? PackageFamilyNames { get; init; }

    // Additional manifest/installed metadata surfaced by the COM backend (null elsewhere).
    // Every one is optional and rendered only when present, so sparse packages stay clean.
    public string? Author { get; init; }
    public string? Copyright { get; init; }
    public string? PrivacyUrl { get; init; }
    public string? PurchaseUrl { get; init; }
    public string? InstallationNotes { get; init; }
    public string? InstalledLocation { get; init; }
    public string? InstalledScope { get; init; }

    /// <summary>
    /// For search-result detail: the non-obvious field the package matched on (see
    /// <see cref="Package.MatchField"/>). Carried over from the originating list row.
    /// </summary>
    public string? MatchField { get; set; }

    /// <summary>
    /// True when <see cref="Description"/> holds a synthesized "couldn't fetch / nothing available"
    /// note rather than real manifest copy. Used by the detail panel to dim/annotate the line so
    /// it's not mistaken for the real package description.
    /// </summary>
    public bool IsDescriptionDegraded { get; set; }

    /// <summary>
    /// Merge in fields known from the originating <see cref="Package"/> list row that
    /// `winget show` doesn't always emit: Source, Version (installed), AvailableVersion.
    /// Mirrors upstream src/cli_backend.rs's `merge_over` behavior.
    /// </summary>
    public void MergeContext (Package context)
    {
        if (string.IsNullOrEmpty (Source))
        {
            Source = context.Source;
        }

        // `winget show` reports the latest manifest version. The installed version is on the
        // list row, so prefer that for the detail header.
        if (!string.IsNullOrEmpty (context.Version))
        {
            Version = context.Version;
        }

        if (string.IsNullOrEmpty (AvailableVersion) && !string.IsNullOrEmpty (context.AvailableVersion))
        {
            AvailableVersion = context.AvailableVersion;
        }

        if (string.IsNullOrEmpty (InstalledVersion) && !string.IsNullOrEmpty (context.InstalledVersion))
        {
            InstalledVersion = context.InstalledVersion;
        }

        if (!PinState.IsPinned && context.PinState.IsPinned)
        {
            PinState = context.PinState;
        }

        // The "matched on" hint is only known from the search row, not from `show`/FindById.
        if (string.IsNullOrEmpty (MatchField) && !string.IsNullOrEmpty (context.MatchField))
        {
            MatchField = context.MatchField;
        }
    }

    /// <summary>
    /// When the manifest-derived fields are all empty (no Publisher, Description, Homepage,
    /// License), synthesize a one-line note in <see cref="Description"/> explaining that
    /// detail isn't available — so the panel doesn't render a row of empty values.
    /// Mirrors upstream src/app.rs::ensure_detail_hint.
    /// </summary>
    public void EnsureDetailHint ()
    {
        if (!string.IsNullOrEmpty (Description))
        {
            return;
        }

        bool sparse = string.IsNullOrEmpty (Publisher)
                      && string.IsNullOrEmpty (Homepage)
                      && string.IsNullOrEmpty (License)
                      && string.IsNullOrEmpty (ReleaseNotesUrl);

        if (sparse)
        {
            Description = "Additional metadata is not available for this package in any configured winget source.";
            IsDescriptionDegraded = true;
        }
    }
}

public enum OperationKind
{
    Install,
    Uninstall,
    Upgrade,
    Pin,
    Unpin,
    BatchUpgrade,
    Download,
    Repair
}

public enum InstallScopePref
{
    Default,
    User,
    Machine
}

public enum InstallModePref
{
    Default,
    Silent,
    Interactive
}

public enum InstallArchPref
{
    Default,
    X64,
    X86,
    Arm64
}

/// <summary>
/// User-chosen advanced install options, gathered from the advanced-install panel and passed to
/// <see cref="IBackend.InstallAsync"/>. The COM backend maps these onto <c>InstallOptions</c>
/// (scope / mode / AllowedArchitectures / AdditionalInstallerArguments); the CLI backend maps them
/// onto winget flags; the mock ignores them. A null settings object means "backend defaults".
/// </summary>
public sealed class InstallSettings
{
    public InstallScopePref Scope { get; init; } = InstallScopePref.Default;
    public InstallModePref Mode { get; init; } = InstallModePref.Default;
    public InstallArchPref Architecture { get; init; } = InstallArchPref.Default;
    public string? CustomArgs { get; init; }

    /// <summary>True when nothing was customized — callers normalize this to null ("backend defaults").</summary>
    public bool IsDefault
        => Scope == InstallScopePref.Default
           && Mode == InstallModePref.Default
           && Architecture == InstallArchPref.Default
           && string.IsNullOrWhiteSpace (CustomArgs);
}

/// <summary>A labelled documentation link from a package manifest (Documentations entry).</summary>
public sealed record DocLink (string Label, string Url);

public enum VerifyOutcome
{
    Ok,            // all checks passed
    Issues,        // at least one check failed (files/registration missing → corrupt install)
    NotApplicable, // nothing to check (not installed, or no checks returned)
    Error          // the check itself failed, or the backend can't verify (CLI)
}

/// <summary>One installed-status check (e.g. a registry entry or install-location file).</summary>
public sealed record VerifyCheck (string Label, bool Ok, string? Detail);

/// <summary>
/// Result of the "verify install" action, backend-agnostic. The COM backend maps
/// <c>CheckInstalledStatusAsync</c> onto this; the CLI backend returns null (no equivalent).
/// </summary>
public sealed class InstallVerification
{
    public required VerifyOutcome Outcome { get; init; }
    public IReadOnlyList<VerifyCheck> Checks { get; init; } = [];

    public string Summary
        => Outcome switch
        {
            VerifyOutcome.Ok => "Installed correctly — all checks passed.",
            VerifyOutcome.Issues => $"{Checks.Count (c => !c.Ok)} of {Checks.Count} check(s) failed — the install may be corrupt.",
            VerifyOutcome.NotApplicable => "No installed-status checks were applicable.",
            _ => "Could not verify the install."
        };
}

public sealed class Operation
{
    public required OperationKind Kind { get; init; }
    public string? PackageId { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<string>? Ids { get; init; }
}

public sealed class OpResult
{
    public required Operation Operation { get; init; }
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Coarse phase of a long-running install/upgrade/uninstall, backend-agnostic. The COM
/// backend maps the WinGet <c>InstallProgress</c>/<c>UninstallProgress</c> states onto these;
/// the mock backend synthesizes them; the CLI backend can't report progress so it reports none.
/// </summary>
public enum OpPhase
{
    Queued,
    Downloading,
    Installing,
    Uninstalling,
    Repairing,
    Finalizing,
    Done
}

/// <summary>
/// A single progress sample for an in-flight operation. <see cref="Fraction"/> is 0..1 within
/// the current <see cref="Phase"/> (or overall once installing). Reported via
/// <see cref="System.IProgress{T}"/> through the backend operation methods.
/// </summary>
public readonly record struct OpProgress (OpPhase Phase, double Fraction)
{
    /// <summary>Human-readable label for the status bar.</summary>
    public string Label
        => Phase switch
        {
            OpPhase.Queued => "Queued",
            OpPhase.Downloading => "Downloading",
            OpPhase.Installing => "Installing",
            OpPhase.Uninstalling => "Uninstalling",
            OpPhase.Repairing => "Repairing",
            OpPhase.Finalizing => "Finalizing",
            OpPhase.Done => "Done",
            _ => string.Empty
        };
}

/// <summary>
/// What would actually be installed for a package, shown in the install confirm dialog. Sourced
/// from the WinGet COM API's applicable-installer resolution (type/arch/scope/elevation). Note:
/// the COM API exposes no installer download size, so size is intentionally absent. Backends that
/// can't resolve this (CLI) return null; the mock fills representative values.
/// </summary>
public sealed class InstallerPreview
{
    /// <summary>Friendly installer type, e.g. "MSI", "EXE", "MSIX", "Store".</summary>
    public string? InstallerType { get; init; }

    /// <summary>Friendly architecture, e.g. "x64", "arm64", "x86".</summary>
    public string? Architecture { get; init; }

    /// <summary>Install scope: "machine", "user", or null when unknown.</summary>
    public string? Scope { get; init; }

    /// <summary>True when the installer requires elevation (admin).</summary>
    public bool RequiresElevation { get; init; }

    /// <summary>The version this installer resolves to, when known.</summary>
    public string? Version { get; init; }

    /// <summary>One-line summary like <c>MSI · x64 · machine · admin</c> for the confirm dialog.</summary>
    public string Summary
    {
        get
        {
            List<string> parts = [];

            if (!string.IsNullOrWhiteSpace (InstallerType))
            {
                parts.Add (InstallerType);
            }

            if (!string.IsNullOrWhiteSpace (Architecture))
            {
                parts.Add (Architecture);
            }

            if (!string.IsNullOrWhiteSpace (Scope))
            {
                parts.Add (Scope);
            }

            if (RequiresElevation)
            {
                parts.Add ("admin");
            }

            return string.Join (" · ", parts);
        }
    }
}
