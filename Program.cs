using WingetTuiSharp;

if (ProcessRunner.TryExecPosixContainedTarget (args))
{
    return;
}

if (args.Length > 0 && args [0] is "--dump")
{
    // Diagnostic mode: invoke winget the way the backend would and print the raw output
    // verbatim, plus a hex dump of the bytes immediately around the dash-line separator.
    // Use this on Windows to verify the encoding and figure out why ParseTable is empty:
    //
    //     winget-tui-sharp.exe --dump search vscode
    //     winget-tui-sharp.exe --dump list
    //     winget-tui-sharp.exe --dump upgrade
    string [] cmd = args.Length > 1
                        ? [.. args.Skip (1), "--accept-source-agreements"]
                        : ["list", "--accept-source-agreements"];

    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine ($"Invoking: winget {string.Join (' ', cmd)}");
    Console.WriteLine ($"Console.OutputEncoding: {Console.OutputEncoding.WebName}");
    Console.WriteLine ();

    (int code, string output) = await CliBackend.RunWithCodeAsync (cmd, CancellationToken.None);
    Console.WriteLine ($"--- exit code: {code}");
    Console.WriteLine ($"--- output length: {output.Length} chars");
    Console.WriteLine ("--- output:");
    Console.WriteLine (output);
    Console.WriteLine ("--- parser trace:");

    if (cmd [0] == "show")
    {
        // Extract the id from --id <X> for the parser's name fallback.
        int idIdx = Array.IndexOf (cmd, "--id");
        string id = idIdx >= 0 && idIdx + 1 < cmd.Length ? cmd [idIdx + 1] : string.Empty;
        PackageDetail? detail = CliBackend.ParseShowTraced (id, output, Console.Out);
        Console.WriteLine ();

        if (detail is null)
        {
            Console.WriteLine ("--- parsed detail: null");
        }
        else
        {
            Console.WriteLine ("--- parsed detail:");
            Console.WriteLine ($"  Name={detail.Name}");
            Console.WriteLine ($"  Id={detail.Id}");
            Console.WriteLine ($"  Version={detail.Version}");
            Console.WriteLine ($"  Publisher={detail.Publisher}");
            Console.WriteLine ($"  Homepage={detail.Homepage}");
            Console.WriteLine ($"  License={detail.License}");
            Console.WriteLine ($"  ReleaseNotesUrl={detail.ReleaseNotesUrl}");
            Console.WriteLine ($"  Description={detail.Description}");
        }
    }
    else
    {
        IReadOnlyList<Package> rows = CliBackend.ParseTableTraced (output, hasAvailable: cmd [0] == "upgrade", Console.Out);
        Console.WriteLine ();
        Console.WriteLine ($"--- parsed rows: {rows.Count}");

        foreach (Package row in rows.Take (5))
        {
            Console.WriteLine ($"  Name='{row.Name}' Id='{row.Id}' Version='{row.Version}' Available='{row.AvailableVersion}' Source='{row.Source}'");
        }
    }

    return;
}

