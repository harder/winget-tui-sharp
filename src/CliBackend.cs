using System.Globalization;
using System.Text.RegularExpressions;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace WingetTuiSharp;


/// <summary>
/// Shells out to the winget CLI and parses its tabular output.
/// Mirrors src/cli_backend.rs from shanselman/winget-tui.
/// </summary>
public sealed partial class CliBackend : IBackend
{
    public async Task<IReadOnlyList<Package>> SearchAsync (string query, string? source, CancellationToken ct)
    {
        string output = await RunAsync (SearchArgs (query, source), ct);

        return ParseTable (output, hasAvailable: false);
    }

    public async Task<IReadOnlyList<Package>> ListInstalledAsync (string? source, CancellationToken ct)
    {
        string output = await RunAsync (ListInstalledArgs (source), ct);

        return ParseTable (output, hasAvailable: false);
    }

    public async Task<IReadOnlyList<Package>> ListUpgradesAsync (string? source, CancellationToken ct)
    {
        string output = await RunAsync (ListUpgradesArgs (source), ct);

        return ParseTable (output, hasAvailable: true);
    }

    /// <summary>
    /// The configured source names from `winget source list`, e.g. ["winget", "msstore"].
    /// Falls back to the two predefined sources if the command fails or parses empty.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListSourcesAsync (CancellationToken ct)
    {
        try
        {
            string output = await RunAsync (["source", "list"], ct);
            IReadOnlyList<string> names = ParseSourceNames (output);

            return names.Count > 0 ? names : ["winget", "msstore"];
        }
        catch
        {
            return ["winget", "msstore"];
        }
    }

    public async Task<PackageDetail?> ShowAsync (string id, CancellationToken ct)
    {
        string output = await RunAsync (ShowArgs (id), ct);

        return ParseShow (id, output);
    }

    // The CLI has no structured version list or applicable-installer query worth scraping, so
    // these degrade: an empty version list makes the UI fall back to a free-text version prompt,
    // and a null preview means the install confirm shows no installer summary line. The COM
    // backend is the one that answers these.
    public Task<IReadOnlyList<string>> ListVersionsAsync (string id, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>> ([]);

    public Task<InstallerPreview?> GetInstallerPreviewAsync (string id, string? version, CancellationToken ct)
        => Task.FromResult<InstallerPreview?> (null);

    // No CLI equivalent to CheckInstalledStatus — null signals "verify unavailable on this backend".
    public Task<InstallVerification?> VerifyInstalledAsync (string id, CancellationToken ct)
        => Task.FromResult<InstallVerification?> (null);

    // Repair is COM-only by design (see the Repair spec). The UI gates on CanRepair, so this
    // safety-net failure is never reached through the app.
    public bool CanRepair => false;

    public Task<OpResult> RepairAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct)
        => Task.FromResult (new OpResult
        {
            Operation = new () { Kind = OperationKind.Repair, PackageId = id },
            Success = false,
            Message = "Repair is only available on the COM backend."
        });

    // progress is unused: winget.exe only emits an ANSI progress bar to stdout, which we
    // capture as a whole rather than scrape. The COM backend is the one that reports progress.
    public async Task<OpResult> InstallAsync (string id, string? version, InstallSettings? settings, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        (int code, string output) = await RunWithCodeAsync (InstallArgs (id, version, settings), ct);
        Operation op = new () { Kind = OperationKind.Install, PackageId = id, Version = version };

        return new () { Operation = op, Success = code == 0, Message = output };
    }

    public async Task<OpResult> DownloadAsync (string id, string? version, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        string dir = Path.Combine (
            Environment.GetFolderPath (Environment.SpecialFolder.UserProfile),
            "Downloads",
            "winget-tui");
        Operation op = new () { Kind = OperationKind.Download, PackageId = id, Version = version };

        try
        {
            Directory.CreateDirectory (dir);
        }
        catch (Exception ex)
        {
            return new () { Operation = op, Success = false, Message = $"Could not prepare download folder '{dir}': {ex.Message}" };
        }

        (int code, string output) = await RunWithCodeAsync (DownloadArgs (id, version, dir), ct);

        return new () { Operation = op, Success = code == 0, Message = code == 0 ? $"Downloaded to {dir}" : output };
    }

    public async Task<OpResult> UninstallAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        (int code, string output) = await RunWithCodeAsync (UninstallArgs (id), ct);
        Operation op = new () { Kind = OperationKind.Uninstall, PackageId = id };

