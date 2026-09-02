
namespace WingetTuiSharp;

/// <summary>
/// Mock backend used when winget is not available (e.g., running this on Linux/macOS for parity testing).
/// </summary>
public sealed class MockBackend : IBackend
{
    private static readonly PackageTemplate [] InstalledTemplates =
    [
        new ("Microsoft.VisualStudioCode", "Microsoft Visual Studio Code", "1.95.0", "winget"),
        new ("Git.Git", "Git", "2.46.0", "winget", "2.47.0"),
        new ("GitHub.cli", "GitHub CLI", "2.55.0", "winget"),
        new ("Microsoft.PowerShell", "PowerShell", "7.4.5", "winget", "7.5.0"),
        new ("9NKSQGP7F2NH", "WhatsApp Desktop", "2.2412.10.0", "msstore"),
        new ("Notepad++.Notepad++", "Notepad++", "8.6.9", "winget"),
        new ("Mozilla.Firefox", "Mozilla Firefox", "131.0.3", "winget", "132.0.1"),
        new ("Microsoft.WindowsTerminal", "Windows Terminal", "1.21.3231.0", "winget"),
        new ("Python.Python.3.12", "Python 3.12", "3.12.7", "winget"),
        new ("Docker.DockerDesktop", "Docker Desktop", "4.34.0", "winget", "4.35.1")
    ];

    private static readonly PackageTemplate [] SearchTemplates =
    [
        new ("Microsoft.VisualStudioCode", "Visual Studio Code", "1.95.0", "winget"),
        new ("Microsoft.VisualStudioCode.Insiders", "Visual Studio Code Insiders", "1.96.0", "winget"),
        new ("Anthropic.Claude", "Claude", "0.7.5", "winget"),
        new ("JetBrains.Rider", "JetBrains Rider", "2024.2.7", "winget"),
        new ("Neovim.Neovim", "Neovim", "0.10.2", "winget")
    ];