#if WINGET_COM
// Apartment + COM-activation probe. This is the decisive "is the COM server actually reachable?"
// diagnostic — run it as the FIRST COM activity after a clean reboot to separate a genuine
// AOT-activation bug from a wedged WinGet OOP server (both surface as 0x80073D54
// APPMODEL_ERROR_NO_PACKAGE). Prints the apartment state + catalog count for the main and a
// threadpool thread; a healthy in-proc activation reports "activation OK; catalogs = 3".
//   winget-tui-sharp.exe --comdiag
if (args.Length > 0 && args [0] is "--comdiag")
{
    Console.WriteLine ($"main thread apartment = {Thread.CurrentThread.GetApartmentState ()}");

    try
    {
        Microsoft.Management.Deployment.PackageManager pm = new ();
        Console.WriteLine ($"main-thread activation OK; catalogs = {pm.GetPackageCatalogs ().Count}");
    }
    catch (Exception ex)
    {
        Console.WriteLine ($"main-thread activation FAILED: 0x{(uint) ex.HResult:X8} {ex.Message}");
    }

    await Task.Run (() =>
                    {
                        Console.WriteLine ($"threadpool apartment = {Thread.CurrentThread.GetApartmentState ()}");

                        try
                        {
                            Microsoft.Management.Deployment.PackageManager pm = new ();
                            Console.WriteLine ($"threadpool activation OK; catalogs = {pm.GetPackageCatalogs ().Count}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine ($"threadpool activation FAILED: 0x{(uint) ex.HResult:X8} {ex.Message}");
                        }
                    });

    return;
}
#endif

// Backend selection (precedence: --mock > --cli > --com > default):
//   --mock / -m   the in-memory mock (cross-platform dev/parity)
//   --cli         the winget.exe CLI parser
//   --com         the WinGet COM API backend (Windows builds only)
//   (default)     COM on Windows builds, CLI elsewhere
// These are preferences, not hard guarantees: a requested backend that can't run degrades
// (with a stderr note) — --com on a non-Windows build → CLI, and any CLI path with no winget
// on PATH → mock. Scripts that need a guaranteed backend should check that note.
bool smokeMode = args.Any (a => a == "--smoke");
IBackend backend = SelectBackend (args);

// Theme selection: --theme=<amber|sage|moss|rose>. Defaults to Sage. An unrecognized id
// degrades gracefully (stderr note, keep the default) rather than crashing. TryApply already
// registers schemes on success, so Register() below only needs to run for the default case.
string? themeArg = args.FirstOrDefault (a => a.StartsWith ("--theme=", StringComparison.OrdinalIgnoreCase))
                       ?[8..];

if (themeArg is null || !Theme.TryApply (themeArg))
{
    if (themeArg is not null)
    {
        string validIds = string.Join (", ", Theme.Palettes.Select (p => p.Id));
        Console.Error.WriteLine ($"Unknown theme '{themeArg}' - valid values are: {validIds}. Using the default (sage).");
    }

    Theme.Register ();
}

IApplication? app = null;
App? window = null;
int exitCode = 0;

try
{
    app = Application.Create ().Init ();
    window = new (backend, smokeMode ? TimeSpan.FromSeconds (1) : null);
    app.Run (window);
}
catch (Exception ex)
{
    Console.Error.WriteLine ($"Application failed: {ex.Message}");
    exitCode = 1;
}
finally
{
    if (window is not null)
    {
        try
        {
            bool drained = await window.ShutdownAsync (TimeSpan.FromSeconds (5));

            if (!drained)
            {
                Console.Error.WriteLine ("Shutdown timed out while waiting for background work.");
                exitCode = 1;
            }

            if (window.BackgroundFailures.Count > 0 || window.DroppedBackgroundFailureCount > 0)
            {
                Console.Error.WriteLine (
                    $"Background work failed {window.BackgroundFailures.Count + window.DroppedBackgroundFailureCount} time(s)." +
                    (window.DroppedBackgroundFailureCount > 0
                         ? $" {window.DroppedBackgroundFailureCount} additional failure(s) were not retained."
                         : string.Empty));

                foreach (Exception failure in window.BackgroundFailures)
                {
                    Console.Error.WriteLine ($"- {failure.GetType ().Name}: {failure.Message}");
                }

                exitCode = 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine ($"Shutdown failed: {ex.Message}");
            exitCode = 1;
        }

        window.Dispose ();
    }

    app?.Dispose ();
}

Environment.ExitCode = exitCode;

return;

static IBackend SelectBackend (string [] args)
{
    bool wantMock = args.Any (a => a is "--mock" or "-m" or "--smoke");
    bool wantCli = args.Any (a => a is "--cli");
    bool wantCom = args.Any (a => a is "--com");

    if (wantMock)
    {
        return new MockBackend ();
    }

#if WINGET_COM
    // Precedence: an explicit --cli always wins over --com (and over the Windows COM default).
    // So COM is chosen only when --cli was NOT passed and either --com was, or we're the
    // default on Windows.
    if (!wantCli && (wantCom || OperatingSystem.IsWindows ()))
    {
        try
        {
            return new ComBackend ();
        }
        catch (Exception ex)
        {
            // COM server not registered / activation failed — degrade gracefully rather than crash.
            // The stderr note is immediately painted over by the TUI redraw (invisible in practice),
            // so also stash the reason for the in-app Help dialog ('?') to surface.
            StartupDiagnostics.ComFallbackReason = $"0x{(uint) ex.HResult:X8} — {ex.Message}";
            Console.Error.WriteLine ($"COM backend unavailable ({ex.Message}); falling back to the CLI backend.");
        }
    }
#else
    if (wantCom)
    {
        Console.Error.WriteLine ("--com is only available in the Windows build (net10.0-windows…); using the CLI backend instead.");
    }
#endif

    // CLI path (explicit --cli, or the non-Windows / COM-unavailable default).
    if (!IsWingetAvailable ())
    {
        if (!wantCli)
        {
            Console.Error.WriteLine ("winget not found on PATH — falling back to mock backend. Run with `winget` available to drive the real CLI.");
        }
        else
        {
            Console.Error.WriteLine ("winget not found on PATH — mock backend used despite --cli.");
        }

        return new MockBackend ();
    }

    return new CliBackend ();
}

static bool IsWingetAvailable ()
{
    try
    {
        (int code, _) = CliBackend.ProbeWingetAsync (TimeSpan.FromMilliseconds (1500), CancellationToken.None)
                                  .GetAwaiter ()
                                  .GetResult ();

        return code == 0;
    }
    catch
    {
        return false;
    }
}

namespace WingetTuiSharp
{
    /// <summary>
    /// Startup-time diagnostics carried into the running app. Currently just the reason a requested
    /// COM backend fell back to CLI — the stderr note is invisible (overdrawn by the TUI), so the
    /// Help dialog surfaces this so a Windows user can see *why* the badge reads "CLI".
    /// </summary>
    internal static class StartupDiagnostics
    {
        public static string? ComFallbackReason;
    }
}