        return new () { Operation = op, Success = code == 0, Message = output };
    }

    public async Task<OpResult> UpgradeAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct)
    {
        // Upstream tries id (non-exact) first, then falls back to name (exact). Match that.
        (int code, string output) = await RunWithCodeAsync (UpgradeByIdArgs (id), ct);

        if (code != 0)
        {
            (code, output) = await RunWithCodeAsync (UpgradeByNameArgs (id), ct);
        }

        Operation op = new () { Kind = OperationKind.Upgrade, PackageId = id };

        return new () { Operation = op, Success = code == 0, Message = output };
    }

    public async Task<OpResult> PinAsync (string id, CancellationToken ct)
    {
        (int code, string output) = await RunWithCodeAsync (PinAddArgs (id), ct);
        Operation op = new () { Kind = OperationKind.Pin, PackageId = id };

        return new () { Operation = op, Success = code == 0, Message = output };
    }

    public async Task<OpResult> UnpinAsync (string id, CancellationToken ct)
    {
        (int code, string output) = await RunWithCodeAsync (PinRemoveArgs (id), ct);
        Operation op = new () { Kind = OperationKind.Unpin, PackageId = id };

        return new () { Operation = op, Success = code == 0, Message = output };
    }

    // ──────────────────────────────────────────────────────────────────────
    // CLI argument construction — extracted as static helpers so they're
    // unit-testable without invoking the winget subprocess. Each method must
    // match upstream src/cli_backend.rs exactly; tests in tests/ParserTests.cs
    // catch any drift.
    // ──────────────────────────────────────────────────────────────────────

    internal static string [] SearchArgs (string query, string? source)
    {
        List<string> args = ["search", query, "--accept-source-agreements"];
        AppendSource (args, source);

        return [.. args];
    }

    internal static string [] ListInstalledArgs (string? source)
    {
        List<string> args = ["list", "--accept-source-agreements"];
        AppendSource (args, source);

        return [.. args];
    }

    internal static string [] ListUpgradesArgs (string? source)
    {
        List<string> args = ["upgrade", "--accept-source-agreements", "--include-pinned"];
        AppendSource (args, source);

        return [.. args];
    }

    /// <summary>Append `--source &lt;name&gt;` when a specific source is selected; nothing for "All" (null/empty).</summary>
    private static void AppendSource (List<string> args, string? source)
    {
        if (!string.IsNullOrEmpty (source))
        {
            args.Add ("--source");
            args.Add (source);
        }
    }

    internal static string [] ShowArgs (string id) =>
        ["show", "--id", id, "--exact", "--accept-source-agreements"];

    internal static string [] InstallArgs (string id, string? version, InstallSettings? settings = null)
    {
        // Match upstream's argument list: no `--exact`. Some ids need substring match
        // against the catalog (e.g. monikered store packages).
        List<string> args = ["install", "--id", id, "--accept-source-agreements", "--accept-package-agreements"];

        if (!string.IsNullOrEmpty (version))
        {
            args.Add ("--version");
            args.Add (version);
        }

        AppendInstallSettings (args, settings);

        return [.. args];
    }

    // Map the advanced-install options onto winget flags. --custom appends to the installer's
    // default args (matching the COM AdditionalInstallerArguments semantics).
    private static void AppendInstallSettings (List<string> args, InstallSettings? settings)
    {
        if (settings is null)
        {
            return;
        }

        switch (settings.Scope)
        {
            case InstallScopePref.User:
                args.Add ("--scope");
                args.Add ("user");

                break;
            case InstallScopePref.Machine:
                args.Add ("--scope");
                args.Add ("machine");

                break;
        }

        switch (settings.Mode)
        {
            case InstallModePref.Silent:
                args.Add ("--silent");

                break;
            case InstallModePref.Interactive:
                args.Add ("--interactive");

                break;
        }

        string? arch = settings.Architecture switch
        {
            InstallArchPref.X64 => "x64",
            InstallArchPref.X86 => "x86",
            InstallArchPref.Arm64 => "arm64",
            _ => null
        };

        if (arch is not null)
        {
            args.Add ("--architecture");
            args.Add (arch);
        }

        if (!string.IsNullOrWhiteSpace (settings.CustomArgs))
        {
            args.Add ("--custom");
            args.Add (settings.CustomArgs);
        }
    }

    internal static string [] DownloadArgs (string id, string? version, string directory)
    {
        List<string> args =
        [
            "download", "--id", id,
            "--accept-source-agreements", "--accept-package-agreements",
            "--download-directory", directory
        ];

        if (!string.IsNullOrEmpty (version))
        {
            args.Add ("--version");
            args.Add (version);
        }

        return [.. args];
    }

    internal static string [] UninstallArgs (string id) =>
        ["uninstall", "--id", id, "--accept-source-agreements"];

    internal static string [] UpgradeByIdArgs (string id) =>
        ["upgrade", "--id", id, "--accept-source-agreements", "--accept-package-agreements"];

    internal static string [] UpgradeByNameArgs (string id) =>
        ["upgrade", "--name", id, "--exact", "--accept-source-agreements", "--accept-package-agreements"];

    internal static string [] PinAddArgs (string id) =>
        ["pin", "add", "--id", id, "--exact", "--blocking", "--disable-interactivity"];

    internal static string [] PinRemoveArgs (string id) =>
        ["pin", "remove", "--id", id, "--exact", "--disable-interactivity"];

    internal static string [] PinListArgs () => ["pin", "list"];

    public async Task<IReadOnlyDictionary<string, PinState>> ListPinsAsync (CancellationToken ct)
    {
        string output = await RunAsync (["pin", "list"], ct);

        return ParsePins (output);
    }

    public async Task<string> DescribeAsync (CancellationToken ct)
    {
        try
        {
            (int code, string output) = await RunWithCodeAsync (["--version"], ct);
            string version = code == 0 ? output.Trim ().TrimStart ('v', 'V') : string.Empty;

            return string.IsNullOrEmpty (version) ? "CLI · winget" : $"CLI · winget {version}";
        }
        catch
        {
            return "CLI · winget";
        }
    }

    /// <summary>
    /// Parses `winget pin list` output, distinguishing Blocking / Gating(version) / Pinned
    /// states. Mirrors upstream src/cli_backend.rs::parse_pins_from_table + parse_pin_state.
    /// </summary>
    public static IReadOnlyDictionary<string, PinState> ParsePins (string output)
    {
        Dictionary<string, PinState> pins = new (StringComparer.OrdinalIgnoreCase);

        if (output.ToLowerInvariant ().Contains ("no pins configured"))
        {
            return pins;
        }

        output = StripAnsi (output);
        string [] lines = SplitLines (output);
        int sepIdx = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = StripControl (lines [i]).TrimEnd ();

            if (line.Length >= 10 && line.All (c => c == '-' || c == '─'))
            {
                sepIdx = i;

                break;
            }
        }

        if (sepIdx <= 0)
        {
            return pins;
        }

        List<(string Name, int Start)> columns = ParseHeader (StripControl (lines [sepIdx - 1]));

        if (columns.Count == 0)
        {
            return pins;
        }

        int idIdx = ColIndex (columns, "id", "id.");
        int versionIdx = ColIndex (columns, "pinned", "pinned version", "pinnedversion", "version", "versión", "versão");
        int typeIdx = ColIndex (columns, "type", "typ", "tipo");

        // Positional fallback for unrecognized locales.
        if (idIdx < 0 && columns.Count >= 4)
        {
            idIdx = 1;

            if (columns.Count >= 5)
            {
                versionIdx = versionIdx >= 0 ? versionIdx : 3;
                typeIdx = typeIdx >= 0 ? typeIdx : 4;
            }
            else
            {
                versionIdx = versionIdx >= 0 ? versionIdx : 2;
                typeIdx = typeIdx >= 0 ? typeIdx : 3;
            }
        }

        for (int i = sepIdx + 1; i < lines.Length; i++)
        {
            string raw = lines [i];

            if (string.IsNullOrWhiteSpace (raw))
            {
                continue;
            }

            string sanitized = StripControl (raw);

            if (IsFooterLine (sanitized))
            {
                break;
            }

            string id = SliceColumn (sanitized, columns, idIdx).Trim ();

            if (string.IsNullOrEmpty (id))
            {
                continue;
            }

            string pinnedVersion = SliceColumn (sanitized, columns, versionIdx).Trim ();
            string pinType = SliceColumn (sanitized, columns, typeIdx).Trim ();
            pins [id] = ParsePinState (pinType, pinnedVersion);
        }

        return pins;
    }

    /// <summary>
    /// Map winget's pin-type column text to a <see cref="PinState"/>. Mirrors upstream's
    /// parse_pin_state: prefer Blocking, then Gating(version), then Pinned, in that order.
    /// </summary>
    internal static PinState ParsePinState (string pinType, string pinnedVersion)
    {
        string kind = pinType.ToLowerInvariant ().Trim ();
        string version = pinnedVersion.Trim ();

        if (kind.Contains ("block"))
        {
            return new (PinStateKind.Blocking);
        }

        if (!string.IsNullOrEmpty (version) && !version.Equals ("latest", StringComparison.OrdinalIgnoreCase))
        {
            return new (PinStateKind.Gating, version);
        }

        if (kind.Contains ("gate"))
        {
            return string.IsNullOrEmpty (version)
                       ? new (PinStateKind.Pinned)
                       : new (PinStateKind.Gating, version);
        }

        if (kind.Contains ("pin") || !string.IsNullOrEmpty (kind))
        {
            return new (PinStateKind.Pinned);
        }

        return PinState.Unpinned;
    }

    private static async Task<string> RunAsync (IReadOnlyList<string> args, CancellationToken ct)
    {
        (int _, string output) = await RunWithCodeAsync (args, ct);

        return output;
    }

    /// <summary>
    /// Parse the Name column out of `winget source list`'s table (Name / Argument columns), reusing
    /// the same dash-separator + column-slice machinery as the package table. Falls back to the
    /// first whitespace-delimited token per row if the Name column can't be located.
    /// </summary>
    internal static IReadOnlyList<string> ParseSourceNames (string output)
    {
        output = StripAnsi (output);
        string [] lines = SplitLines (output);
        int sepIdx = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = StripControl (lines [i]).TrimEnd ();

            if (line.Length >= 4 && line.All (c => c is '-' or '─'))
            {
                sepIdx = i;

                break;
            }
        }

        if (sepIdx <= 0)
        {
            return [];
        }

        List<(string Name, int Start)> columns = ParseHeader (StripControl (lines [sepIdx - 1]));
        int nameIdx = ColIndex (columns, "name", "nom", "nombre", "nome");

        if (nameIdx < 0)
        {
            nameIdx = 0;
        }

        List<string> names = [];

        for (int i = sepIdx + 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace (lines [i]))
            {
                continue;
            }

            string sanitized = StripControl (lines [i]);
            string name = SliceColumn (sanitized, columns, nameIdx).Trim ();

            if (string.IsNullOrEmpty (name))
            {
                string [] parts = sanitized.Trim ().Split (' ', StringSplitOptions.RemoveEmptyEntries);
                name = parts.Length > 0 ? parts [0] : string.Empty;
            }

            if (!string.IsNullOrWhiteSpace (name))
            {
                names.Add (name);
            }
        }

        return names;
    }

    public static async Task<(int Code, string Output)> RunWithCodeAsync (IReadOnlyList<string> args, CancellationToken ct)
    {
        ProcessStartInfo psi = new ()
        {
            FileName = "winget",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // Don't force UTF-8. Modern winget on a UTF-8 codepage (chcp 65001) emits UTF-8;
            // on the default English Windows codepage it emits the active OEM codepage. Letting
            // .NET pick `Console.OutputEncoding` matches what the user would see if they ran
            // winget in the same shell. Override via WINGETTUI_ENCODING if it's wrong.
            StandardOutputEncoding = ResolveEncoding ()
        };

        foreach (string a in args)
        {
            psi.ArgumentList.Add (a);
        }

        using Process p = new () { StartInfo = psi };
        p.Start ();

        Task<string> stdout = p.StandardOutput.ReadToEndAsync (ct);
        Task<string> stderr = p.StandardError.ReadToEndAsync (ct);
        await p.WaitForExitAsync (ct);

        string combined = await stdout + await stderr;

        return (p.ExitCode, combined);
    }

    private static Encoding ResolveEncoding ()
    {
        string? env = Environment.GetEnvironmentVariable ("WINGETTUI_ENCODING");

        if (!string.IsNullOrEmpty (env))
        {
            try
            {
                return Encoding.GetEncoding (env);
            }
            catch
            {
                // fall through
            }
        }

        // .NET's default Console.OutputEncoding usually matches what winget emits when piped
        // on the same machine. If winget was started in a chcp 65001 shell, this is UTF-8.
        return Console.OutputEncoding;
    }

    /// <summary>
    /// Diagnostic variant of <see cref="ParseTable"/> that writes a trace of internal state to
    /// the provided writer: sepIdx, parsed columns, per-row slices for the first few rows, and
    /// which filter caused each skipped row. Used by Program.cs --dump.
    /// </summary>
    public static IReadOnlyList<Package> ParseTableTraced (string output, bool hasAvailable, TextWriter trace)
    {
        output = StripAnsi (output);
        string [] lines = SplitLines (output);
        trace.WriteLine ($"[parse] line count: {lines.Length}");

        List<Package> rows = ParseOneTable (lines, 0, hasAvailable, trace, out int afterFirstTable);

        // Handle the secondary table that `winget upgrade --include-pinned` appends after the
        // footer for packages whose pins block upgrade. Upstream src/cli_backend.rs parses it
        // too so pinned packages still surface in the Upgrades view.
        if (afterFirstTable >= 0 && afterFirstTable < lines.Length)
        {
            List<Package> pinned = ParseOneTable (lines, afterFirstTable, hasAvailable, trace, out _);

            if (pinned.Count > 0)
            {
                trace.WriteLine ($"[parse] secondary (pinned) table contributed {pinned.Count} rows");
                rows.AddRange (pinned);
            }
        }

        rows = DedupePackages (rows);
        trace.WriteLine ($"[parse] returned {rows.Count} rows (after dedupe)");

        return rows;
    }

    /// <summary>
    /// Parses one table starting at or after the given line index. Returns the rows it
    /// extracted and, via <paramref name="nextLineAfterFooter"/>, the line index immediately
    /// after the footer (or <c>-1</c> if no footer was encountered).
    /// </summary>
    private static List<Package> ParseOneTable (string [] lines, int startIdx, bool hasAvailable, TextWriter trace, out int nextLineAfterFooter)
    {
        nextLineAfterFooter = -1;
        int sepIdx = -1;

        for (int i = startIdx; i < lines.Length; i++)
        {
            string line = StripControl (lines [i]).TrimEnd ();

            if (line.Length >= 10 && line.All (c => c == '-' || c == '─'))
            {
                sepIdx = i;
                trace.WriteLine ($"[parse] dash separator at line {i} (length {line.Length})");

                break;
            }
        }

        if (sepIdx <= startIdx)
        {
            if (startIdx == 0)
            {
                trace.WriteLine ("[parse] FAIL: no dash separator found");
            }

            return [];
        }

        string headerLine = StripControl (lines [sepIdx - 1]);
        trace.WriteLine ($"[parse] header line: {Quote (headerLine)}");
        List<(string Name, int Start)> columns = ParseHeader (headerLine);
        trace.WriteLine ($"[parse] columns ({columns.Count}):");

        foreach ((string n, int s) in columns)
        {
            trace.WriteLine ($"  - {Quote (n)} @ {s}");
        }

        PackageColumnMap colMap = BuildColumnMap (columns);
        trace.WriteLine ($"[parse] indices: name={colMap.Name} id={colMap.Id} version={colMap.Version} available={colMap.Available} source={colMap.Source}");

        if (columns.Count == 0)
        {
            return [];
        }

        List<Package> rows = [];
        int sampled = 0;

        for (int i = sepIdx + 1; i < lines.Length; i++)
        {
            string raw = lines [i];

            if (string.IsNullOrWhiteSpace (raw))
            {
                continue;
            }

            string sanitized = StripControl (raw);

            // Stop at the first footer line (e.g. "61 upgrades available."). Upstream uses
            // take_while-stop, we use the same — important so we don't accidentally pick up
            // rows from the pinned-packages secondary table.
            if (IsFooterLine (sanitized))
            {
                trace.WriteLine ($"[parse] line {i}: STOP (footer) {Quote (sanitized)}");
                nextLineAfterFooter = i + 1;

                break;
            }

            string name = SliceColumn (sanitized, columns, colMap.Name);
            string id = SliceColumn (sanitized, columns, colMap.Id).Trim ();
            string version = SliceColumn (sanitized, columns, colMap.Version);
            string available = SliceColumn (sanitized, columns, colMap.Available);
            string source = SliceColumn (sanitized, columns, colMap.Source);

            if (sampled < 3)
            {
                trace.WriteLine ($"[parse] line {i} (len {sanitized.Length}): {Quote (sanitized)}");
                trace.WriteLine ($"  name={Quote (name.Trim ())} id={Quote (id)} version={Quote (version.Trim ())} src={Quote (source.Trim ())}");
                sampled++;
            }

            // Reject rows whose id is empty or doesn't look like a real package id. winget
            // sometimes emits long localized notices that land in the id column; valid ids
            // contain '.' (Microsoft.WindowsTerminal), '\' (ARP\Machine\…), or are pure
            // alphanumeric Store product ids. Mirrors upstream's parse_table_row filter.
            if (string.IsNullOrWhiteSpace (id))
            {
                continue;
            }

            if (!id.Contains ('.') && !id.Contains ('\\') && id.Contains (' '))
            {
                continue;
            }

            rows.Add (new ()
            {
                Id = id,
                Name = name.Trim (),
                Version = version.Trim (),
                AvailableVersion = hasAvailable && !string.IsNullOrWhiteSpace (available) ? available.Trim () : null,
                Source = source.Trim ()
            });
        }

        return rows;
    }

    /// <summary>
    /// "N text" prefix (digits + space). Matches the winget summary footers like
    /// "61 upgrades available." or "5 packages available." in any locale (the leading
    /// number is the locale-independent signature).
    /// </summary>
    private static bool IsFooterLine (string line)
    {
        ReadOnlySpan<char> s = line.AsSpan ().TrimStart ();
        int d = 0;

        while (d < s.Length && char.IsAsciiDigit (s [d]))
        {
            d++;
        }

        return d > 0 && d < s.Length && s [d] == ' ';
    }

    private readonly record struct PackageColumnMap (int Name, int Id, int Version, int Available, int Source);

    /// <summary>
    /// Locale-aware column-name → index map. Mirrors upstream's package_column_map: also
    /// accepts French (Nom), Spanish (Nombre, Versión, Origen), Portuguese (Nome, Versão,
    /// Fonte, Disponível), Italian (Versione, Origine, Disponibile), German (Quelle,
    /// Verfügbar). Falls back to positional indices for unrecognized locales.
    /// </summary>
    private static PackageColumnMap BuildColumnMap (List<(string Name, int Start)> cols)
    {
        int name = ColIndex (cols, "name", "nom", "nombre", "nome");
        int id = ColIndex (cols, "id", "id.");
        int version = ColIndex (cols, "version", "versión", "versão", "versione");
        int source = ColIndex (cols, "source", "quelle", "origen", "fonte", "origine");
        int available = ColIndex (cols, "available", "verfügbar", "disponible", "disponível", "disponibile");

        // Positional fallback when locale wasn't recognized.
        if (id < 0 && cols.Count >= 4)
        {
            name = name >= 0 ? name : 0;
            id = 1;
            version = version >= 0 ? version : 2;

            if (cols.Count >= 5)
            {
                available = available >= 0 ? available : 3;
                source = source >= 0 ? source : 4;
            }
            else
            {
                source = source >= 0 ? source : 3;
            }
        }

        return new (name, id, version, available, source);
    }

    /// <summary>
    /// Dedupe by (id, source_lowercase). When the same key appears twice, prefer the row
    /// with more metadata (non-empty available_version and source). Mirrors upstream's
    /// dedupe_packages + prefer_package.
    /// </summary>
    internal static List<Package> DedupePackages (List<Package> rows)
    {
        Dictionary<(string, string), int> index = new ();
        List<Package> deduped = [];

        foreach (Package pkg in rows)
        {
            (string, string) key = (pkg.Id, pkg.Source.ToLowerInvariant ());

            if (index.TryGetValue (key, out int existing))
            {
                if (Prefer (pkg, deduped [existing]))
                {
                    deduped [existing] = pkg;
                }
            }
            else
            {
                index [key] = deduped.Count;
                deduped.Add (pkg);
            }
        }

        return deduped;

        static bool Prefer (Package candidate, Package existing)
        {
            // Compare versions first: a newer version wins outright. Only when versions
            // are equal (or unparseable) does metadata richness tiebreak. Mirrors upstream
            // src/cli_backend.rs::prefer_package + compare_versions_like.
            int versionCmp = CompareVersionsLike (candidate.Version, existing.Version);

            if (versionCmp > 0)
            {
                return true;
            }

            if (versionCmp < 0)
            {
                return false;
            }

            int candidateScore = (string.IsNullOrEmpty (candidate.AvailableVersion) ? 0 : 1)
                                 + (string.IsNullOrEmpty (candidate.Source) ? 0 : 1);
            int existingScore = (string.IsNullOrEmpty (existing.AvailableVersion) ? 0 : 1)
                                + (string.IsNullOrEmpty (existing.Source) ? 0 : 1);

            return candidateScore > existingScore;
        }
    }

    /// <summary>
    /// Compares two version-like strings (e.g. "1.119.0" vs "1.121.0"). Splits on common
    /// separators (`.`, `-`, `+`), compares each segment numerically when both are integers,
    /// lexicographically otherwise. Returns &gt;0 if a is newer, &lt;0 if older, 0 if equal.
    /// Mirrors upstream's compare_versions_like in spirit.
    /// </summary>
    internal static int CompareVersionsLike (string a, string b)
    {
        if (string.IsNullOrEmpty (a) && string.IsNullOrEmpty (b))
        {
            return 0;
        }

        if (string.IsNullOrEmpty (a))
        {
            return -1;
        }

        if (string.IsNullOrEmpty (b))
        {
            return 1;
        }

        char [] seps = ['.', '-', '+'];
        string [] aParts = a.Split (seps);
        string [] bParts = b.Split (seps);
        int len = Math.Max (aParts.Length, bParts.Length);

        for (int i = 0; i < len; i++)
        {
            string ap = i < aParts.Length ? aParts [i] : "0";
            string bp = i < bParts.Length ? bParts [i] : "0";

            if (int.TryParse (ap, out int an) && int.TryParse (bp, out int bn))
            {
                if (an != bn)
                {
                    return an.CompareTo (bn);
                }
            }
            else
            {
                int cmp = string.CompareOrdinal (ap, bp);

                if (cmp != 0)
                {
                    return cmp;
                }
            }
        }

        return 0;
    }

    private static string Quote (string s) => "'" + s.Replace ("\r", "\\r").Replace ("\n", "\\n").Replace ("\t", "\\t") + "'";

    /// <summary>
    /// Parses winget's tabular output. Looks for a separator line of dashes, captures the header
    /// to determine column positions (display-width), then slices each subsequent row by those
    /// column boundaries. Stops at footer lines like "N upgrades available".
    /// </summary>
    public static IReadOnlyList<Package> ParseTable (string output, bool hasAvailable) => ParseTableTraced (output, hasAvailable, TextWriter.Null);

    private static List<(string Name, int Start)> ParseHeader (string header)
    {
        List<(string Name, int Start)> cols = [];
        int i = 0;

        while (i < header.Length)
        {
            while (i < header.Length && char.IsWhiteSpace (header [i]))
            {
                i++;
            }

            if (i >= header.Length)
            {
                break;
            }

            int start = i;
            StringBuilder name = new ();

            while (i < header.Length && !char.IsWhiteSpace (header [i]))
            {
                name.Append (header [i]);
                i++;
            }

            // Words separated by single space are part of same column ("Available", but "Name Id" are separate cols).
            // winget uses 2+ spaces between columns.
            while (i < header.Length - 1 && header [i] == ' ' && header [i + 1] != ' ')
            {
                name.Append (' ');
                i++;

                while (i < header.Length && !char.IsWhiteSpace (header [i]))
                {
                    name.Append (header [i]);
                    i++;
                }
            }

            cols.Add ((name.ToString (), start));
        }

        return cols;
    }

    /// <summary>
    /// Finds the first column whose name matches any of the given lookups, with
    /// LOOKUP-PRIORITY ordering — the earliest lookup that hits any column wins. This is
    /// important for the pin-list parser, where both "Version" and "Pinned Version" can be
    /// present in the same header: passing <c>"pinned version"</c> ahead of <c>"version"</c>
    /// in the lookup list now actually selects the pinned column, not the installed-version
    /// column. Upstream's <c>find_column_ci</c> iterates columns-first and silently picks
    /// the wrong one here (their tests don't cover the full pin-table parse).
    /// </summary>
    private static int ColIndex (List<(string Name, int Start)> cols, params string [] lookups)
    {
        foreach (string lookup in lookups)
        {
            for (int i = 0; i < cols.Count; i++)
            {
                if (cols [i].Name.Equals (lookup, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Slice a column out of a data row using DISPLAY-WIDTH positions (not char/byte
    /// indices). winget aligns columns by terminal display columns, so a name containing
    /// CJK characters (width 2 each) occupies fewer chars than display columns, and a
    /// naive char-index slice would land in the middle of the next column.
    ///
    /// Walks the row by rune, accumulating display width via <see cref="Rune.GetColumns"/>,
    /// and keeps the runes whose accumulated display position falls inside the column.
    /// Mirrors upstream src/cli_backend.rs::extract_field which uses the same approach via
    /// the unicode-width crate.
    /// </summary>
    private static string SliceColumn (string row, List<(string Name, int Start)> cols, int idx)
    {
        if (idx < 0 || idx >= cols.Count)
        {
            return string.Empty;
        }

        int colStart = cols [idx].Start;
        int colEnd = idx + 1 < cols.Count ? cols [idx + 1].Start : int.MaxValue;
        StringBuilder result = new ();
        int displayPos = 0;

        foreach (Rune r in row.EnumerateRunes ())
        {
            int w = r.GetColumns ();

            if (w < 0)
            {
                w = 0;
            }

            // Keep this rune if its display footprint overlaps the [colStart, colEnd) window.
            if (displayPos + w > colStart && displayPos < colEnd)
            {
                result.Append (r.ToString ());
            }

            displayPos += w;

            if (displayPos >= colEnd)
            {
                break;
            }
        }

        return result.ToString ();
    }

    private static string StripControl (string s)
    {
        StringBuilder sb = new (s.Length);

        foreach (char c in s)
        {
            if (c < 0x20 && c != '\t' || c == 0x7F)
            {
                continue;
            }

            sb.Append (c);
        }

        return sb.ToString ();
    }

    /// <summary>
    /// Source-generated matcher for ANSI CSI escape sequences. Generated at compile time
    /// because runtime-compiled Regex (<c>RegexOptions.Compiled</c>) emits IL dynamically,
    /// which the AOT analyzer rejects.
    /// </summary>
    [GeneratedRegex (@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiCsiRegex ();

    /// <summary>
    /// Strips ANSI CSI escape sequences (color, cursor movement, line erase) from the input.
    /// Required because modern winget may emit progress-bar repaint codes between the header
    /// and the data rows when stdout is piped from a console host that advertises VT support.
    /// </summary>
    private static string StripAnsi (string s) => AnsiCsiRegex ().Replace (s, string.Empty);

    /// <summary>
    /// Splits output into lines while honoring carriage-return overwrites.
    /// winget emits a spinner using bare '\r' (CR without LF) to redraw the current line in
    /// place — when stdout is captured, those frames accumulate within a single line of text
    /// instead of being overwritten as they would on a terminal. We replicate the terminal's
    /// final-state behavior by keeping only the content after the last '\r' in each line.
    /// </summary>
    private static string [] SplitLines (string output)
    {
        string [] lines = output.Replace ("\r\n", "\n").Split ('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            int lastCr = lines [i].LastIndexOf ('\r');

            if (lastCr >= 0)
            {
                lines [i] = lines [i] [(lastCr + 1)..];
            }
        }

        return lines;
    }

    public static PackageDetail? ParseShow (string id, string output) => ParseShowTraced (id, output, TextWriter.Null);

    /// <summary>
    /// Parses `winget show --id X --exact` output:
    ///
    /// - The name + id come from a "PREFIX <name> [<id>]" line. We don't match the
    ///   English word "Found": we look for a trailing `]` with a matching `[`, and the
    ///   line must contain no `:` (to avoid false positives from indented release-note
    ///   lines that contain bracketed references). This is locale-independent —
    ///   handles German "Gefunden Chrome [Google.Chrome]", French "Trouvé …", etc.
    /// - Top-level `Key: Value` lines are walked, normalizing localized keys via
    ///   <see cref="NormalizeKey"/>.
    /// - Continuation lines (leading whitespace) extend the previous key's value;
    ///   the Description value is typically split across several indented lines.
    /// </summary>
    public static PackageDetail? ParseShowTraced (string id, string output, TextWriter trace)
    {
        output = StripAnsi (output);
        string [] lines = SplitLines (output);
        trace.WriteLine ($"[show] line count: {lines.Length}");

        Dictionary<string, string> kv = new (StringComparer.OrdinalIgnoreCase);
        string? extractedName = null;
        string? extractedId = null;
        int sampled = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines [i];

            if (string.IsNullOrWhiteSpace (raw))
            {
                continue;
            }

            string sanitized = StripControl (raw);
            string trimmed = sanitized.Trim ();

            if (sampled < 8)
            {
                trace.WriteLine ($"[show] line {i}: {Quote (trimmed)}");
                sampled++;
            }

            // Locale-independent "PREFIX <name> [<id>]" line. Requires trailing `]` and no `:`.
            if (extractedName is null
                && trimmed.EndsWith (']')
                && !trimmed.Contains (':'))
            {
                int bracketEnd = trimmed.LastIndexOf (']');
                int bracketStart = trimmed.LastIndexOf ('[');

                if (bracketStart > 0 && bracketEnd > bracketStart)
                {
                    string beforeBracket = trimmed [..bracketStart].Trim ();
                    int firstSpace = beforeBracket.IndexOf (' ');

                    if (firstSpace > 0)
                    {
                        extractedName = beforeBracket [(firstSpace + 1)..].Trim ();
                    }

                    extractedId = trimmed [(bracketStart + 1)..bracketEnd].Trim ();
                    trace.WriteLine ($"[show] extracted name={Quote (extractedName ?? "")}, id={Quote (extractedId)}");

                    continue;
                }
            }

            // Only top-level (non-indented) lines start new key:value pairs.
            if (sanitized.Length > 0 && char.IsWhiteSpace (sanitized [0]))
            {
                continue;
            }

            int colon = trimmed.IndexOf (':');

            if (colon <= 0 || colon >= 48)
            {
                continue;
            }

            string key = NormalizeKey (trimmed [..colon].Trim ());

            if (string.IsNullOrEmpty (key))
            {
                continue;
            }

            string value = trimmed [(colon + 1)..].Trim ();

            // For description, peek ahead and consume indented continuation lines.
            if (key == "description")
            {
                StringBuilder desc = new (value);

                while (i + 1 < lines.Length && lines [i + 1].StartsWith ("  ", StringComparison.Ordinal))
                {
                    i++;
                    string continuation = StripControl (lines [i]).Trim ();

                    if (desc.Length > 0)
                    {
                        desc.Append (' ');
                    }

                    desc.Append (continuation);
                }

                kv [key] = desc.ToString ();
            }
            else
            {
                kv [key] = value;
            }
        }

        trace.WriteLine ($"[show] extracted {kv.Count} key/value pairs");

        foreach (KeyValuePair<string, string> entry in kv.Take (10))
        {
            trace.WriteLine ($"  {entry.Key} = {Quote (entry.Value)}");
        }

        if (extractedName is null && kv.Count == 0)
        {
            return null;
        }

        // For Homepage: empty-string value should NOT block the publisher_url fallback.
        // GetValueOrDefault returns null only for missing keys, so a present-but-empty
        // value would otherwise win over a non-empty publisher_url.
        string? homepage = kv.GetValueOrDefault ("homepage");

        if (string.IsNullOrEmpty (homepage))
        {
            homepage = kv.GetValueOrDefault ("publisher_url");
        }

        return new ()
        {
            Id = extractedId ?? id,
            Name = extractedName ?? id,
            Version = kv.GetValueOrDefault ("version", string.Empty),
            Source = kv.GetValueOrDefault ("source", string.Empty),
            Publisher = kv.GetValueOrDefault ("publisher"),
            Description = kv.GetValueOrDefault ("description"),
            Homepage = string.IsNullOrEmpty (homepage) ? null : homepage,
            License = kv.GetValueOrDefault ("license"),
            ReleaseNotesUrl = kv.GetValueOrDefault ("release_notes_url")
        };
    }

    /// <summary>
    /// Normalizes a `winget show` key to its canonical English snake_case form.
    /// Mirrors the locale table in upstream's <c>normalize_show_key</c>: handles English,
    /// German, French, Italian, Spanish, Portuguese variants for the keys we care about.
    /// Returns an empty string for unknown keys so the caller skips them.
    /// </summary>
    private static string NormalizeKey (string key)
    {
        string lower = key.ToLower (CultureInfo.InvariantCulture);

        return lower switch
        {
            "version" or "packageversion" => "version",
            "publisher" or "herausgeber" or "éditeur" or "editore" or "editor" => "publisher",
            "description" or "beschreibung" or "descripción" or "descrição" or "descrizione" => "description",
            "homepage" or "startseite" => "homepage",
            "publisher url" or "herausgeber-url" => "publisher_url",
            "release notes url" or "versionshinweise url" => "release_notes_url",
            "license" or "lizenz" or "licence" or "licencia" or "licença" or "licenza" => "license",
            "source" or "quelle" or "origen" or "fonte" or "origine" => "source",
            _ => string.Empty
        };
    }
}