    private readonly object _pinsGate = new ();
    private readonly Dictionary<string, PinState> _pins = new (StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<Package>> SearchAsync (string query, string? source, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<Package>> (
            Materialize (
                SearchTemplates.Concat (InstalledTemplates),
                template => (string.IsNullOrEmpty (query)
                             || template.Name.Contains (query, StringComparison.OrdinalIgnoreCase)
                             || template.Id.Contains (query, StringComparison.OrdinalIgnoreCase))
                            && (string.IsNullOrEmpty (source)
                                || template.Source.Equals (source, StringComparison.OrdinalIgnoreCase)),
                ct));
    }

    public Task<IReadOnlyList<Package>> ListInstalledAsync (string? source, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Package>> (
            Materialize (
                InstalledTemplates,
                template => string.IsNullOrEmpty (source)
                            || template.Source.Equals (source, StringComparison.OrdinalIgnoreCase),
                ct));

    public Task<IReadOnlyList<Package>> ListUpgradesAsync (string? source, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Package>> (
            Materialize (
                InstalledTemplates,
                template => template.AvailableVersion is not null
                            && (string.IsNullOrEmpty (source)
                                || template.Source.Equals (source, StringComparison.OrdinalIgnoreCase)),
                ct));

    public Task<IReadOnlyList<string>> ListSourcesAsync (CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested ();

        return Task.FromResult<IReadOnlyList<string>> (["winget", "msstore"]);
    }

    public Task<PackageDetail?> ShowAsync (string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested ();
        PackageTemplate? template = InstalledTemplates.Concat (SearchTemplates)
                                                     .FirstOrDefault (x => x.Id.Equals (id, StringComparison.OrdinalIgnoreCase));

        if (template is null)
        {
            return Task.FromResult<PackageDetail?> (null);
        }

        Package p = CreatePackage (template, SnapshotPins (ct));

        PackageDetail detail = new ()
        {
            Id = p.Id,
            Name = p.Name,
            Version = p.Version,
            AvailableVersion = p.AvailableVersion,
            Source = p.Source,
            PinState = p.PinState,
            Publisher = $"{p.Name.Split (' ') [0]} Team",
            Description = $"{p.Name} is a placeholder description for the mock backend. "
                          + "When running on Windows with winget installed, real manifest data is fetched here. ",
            Homepage = $"https://example.invalid/{p.Id}",
            License = "MIT",
            ReleaseNotesUrl = $"https://example.invalid/{p.Id}/releases",
            SupportUrl = $"https://example.invalid/{p.Id}/support",
            Tags = ["mock", "cli", "utility"],
            Documentation = [new ("Getting started", $"https://example.invalid/{p.Id}/docs")],
            ProductCodes = [$"{{{p.Id}-0000}}"],
            PackageFamilyNames = p.Source == "msstore" ? [$"{p.Id}_8wekyb3d8bbwe"] : null
        };

        return Task.FromResult<PackageDetail?> (detail);
    }

    public Task<IReadOnlyList<string>> ListVersionsAsync (string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested ();
        PackageTemplate? p = SearchTemplates.Concat (InstalledTemplates)
                                            .FirstOrDefault (x => x.Id.Equals (id, StringComparison.OrdinalIgnoreCase));
        string baseV = p?.AvailableVersion ?? p?.Version ?? "1.0.0";

        // A small, distinct, newest-first list so the version picker is exercisable on any host.
        IReadOnlyList<string> versions = new [] { baseV, "1.1.0", "1.0.0", "0.9.0" }
                                         .Distinct (StringComparer.OrdinalIgnoreCase)
                                         .ToList ();

        return Task.FromResult (versions);
    }

    public Task<InstallerPreview?> GetInstallerPreviewAsync (string id, string? version, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested ();
        PackageTemplate? p = SearchTemplates.Concat (InstalledTemplates)
                                            .FirstOrDefault (x => x.Id.Equals (id, StringComparison.OrdinalIgnoreCase));

        InstallerPreview preview = new ()
        {
            InstallerType = p?.Source == "msstore" ? "Store" : "MSI",
            Architecture = "x64",
            Scope = "machine",
            RequiresElevation = true,
            Version = version ?? p?.AvailableVersion ?? p?.Version
        };

        return Task.FromResult<InstallerPreview?> (preview);
    }

    public Task<InstallVerification?> VerifyInstalledAsync (string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested ();
        // Deterministically fake a "corrupt" result for one package so the Issues path is visible.
        bool corrupt = id.Contains ("Firefox", StringComparison.OrdinalIgnoreCase);

        InstallVerification v = new ()
        {
            Outcome = corrupt ? VerifyOutcome.Issues : VerifyOutcome.Ok,
            Checks =
            [
                new ("Registry entry", true, @"HKLM\…\Uninstall"),
                new ("Install location", !corrupt, @"C:\Program Files\" + id),
                new ("Install-location file", !corrupt, corrupt ? "missing: app.exe" : "app.exe")
            ]
        };

        return Task.FromResult<InstallVerification?> (v);
    }

    public bool CanRepair => true;

    public async Task<OpResult> RepairAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested ();
        // Synthesize a repair ramp so the flow is exercisable on Linux (mirrors how the mock's
        // Verify fakes a result). No download phase — repair re-runs the local installer.
        if (progress is not null)
        {
            for (int i = 0; i <= 10; i++)
            {
                progress.Report (new (OpPhase.Repairing, i / 10.0));
                await Task.Delay (45, ct);
            }

            progress.Report (new (OpPhase.Done, 1.0));
        }

        return new ()
        {
            Operation = new () { Kind = OperationKind.Repair, PackageId = id },
            Success = true,
            Message = $"[mock] Repaired {id}"
        };
    }

    public async Task<OpResult> InstallAsync (string id, string? version, InstallSettings? settings, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested ();
        await SimulateProgressAsync (progress, downloads: true, ct);

        string note = settings is null
                          ? string.Empty
                          : $" [{settings.Scope}/{settings.Mode}/{settings.Architecture}{(string.IsNullOrWhiteSpace (settings.CustomArgs) ? string.Empty : $"/\"{settings.CustomArgs}\"")}]";

        return new ()
        {
            Operation = new () { Kind = OperationKind.Install, PackageId = id, Version = version },
            Success = true,
            Message = $"[mock] Installed {id}" + (version is null ? string.Empty : $" v{version}") + note
        };
    }

