
namespace WingetTuiSharp;

/// <summary>
/// Mock backend used when winget is not available (e.g., running this on Linux/macOS for parity testing).
/// </summary>
public sealed class MockBackend : IBackend
{
    private static readonly Package [] _installed =
    [
        new () { Id = "Microsoft.VisualStudioCode", Name = "Microsoft Visual Studio Code", Version = "1.95.0", Source = "winget" },
        new () { Id = "Git.Git", Name = "Git", Version = "2.46.0", Source = "winget", AvailableVersion = "2.47.0" },
        new () { Id = "GitHub.cli", Name = "GitHub CLI", Version = "2.55.0", Source = "winget" },
        new () { Id = "Microsoft.PowerShell", Name = "PowerShell", Version = "7.4.5", Source = "winget", AvailableVersion = "7.5.0" },
        new () { Id = "9NKSQGP7F2NH", Name = "WhatsApp Desktop", Version = "2.2412.10.0", Source = "msstore" },
        new () { Id = "Notepad++.Notepad++", Name = "Notepad++", Version = "8.6.9", Source = "winget" },
        new () { Id = "Mozilla.Firefox", Name = "Mozilla Firefox", Version = "131.0.3", Source = "winget", AvailableVersion = "132.0.1" },
        new () { Id = "Microsoft.WindowsTerminal", Name = "Windows Terminal", Version = "1.21.3231.0", Source = "winget" },
        new () { Id = "Python.Python.3.12", Name = "Python 3.12", Version = "3.12.7", Source = "winget" },
        new () { Id = "Docker.DockerDesktop", Name = "Docker Desktop", Version = "4.34.0", Source = "winget", AvailableVersion = "4.35.1" }
    ];

    private static readonly Package [] _searchResults =
    [
        new () { Id = "Microsoft.VisualStudioCode", Name = "Visual Studio Code", Version = "1.95.0", Source = "winget" },
        new () { Id = "Microsoft.VisualStudioCode.Insiders", Name = "Visual Studio Code Insiders", Version = "1.96.0", Source = "winget" },
        new () { Id = "Anthropic.Claude", Name = "Claude", Version = "0.7.5", Source = "winget" },
        new () { Id = "JetBrains.Rider", Name = "JetBrains Rider", Version = "2024.2.7", Source = "winget" },
        new () { Id = "Neovim.Neovim", Name = "Neovim", Version = "0.10.2", Source = "winget" }
    ];

    private readonly Dictionary<string, PinState> _pins = new (StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<Package>> SearchAsync (string query, string? source, CancellationToken ct)
    {
        IEnumerable<Package> q = _searchResults
                                 .Concat (_installed)
                                 .Where (p => string.IsNullOrEmpty (query)
                                              || p.Name.Contains (query, StringComparison.OrdinalIgnoreCase)
                                              || p.Id.Contains (query, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty (source))
        {
            q = q.Where (p => p.Source == source);
        }

        return Task.FromResult<IReadOnlyList<Package>> (q.ToArray ());
    }

    public Task<IReadOnlyList<Package>> ListInstalledAsync (string? source, CancellationToken ct)
    {
        Package [] q = string.IsNullOrEmpty (source)
                           ? _installed
                           : _installed.Where (p => p.Source == source).ToArray ();

        foreach (Package p in q)
        {
            if (_pins.TryGetValue (p.Id, out PinState ps))
            {
                p.PinState = ps;
            }
        }

        return Task.FromResult<IReadOnlyList<Package>> (q);
    }

    public Task<IReadOnlyList<Package>> ListUpgradesAsync (string? source, CancellationToken ct)
    {
        Package [] q = _installed.Where (p => p.AvailableVersion is not null).ToArray ();

        if (!string.IsNullOrEmpty (source))
        {
            q = q.Where (p => p.Source == source).ToArray ();
        }

        foreach (Package p in q)
        {
            if (_pins.TryGetValue (p.Id, out PinState ps))
            {
                p.PinState = ps;
            }
        }

        return Task.FromResult<IReadOnlyList<Package>> (q);
    }

    public Task<IReadOnlyList<string>> ListSourcesAsync (CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>> (["winget", "msstore"]);

    public Task<PackageDetail?> ShowAsync (string id, CancellationToken ct)
    {
        Package? p = _installed.Concat (_searchResults).FirstOrDefault (x => x.Id == id);

        if (p is null)
        {
            return Task.FromResult<PackageDetail?> (null);
        }

        PackageDetail detail = new ()
        {
            Id = p.Id,
            Name = p.Name,
            Version = p.Version,
            AvailableVersion = p.AvailableVersion,
            Source = p.Source,
            PinState = _pins.GetValueOrDefault (p.Id, PinState.Unpinned),
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
        Package? p = _searchResults.Concat (_installed)
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
        Package? p = _searchResults.Concat (_installed)
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
        _pins [id] = new (PinStateKind.Blocking);

        return Task.FromResult (new OpResult
        {
            Operation = new () { Kind = OperationKind.Pin, PackageId = id },
            Success = true,
            Message = $"[mock] Pinned {id}"
        });
    }

    public Task<OpResult> UnpinAsync (string id, CancellationToken ct)
    {
        _pins.Remove (id);

        return Task.FromResult (new OpResult
        {
            Operation = new () { Kind = OperationKind.Unpin, PackageId = id },
            Success = true,
            Message = $"[mock] Unpinned {id}"
        });
    }

    public Task<IReadOnlyDictionary<string, PinState>> ListPinsAsync (CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<string, PinState>> (_pins);

    public Task<string> DescribeAsync (CancellationToken ct) => Task.FromResult ("Mock backend");
}
