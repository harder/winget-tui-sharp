# Scout for WinGet

> **WinGet Scout** is a terminal app for managing Windows packages: search, install, upgrade, uninstall, and manage pins without leaving the terminal. On Windows it drives the **WinGet COM API** by default for structured results (falling back to the `winget` CLI if COM can't activate), and ships as a single Native AOT `.exe` — no .NET runtime required. **Install / uninstall / upgrade / repair actions operate on your real package state** — run it on a machine you're comfortable changing.

[![C#](https://img.shields.io/badge/C%23-239120?style=flat&logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![Terminal.Gui](https://img.shields.io/badge/Terminal.Gui-v2-FF6F00?style=flat&logo=windowsterminal&logoColor=white)](https://github.com/gui-cs/Terminal.Gui)
[![Windows](https://img.shields.io/badge/Windows-x64%20%7C%20arm64-0078D4?style=flat&logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow?style=flat)](LICENSE)

[![CI](https://github.com/harder/wingetscout/actions/workflows/ci.yml/badge.svg)](https://github.com/harder/wingetscout/actions/workflows/ci.yml)
[![Release](https://github.com/harder/wingetscout/actions/workflows/release.yml/badge.svg)](https://github.com/harder/wingetscout/actions/workflows/release.yml)

## Quick start

**Prerequisites:** Windows 10/11, [winget](https://github.com/microsoft/winget-cli) 1.4+, a terminal with Unicode support (Windows Terminal recommended).

You do **not** need .NET installed.

1. Download the latest Windows binary from the [Releases page](https://github.com/harder/wingetscout/releases/latest):
   - `wingetscout-x64.exe` for Windows on Intel/AMD x86
   - `wingetscout-arm64.exe` for Windows on ARM

   These filenames start with the first Scout release. Earlier releases retain the filenames shown on their release pages.
2. Run it from Windows Terminal:

```powershell
.\wingetscout-x64.exe

.\wingetscout-arm64.exe
```

The released binaries are **not code-signed** yet (see [code-signing.md](code-signing.md)), so Microsoft Defender SmartScreen will warn on first run. Workaround:

```powershell
Unblock-File -Path .\wingetscout-x64.exe
```

Or right-click the exe → *Properties* → check *Unblock* → *OK*. On the first run after unblocking, click *More info → Run anyway* and SmartScreen will remember the decision.

## Origin & attribution

Scout for WinGet began as an independent C# and Terminal.Gui exploration inspired by [Scott Hanselman's Rust WinGet TUI](https://github.com/shanselman/winget-tui). That project is MIT-licensed; its code was not copied.

The app now has its own workflows for scheduled checks, saved package sets, and run history. Its default theme is **Sage**; **Amber** remains available via `t` or `--theme=amber`.

With the COM backend now stable under Native AOT, this port has grown from a benchmark exercise into a usable tool in its own right - the Terminal.Gui benchmarking goal continues alongside it, and differences between the two implementations, including Terminal.Gui feature gaps surfaced along the way, are tracked in [feature-gaps.md](feature-gaps.md).

This port is also MIT-licensed; see [LICENSE](LICENSE).

## What's in the box

| Area                                                                      | Status                                                                                |
| ------------------------------------------------------------------------- | ------------------------------------------------------------------------------------- |
| Three-tab UI (Search / Installed / Upgrades)                              | ✅                                                                                    |
| Compact Skill View-style shell                                             | ✅ (border title, right-aligned clickable tabs, context row, resize guard)            |
| Package list table (Name, Id, Version, Source / Available)                | ✅                                                                                    |
| Detail panel: publisher, description, homepage, changelog, license        | ✅                                                                                    |
| Richer COM-only detail: tags, product code, author, copyright, support / privacy / docs links | ✅ (populated from the COM API; absent fields omitted) |
| Status bar: source filter, pin filter, hotkey hints, spinner              | ✅                                                                                    |
| Search mode (`/` or `s`) with deferred backend search                     | ✅                                                                                    |
| Local filter for Installed / Upgrades (auto-cleared on view switch)       | ✅                                                                                    |
| Source filter cycling (`f`)                                               | ✅                                                                                    |
| Pin filter cycling (`P`)                                                  | ✅                                                                                    |
| Sort cycling (`S`) - None → Name↑↓ → Id↑↓ → Version↑↓                     | ✅                                                                                    |
| Install / Install-version / Uninstall / Upgrade / Pin                     | ✅                                                                                    |
| Verify install (`V`) — COM `CheckInstalledStatus`, per-installer            | ✅ (COM only; CLI shows a neutral "COM only" note)                                    |
| Repair install (`R`) — COM `RepairPackage`, friendly "no repair" message    | ✅ (COM only)                                                                         |
| Download-only (`d`) and advanced install (`A`: scope / mode / arch / args)  | ✅                                                                                    |
| Install preview (`i`) + real version picker (`I`)                           | ✅ (COM enumerates installer type/arch/scope and the real version list; CLI uses a free-text version prompt) |
| Live determinate progress bar + cooperative `Esc` cancel                    | ✅ (COM `IProgress` marshaling; CLI watches winget output)                            |
| Pin states distinguished: Pinned / Blocking / Gating(version)             | ✅                                                                                    |
| Batch-select (Space / `a`) and batch upgrade (`U`)                        | ✅                                                                                    |
| Search selection (`Space` / `a`), source-aware install review (`B`)        | ✅ (skips installed or unresolved entries before any changes)                         |
| Saved package sets (`g`) and recent run results (`L`)                      | ✅ (local files; each run shows success, skip, and failure counts)                    |
| Optional daily update checks (`C` in Upgrades)                             | ✅ (current-user Task Scheduler task; notifications on changes or failure)            |
| Confirm dialog, version-picker / version-input dialog, help overlay        | ✅                                                                                    |
| CSV export (`e`)                                                          | ✅                                                                                    |
| Open homepage (`o`) / changelog (`c`)                                     | ✅                                                                                    |
| Refresh (`r`) with cursor-anchor by package id                            | ✅                                                                                    |
| Vim navigation (`j`/`k`) + arrow / PgUp / PgDn / Home / End               | ✅ (detail pane scrolls when it has focus)                                            |
| Navigation while filter input has focus                                   | ✅                                                                                    |
| Truncation guard for ops on `…`-suffixed ids                              | ✅                                                                                    |
| Focus-driven border weight: Heavy when focused, Rounded when not          | ✅                                                                                    |
| Rich-text detail panel: inline span styling, accent label, info-blue URLs | ✅ (via direct drawing, plus clickable homepage/release links via tiny Markdown rows) |
| CJK / display-width column slicing                                        | ✅                                                                                    |
| Bracketed-paste support on search/version inputs                          | ✅ (via Terminal.Gui v2 paste pipeline)                                               |
| Switchable theme: Sage (default), Amber (warm palette), Moss & Olive, Dusty Rose | ✅ (`t` in-app picker or `--theme=`)                          |
| Mock backend for non-Windows hosts                                        | ✅                                                                                    |
| Native AOT standalone exe, no .NET runtime needed                         | ✅                                                                                    |

### Install plans, saved sets, and results

In Search, press `Space` to select one result or `a` to select the visible results. The selection count stays visible as you search. Press `B` to review an install plan: it checks the installed inventory and configured sources, then marks packages that cannot be installed. Only ready packages run after you confirm. Press `g` to save, load, or delete a named package set. Loading a set restores the selection; review it with `B` before installing. In Upgrades, the existing `Space` / `a` selection now gets a review plan before `U` upgrades the ready packages.

Press `L` to see the 20 most recent operation runs. A run lists each package as succeeded, skipped, or failed, with a short reason. Package sets and run history are stored under `%LOCALAPPDATA%\WinGetScout`.

### Scheduled update checks

From Upgrades, press `C` to open the check settings. Choose **Check now** for a manual inventory check, or set a daily local time and enable checks. Enabling registers a current-user Windows Task Scheduler task that runs the published executable with `--check-updates`. Keep that executable and its companion files at the same path. Checks only inspect upgrades and pins; they never install or upgrade packages. The first successful check establishes a baseline. Later checks can notify when an unpinned upgrade appears or its available version changes, or when a check fails. If notifications are enabled, the portable app registers a current-user Start Menu shortcut with an app ID so Windows can display the toast. Notifications remain subject to Windows notification settings.

The Upgrades header shows the last check time or failure. The latest result, last successful baseline, and schedule settings are stored under `%LOCALAPPDATA%\WinGetScout`. Disabling checks removes the scheduled task. Run the app from a published executable to enable a schedule; `dotnet run` supports **Check now** but cannot provide a stable executable path for Task Scheduler.

The renamed MSIX has a new package identity. Remove a previously installed package before installing the Scout MSIX.

## Building

`winget` itself is Windows-only, so the deployed target is Windows. The build uses **.NET Native AOT** to produce a standalone `.exe` (~22 MB) that runs without `dotnet` installed on the target machine. The Windows build additionally ships the **in-process WinGet COM engine** (`WindowsPackageManager.dll` ~7 MB + `Microsoft.Management.Deployment.InProc.dll`) into the `publish` folder beside the exe — this is what lets the COM backend activate under Native AOT (see [Choosing a backend](#choosing-a-backend-at-runtime)). Ship the `publish` folder together; an exe copied off on its own still runs, but falls back to the CLI backend.

### Build the standalone executable

The architecture you build for must match where the binary will run:

The project multi-targets `net10.0` (cross-platform; mock/CLI backends) and
`net10.0-windows10.0.26100.0` (the Windows deploy target, which adds the COM backend).
Windows release builds must select the Windows TFM with `-f`:

| Target Windows machine                                          | Command                                                                       |
| --------------------------------------------------------------- | ----------------------------------------------------------------------------- |
| Intel / AMD x64 (most Windows PCs)                              | `dotnet publish -c Release -f net10.0-windows10.0.26100.0 -r win-x64`   |
| ARM64 (Surface Pro X, Snapdragon Copilot+ PCs, Windows Dev Kit) | `dotnet publish -c Release -f net10.0-windows10.0.26100.0 -r win-arm64` |

```powershell
# x64 (Intel/AMD)
dotnet publish -c Release -f net10.0-windows10.0.26100.0 -r win-x64
.\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\wingetscout.exe

# arm64
dotnet publish -c Release -f net10.0-windows10.0.26100.0 -r win-arm64
.\bin\Release\net10.0-windows10.0.26100.0\win-arm64\publish\wingetscout.exe
```

**Cross-architecture compile** (`x64 → arm64` or `arm64 → x64`) works on Windows as long
as the matching VS C++ build tools component is installed. Building on Windows arm64
produces an arm64 exe that runs natively (no x64 emulation).

For the COM backend, keep `wingetscout.exe` together with the `WindowsPackageManager.dll` and `Microsoft.Management.Deployment.InProc.dll` that `publish` drops next to it; the exe alone still runs but degrades to the CLI backend.

> **Building Native AOT on an ARM64 host:** a plain `dotnet publish` fails at the ILC native-link step (`'vswhere.exe' is not recognized`) because ILC calls a bare `vswhere.exe` that isn't on PATH. Run the publish inside a VS Dev Shell for the x64 cross-target with the VS Installer dir on PATH:
>
> ```powershell
> $installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
> $root = & "$installer\vswhere.exe" -latest -products * -property installationPath
> Import-Module (Join-Path $root "Common7\Tools\Microsoft.VisualStudio.DevShell.dll")
> Enter-VsDevShell -VsInstallPath $root -SkipAutomaticLocation -DevCmdArguments "-arch=x64 -host_arch=arm64" | Out-Null
> $env:PATH = "$installer;$env:PATH"   # Enter-VsDevShell does NOT add this; ILC needs bare vswhere
> dotnet publish -c Release -f net10.0-windows10.0.26100.0 -r win-x64
> ```
>
> Only AOT `publish` (the native link) needs this; `dotnet build` / `dotnet run` do not.

### CLI-only build (no COM projection/in-proc-server DLLs)

The `net10.0-windows10.0.26100.0` TFM above is the full COM-capable build: it bundles the
WinGet COM projection (`Microsoft.WindowsPackageManager.ComInterop`) and the in-proc COM
server (`Microsoft.WindowsPackageManager.InProcCom`, ~7 MB) so `ComBackend` can activate
under Native AOT without needing an installer's out-of-process registration. If you'd
rather ship a smaller, dependency-free exe and are fine relying purely on the system's
`winget.exe` CLI, publish the cross-platform `net10.0` TFM for a Windows RID instead — it
has no COM package references at all, so it's a plain CLI/mock build:

```powershell
dotnet publish -c Release -f net10.0 -r win-x64
.\bin\Release\net10.0\win-x64\publish\wingetscout.exe
```

This is exactly the "no COM available" case the runtime backend selection already handles —
no flags needed, it just runs the CLI backend since COM isn't compiled in.

### Dev iteration on any host (including WSL / macOS / Linux)

For iterating on the code, `dotnet run` is faster than re-publishing AOT each time, and unlike the AOT publish it works on any OS - handy for hacking on the UI from WSL. Because the project multi-targets, pick the cross-platform TFM with `-f net10.0` off-Windows. There's no `winget` to invoke on non-Windows hosts, so use `--mock`:

```bash
dotnet run -f net10.0                # any host: auto-falls back to mock if winget is absent
dotnet run -f net10.0 -- --mock      # any host: force the mock backend (UI development)
```

On Windows, select the COM-capable target **and the machine's architecture** for a COM development run:

```powershell
dotnet run -f net10.0-windows10.0.26100.0 -r win-arm64 # ARM64
dotnet run -f net10.0-windows10.0.26100.0 -r win-x64   # Intel/AMD x64
dotnet run -f net10.0-windows10.0.26100.0 -r win-arm64 -- --comdiag # activation check on ARM64
```

Bare `dotnet run` cannot choose between this project's two target frameworks. A Windows-target Debug run without a runtime identifier may omit the in-process native DLLs from the executable's directory and fall back to CLI with `0x8007007E`. The header badge shows the live backend; `?` Help includes the COM fallback reason.

#### Choosing a backend at runtime

| Flag        | Backend       | Notes                                                            |
| ----------- | ------------- | ---------------------------------------------------------------- |
| `--mock` / `-m` | `MockBackend`  | In-memory fixtures; works on any OS.                             |
| `--cli`     | `CliBackend`   | Shells out to `winget.exe` and parses its table output.          |
| `--com`     | `ComBackend`   | WinGet **COM API** — structured results, no stdout parsing. Windows build only. |
| _(default)_ | COM on Windows COM builds, CLI elsewhere | Falls back to CLI if COM activation fails (missing/unregistered COM server); falls back further to the mock backend if `winget` isn't usable either. |

The COM backend talks to the WinGet COM API directly instead of parsing CLI output, which is what unlocks the COM-only features (Verify, Repair, install preview, real version list, richer detail, live progress). Pinning has no COM surface, so pin/unpin/list-pins transparently delegate to the CLI. COM is the default on the Windows COM build (see "Build the standalone executable" below) because it gives structured results without shelling out — CLI is the automatic fallback whenever COM can't activate, and also the *only* backend compiled into the lean `net10.0` Windows build described under "CLI-only build" for when you don't want to ship the COM projection/in-proc-server DLLs.

Activating COM under **Native AOT** required shipping the **in-process** WinGet server and routing activation to it with a registration-free WinRT manifest ([`app.manifest`](app.manifest)): the out-of-process App Installer server can't be activated from an AOT process (it throws `0x80073D54 APPMODEL_ERROR_NO_PACKAGE` — the manual-activation shim was dropped from `ComInterop ≥ 1.10.x`, and AOT has no CsWinRT runtime fallback to reach the registered OOP server). The in-process path needs neither the OOP server nor package identity, so it activates fine. The companion native DLLs are added by the `Microsoft.WindowsPackageManager.InProcCom` package; `--comdiag` prints a quick activation probe.

The full activation story — including the alternative of giving the app **package identity** (a signed MSIX) so it can reach the in-box out-of-process server while shipping only a 61 KB metadata file instead of the engine — is in [com-activation.md](com-activation.md). The `WingetComMode` build property selects between the two (`InProc` for the portable build, `Identity` for the MSIX).

#### Choosing a theme at runtime

`--theme=<amber|sage|moss|rose>` picks the starting palette (default: `sage`). An unrecognized value falls back to the default with a stderr note instead of failing to launch. Switch at any time in-app with the `t` keybinding — see [Keybindings](#keybindings).

```powershell
wingetscout.exe --theme=amber
```

### Run the test suite

```bash
dotnet test --project tests/WinGetScout.Tests.csproj
```

The repository uses Microsoft.Testing.Platform through `global.json`, so the test project must
be supplied with `--project` rather than as a positional argument. IDE test discovery requires
Microsoft.Testing.Platform support.

The xUnit suite under `tests/` covers:

- **Parser pipeline** - table parsing, ANSI/CR handling, display-width column slicing for
  CJK, dedupe with version-first preference, footer stop and secondary-table parsing,
  bad-id rejection, store product ids, ARP\Machine\… ids, truncated ids, digit-prefixed
  package names.
- **`winget show`** - Found-line extraction, locale-independent prefix (German `Gefunden`),
  multi-line description continuation, German keys, bracketed release-notes don't hijack
  the Found-line detector, homepage / publisher_url fallback, release-notes-url
  extraction.
- **CLI argument construction** - install/upgrade-by-id don't include `--exact`,
  upgrade-by-name does, pin add uses `--blocking`, pin remove avoids `--installed`,
  upgrade includes `--include-pinned`, list doesn't.
- **Pin state precedence** - Blocking trumps all, Gating(version), `"latest"` is Pinned
  not Gating, empty inputs degrade to None.
- **Models** - `Package.IsTruncated`, `PinState.DisplayLabel`,
  `PackageDetail.MergeContext`, `EnsureDetailHint`.
- **Version comparison** - numeric vs lexical, longer-prefix-wins, empty handling.
- **Terminal.Gui compatibility** - `Theme.Register` round-trip, every named scheme
  resolves, `Rune.GetColumns()` returns 2 for CJK and 1 for ASCII, `string.GetColumns()`
  walks grapheme clusters correctly, `TabBar` reports clicks via `TabClicked`,
  `MarkedTableSource` nested type still exists.
  These catch breakages on Terminal.Gui version upgrades.
- **App behavior** (`AppBehaviorTests.cs`) - click-to-sort header→sort-field mapping,
  truncated-id upgrade falling back to match-by-name, and the contextual empty-state
  messages (up-to-date / no pinned / no unpinned / no filter match).

Every test is anchored to a real bug found during development or a Terminal.Gui surface
we depend on; **245+ tests** run across the cross-platform and Windows CI jobs.

### Diagnose winget parser issues at runtime

The `--dump` mode invokes winget and prints the raw output plus a parser trace. Useful
when real `winget` output doesn't match what the parser expects:

```powershell
wingetscout.exe --dump search vscode
wingetscout.exe --dump list
wingetscout.exe --dump upgrade
wingetscout.exe --dump show --id Microsoft.VisualStudioCode --exact
```

## Keybindings

Keyboard controls:

| Key                             | Action                                                                         |
| ------------------------------- | ------------------------------------------------------------------------------ |
| `/` or `s`                      | Search (Search tab) / local filter                                             |
| `↑`/`k`, `↓`/`j`                | Move selection, or scroll the detail pane when it has focus                    |
| `←`/`→`                         | Switch tab                                                                     |
| `1` / `2` / `3`                 | Jump to Search / Installed / Upgrades                                          |
| `Tab` / `Shift+Tab`             | Toggle focus between list and detail                                           |
| `PgUp` / `PgDn`, `Home` / `End` | Page navigation, or page/start/end scroll in the detail pane when it has focus |
| `f`                             | Cycle source filter (All / Winget / MsStore)                                   |
| `P`                             | Cycle pin filter                                                               |
| `S`                             | Cycle sort column / direction                                                  |
| `r`                             | Refresh (preserves selection by id)                                            |
| `e`                             | Export visible list to CSV                                                     |
| `i`                             | Install (shows an installer preview on the COM backend)                        |
| `I`                             | Install specific version (real version list on COM, free-text on CLI)          |
| `A`                             | Advanced install (scope / mode / arch / custom args)                           |
| `d`                             | Download installer only (no install)                                           |
| `u`                             | Upgrade                                                                        |
| `U`                             | Batch upgrade                                                                  |
| `x`                             | Uninstall                                                                      |
| `V`                             | Verify install (COM only)                                                      |
| `R`                             | Repair install (COM only)                                                      |
| `p`                             | Pin / unpin                                                                    |
| `Space`                         | Toggle batch select (Upgrades)                                                 |
| `a`                             | Toggle select-all (Upgrades)                                                   |
| `o`                             | Open homepage                                                                  |
| `c`                             | Open changelog                                                                 |
| `?`                             | Toggle help                                                                    |
| `t`                             | Open theme picker (Amber / Sage / Moss & Olive / Dusty Rose)                   |
| `q` / `Ctrl+Q` / `Ctrl+C`       | Quit (`Ctrl+Q` also works from fields and dialogs)                             |
| `Esc`                         | Cancel an operation or leave an input; at the top level, show the quit hint    |

## Architecture

```
                    ┌──────────┐
                    │   user   │  keyboard, mouse, paste
                    └─────┬────┘
                          ▼
   ┌─────────────────────────────────────────────────────────────┐
   │                            App                              │
   │  Window title + right-aligned TabBar + context row          │
   │  ┌──────────────────────┐  ┌────────────────────────────┐  │
   │  │ PackageList          │  │ DetailPanel                │  │
   │  │ TableView + markers  │  │ Scrollable package details │  │
   │  └──────────────────────┘  └────────────────────────────┘  │
   │  StatusBar + small-terminal resize guard                    │
   │                      ┌──────────────────────────────────┐   │
   │                      │  Modals: HelpDialog, VersionInput│   │
   │                      └──────────────────────────────────┘   │
   └────────────────────────────────┬────────────────────────────┘
                                    │ reads / mutates
                                    ▼
   ┌─────────────────────────────────────────────────────────────┐
   │                          AppState                           │
   │  Mode (Search/Installed/Upgrades)                           │
   │  Filtered packages, cursor, batch selection                 │
   │  Source filter, pin filter, sort field/dir, local filter    │
   │  DetailCache, view_generation, detail_generation            │
   └────────────────────────────────┬────────────────────────────┘
                                    │ async (CancellationToken,
                                    │        generation guard)
                                    ▼
   ┌─────────────────────────────────────────────────────────────┐
   │                         IBackend                            │
   │   Search · ListInstalled · ListUpgrades · Show              │
   │   Install · Uninstall · Upgrade · Pin · Unpin · ListPins    │
   └────────┬──────────────────┬───────────────────────┬─────────┘
            ▼                  ▼                       ▼
   ┌─────────────────┐ ┌──────────────────┐ ┌──────────────────────┐
   │ ComBackend      │ │  CliBackend      │ │  MockBackend (--mock)│
   │ (--com, Win;    │ │  (--cli)         │ │  in-memory fixtures  │
   │  default on Win)│ │  ParseTable /    │ │  so the UI runs on   │
   │ WinGet COM API; │ │  ParseShow /     │ │  any host for dev    │
   │ indexed access; │ │  ParsePins /     │ └──────────────────────┘
   │ pins → CLI      │ │  dedupe          │
   └───────┬─────────┘ └────────┬─────────┘
           ▼                    ▼
   ┌─────────────────┐ ┌─────────────────────────────────────────┐
   │ WinGet COM      │ │   winget.exe  (system, Windows-only)    │
   │ server (Win)    │ └─────────────────────────────────────────┘
   └─────────────────┘
```

Three layers, top to bottom: **UI** (`App` owns the widgets from `Ui.cs` plus
`DetailPanel`), **state** (`AppState` is the single source of truth for what's filtered
and selected, with generation counters that invalidate stale async responses), and
**backend** (`IBackend` interface, three implementations selected at runtime — see
[Choosing a backend](#choosing-a-backend-at-runtime)). The `ComBackend` is compiled
only into the Windows TFM. Async results from the backend flow back through
`App.Invoke` on the UI thread, where they pass through the generation guard before
mutating `AppState` and triggering a redraw.

## Project layout

```
wingetscout/
├── Program.cs               # Entry point + winget-detection + --dump / --comdiag diagnostics
├── WinGetScout.csproj         # Multi-targets net10.0 + net10.0-windows; Terminal.Gui; ComInterop + InProcCom; AOT-configured
├── app.manifest             # Reg-free WinRT manifest routing COM activation to the in-process server (Windows build)
├── README.md
├── LICENSE                  # MIT
├── feature-gaps.md          # Terminal.Gui rendering notes
├── code-signing.md          # Code-signing options + the recommended next step
├── src/
│   ├── GlobalUsings.cs      # Centralized using directives
│   ├── Models.cs            # Package, PackageDetail, enums, OpResult
│   ├── Backend.cs           # IBackend interface
│   ├── CliBackend.cs        # Shells out to winget; parses table output
│   ├── ComBackend.cs        # WinGet COM API backend (Windows TFM only; pins → CLI)
│   ├── MockBackend.cs       # Fake packages so the UI runs anywhere
│   ├── AppState.cs          # Filters, sort, selection, generation counters
│   ├── Theme.cs             # Switchable palettes + Schemes
│   ├── DetailPanel.cs       # Scrollable package detail view with inline rich-text rendering
│   ├── Ui.cs                # TabBar, StatusBar, Dialogs (widgets)
│   └── App.cs               # Main Runnable; state coordination; nested MarkedTableSource
└── tests/
    ├── WinGetScout.Tests.csproj
    ├── ParserTests.cs       # xUnit suite covering the parser pipeline + Terminal.Gui surfaces
    └── AppBehaviorTests.cs  # Sort-field mapping, truncated-id fallback, empty-state messages
```

## Status & roadmap

This started as a POC and is now actively used as a daily winget TUI. The WinGet **COM backend is the default on Windows and activates under Native AOT** (via the in-process server described above), so the shipped AOT build runs the structured COM path rather than parsing CLI output. Known limitations are listed in [feature-gaps.md](feature-gaps.md). Terminal.Gui is under active development and this application will be updated periodically to reflect improvements, fixes, and new features in that library. PRs that close parity gaps, fix bugs, or add new features are all welcome.

Not yet implemented:

- Configuration file support

## Contributing

Contributions welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Related


- **Terminal.Gui v2**: [gui-cs/Terminal.Gui](https://github.com/gui-cs/Terminal.Gui)
- **winget**: [microsoft/winget-cli](https://github.com/microsoft/winget-cli)
