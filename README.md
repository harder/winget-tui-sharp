# winget-tui-sharp

> ⚠️ **Proof of concept** This project exists to **benchmark [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) v2 against Ratatui**: feature parity, rendering fidelity, performance, and UX. However it is fully operational: on Windows it drives the **WinGet COM API** by default (falling back to the `winget` CLI if COM can't activate), so **install / uninstall / upgrade / repair actions operate on your real package state**. Run it on a machine you're comfortable changing.

Winget-tui-sharp is a C# / [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) reimplementation of the wonderful [winget-tui](https://github.com/shanselman/winget-tui) - a Rust + Ratatui based TUI for the [Windows Package Manager (winget)](https://github.com/microsoft/winget-cli). **Winget-tui** is a beautiful terminal app - you should go download it and try it if you have a Windows machine! [Go download winget-tui](https://github.com/shanselman/winget-tui).


This application shows what is possible with a .NET terminal UI, and helps us improve the Terminal.Gui open source library. Release binaries are Native AOT and self-contained. You do NOT need the .NET runtime to use them.

[![C#](https://img.shields.io/badge/C%23-239120?style=flat&logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![Terminal.Gui](https://img.shields.io/badge/Terminal.Gui-v2-FF6F00?style=flat&logo=windowsterminal&logoColor=white)](https://github.com/gui-cs/Terminal.Gui)
[![Windows](https://img.shields.io/badge/Windows-x64%20%7C%20arm64-0078D4?style=flat&logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow?style=flat)](LICENSE)

[![CI](https://github.com/harder/winget-tui-sharp/actions/workflows/ci.yml/badge.svg)](https://github.com/harder/winget-tui-sharp/actions/workflows/ci.yml)
[![Release](https://github.com/harder/winget-tui-sharp/actions/workflows/release.yml/badge.svg)](https://github.com/harder/winget-tui-sharp/actions/workflows/release.yml)

![winget-tui-sharp screenshot](img/winget-tui-sharp.png)

## Origin & attribution

This is a from-scratch C# / Terminal.Gui port of [**shanselman/winget-tui**](https://github.com/shanselman/winget-tui): Scott Hanselman's Rust + Ratatui TUI for the Windows Package Manager. Winget-tui is copyright © [Scott Hanselman](https://github.com/shanselman), MIT-licensed.

UI layout, keybindings, color palette, table structure, winget output parsing, dedupe / pin-state / locale handling, and the "Found `<name>` [`<id>`]" detail-header convention all follow the [upstream source](https://github.com/shanselman/winget-tui/tree/main/src). **No upstream code was copied** - the upstream served as the behavioral and visual specification.

Differences between the two implementations, including Terminal.Gui feature gaps surfaced while porting, are documented in [feature-gaps.md](feature-gaps.md).

This port is also MIT-licensed; see [LICENSE](LICENSE).

## Prerequisites

- Windows 10/11
- [winget](https://github.com/microsoft/winget-cli) 1.4+ installed
- A terminal with Unicode support (Windows Terminal recommended)

## Installation

### Download a release

You do **not** need .NET to run `winget-tui-sharp`.

1. Download the latest Windows binary from the [Releases page](https://github.com/harder/winget-tui-sharp/releases/latest):
   - `winget-tui-sharp-x64.exe` for Windows on Intel/AMD x86
   - `winget-tui-sharp-arm64.exe` for Windows on ARM
2. Run the `.exe` from Windows Terminal:

```powershell
.\winget-tui-sharp-x64.exe

.\winget-tui-sharp-arm64.exe
```

### Code signing

The released binaries are **not code-signed** yet. This POC doesn't have a Azure Trusted Signing subscription set up, so users will see a Microsoft Defender SmartScreen warning on first run. See [code-signing.md](code-signing.md) for the full breakdown of options researched (Azure Trusted Signing, SignPath.io OSS sponsorship, EV cert via Azure Key Vault, GitHub Attestations) and which I'd adopt first if this graduates from POC.

**Workaround for users on the unsigned binary:**

```powershell
Unblock-File -Path .\winget-tui-sharp-x64.exe
```

Or right-click the exe → *Properties* → check *Unblock* → *OK*. On the first run after unblocking, click *More info → Run anyway* and SmartScreen will remember the decision.


## What's in the box

| Area                                                                      | Status                                                                                |
| ------------------------------------------------------------------------- | ------------------------------------------------------------------------------------- |
| Three-tab UI (Search / Installed / Upgrades)                              | ✅                                                                                    |
| Pixel-art logo + tab bar header                                           | ✅ (3-row half-block art, mouse-clickable tabs)                                       |
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
| Warm-amber theme matching upstream `theme.rs` palette                     | ✅                                                                                    |
| Mock backend for non-Windows hosts                                        | ✅                                                                                    |
| Native AOT standalone exe, no .NET runtime needed                         | ✅                                                                                    |

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
.\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\winget-tui-sharp.exe

# arm64
dotnet publish -c Release -f net10.0-windows10.0.26100.0 -r win-arm64
.\bin\Release\net10.0-windows10.0.26100.0\win-arm64\publish\winget-tui-sharp.exe
```

**Cross-architecture compile** (`x64 → arm64` or `arm64 → x64`) works on Windows as long
as the matching VS C++ build tools component is installed. Building on Windows arm64
produces an arm64 exe that runs natively (no x64 emulation).

For the COM backend, keep `winget-tui-sharp.exe` together with the `WindowsPackageManager.dll` and `Microsoft.Management.Deployment.InProc.dll` that `publish` drops next to it; the exe alone still runs but degrades to the CLI backend.

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

### Dev iteration on any host (including WSL / macOS / Linux)

For iterating on the code, `dotnet run` is faster than re-publishing AOT each time, and unlike the AOT publish it works on any OS - handy for hacking on the UI from WSL. Because the project multi-targets, pick the cross-platform TFM with `-f net10.0` off-Windows. There's no `winget` to invoke on non-Windows hosts, so use `--mock`:

```bash
dotnet run -f net10.0                # any host: auto-falls back to mock if winget is absent
dotnet run -f net10.0 -- --mock      # any host: force the mock backend (UI development)
```

#### Choosing a backend at runtime

| Flag        | Backend       | Notes                                                            |
| ----------- | ------------- | ---------------------------------------------------------------- |
| `--mock` / `-m` | `MockBackend`  | In-memory fixtures; works on any OS.                             |
| `--cli`     | `CliBackend`   | Shells out to `winget.exe` and parses its table output.          |
| `--com`     | `ComBackend`   | WinGet **COM API** — structured results, no stdout parsing. Windows build only. |
| _(default)_ | COM on Windows builds, CLI elsewhere | Either degrades to the mock backend if `winget` isn't usable. |

The COM backend talks to the WinGet COM API directly instead of parsing CLI output, which is what unlocks the COM-only features (Verify, Repair, install preview, real version list, richer detail, live progress). Pinning has no COM surface, so pin/unpin/list-pins transparently delegate to the CLI.

Activating COM under **Native AOT** required shipping the **in-process** WinGet server and routing activation to it with a registration-free WinRT manifest ([`app.manifest`](app.manifest)): the out-of-process App Installer server can't be activated from an AOT process (it throws `0x80073D54 APPMODEL_ERROR_NO_PACKAGE` — the manual-activation shim was dropped from `ComInterop ≥ 1.10.x`, and AOT has no CsWinRT runtime fallback to reach the registered OOP server). The in-process path needs neither the OOP server nor package identity, so it activates fine. The companion native DLLs are added by the `Microsoft.WindowsPackageManager.InProcCom` package; `--comdiag` prints a quick activation probe.

### Run the test suite

```bash
dotnet test tests/WingetTuiSharp.Tests.csproj
```

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
  walks grapheme clusters correctly, `Logo` instantiates with expected dimensions,
  `TabBar` reports clicks via `TabClicked`, `MarkedTableSource` nested type still exists.
  These catch breakages on Terminal.Gui version upgrades.
- **App behavior** (`AppBehaviorTests.cs`) - click-to-sort header→sort-field mapping,
  truncated-id upgrade falling back to match-by-name, and the contextual empty-state
  messages (up-to-date / no pinned / no unpinned / no filter match).

Every test is anchored to a real bug found during development or a Terminal.Gui surface
we depend on; **109 tests**, runs in <1 second.

### Diagnose winget parser issues at runtime

The `--dump` mode invokes winget and prints the raw output plus a parser trace. Useful
when real `winget` output doesn't match what the parser expects:

```powershell
winget-tui-sharp.exe --dump search vscode
winget-tui-sharp.exe --dump list
winget-tui-sharp.exe --dump upgrade
winget-tui-sharp.exe --dump show --id Microsoft.VisualStudioCode --exact
```

## Keybindings

Mirrors `src/handler.rs` in the upstream:

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
| `q` / `Esc` / `Ctrl+C`          | Quit                                                                           |

## Architecture

```
                    ┌──────────┐
                    │   user   │  keyboard, mouse, paste
                    └─────┬────┘
                          ▼
   ┌─────────────────────────────────────────────────────────────┐
   │                            App                              │
   │  ┌────┐ ┌─────────┐  ┌──────────────┐  ┌─────────────────┐  │
   │  │Logo│ │ TabBar  │  │ PackageList  │  │  DetailPanel    │  │
   │  └────┘ └─────────┘  │ (TableView + │  │  (direct-draw   │  │
   │                      │  MarkedTable │  │   span model)   │  │
   │  ┌────────────────┐  │  Source)     │  │                 │  │
   │  │   StatusBar    │  └──────────────┘  └─────────────────┘  │
   │  └────────────────┘  ┌──────────────────────────────────┐   │
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
winget-tui-sharp/
├── Program.cs               # Entry point + winget-detection + --dump / --comdiag diagnostics
├── WingetTuiSharp.csproj         # Multi-targets net10.0 + net10.0-windows; Terminal.Gui; ComInterop + InProcCom; AOT-configured
├── app.manifest             # Reg-free WinRT manifest routing COM activation to the in-process server (Windows build)
├── README.md
├── LICENSE                  # MIT
├── feature-gaps.md          # Terminal.Gui parity findings vs upstream
├── code-signing.md          # Code-signing options researched but not adopted (POC)
├── src/
│   ├── GlobalUsings.cs      # Centralized using directives
│   ├── Models.cs            # Package, PackageDetail, enums, OpResult
│   ├── Backend.cs           # IBackend interface
│   ├── CliBackend.cs        # Shells out to winget; parses table output
│   ├── ComBackend.cs        # WinGet COM API backend (Windows TFM only; pins → CLI)
│   ├── MockBackend.cs       # Fake packages so the UI runs anywhere
│   ├── AppState.cs          # Filters, sort, selection, generation counters
│   ├── Theme.cs             # Warm-amber palette + Schemes + pixel-art Logo
│   ├── DetailPanel.cs       # Scrollable package detail view with inline rich-text rendering
│   ├── Ui.cs                # TabBar, StatusBar, Dialogs (widgets)
│   └── App.cs               # Main Runnable; state coordination; nested MarkedTableSource
└── tests/
    ├── WingetTuiSharp.Tests.csproj
    ├── ParserTests.cs       # xUnit suite covering the parser pipeline + Terminal.Gui surfaces
    └── AppBehaviorTests.cs  # Sort-field mapping, truncated-id fallback, empty-state messages
```

## Status & roadmap

This is a POC. The WinGet **COM backend is the default on Windows and activates under Native AOT** (via the in-process server described above), so the shipped AOT build runs the structured COM path rather than parsing CLI output. Things known to be unfinished or different from upstream are listed in [feature-gaps.md](feature-gaps.md). Terminal.Gui is under active development and this application will be updated periodically to reflect improvements, fixes, and new features in that library. PRs that close parity gaps are welcome.

Things explicitly **out of scope**:

- Configuration file support (`%APPDATA%\winget-tui\config.toml`)

## Contributing

Contributions welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Related

- **Upstream**: [shanselman/winget-tui](https://github.com/shanselman/winget-tui) (Rust + Ratatui)
- **Terminal.Gui v2**: [gui-cs/Terminal.Gui](https://github.com/gui-cs/Terminal.Gui)
- **winget**: [microsoft/winget-cli](https://github.com/microsoft/winget-cli)
