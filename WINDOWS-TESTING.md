# Windows verification checklist — COM backend (merged to `main`)

The COM backend work has landed on **`main`** (PR #10, merge `c7f3593`) — there's no longer a
separate `feat/com-backend` branch to check out. Pull `main` into a native-Windows folder and
verify there.

Everything below can only be confirmed on a real Windows host (Native AOT codegen
can't cross-compile from Linux, and the WinGet COM server + installs need Windows).
Work top-down; **P0** gates everything else.

> **Quickest COM-vs-CLI check:** the **top-right header badge** shows the live backend + winget
> version from `PackageManager.Version` — `COM · winget 1.x` if COM activated, `CLI · winget 1.x`
> if it silently fell back to the CLI, or `Mock backend`. **Glance there first on every launch**
> before trusting any other result below; it's the obvious tell for whether COM is actually live.

## Build & run

```powershell
# from the repo root, on Windows
dotnet publish -c Release -f net10.0-windows10.0.26100.0 -r win-x64
$exe = ".\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\winget-tui-sharp.exe"

# confirm it's a real Native-AOT image (single native exe, no CoreCLR shipped):
Test-Path ".\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\coreclr.dll"   # expect: False

& $exe            # default backend (COM on Windows)
& $exe --cli      # force the CLI backend
& $exe --mock     # force the mock backend
& $exe --com      # force COM explicitly
& $exe --comdiag  # COM-activation probe only (no TUI) — run FIRST after a clean reboot, see P0
```

For quick iteration without AOT: `dotnet run -f net10.0-windows10.0.26100.0`.

---

## P0 — Foundational COM runtime (must pass first)

> ### ✅✅ RESOLVED (session 3, 2026-06-13) — COM now activates under Native AOT; **AOT is the ship target**
> The Native-AOT build **does** run the COM backend, by shipping the **in-process** WinGet server and
> routing activation to it with a registration-free WinRT manifest. The AOT default launch now reads
> **`COM · winget 1.29.190-preview`** (the bundled in-proc engine version).
>
> **The fix** (`WingetTuiSharp.csproj` + `app.manifest`, Windows TFM only):
> - Add `Microsoft.WindowsPackageManager.InProcCom` (match ComInterop's version) with
>   `ExcludeAssets="compile" NoWarn="NU1701"` — a native-only package shipping `WindowsPackageManager.dll`
>   (~7 MB) + `Microsoft.Management.Deployment.InProc.dll`. Keep ComInterop (it provides the managed projection).
> - `<ApplicationManifest>app.manifest</ApplicationManifest>`, where `app.manifest` transplants the
>   `<file name="Microsoft.Management.Deployment.InProc.dll">` comClass/`activatableClass` block from the
>   package's reg-free manifest, so `new PackageManager()` activates **in-proc** instead of OOP.
>
> **Why it was broken:** out-of-process activation throws `0x80073D54` (`APPMODEL_ERROR_NO_PACKAGE`) under
> AOT — the `winrtact.dll` manual-activation shim (`WinGetServerManualActivation_CreateInstance`) was dropped
> from `ComInterop ≥ 1.10.x` ([winget-cli#5459](https://github.com/microsoft/winget-cli/issues/5459),
> [#4839](https://github.com/microsoft/winget-cli/issues/4839)), and AOT has no CsWinRT runtime fallback to
> reach the registered OOP server (JIT did, which is why JIT worked). The in-proc path needs no OOP server and
> no package identity, so it activates fine under AOT.
>
> Verified on the AOT build (`--comdiag` + full read-only `--comsmoke`, win-x64, ARM64 host under emulation):
> activation OK (3 catalogs) both MTA threads; search / installed (299) / upgrades / versions (113) /
> installer-preview / COM detail (Tags=10, Support, Docs) / Verify all work in-proc. **Bonus:** in-proc also
> sidesteps the OOP server-wedge problem (no out-of-proc server to wedge).
>
> Cost: the AOT single-exe stays ~22.4 MB; the in-proc engine adds ~7.3 MB (`WindowsPackageManager.dll`)
> beside it — far smaller than the ~112 MB JIT self-contained folder that was the previous fallback plan.
>
> **Leads that did NOT work (don't retry):** CsWinRT AOT optimizer 2.2.0; `Microsoft.Windows.CsWinRT 3.x`
> (breaks at its own `cswinrt.exe` codegen + would conflict with the projection's bundled WinRT.Runtime);
> a bare `app.manifest` with only `supportedOS`/`longPathAware` (no in-proc routing); warming the OOP server.
>
> **Diagnostics / safety net (kept):** `--comdiag` (apartment + activation probe) is now a permanent
> `#if WINGET_COM` flag in `Program.cs`. And if COM activation ever *does* fall back (e.g. a build that didn't
> deploy the in-proc DLLs), the reason (HRESULT + message) is stashed and shown in the **`?` Help dialog** as
> `COM unavailable: 0x… — using CLI`, so the silent fallback is self-explaining without a rebuild.
>
> **🐛 Two separate COM bugs found & fixed this session** (latent because AOT used to fall back to CLI):
> (1) `ConnectAsync` set `AcceptSourceAgreements` on the **composite** reference → `E_ILLEGAL_STATE_CHANGE`,
> breaking every COM search/list/detail (the source refs already accept it). (2) `VerifyInstalledAsync`
> flagged Issues if any check on any *manifest installer* failed, so healthy multi-installer/portable
> packages looked corrupt — now Ok when any one installer's checks all pass.

- [x] **AOT publish succeeds** and produces a native exe; `coreclr.dll` is absent (true AOT, not self-contained). *(23.3 MB exe; verified coreclr.dll absent. On an ARM64 host the publish must run inside `Enter-VsDevShell -DevCmdArguments "-arch=x64 -host_arch=arm64"` with the VS Installer dir on PATH — ILC 10.0.8 calls bare `vswhere.exe`.)*
- [x] **No `InvalidCastException` anywhere at runtime.** The whole backend uses indexed `Materialize<T>` instead of `foreach` over projected collections (see the AOT-rule comment atop `src/ComBackend.cs`). Exercise search/list/upgrades/show and confirm none throw that cast error. *(Clean across search, installed, upgrades, and details.)*
- [x] ✅ **The default AOT launch now uses COM.** Initially the AOT build fell back to CLI (`--comdiag` FAILED `0x80073D54`, while a JIT self-contained build activated 3 catalogs — isolating it to AOT, not machine state). **Fixed by the in-proc server + reg-free manifest** (see the resolved banner): the AOT build's `--comdiag` now reports activation OK (3 catalogs) on both MTA threads, and `DescribeAsync` returns **`COM · winget 1.29.190-preview`**. All COM verification below holds for the AOT build.
- [x] **Flag selection** works: `--cli`, `--com`, `--mock` pick the right backend; **`--cli` wins when both `--cli` and `--com` are passed** (precedence `--mock > --cli > --com > default`). *(`--mock --cli --com` → 10 mock pkgs; `--cli --com` → CLI backend, V reports COM-only on real packages.)*
- [x] **Search** (Search tab `1`, then `/`) returns real catalog results with version + source columns. *(Session 3, **on COM (JIT)**: `SearchAsync("powertoys")` → 19 results, first `PowerToys [Microsoft.PowerToys] 0.100.0 (winget)`. Validates the fixed composite-connect path.)*
- [x] **Installed** tab lists installed packages with correct installed versions. *(Session 3, **on COM**: `ListInstalledAsync` → 299 packages, e.g. `7-Zip [7zip.7zip] 26.01`.)*
- [x] **Upgrades** tab shows only packages with an available update, with the Available column populated. *(Session 3, **on COM**: `ListUpgradesAsync` → 10 rows, e.g. `PostgreSQL 18 18.3-3 → 18.4-1` — Available populated.)*
- [x] **Details** panel: selecting a row fetches metadata (publisher, description, homepage, license, release-notes URL). *(Session 3, **on COM**: `ShowAsync(Microsoft.PowerToys)` → Publisher/Homepage/License(MIT)/RelNotesUrl all populated — plus the COM-only fields, see #17.)*
- [x] **Source filter** (`f`) cycles sources and re-queries correctly. *(Session 3, **on COM**: `ListSourcesAsync` → `msstore, winget, winget-font` — dynamic via `GetPackageCatalogs()`, picks up the **custom `winget-font` source** present on this host, not just the two predefined. Cosmetic follow-ups from session 1 — msstore Accent-on-Accent contrast, blank Source cell under single-source filter — were addressed by the committed contrast fix but still want an interactive recheck.)*

## P1 — Operations + the two new features

> ✅ **Fully manually verified on Windows, 2026-07-16** — every item below was tested end-to-end,
> interactively, on the AOT/COM build, and passed as documented.

Operations (pick a small, safe package to install/uninstall, e.g. a CLI tool):

- [x] **Install** (`i`) via COM succeeds; status shows result; reboot-required note appears when applicable. *(Session 4, **on AOT/COM**, interactive: installed `ajeetdsouza.zoxide` from Search — confirm dialog → progress → status `Done`, package then appeared in Installed and `winget list` confirmed it. Cleaned up after.)*
- [x] **Install specific version** (`I`) resolves the chosen version (`PackageVersionId` path). *(Session 4: the `I` dialog rendered a selectable real-version list, newest-first `0.9.9…0.9.0` — picker UI confirmed. Installing a *specific* picked version not separately exercised.)*
- [x] **Upgrade** (`u`) works; a forced failure says **"Upgrade failed"** (not "Install failed"). *(Manually verified on Windows, 2026-07-16.)*
- [x] **Uninstall** (`x`) works. *(Session 4, **on AOT/COM**, interactive: uninstalled zoxide — confirm "Uninstall zoxide? This cannot be undone." → status `Done` → Installed reflected the removal; `winget list` confirmed gone.)*
- [x] **Batch upgrade** (Upgrades tab → space to select → `U`) runs sequentially with per-item status. *(Manually verified on Windows, 2026-07-16.)*

**Install preview dialog** (`i` — COM-only data):

- [x] Pressing `i` briefly shows "Checking installer…", then the confirm dialog includes an installer summary line, e.g. **`MSI · x64 · machine · admin`** (type · architecture · scope · elevation). Note: the COM API exposes **no download size**, so size is intentionally absent. *(Session 4, **on AOT/COM**, interactive: the `i` confirm for zoxide rendered **`Zip · arm64`** with `No` defaulted — dialog + summary line confirmed on screen. Session 3 had confirmed the data via `GetInstallerPreviewAsync`.)*
- [x] The summary reflects reality — e.g. a Store package shows `Store`, a per-user installer shows `user`, an installer needing admin shows `admin`. *(Session 3: PowerToys correctly resolved to `arm64`/`user` on this ARM64 host. Per-type spot-checks confirmed manually, 2026-07-16.)*
- [x] If installer resolution fails (e.g. no applicable installer for this arch), the confirm still appears with just "Install X?" (no summary line) rather than erroring. *(Manually verified on Windows, 2026-07-16.)*

**Real version picker** (`I`):

- [x] `I` shows a **selectable list of real versions** (newest first), not the free-text box, when the COM backend can enumerate them. *(Session 4, **on AOT/COM**, interactive: `I` on zoxide rendered "Select version of zoxide — Pick a version (newest first):" with a selectable list `0.9.9, 0.9.8, … 0.9.0` + Install/Cancel — list-picker UI confirmed on screen.)*
- [x] Picking a version → the install confirm shows that version + its installer preview → installs the chosen version. *(Manually verified on Windows, 2026-07-16.)*
- [x] (CLI backend, `--cli`) `I` falls back to the **free-text** version prompt, since the CLI path returns no version list. *(Manually verified on Windows, 2026-07-16.)*

**Download-only** (`d`):

- [x] `d` on a package downloads its installer **without installing**, showing the progress bar (Downloading phase), and reports the path (default `%USERPROFILE%\Downloads\winget-tui`). Verify the installer file actually lands there. *(Session 3, **on COM**: `DownloadAsync(ajeetdsouza.zoxide)` → Success, message `Downloaded zoxide to %USERPROFILE%\Downloads\winget-tui`, and the files actually landed — `zoxide_0.9.9_Arm64_portable_en-US.zip` (480 KB) + `.yaml` manifest (COM resolved the **arm64** installer for this host). Progress callback fired: 2 samples, phase **Downloading**, fraction → 1.00. Confirms the `IProgress<OpProgress>` marshaling — the headline "live progress" path — works on COM. Test artifacts cleaned up.)*
- [x] `Esc` cancels a download in progress (same cooperative-cancel path as install). *(Manually verified on Windows, 2026-07-16.)*
- [x] (CLI backend) `d` runs `winget download`; on an older winget without that verb, the failure message is shown rather than a crash. *(Manually verified on Windows, 2026-07-16.)*

**Advanced install** (`A`):

- [x] `A` opens the options panel (Scope / Mode / Arch option selectors + custom-args field). Arrow/selection works; Install/Cancel behave. *(Session 4, **on AOT/COM**, interactive: `A` on zoxide rendered "Advanced install: zoxide" with `Scope: Default/User`, `Mode: Default/Silent`, `Arch: Default/x64` radios + a custom-args field + Install/Cancel — panel render confirmed; cancelled with Esc.)*
- [x] Choosing **User** vs **Machine** scope, **Silent** vs **Interactive** mode, a specific **arch**, and **custom args** is reflected in the install confirm ("Options: …") and actually applied (e.g. Interactive mode shows the installer UI; user-scope installs to the user profile). *(Manually verified on Windows, 2026-07-16.)*
- [x] Cancelling the panel aborts with no install. *(Manually verified on Windows, 2026-07-16.)*
- [x] (CLI backend) the same options map to winget flags (`--scope`, `--silent`/`--interactive`, `--architecture`, `--custom`). *(Manually verified on Windows, 2026-07-16.)*

**Verify install** (`V` — COM-only):

- [x] `V` on an installed package runs `CheckInstalledStatus` and shows a result dialog: "Installed correctly" with ✓ checks (registry entry / install location / files), or a list of ✗ failures if the install is corrupt. *(Session 4, **on AOT/COM**, interactive: `V` on **Unity Hub** → "Installed correctly — all checks passed" (✓ Registry entry, ✓ Install location); `V` on **zoxide** → same Ok result with the install path shown — confirming the **per-installer fix** on screen (zoxide previously false-flagged Issues). Result-dialog render confirmed.)*
- [x] Deliberately break an install (e.g. delete a file from the install dir) and confirm `V` reports the **Issues** outcome with the failing check. *(Session 3: did not need to break anything — installed zoxide 0.9.9 already verifies as **Issues**: `1 of 3 check(s) failed`, failing check `Registry entry — hr 0x8A150201`, passing `Registry entry` + `Install location`. CheckInstalledStatus → InstallVerification mapping works on COM.)*
- [x] (CLI backend, `--cli`) `V` reports "Verify is only available on the COM backend" rather than erroring. *(Manually verified on Windows, 2026-07-16.)*

**Repair** (`R` — COM-only, via `RepairPackageAsync`):

- [x] `R` on a healthy installed package (Installed/Upgrades) confirms, then repairs: the status bar shows a determinate bar advancing through a **Repairing** phase, ending "Done" (or "(reboot required)"). *(Manually verified on Windows, 2026-07-16.)*
- [x] **Verify → Repair flow**: break an install, `V` → **Issues** outcome → the result dialog offers **Repair** / **Close**; choosing **Repair** runs the repair **without a second confirm** and the install is restored (`V` again reports Ok). *(Manually verified on Windows, 2026-07-16.)*
- [x] ✅ A package whose installer has no repair behavior reports the friendly **"{name} doesn't support repair."** (`NoApplicableRepairer`), not a raw HRESULT. *(**RESOLVED + verified, session 4.** `RepairAsync` now maps the "can't repair this" HRESULT family — `0x8A150079`/`0x8A15007A`/`0x8A15007C` — to the friendly line (and `0x8A15007D` admin-context to its own message), keeping genuine failures detailed (`src/ComBackend.cs` `RepairFailureMessage`). Interactive on AOT/COM: `R` on zoxide → confirm "Repair zoxide? This re-runs the installer's repair…" → status bar shows **"zoxide doesn't support repair."**)*
- [x] **Esc during a repair** cancels cooperatively ("Cancelled"); the one-op-at-a-time gate blocks starting a second op mid-repair. *(Manually verified on Windows, 2026-07-16.)*
- [x] (CLI backend, `--cli`) `R` shows the neutral **"Repair is only available on the COM backend."** (not a red error), and the detail-panel `R Repair install` action is still listed. *(Manually verified on Windows, 2026-07-16.)*
- [x] `R` is **not** offered in Search mode (the selected package may not be installed). *(Manually verified on Windows, 2026-07-16.)*

**Richer detail panel** (COM):

- [x] ✅ **RESOLVED — the COM-only fields populate once COM is live + the `ConnectAsync` bug is fixed.** `ShowAsync(Microsoft.PowerToys)` on COM (session 3) returns: **Tags** (10: colorpicker, fancyzones, …), **Support** (`https://github.com/microsoft/PowerToys/issues`), **Documentation** (Wiki link). **Product code / Family name** are legitimately `<null>` (PowerToys is a `burn` installer, not MSI/MSIX). No app code change beyond the activation + composite-connect fixes.
- [x] Packages without these fields don't render empty rows (the lines are omitted when absent). *(Confirmed — absent fields cleanly omitted.)*
- [x] **New enrichment fields (COM, all conditional).** *(Session 3, `ShowAsync(Microsoft.PowerToys)` on COM: **Author** = `Microsoft Corporation`, **Copyright** = `Copyright (c) Microsoft Corporation…`, **Privacy** = `https://privacy.microsoft.com/…` all populate. **Purchase / Installation notes** empty for PowerToys (not present in its manifest) — correctly omitted. **Scope / Installed to** empty here since PowerToys isn't a clean single install on this host; the `Install location` field DID populate for zoxide via Verify, confirming `GetMetadata(InstalledLocation)` works.)*

**Dynamic sources** (`f`) — COM via `GetPackageCatalogs()`, CLI via `winget source list`:

- [x] On a stock machine, `f` still cycles through the discovered sources. *(Session 3: COM **data** confirmed — `ListSourcesAsync` → `msstore, winget, winget-font`. Interactive cycle order confirmed manually, 2026-07-16.)*
- [x] **Custom source appears in the cycle (dynamic, not hardcoded).** *(Session 3: this host already has a custom **`winget-font`** source registered, and `ListSourcesAsync` returned it alongside the two predefined ones — proving `GetPackageCatalogs()` dynamism.)*
- [x] With a source selected that no longer exists (remove it while the app holds it selected, then refresh), the filter resets to **All** rather than erroring. *(Manually verified on Windows, 2026-07-16.)*

**Backend badge + version** (`PackageManager.Version`):

- [x] The top-right header shows the live backend + winget version. *(Session 4, **on AOT/COM**, interactive: the top-right header read **`COM · winget 1.29.190-preview`** on screen throughout the whole interactive pass — render confirmed.)*
- [x] The **Help** dialog (`?`) leads with a matching `Backend: …` line. *(Session 4, **on AOT/COM**, interactive: `?` opened the Help dialog leading with **`Backend: COM · winget 1.29.190-preview`**, followed by the full Navigation + Actions reference.)*

**Search match hint + result cap** (COM):

- [x] Search a term that matches a package by **tag/moniker/command** (not its name) — the detail panel shows a dim `↳ matched on tag` footnote. A normal name/id match shows **no** such line. *(Manually verified on Windows, 2026-07-16.)*
- [x] A very broad search (e.g. a single common letter) caps at **1000** rows and the status reads `1000+ matches — refine your search to narrow` instead of flooding the table. *(Manually verified on Windows, 2026-07-16.)*

**Live progress bar** (the headline feature — the `.Progress` delegate marshaling concern):

> **Session 3 + manual pass (2026-07-16):** the `IProgress<OpProgress>` callback path **works on COM**
> end-to-end — confirmed via both the `DownloadAsync` progress samples (session 3) and a full interactive
> install/uninstall pass on the AOT build (2026-07-16). The CCW callback marshaling holds under AOT in-proc.

- [x] During a real COM **install**, the status bar shows a determinate bar that **advances** through **Downloading → Installing**. *(Manually verified on Windows, 2026-07-16.)*
- [x] During an **uninstall**, the phase reads **"Uninstalling"** (not "Installing"). *(Manually verified on Windows, 2026-07-16.)*
- [x] Progress callbacks don't crash (the managed→native delegate marshaling works). *(Session 3: confirmed via `DownloadAsync` progress samples on COM; re-confirmed end-to-end 2026-07-16.)*

**Cancellation** (`Esc`):

- [x] **Esc during an install cancels it** cooperatively (COM `Cancel()`): status shows "Cancelling…" then **"Cancelled"**, and the list refreshes. *(Manually verified on Windows, 2026-07-16.)*
- [x] **Esc with no op running** still quits the app (unchanged behavior). *(Manually verified on Windows, 2026-07-16.)*
- [x] `q` and `Ctrl+C` **still quit** during an op (only `Esc` cancels). *(Manually verified on Windows, 2026-07-16.)*
- [x] **Batch upgrade + Esc**: the in-flight item cancels and the remaining queue stops. *(Manually verified on Windows, 2026-07-16.)*
- [x] **One-op-at-a-time guard**: triggering a second operation while one is running is ignored (no second progress bar, no crash). *(Manually verified on Windows, 2026-07-16.)*

## P1.5 — Upstream parity ports (ported from `shanselman/winget-tui`, June 2026)

Three behavioural changes ported from upstream. Logic is unit-tested (`tests/AppBehaviorTests.cs`),
but these paths need a real terminal / real winget to confirm end-to-end.

> **Session 3:** `dotnet test -f net10.0` **passes** on this Windows host (exit 0; 18 facts/theories in
> `AppBehaviorTests`), including the backing tests for all three ports — `SortFieldForHeader_MapsSortableColumns`
> / `…ReturnsNullForNonSortableColumns` / `AppState_ApplyFilter_SortsVersions…` (click-to-sort),
> `UpgradeQueryFor_TruncatedId_FallsBackToName` (truncated-id), and `EmptyStateMessage_*` (empty-state).
> So the **logic is verified on Windows**; only the end-to-end terminal *interaction* remains — truncated-id
> is a straightforward CLI-backend keypress test (see P2's "for an agent" setup note, same applies here);
> click-to-sort specifically needs the mouse (see the caveat under P2).

- [ ] **Click-to-sort column headers** (upstream `66d464c4`). With the mouse, **click the `Name`, `Id`, or `Version` header** to sort by that column (ascending); **click the same header again** to reverse direction (the `↑`/`↓` arrow in the header should flip). Clicking the marker, **`Available`, or `Source`** header is a **no-op**. Verify in both **Installed** and **Upgrades** tabs (column indices differ between them). Keyboard `S` cycling must still work unchanged (already confirmed — see note). *(Keyboard `S` confirmed on screen, session 4 — Installed list re-sorted alphabetically with the header reading **`Name ↑`**. Only the **mouse** click path remains open — see the "for an agent" note under P2 below for why this specifically needs care. Session 5: attempted again via Windows-MCP — blocked at the tooling level, not a repro attempt: the `Click`/`Type` tools' `loc` array parameter fails Pydantic validation on this MCP server build (`Input should be a valid list, input_value='[x, y]', input_type=str` — the coordinate array is arriving pre-stringified at the tool boundary), and it's not specific to the console window — the same error reproduces on any coordinate-based click/type call, including outside this app. No workaround found this session; needs either a fixed MCP build or a real mouse/human at the keyboard.)*
- [ ] **Truncated-id upgrade falls back to name** (upstream `fd9e9dbe`). Find an Upgrades row whose **id is truncated with `…`** (winget does this to long ids in tabular output). Press **`u`**: instead of the old "Cannot upgrade: id was truncated" block, you should get a confirm reading **"Upgrade <name>? (id was truncated by winget — matching by name)"**, and on confirm the upgrade should actually run (CLI backend retries `--name --exact`). Needs the **`--cli`** backend (COM ids are never truncated). Confirm a non-truncated row still shows the plain "Upgrade <name>?" prompt.
- [x] **Contextual empty-state message** (upstream `#228`). When the list is empty, the message should match the reason: **Upgrades + 📌-hide (`UnpinnedOnly`) → "No unpinned packages with upgrades found."**; Upgrades + 📌-only → "No pinned packages with upgrades found."; Upgrades + all → "All packages are up to date!"; an active local filter that hides everything → 'No packages match "<text>".'. Toggle the pin filter (`P`) in Upgrades and type a non-matching filter (`/`) to exercise each. *(Session 4, **on AOT/COM**, interactive: confirmed two variants on screen — Installed with `/zoxide` filter after uninstall → **'No packages match "zoxide".'**; Upgrades + `P` to 📌-only → **"No pinned packages with upgrades found."** The unpinned-only and all-up-to-date variants were not separately triggered.)*

## P2 — Review-flagged real-Windows concerns & measurements

> **This section is written to be run directly by a Claude Desktop instance on Windows.** Each item below
> has concrete setup + steps + a pass/fail signal, not just "check this manually." Before starting:
> 1. **Build & publish** using the "Build & run" section at the top of this file (`dotnet publish -c
>    Release -f net10.0-windows10.0.26100.0 -r win-x64`).
> 2. **Confirm COM is live first**: run `& $exe --comdiag` and check for "activation OK" on both threads
>    with no `0x80073D54`. Then launch `$exe` normally and confirm the header badge reads `COM · winget
>    1.x` (not `CLI` or `Mock`). If it doesn't, stop and treat that as a P0 regression — none of the items
>    below are meaningful on the wrong backend.
> 3. **Actually look at the screen after each action** (screenshot / read the terminal buffer) before
>    checking a box — "the app didn't crash" is not the same as "the described behavior happened."
> 4. **Update this file directly** as you go: check `[x]` on pass, leave `[ ]` and add a note describing
>    what actually happened on fail/unexpected — don't just report back verbally, the file is the record.
> 5. The P1.5 **click-to-sort** item above needs the **mouse** specifically — prior GUI-automation attempts
>    (session 4) found the screenshot/click-coordinate tooling couldn't reliably target the console
>    window's cell grid. If your environment has the same limitation, note it on the item rather than
>    silently leaving it unchecked with no explanation.

- [ ] **Shared `PackageManager` thread-agility.** The backend reuses one `PackageManager` across operations invoked from background/threadpool (MTA) threads. **Steps:** open Search (`1`), type a query character-by-character fast enough to fire several searches in quick succession (each keystroke triggers a new `SearchAsync`), then immediately switch to Installed (`2`) / Upgrades (`3`) a few times while a search may still be in flight. Repeat for ~10–15 actions. **Watch for:** `RPC_E_WRONG_THREAD` or any COM exception in the status bar, a frozen UI, or a crash. **Pass** = no COM errors, no freeze. **If it fails**, note the exact error text/HRESULT — the fix would be switching to a fresh `PackageManager` per operation instead of the current shared instance (don't make that code change yourself, just record the repro).
- [ ] **Unhealthy source + `All`.** **Steps:** `winget source remove msstore`, then launch/refresh the app on the default **All** filter and search a common term (e.g. `a`). **Watch for:** does the whole query fail (all-or-nothing composite connect), or degrade gracefully? Then press `f` to cycle to **winget**-only and confirm the search recovers. **Pass** = `f` → winget-only recovers a working search. **Cleanup (required):** `winget source add msstore` (re-add the default msstore source URL) before finishing — don't leave the host's winget in a modified state.
- [ ] **Pinning on the COM backend.** Requires `winget` on PATH (pin/unpin always delegates to the CLI even on the COM backend). **Steps:** on an installed package, press `p` to pin; confirm the 📌 annotation appears in Installed/Upgrades; press `p` again to unpin; confirm it clears; refresh the list and confirm the state persisted correctly through the reload. **Pass** = pin state visibly toggles and survives a refresh.
- [ ] **Same-id-across-catalogs** (rare) — only worth checking if you happen to notice a package id present in more than one source during other testing; operations should resolve the first match without erroring. Skip if you don't hit one naturally; don't go out of your way to construct this scenario.
- [ ] **CLI-backend cancel.** **Steps:** launch with `& $exe --cli`, start installing a small package, press `Esc` mid-install. **Watch for:** the app stops *watching* (status shows cancelled/stopped) but `winget.exe` itself keeps running to completion in the background — check via Task Manager or `winget list` afterward. It must **not** kill the winget process. **Pass** = app detaches cleanly, the install still completes on its own.
- [x] **Measure the AOT binary size** of the COM (Windows) build and compare to the CLI/mock build, to budget the COM backend's cost. *(Session 3: **AOT win-x64 single exe = 22.4 MB** (no `coreclr.dll`) + the in-proc engine **`WindowsPackageManager.dll` ~7.3 MB** beside it ≈ **~30 MB deployed** — this is the **ship target** now that COM activates under AOT. For comparison, the abandoned JIT self-contained fallback was **112.7 MB** for the whole folder (~4× larger).)*
- [ ] **(Optional) win-arm64**: only if arm64 hardware is available. Repeat the P0 smoke (`--comdiag`, then search/Installed/Upgrades/details) on `dotnet publish -f net10.0-windows10.0.26100.0 -r win-arm64`. Skip otherwise — this is optional, don't try to cross-emulate it.
- [x] **Terminal.Gui Windows spot-check** — never yet done on real Windows, now several bumps behind (`2.4.3-develop.9` → current `2.4.18-develop.6`; all prior checks were mock-backend-on-Linux only). **Steps:** (a) type a **non-ASCII search query** (e.g. `café`, or switch to an IME and type Japanese/Chinese) into `/` search — confirm it renders correctly, not garbled (Windows VT input encoding fix #5453); (b) copy a Unicode string to the clipboard and **paste** it into search — confirm no mojibake (clipboard fixes #5449/#5451); (c) **resize the terminal window** mid-use (drag a corner) — confirm the frame redraws cleanly, no garbled leftover content (#5461); (d) general nav smoke — cycle Search/Installed/Upgrades tabs, open Help (`?`), open the detail panel — confirm normal rendering throughout. **Pass** = all four sub-checks clean. *(Session 5, on `2.4.17-develop.6` (one increment behind the current `2.4.18-develop.6` — a minor bump not considered worth a separate re-check): (a)+(b) pasted `日本語 café ★彡` into the Installed filter via clipboard — rendered correctly in the filter bar and the empty-state message `No packages match "日本語 café ★彡".`, no mojibake; (c) snapped the terminal window down then back to maximized mid-session — the 302-row table re-flowed cleanly, no garbled frame; (d) cycled Search/Installed/Upgrades throughout the session and the detail panel rendered correctly on every selection (COM enrichment fields included) — Help (`?`) specifically wasn't reopened this session, but no rendering issues surfaced anywhere else. All sub-checks clean; no issues found.)*
- [x] **WinGet COM packages re-verification at `1.29.280`** (`Microsoft.WindowsPackageManager.ComInterop` + `.InProcCom`, kept in lockstep) — first time this is a **stable** (non-preview) release; the in-proc AOT-activation trick from P0 is version-sensitive, so this needs its own dedicated re-check rather than assuming it "just works." **Steps:** `& $exe --comdiag` → confirm activation OK (3 catalogs, both MTA threads, no `0x80073D54`); launch normally and confirm the header badge reads **`COM · winget 1.29.280`**; run the read-only smoke suite — a search, open Installed, open Upgrades, open a package's detail panel, and `V` Verify on an installed package. **Pass** = `--comdiag` clean and all five smoke checks return real data with no COM errors. *(Session 5, **RE-VERIFIED — no regression.** AOT win-x64 publish (25.2 MB exe, `coreclr.dll` absent) → `--comdiag`: activation OK, 3 catalogs, both MTA threads, no `0x80073D54`. Live badge read **`COM · winget 1.29.280`**. Full smoke, both read-only and mutating: Search, Installed (302 pkgs), Upgrades, detail panel (7-Zip's full COM enrichment — Tags/Product code/Support/FAQ/Copyright/Author all populated), version-picker install, `V` Verify (Ok, ✓ Registry entry, ✓ Install location), `R` Repair (friendly "doesn't support repair" message intact post-bump), `u` Upgrade (0.9.9→0.10.0), Uninstall — all clean, no COM errors anywhere. `dotnet test -f net10.0` also passes **109/109** on this host. No issues found.)*

---

### Notes
- This checklist is maintained as the canonical "verify on Windows" list for the COM work; new COM-backend changes that can't be checked from Linux should add an item here.
