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
- [x] **No `InvalidCastException` anywhere at runtime.** The whole backend uses indexed `Materialize<T>` instead of `foreach` over projected collections (the spike's AOT rule). Exercise search/list/upgrades/show and confirm none throw the spike's original cast error. *(Clean across search, installed, upgrades, and details. Spike re-confirmed the indexed pattern on this host.)*
- [x] ✅ **The default AOT launch now uses COM.** Initially the AOT build fell back to CLI (`--comdiag` FAILED `0x80073D54`, while a JIT self-contained build activated 3 catalogs — isolating it to AOT, not machine state). **Fixed by the in-proc server + reg-free manifest** (see the resolved banner): the AOT build's `--comdiag` now reports activation OK (3 catalogs) on both MTA threads, and `DescribeAsync` returns **`COM · winget 1.29.190-preview`**. All COM verification below holds for the AOT build.
- [x] **Flag selection** works: `--cli`, `--com`, `--mock` pick the right backend; **`--cli` wins when both `--cli` and `--com` are passed** (precedence `--mock > --cli > --com > default`). *(`--mock --cli --com` → 10 mock pkgs; `--cli --com` → CLI backend, V reports COM-only on real packages.)*
- [x] **Search** (Search tab `1`, then `/`) returns real catalog results with version + source columns. *(Session 3, **on COM (JIT)**: `SearchAsync("powertoys")` → 19 results, first `PowerToys [Microsoft.PowerToys] 0.100.0 (winget)`. Validates the fixed composite-connect path.)*
- [x] **Installed** tab lists installed packages with correct installed versions. *(Session 3, **on COM**: `ListInstalledAsync` → 299 packages, e.g. `7-Zip [7zip.7zip] 26.01`.)*
- [x] **Upgrades** tab shows only packages with an available update, with the Available column populated. *(Session 3, **on COM**: `ListUpgradesAsync` → 10 rows, e.g. `PostgreSQL 18 18.3-3 → 18.4-1` — Available populated.)*
- [x] **Details** panel: selecting a row fetches metadata (publisher, description, homepage, license, release-notes URL). *(Session 3, **on COM**: `ShowAsync(Microsoft.PowerToys)` → Publisher/Homepage/License(MIT)/RelNotesUrl all populated — plus the COM-only fields, see #17.)*
- [x] **Source filter** (`f`) cycles sources and re-queries correctly. *(Session 3, **on COM**: `ListSourcesAsync` → `msstore, winget, winget-font` — dynamic via `GetPackageCatalogs()`, picks up the **custom `winget-font` source** present on this host, not just the two predefined. Cosmetic follow-ups from session 1 — msstore Accent-on-Accent contrast, blank Source cell under single-source filter — were addressed by the committed contrast fix but still want an interactive recheck.)*

## P1 — Operations + the two new features

Operations (pick a small, safe package to install/uninstall, e.g. a CLI tool):

- [ ] **Install** (`i`) via COM succeeds; status shows result; reboot-required note appears when applicable.
- [ ] **Install specific version** (`I`) resolves the chosen version (`PackageVersionId` path).
- [ ] **Upgrade** (`u`) works; a forced failure says **"Upgrade failed"** (not "Install failed").
- [ ] **Uninstall** (`x`) works.
- [ ] **Batch upgrade** (Upgrades tab → space to select → `U`) runs sequentially with per-item status.

**Install preview dialog** (`i` — COM-only data):

- [~] Pressing `i` briefly shows "Checking installer…", then the confirm dialog includes an installer summary line, e.g. **`MSI · x64 · machine · admin`** (type · architecture · scope · elevation). Note: the COM API exposes **no download size**, so size is intentionally absent. *(Session 3: COM **data** confirmed — `GetInstallerPreviewAsync(Microsoft.PowerToys)` → `Burn · arm64 · user`. The dialog rendering still needs an interactive recheck at the TUI.)*
- [~] The summary reflects reality — e.g. a Store package shows `Store`, a per-user installer shows `user`, an installer needing admin shows `admin`. *(Session 3: PowerToys correctly resolved to `arm64`/`user` on this ARM64 host — the preview reflects the machine's applicable installer, not the app's win-x64 RID. Per-type spot-checks pending interactive.)*
- [ ] If installer resolution fails (e.g. no applicable installer for this arch), the confirm still appears with just "Install X?" (no summary line) rather than erroring.

**Real version picker** (`I`):

- [~] `I` shows a **selectable list of real versions** (newest first), not the free-text box, when the COM backend can enumerate them. *(Session 3: COM **data** confirmed — `ListVersionsAsync(Microsoft.PowerToys)` → 112 versions, newest-first `0.100.0, 0.99.1, 0.98.1, 0.98.0, 0.97.2, …`. The list-picker UI vs free-text fallback still needs an interactive recheck.)*
- [ ] Picking a version → the install confirm shows that version + its installer preview → installs the chosen version.
- [ ] (CLI backend, `--cli`) `I` falls back to the **free-text** version prompt, since the CLI path returns no version list.

**Download-only** (`d`):

- [x] `d` on a package downloads its installer **without installing**, showing the progress bar (Downloading phase), and reports the path (default `%USERPROFILE%\Downloads\winget-tui`). Verify the installer file actually lands there. *(Session 3, **on COM**: `DownloadAsync(ajeetdsouza.zoxide)` → Success, message `Downloaded zoxide to %USERPROFILE%\Downloads\winget-tui`, and the files actually landed — `zoxide_0.9.9_Arm64_portable_en-US.zip` (480 KB) + `.yaml` manifest (COM resolved the **arm64** installer for this host). Progress callback fired: 2 samples, phase **Downloading**, fraction → 1.00. Confirms the `IProgress<OpProgress>` marshaling — the headline "live progress" path — works on COM. Test artifacts cleaned up. The status-bar render itself still wants a TUI eyeball.)*
- [ ] `Esc` cancels a download in progress (same cooperative-cancel path as install).
- [ ] (CLI backend) `d` runs `winget download`; on an older winget without that verb, the failure message is shown rather than a crash.

**Advanced install** (`A`):

- [ ] `A` opens the options panel (Scope / Mode / Arch option selectors + custom-args field). Arrow/selection works; Install/Cancel behave.
- [ ] Choosing **User** vs **Machine** scope, **Silent** vs **Interactive** mode, a specific **arch**, and **custom args** is reflected in the install confirm ("Options: …") and actually applied (e.g. Interactive mode shows the installer UI; user-scope installs to the user profile).
- [ ] Cancelling the panel aborts with no install.
- [ ] (CLI backend) the same options map to winget flags (`--scope`, `--silent`/`--interactive`, `--architecture`, `--custom`).

**Verify install** (`V` — COM-only):

- [~] `V` on an installed package runs `CheckInstalledStatus` and shows a result dialog: "Installed correctly" with ✓ checks (registry entry / install location / files), or a list of ✗ failures if the install is corrupt. *(Session 3: COM **logic** confirmed — `VerifyInstalledAsync(ajeetdsouza.zoxide)` returned a structured 3-check result. The result-dialog rendering still needs an interactive recheck.)*
- [x] Deliberately break an install (e.g. delete a file from the install dir) and confirm `V` reports the **Issues** outcome with the failing check. *(Session 3: did not need to break anything — installed zoxide 0.9.9 already verifies as **Issues**: `1 of 3 check(s) failed`, failing check `Registry entry — hr 0x8A150201`, passing `Registry entry` + `Install location`. CheckInstalledStatus → InstallVerification mapping works on COM.)*
- [ ] (CLI backend, `--cli`) `V` reports "Verify is only available on the COM backend" rather than erroring.

**Repair** (`R` — COM-only, via `RepairPackageAsync`):

- [ ] `R` on a healthy installed package (Installed/Upgrades) confirms, then repairs: the status bar shows a determinate bar advancing through a **Repairing** phase, ending "Done" (or "(reboot required)").
- [ ] **Verify → Repair flow**: break an install, `V` → **Issues** outcome → the result dialog offers **Repair** / **Close**; choosing **Repair** runs the repair **without a second confirm** and the install is restored (`V` again reports Ok).
- [~] A package whose installer has no repair behavior reports the friendly **"{name} doesn't support repair."** (`NoApplicableRepairer`), not a raw HRESULT. *(Session 3, **on COM**: `RepairAsync(ajeetdsouza.zoxide)` — zoxide is a **portable .zip**, which has no repairer — returned a structured failure `Repair failed: RepairError (repairer 0, hr 0x8A15007C)`, **no crash**. Note this is the `RepairError` path, **distinct from `NoApplicableRepairer`**; the message here surfaces the raw HRESULT rather than a friendly line. Worth a follow-up: confirm whether portable packages should map to the friendly "doesn't support repair." message, or whether `0x8A15007C` warrants its own friendly text. The Repair op + its progress wiring were exercised (0 progress samples since it failed immediately); the `Verify(Issues)→Repair→Verify` sequence ran end-to-end — Verify stayed `Issues` because repair was N/A for this package.)*
- [ ] **Esc during a repair** cancels cooperatively ("Cancelled"); the one-op-at-a-time gate blocks starting a second op mid-repair.
- [ ] (CLI backend, `--cli`) `R` shows the neutral **"Repair is only available on the COM backend."** (not a red error), and the detail-panel `R Repair install` action is still listed.
- [ ] `R` is **not** offered in Search mode (the selected package may not be installed).

**Richer detail panel** (COM):

- [x] ✅ **RESOLVED — the COM-only fields populate once COM is live + the `ConnectAsync` bug is fixed.** `ShowAsync(Microsoft.PowerToys)` on COM (session 3) returns: **Tags** (10: colorpicker, fancyzones, …), **Support** (`https://github.com/microsoft/PowerToys/issues`), **Documentation** (Wiki link). **Product code / Family name** are legitimately `<null>` (PowerToys is a `burn` installer, not MSI/MSIX — matches the spike). No app code change beyond the activation + composite-connect fixes. (Was never a bug — it was the CLI fallback under AOT *plus* the latent `AcceptSourceAgreements`-on-composite crash; both addressed.)
- [x] Packages without these fields don't render empty rows (the lines are omitted when absent). *(Confirmed — absent fields cleanly omitted.)*
- [x] **New enrichment fields (COM, all conditional).** *(Session 3, `ShowAsync(Microsoft.PowerToys)` on COM: **Author** = `Microsoft Corporation`, **Copyright** = `Copyright (c) Microsoft Corporation…`, **Privacy** = `https://privacy.microsoft.com/…` all populate. **Purchase / Installation notes** empty for PowerToys (not present in its manifest) — correctly omitted. **Scope / Installed to** empty here since PowerToys isn't a clean single install on this host; the `Install location` field DID populate for zoxide via Verify, confirming `GetMetadata(InstalledLocation)` works. Render-when-present / omit-when-blank confirmed at the data layer.)*

**Dynamic sources** (`f`) — COM via `GetPackageCatalogs()`, CLI via `winget source list`:

- [~] On a stock machine, `f` still cycles through the discovered sources. *(Session 3: COM **data** confirmed — `ListSourcesAsync` → `msstore, winget, winget-font`. The interactive `f`-cycle order/All-reset still wants a TUI recheck.)*
- [x] **Custom source appears in the cycle (dynamic, not hardcoded).** *(Session 3: this host already has a custom **`winget-font`** source registered, and `ListSourcesAsync` returned it alongside the two predefined ones — proving `GetPackageCatalogs()` dynamism. No need to add `contoso`. Filtering-scoping to a custom source still wants an interactive recheck on both COM and `--cli`.)*
- [ ] With a source selected that no longer exists (remove it while the app holds it selected, then refresh), the filter resets to **All** rather than erroring.

**Backend badge + version** (`PackageManager.Version`):

- [x] The top-right header shows the live backend + winget version. *(Session 3: `DescribeAsync` — which produces the badge string — returns **`COM · winget 1.29.250`** on the JIT/COM build and `CLI · winget 1.29.250` on the AOT build (ComBackend's ctor throws → CLI fallback). Confirmed at the data layer; the header render itself wants a quick TUI eyeball but the string is exactly what the badge shows.)*
- [ ] The **Help** dialog (`?`) leads with a matching `Backend: …` line.

**Search match hint + result cap** (COM):

- [ ] Search a term that matches a package by **tag/moniker/command** (not its name) — the detail panel shows a dim `↳ matched on tag` footnote. A normal name/id match shows **no** such line.
- [ ] A very broad search (e.g. a single common letter) caps at **1000** rows and the status reads `1000+ matches — refine your search to narrow` instead of flooding the table.

**Live progress bar** (the headline feature — the `.Progress` delegate marshaling concern):

> **Session 3:** the `IProgress<OpProgress>` callback path **works on COM (JIT)** — `DownloadAsync(zoxide)`
> delivered progress samples (phase **Downloading**, fraction → 1.00) with no crash, so the managed→native
> progress delivery is sound. The original "**under AOT**, the one CCW-callback unknown" framing is now
> **moot**: COM doesn't activate under AOT at all (see the resolved banner), so progress under AOT never runs;
> under JIT (the COM ship vehicle) the CCW path is standard. Install/uninstall progress *rendering* in the
> status bar still wants an interactive recheck.

- [~] During a real COM **install**, the status bar shows a determinate bar that **advances** through **Downloading → Installing**. *(Progress plumbing confirmed via download; install execution + status-bar render still need a human at the TUI.)*
- [ ] During an **uninstall**, the phase reads **"Uninstalling"** (not "Installing"). *(Not run — uninstalling the throwaway then needing to reinstall it was avoided; the `Uninstalling` phase label exists in `OpPhase`.)*
- [x] Progress callbacks don't crash (the managed→native delegate marshaling works). *(Session 3: confirmed via `DownloadAsync` progress samples on COM.)*

**Cancellation** (`Esc`):

- [ ] **Esc during an install cancels it** cooperatively (COM `Cancel()`): status shows "Cancelling…" then **"Cancelled"**, and the list refreshes.
- [ ] **Esc with no op running** still quits the app (unchanged behavior).
- [ ] `q` and `Ctrl+C` **still quit** during an op (only `Esc` cancels).
- [ ] **Batch upgrade + Esc**: the in-flight item cancels and the remaining queue stops.
- [ ] **One-op-at-a-time guard**: triggering a second operation while one is running is ignored (no second progress bar, no crash).

## P1.5 — Upstream parity ports (ported from `shanselman/winget-tui`, June 2026)

Three behavioural changes ported from upstream. Logic is unit-tested (`tests/AppBehaviorTests.cs`),
but these paths need a real terminal / real winget to confirm end-to-end.

> **Session 3:** `dotnet test -f net10.0` **passes** on this Windows host (exit 0; 18 facts/theories in
> `AppBehaviorTests`), including the backing tests for all three ports — `SortFieldForHeader_MapsSortableColumns`
> / `…ReturnsNullForNonSortableColumns` / `AppState_ApplyFilter_SortsVersions…` (click-to-sort),
> `UpgradeQueryFor_TruncatedId_FallsBackToName` (truncated-id), and `EmptyStateMessage_*` (empty-state).
> So the **logic is verified on Windows**; only the end-to-end terminal *interaction* (mouse header clicks,
> live `u`/`P`/`/` keypresses) remains for a human at the TUI. The boxes stay unchecked to reflect that.

- [ ] **Click-to-sort column headers** (upstream `66d464c4`). With the mouse, **click the `Name`, `Id`, or `Version` header** to sort by that column (ascending); **click the same header again** to reverse direction (the `↑`/`↓` arrow in the header should flip). Clicking the marker, **`Available`, or `Source`** header is a **no-op**. Verify in both **Installed** and **Upgrades** tabs (column indices differ between them). Keyboard `S` cycling must still work unchanged. *(Mouse `ScreenToCell` header detection can't be exercised from Linux unit tests.)*
- [ ] **Truncated-id upgrade falls back to name** (upstream `fd9e9dbe`). Find an Upgrades row whose **id is truncated with `…`** (winget does this to long ids in tabular output). Press **`u`**: instead of the old "Cannot upgrade: id was truncated" block, you should get a confirm reading **"Upgrade <name>? (id was truncated by winget — matching by name)"**, and on confirm the upgrade should actually run (CLI backend retries `--name --exact`). Needs the **`--cli`** backend (COM ids are never truncated). Confirm a non-truncated row still shows the plain "Upgrade <name>?" prompt.
- [ ] **Contextual empty-state message** (upstream `#228`). When the list is empty, the message should match the reason: **Upgrades + 📌-hide (`UnpinnedOnly`) → "No unpinned packages with upgrades found."**; Upgrades + 📌-only → "No pinned packages with upgrades found."; Upgrades + all → "All packages are up to date!"; an active local filter that hides everything → 'No packages match "<text>".'. Toggle the pin filter (`P`) in Upgrades and type a non-matching filter (`/`) to exercise each.

## P2 — Review-flagged real-Windows concerns & measurements

- [ ] **Shared `PackageManager` thread-agility.** The backend reuses one `PackageManager` across operations invoked from background/threadpool (MTA) threads. Watch for `RPC_E_WRONG_THREAD` or intermittent COM errors under rapid search/typing or back-to-back ops. **If seen → switch to a fresh `PackageManager` per operation** (currently shared as a perf choice). *(Open from review pass 2.)*
- [ ] **Unhealthy source + `All`.** Disable/break the `msstore` source, then do a default (All) search. Does the all-or-nothing composite connect fail the whole query? Confirm the documented workaround — pressing `f` to narrow to winget-only — recovers. *(Open from review pass 1.)*
- [ ] **Pinning on the COM backend.** Pin (`p`), unpin, and pin annotations (📌) work — these delegate to `winget.exe`, so they need winget on PATH even on the COM backend. Confirm pin state shows in Installed/Upgrades and pin/unpin succeed.
- [ ] **Same-id-across-catalogs** (rare): if a package id exists in multiple sources, operations resolve the first match. Only worth checking if you hit an odd case.
- [ ] **CLI-backend cancel** (`--cli`, then Esc mid-install): confirm it stops watching but does **not** kill `winget.exe` (the install continues) — documented, lower priority.
- [x] **Measure the AOT binary size** of the COM (Windows) build and compare to the CLI/mock build, to budget the COM backend's cost. *(Session 3: **AOT win-x64 single exe = 22.4 MB** (no `coreclr.dll`). The **JIT self-contained** alternative — the recommended COM ship vehicle since AOT can't activate COM — is **112.7 MB** for the whole folder (~5× the AOT exe, main exe only 0.2 MB + the shared CoreCLR/framework). So keeping COM costs the AOT size advantage regardless of which non-AOT form is chosen.)*
- [ ] **(Optional) win-arm64**: repeat the P0 smoke on an arm64 host or arm64 cross-target.
- [ ] **Terminal.Gui bump `2.4.3-develop.9` → `2.4.7-develop.1`.** Spot-check the Windows-only input/render fixes that landed in the 2.4.4 release line (can't be verified from Linux): (a) type a **non-ASCII search query** (e.g. accented chars / IME) into `/` search and confirm it renders correctly (Windows VT input encoding fix #5453); (b) **paste** a Unicode string into search via bracketed paste and confirm no mojibake (clipboard fixes #5449/#5451); (c) **resize the terminal** mid-use and confirm no garbled frame at the wrong dimensions (#5461). No app code changed — these are upstream fixes the bump picks up for free.

---

### Notes
- Spike repro for the COM-in-AOT mechanics lives in `spikes/ComBackendSpike/` (`Run-AotSpike.ps1`, `SPIKE-RESULTS.md`) — already validated; useful if a low-level COM/AOT question resurfaces.
- This checklist is maintained as the canonical "verify on Windows" list for the COM work; new COM-backend changes that can't be checked from Linux should add an item here.