    public async Task<OpResult> DownloadAsync (string id, string? version, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested ();
        // Download-only: a download ramp with no install phase.
        if (progress is not null)
        {
            for (int i = 0; i <= 10; i++)
            {
                progress.Report (new (OpPhase.Downloading, i / 10.0));
                await Task.Delay (55, ct);
            }

            progress.Report (new (OpPhase.Done, 1.0));
        }

        return new ()
        {
            Operation = new () { Kind = OperationKind.Download, PackageId = id, Version = version },
            Success = true,
            Message = $"[mock] Downloaded {id} to (mock path)"
        };
    }

    public async Task<OpResult> UninstallAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested ();
        await SimulateProgressAsync (progress, downloads: false, ct);

        return new ()
        {
            Operation = new () { Kind = OperationKind.Uninstall, PackageId = id },
            Success = true,
            Message = $"[mock] Uninstalled {id}"
        };
    }

    public async Task<OpResult> UpgradeAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested ();
        await SimulateProgressAsync (progress, downloads: true, ct);

        return new ()
        {
            Operation = new () { Kind = OperationKind.Upgrade, PackageId = id },
            Success = true,
            Message = $"[mock] Upgraded {id}"
        };
    }

    /// <summary>
    /// Synthesize a believable progress ramp so the status-bar progress UI can be exercised on
    /// any host (the mock has no real work to do). Downloads ramp 0→1 then install ramps 0→1;
    /// uninstall skips the download phase.
    /// </summary>
    private static async Task SimulateProgressAsync (IProgress<OpProgress>? progress, bool downloads, CancellationToken ct)
    {
        if (progress is null)
        {
            return;
        }

        if (downloads)
        {
            for (int i = 0; i <= 10; i++)
            {
                progress.Report (new (OpPhase.Downloading, i / 10.0));
                await Task.Delay (55, ct);
            }
        }

        for (int i = 0; i <= 10; i++)
        {
            progress.Report (new (OpPhase.Installing, i / 10.0));
            await Task.Delay (45, ct);
        }

        progress.Report (new (OpPhase.Done, 1.0));
    }

    public Task<OpResult> PinAsync (string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested ();

        lock (_pinsGate)
        {
            _pins [id] = new (PinStateKind.Blocking);
        }

        return Task.FromResult (new OpResult
        {
            Operation = new () { Kind = OperationKind.Pin, PackageId = id },
            Success = true,
            Message = $"[mock] Pinned {id}"
        });
    }

    public Task<OpResult> UnpinAsync (string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested ();

        lock (_pinsGate)
        {
            _pins.Remove (id);
        }

        return Task.FromResult (new OpResult
        {
            Operation = new () { Kind = OperationKind.Unpin, PackageId = id },
            Success = true,
            Message = $"[mock] Unpinned {id}"
        });
    }

    public Task<IReadOnlyDictionary<string, PinState>> ListPinsAsync (CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<string, PinState>> (SnapshotPins (ct));

    public Task<string> DescribeAsync (CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested ();

        return Task.FromResult ("Mock backend");
    }

    private IReadOnlyList<Package> Materialize (
        IEnumerable<PackageTemplate> templates,
        Func<PackageTemplate, bool> predicate,
        CancellationToken ct)
    {
        IReadOnlyDictionary<string, PinState> pins = SnapshotPins (ct);
        List<Package> packages = [];

        foreach (PackageTemplate template in templates)
        {
            ct.ThrowIfCancellationRequested ();

            if (predicate (template))
            {
                packages.Add (CreatePackage (template, pins));
            }
        }

        return packages;
    }

    private Dictionary<string, PinState> SnapshotPins (CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested ();

        lock (_pinsGate)
        {
            return new (_pins, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Package CreatePackage (
        PackageTemplate template,
        IReadOnlyDictionary<string, PinState> pins) => new ()
    {
        Id = template.Id,
        Name = template.Name,
        Version = template.Version,
        Source = template.Source,
        AvailableVersion = template.AvailableVersion,
        PinState = pins.GetValueOrDefault (template.Id, PinState.Unpinned)
    };

    private sealed record PackageTemplate (
        string Id,
        string Name,
        string Version,
        string Source,
        string? AvailableVersion = null);
}
