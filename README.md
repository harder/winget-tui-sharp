# winget-tui-sharp

A fast, keyboard-driven terminal UI for the [Windows Package Manager (winget)](https://github.com/microsoft/winget-cli): search, install, upgrade, uninstall, and manage pins without leaving the terminal. Talks to winget through its **COM API** for structured results (falls back to the CLI, or a mock backend for dev), and ships as a single Native AOT `.exe` — no .NET runtime required.

⚠️ Install / uninstall / upgrade / pin actions invoke real `winget` operations and will operate on your real package state.

[![C#](https://img.shields.io/badge/C%23-239120?style=flat&logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![Terminal.Gui](https://img.shields.io/badge/Terminal.Gui-v2-FF6F00?style=flat&logo=windowsterminal&logoColor=white)](https://github.com/gui-cs/Terminal.Gui)
[![Windows](https://img.shields.io/badge/Windows-x64%20%7C%20arm64-0078D4?style=flat&logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow?style=flat)](LICENSE)

[![CI](https://github.com/harder/winget-tui-sharp/actions/workflows/ci.yml/badge.svg)](https://github.com/harder/winget-tui-sharp/actions/workflows/ci.yml)
[![Release](https://github.com/harder/winget-tui-sharp/actions/workflows/release.yml/badge.svg)](https://github.com/harder/winget-tui-sharp/actions/workflows/release.yml)

![winget-tui-sharp screenshot](img/winget-tui-sharp.png)

## Quick start

**Prerequisites:** Windows 10/11, [winget](https://github.com/microsoft/winget-cli) 1.4+, a terminal with Unicode support (Windows Terminal recommended).

You do **not** need .NET installed.

1. Download the latest Windows binary from the [Releases page](https://github.com/harder/winget-tui-sharp/releases/latest):
   - `winget-tui-sharp-x64.exe` for Windows on Intel/AMD x86
   - `winget-tui-sharp-arm64.exe` for Windows on ARM
2. Run it from Windows Terminal:

```powershell
.\winget-tui-sharp-x64.exe

.\winget-tui-sharp-arm64.exe
```

The released binaries are **not code-signed** yet (see [code-signing.md](code-signing.md)), so Microsoft Defender SmartScreen will warn on first run. Workaround:

```powershell
Unblock-File -Path .\winget-tui-sharp-x64.exe
```

Or right-click the exe → *Properties* → check *Unblock* → *OK*. On the first run after unblocking, click *More info → Run anyway* and SmartScreen will remember the decision.

## Origin & attribution

winget-tui-sharp began as a from-scratch C# / [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) port of [**shanselman/winget-tui**](https://github.com/shanselman/winget-tui) — Scott Hanselman's Rust + Ratatui TUI for winget — built to benchmark Terminal.Gui v2 against Ratatui on feature parity, rendering fidelity, performance, and UX. **Winget-tui** is a beautiful terminal app in its own right - go download it and try it too! [Go download winget-tui](https://github.com/shanselman/winget-tui). Winget-tui is copyright © [Scott Hanselman](https://github.com/shanselman), MIT-licensed.

UI layout, keybindings, color palette, table structure, winget output parsing, dedupe / pin-state / locale handling, and the "Found `<name>` [`<id>`]" detail-header convention all follow the [upstream source](https://github.com/shanselman/winget-tui/tree/main/src). **No upstream code was copied** - the upstream served as the behavioral and visual specification. (The app's default color theme is now **Sage** rather than the original warm-amber palette - that exact upstream-matching palette is still available as the **Amber** theme, selectable via `t` or `--theme=amber`; see [Choosing a theme at runtime](#choosing-a-theme-at-runtime).)

With the COM backend now stable under Native AOT, this port has grown from a benchmark exercise into a usable tool in its own right - the Terminal.Gui benchmarking goal continues alongside it, and differences between the two implementations, including Terminal.Gui feature gaps surfaced along the way, are tracked in [feature-gaps.md](feature-gaps.md).

This port is also MIT-licensed; see [LICENSE](LICENSE).

## What's in the box

| Area                                                                      | Status                                                                                |
| ------------------------------------------------------------------------- | ------------------------------------------------------------------------------------- |
| Three-tab UI (Search / Installed / Upgrades)                              | ✅                                                                                    |
| Pixel-art logo + tab bar header                                           | ✅ (3-row half-block art, mouse-clickable tabs)                                       |
| Package list table (Name, Id, Version, Source / Available)                | ✅                                                                                    |
| Detail panel: publisher, description, homepage, changelog, license        | ✅                                                                                    |
| Status bar: source filter, pin filter, hotkey hints, spinner              | ✅                                                                                    |
| Search mode (`/` or `s`) with deferred backend search                     | ✅                                                                                    |
| Local filter for Installed / Upgrades (auto-cleared on view switch)       | ✅                                                                                    |
| Source filter cycling (`f`)                                               | ✅                                                                                    |
| Pin filter cycling (`P`)                                                  | ✅                                                                                    |
| Sort cycling (`S`) - None → Name↑↓ → Id↑↓ → Version↑↓                     | ✅                                                                                    |
| Install / Install-version / Uninstall / Upgrade / Pin                     | ✅ (no `--exact` to match upstream behavior)                                          |
| Pin states distinguished: Pinned / Blocking / Gating(version)             | ✅                                                                                    |
| Batch-select (Space / `a`) and batch upgrade (`U`)                        | ✅                                                                                    |
| Confirm dialog, version-input dialog, help overlay                        | ✅                                                                                    |
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
| Switchable theme: Sage (default), Amber (exact upstream `theme.rs` match), Moss & Olive, Dusty Rose | ✅ (`t` in-app picker or `--theme=`)                          |
| Mock backend for non-Windows hosts                                        | ✅                                                                                    |
| Native AOT standalone exe, no .NET runtime needed                         | ✅                                                                                    |

## Building

`winget` itself is Windows-only, so the deployed target is Windows. The build uses **.NET Native AOT** to produce a single standalone `.exe` (~10–15 MB) that runs without `dotnet` installed on the target machine.

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

Copy `winget-tui-sharp.exe` anywhere, no other files required.

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
.\bin\Release\net10.0\win-x64\publish\winget-tui-sharp.exe
```

This is exactly the "no COM available" case the runtime backend selection already handles —
no flags needed, it just runs the CLI backend since COM isn't compiled in.

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
| _(default)_ | COM on Windows COM builds, CLI elsewhere | Falls back to CLI if COM activation fails (missing/unregistered COM server); falls back further to the mock backend if `winget` isn't usable either. |

The COM backend talks to the WinGet COM API directly instead of parsing CLI output. Pinning has no COM surface, so pin/unpin/list-pins transparently delegate to the CLI. COM is the default on the Windows COM build (see "Build the standalone executable" below) because it gives structured results without shelling out — CLI is the automatic fallback whenever COM can't activate, and also the *only* backend compiled into the lean `net10.0` Windows build described under "CLI-only build" for when you don't want to ship the COM projection/in-proc-server DLLs.

#### Choosing a theme at runtime

`--theme=<amber|sage|moss|rose>` picks the starting palette (default: `sage`). An unrecognized value falls back to the default with a stderr note instead of failing to launch. Switch at any time in-app with the `t` keybinding — see [Keybindings](#keybindings).

```powershell
winget-tui-sharp.exe --theme=amber
```

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

Every test is anchored to a real bug found during development or a Terminal.Gui surface
we depend on; **89 tests**, runs in <1 second.

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
| `i`                             | Install                                                                        |
| `I`                             | Install specific version                                                       |
| `u`                             | Upgrade                                                                        |
| `U`                             | Batch upgrade                                                                  |
| `x`                             | Uninstall                                                                      |
| `p`                             | Pin / unpin                                                                    |
| `Space`                         | Toggle batch select (Upgrades)                                                 |
| `a`                             | Toggle select-all (Upgrades)                                                   |
| `o`                             | Open homepage                                                                  |
| `c`                             | Open changelog                                                                 |
| `?`                             | Toggle help                                                                    |
| `t`                             | Open theme picker (Amber / Sage / Moss & Olive / Dusty Rose)                   |
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
├── Program.cs               # Entry point + winget-detection + --dump diagnostic
├── WingetTuiSharp.csproj         # Multi-targets net10.0 + net10.0-windows; Terminal.Gui; AOT-configured
├── README.md
├── LICENSE                  # MIT
├── feature-gaps.md          # Terminal.Gui parity findings vs upstream
├── code-signing.md          # Code-signing options + the recommended next step
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
    └── ParserTests.cs       # xUnit suite covering the parser pipeline
```

## Status & roadmap

Actively used as a daily winget TUI. Things known to be unfinished or different from upstream are listed in [feature-gaps.md](feature-gaps.md). Terminal.Gui is under active development and this application will be updated periodically to reflect improvements, fixes, and new features in that library. PRs that close parity gaps, fix bugs, or add new features are all welcome.

Not yet implemented:

- Configuration file support (`%APPDATA%\winget-tui\config.toml`)

## Contributing

Contributions welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Related

- **Upstream**: [shanselman/winget-tui](https://github.com/shanselman/winget-tui) (Rust + Ratatui)
- **Terminal.Gui v2**: [gui-cs/Terminal.Gui](https://github.com/gui-cs/Terminal.Gui)
- **winget**: [microsoft/winget-cli](https://github.com/microsoft/winget-cli)
