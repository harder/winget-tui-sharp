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
```

For quick iteration without AOT: `dotnet run -f net10.0-windows10.0.26100.0`.

---

## P0 — Foundational COM runtime (must pass first)

> ### 🚨 CRITICAL FINDING — the AOT build cannot activate the COM backend
> **`new PackageManager()` throws `0x80073D54` (`APPMODEL_ERROR_NO_PACKAGE`) in the Native-AOT
> build**, so `SelectBackend` catches it and **silently falls back to the CLI backend** (the
> "COM backend unavailable…" stderr note is painted over by the TUI redraw, so it's invisible).
> The **top-right backend badge now makes this visible at a glance** — on the AOT default launch it
> reads `CLI · winget 1.x`, not `COM · winget 1.x`. (Originally confirmed the harder way, via the
> COM-only `V` action reporting *"Verify is only available on the COM backend"*.)
>
> - The **same source built non-AOT (JIT, self-contained x64) activates COM fine** (3 catalogs),
>   even with the server warmed by JIT moments earlier. So the failure is **AOT-specific**, not
>   server state, not apartment (both threads are MTA).
> - **Did NOT fix it:** CsWinRT AOT optimizer (`Microsoft.Windows.CsWinRT` + `CsWinRTAotOptimizerEnabled=Auto`);
>   an `app.manifest` with Win10 `supportedOS` + `longPathAware`; warming the server first.
> - **Caveat:** EARLY in the session (before heavy COM-diagnostic abuse + a reboot) an AOT *spike*
>   DID activate COM (3 catalogs + the AOT foreach signature), so AOT-COM is not categorically
>   impossible here — the current deterministic failure may be entangled with COM-server/AppModel
>   state, OR a genuine AOT activation bug the early run avoided. Machine state is compromised.
> - **DECISIVE NEXT EXPERIMENT (clean state):** fresh reboot → as the *very first* COM activity,
>   run the AOT build's `--comdiag` (binary in the publish dir still has it). Activates → transient
>   state, redo the real verification on COM. Still fails → genuine AOT bug → CsWinRT 3.x / upstream
>   issue / `InProcCom` / ship the COM build non-AOT (JIT confirmed working).
> - **Consequence for the results below:** items marked ✅ that "passed on COM" were almost
>   certainly exercising the **CLI** backend (which also yields structured search/list/details, so
>   it was indistinguishable). They validate the UI + CLI path, NOT the COM backend. See HANDOFF.md.

- [x] **AOT publish succeeds** and produces a native exe; `coreclr.dll` is absent (true AOT, not self-contained). *(23.3 MB exe; verified coreclr.dll absent. On an ARM64 host the publish must run inside `Enter-VsDevShell -DevCmdArguments "-arch=x64 -host_arch=arm64"` with the VS Installer dir on PATH — ILC 10.0.8 calls bare `vswhere.exe`.)*
- [x] **No `InvalidCastException` anywhere at runtime.** The whole backend uses indexed `Materialize<T>` instead of `foreach` over projected collections (the spike's AOT rule). Exercise search/list/upgrades/show and confirm none throw the spike's original cast error. *(Clean across search, installed, upgrades, and details. Spike re-confirmed the indexed pattern on this host.)*
- [ ] ❓ **RE-CHECK on the freshly-pulled `main` build — does the default launch use COM?** **Look at the top-right badge first:** `COM · winget` = COM activated (the earlier AOT failure is resolved — re-run every ❌/❓ item below on COM); `CLI · winget` = it still silently fell back (the AOT-COM bug stands — see the critical-finding banner and run the decisive `--comdiag` experiment). On the last Windows session this **FAILED** (`CLI`; `V` reported "only available on the COM backend"). See the critical-finding banner above. *(The fast/structured search and full IDs seen on CLI also look structured — they do not by themselves prove COM is active; trust the badge.)*
- [x] **Flag selection** works: `--cli`, `--com`, `--mock` pick the right backend; **`--cli` wins when both `--cli` and `--com` are passed** (precedence `--mock > --cli > --com > default`). *(`--mock --cli --com` → 10 mock pkgs; `--cli --com` → CLI backend, V reports COM-only on real packages.)*
- [x] **Search** (Search tab `1`, then `/`) returns real catalog results with version + source columns. *(18 results for "powertoys" across winget + msstore.)*
- [x] **Installed** tab lists installed packages with correct installed versions. *(248 packages; PowerToys 0.98.1 etc., correlated via COM LocalCatalogs composite.)*
- [x] **Upgrades** tab shows only packages with an available update, with the Available column populated.
- [x] **Details** panel: selecting a row fetches metadata (publisher, description, homepage, license, release-notes URL). *(Verified on Fefedu973.UniversalSearchSuggestions: publisher, MIT license, homepage + release-notes URLs, description all populated.)*
- [x] **Source filter** (`f`) cycles All / winget / msstore and re-queries correctly. *(Functional pass. Cosmetic follow-ups: msstore Source cell unreadable when row highlighted — Accent-on-Accent contrast bug; Source column blank under single-source filter; per-row detail fetch stalled 30–60s for later rows under winget filter — logged to P2 perf watch.)*

## P1 — Operations + the two new features

Operations (pick a small, safe package to install/uninstall, e.g. a CLI tool):

- [ ] **Install** (`i`) via COM succeeds; status shows result; reboot-required note appears when applicable.
- [ ] **Install specific version** (`I`) resolves the chosen version (`PackageVersionId` path).
- [ ] **Upgrade** (`u`) works; a forced failure says **"Upgrade failed"** (not "Install failed").
- [ ] **Uninstall** (`x`) works.
- [ ] **Batch upgrade** (Upgrades tab → space to select → `U`) runs sequentially with per-item status.

**Install preview dialog** (`i` — COM-only data):

- [ ] Pressing `i` briefly shows "Checking installer…", then the confirm dialog includes an installer summary line, e.g. **`MSI · x64 · machine · admin`** (type · architecture · scope · elevation). Note: the COM API exposes **no download size**, so size is intentionally absent.
- [ ] The summary reflects reality — e.g. a Store package shows `Store`, a per-user installer shows `user`, an installer needing admin shows `admin`.
- [ ] If installer resolution fails (e.g. no applicable installer for this arch), the confirm still appears with just "Install X?" (no summary line) rather than erroring.

**Real version picker** (`I`):

- [ ] `I` shows a **selectable list of real versions** (newest first), not the free-text box, when the COM backend can enumerate them.
- [ ] Picking a version → the install confirm shows that version + its installer preview → installs the chosen version.
- [ ] (CLI backend, `--cli`) `I` falls back to the **free-text** version prompt, since the CLI path returns no version list.

**Download-only** (`d`):

- [ ] `d` on a package downloads its installer **without installing**, showing the progress bar (Downloading phase), and reports the path (default `%USERPROFILE%\Downloads\winget-tui`). Verify the installer file actually lands there.
- [ ] `Esc` cancels a download in progress (same cooperative-cancel path as install).
- [ ] (CLI backend) `d` runs `winget download`; on an older winget without that verb, the failure message is shown rather than a crash.

**Advanced install** (`A`):

- [ ] `A` opens the options panel (Scope / Mode / Arch option selectors + custom-args field). Arrow/selection works; Install/Cancel behave.
- [ ] Choosing **User** vs **Machine** scope, **Silent** vs **Interactive** mode, a specific **arch**, and **custom args** is reflected in the install confirm ("Options: …") and actually applied (e.g. Interactive mode shows the installer UI; user-scope installs to the user profile).
- [ ] Cancelling the panel aborts with no install.
- [ ] (CLI backend) the same options map to winget flags (`--scope`, `--silent`/`--interactive`, `--architecture`, `--custom`).

**Verify install** (`V` — COM-only):

- [ ] `V` on an installed package runs `CheckInstalledStatus` and shows a result dialog: "Installed correctly" with ✓ checks (registry entry / install location / files), or a list of ✗ failures if the install is corrupt.
- [ ] Deliberately break an install (e.g. delete a file from the install dir) and confirm `V` reports the **Issues** outcome with the failing check.
- [ ] (CLI backend, `--cli`) `V` reports "Verify is only available on the COM backend" rather than erroring.

**Repair** (`R` — COM-only, via `RepairPackageAsync`):

- [ ] `R` on a healthy installed package (Installed/Upgrades) confirms, then repairs: the status bar shows a determinate bar advancing through a **Repairing** phase, ending "Done" (or "(reboot required)").
- [ ] **Verify → Repair flow**: break an install, `V` → **Issues** outcome → the result dialog offers **Repair** / **Close**; choosing **Repair** runs the repair **without a second confirm** and the install is restored (`V` again reports Ok).
- [ ] A package whose installer has no repair behavior reports the friendly **"{name} doesn't support repair."** (`NoApplicableRepairer`), not a raw HRESULT.
- [ ] **Esc during a repair** cancels cooperatively ("Cancelled"); the one-op-at-a-time gate blocks starting a second op mid-repair.
- [ ] (CLI backend, `--cli`) `R` shows the neutral **"Repair is only available on the COM backend."** (not a red error), and the detail-panel `R Repair install` action is still listed.
- [ ] `R` is **not** offered in Search mode (the selected package may not be installed).

**Richer detail panel** (COM):

- [ ] ⚠️ **Not a bug — symptom of the AOT COM-activation failure.** The extra manifest fields (**Tags**, **Product code**, **Family name**, **Support**, **Documentation**) never render because the app is running on the **CLI** backend (COM didn't activate — see the critical-finding banner), and these fields are COM-only (`null` on CLI by design, per `Models.cs`). The spike proved the COM data exists and is readable via the backend's indexed pattern, so once COM activation is fixed this should work. Re-test after the COM-on-AOT issue is resolved.
- [x] Packages without these fields don't render empty rows (the lines are omitted when absent). *(Confirmed — absent fields cleanly omitted.)*
- [ ] **New enrichment fields (COM, all conditional).** Once COM activates, confirm these render when present and are omitted when blank: **Author**, **Copyright**, **Privacy** (link), **Purchase** (link), **Installation notes** (paragraph section after Description), and for installed packages **Scope** + **Installed to** (location). These come from `CatalogPackageMetadata` (Author/Copyright/PrivacyUrl/PurchaseUrl/InstallationNotes) and `PackageVersionInfo.GetMetadata(InstalledScope/InstalledLocation)`. A package with none of them should look exactly as before.

**Dynamic sources** (`f`) — COM via `GetPackageCatalogs()`, CLI via `winget source list`:

- [ ] On a stock machine, `f` still cycles **All → Winget → MsStore → All** (the discovered list matches the two predefined sources).
- [ ] **Add a custom source** (`winget source add -n contoso <url>`), relaunch, and confirm `f` now includes **contoso** in the cycle and filtering to it scopes the list/search to that source. (This is the whole point of going dynamic — verify it on both COM and `--cli`.)
- [ ] With a source selected that no longer exists (remove it while the app holds it selected, then refresh), the filter resets to **All** rather than erroring.

**Backend badge + version** (`PackageManager.Version`):

- [ ] The top-right header shows the live backend + winget version, e.g. **`COM · winget 1.x`** (or `CLI · winget 1.x` / `Mock backend`). On the AOT build this is the quickest confirmation of whether COM actually activated or silently fell back to CLI.
- [ ] The **Help** dialog (`?`) leads with a matching `Backend: …` line.

**Search match hint + result cap** (COM):

- [ ] Search a term that matches a package by **tag/moniker/command** (not its name) — the detail panel shows a dim `↳ matched on tag` footnote. A normal name/id match shows **no** such line.
- [ ] A very broad search (e.g. a single common letter) caps at **1000** rows and the status reads `1000+ matches — refine your search to narrow` instead of flooding the table.

**Live progress bar** (the headline feature — also tests `.Progress` delegate marshaling under AOT, the one CCW-callback unknown):

- [ ] During a real COM **install**, the status bar shows a determinate bar that **advances**, moving through **Downloading → Installing** phases with changing percentages (not stuck at 0%/100%).
- [ ] During an **uninstall**, the phase reads **"Uninstalling"** (not "Installing").
- [ ] Progress callbacks don't crash under AOT (the managed→native delegate CCW works — same path the spike's awaited `ConnectAsync` exercised).

**Cancellation** (`Esc`):

- [ ] **Esc during an install cancels it** cooperatively (COM `Cancel()`): status shows "Cancelling…" then **"Cancelled"**, and the list refreshes.
- [ ] **Esc with no op running** still quits the app (unchanged behavior).
- [ ] `q` and `Ctrl+C` **still quit** during an op (only `Esc` cancels).
- [ ] **Batch upgrade + Esc**: the in-flight item cancels and the remaining queue stops.
- [ ] **One-op-at-a-time guard**: triggering a second operation while one is running is ignored (no second progress bar, no crash).

## P1.5 — Upstream parity ports (ported from `shanselman/winget-tui`, June 2026)

Three behavioural changes ported from upstream. Logic is unit-tested (`tests/AppBehaviorTests.cs`),
but these paths need a real terminal / real winget to confirm end-to-end.

- [ ] **Click-to-sort column headers** (upstream `66d464c4`). With the mouse, **click the `Name`, `Id`, or `Version` header** to sort by that column (ascending); **click the same header again** to reverse direction (the `↑`/`↓` arrow in the header should flip). Clicking the marker, **`Available`, or `Source`** header is a **no-op**. Verify in both **Installed** and **Upgrades** tabs (column indices differ between them). Keyboard `S` cycling must still work unchanged. *(Mouse `ScreenToCell` header detection can't be exercised from Linux unit tests.)*
- [ ] **Truncated-id upgrade falls back to name** (upstream `fd9e9dbe`). Find an Upgrades row whose **id is truncated with `…`** (winget does this to long ids in tabular output). Press **`u`**: instead of the old "Cannot upgrade: id was truncated" block, you should get a confirm reading **"Upgrade <name>? (id was truncated by winget — matching by name)"**, and on confirm the upgrade should actually run (CLI backend retries `--name --exact`). Needs the **`--cli`** backend (COM ids are never truncated). Confirm a non-truncated row still shows the plain "Upgrade <name>?" prompt.
- [ ] **Contextual empty-state message** (upstream `#228`). When the list is empty, the message should match the reason: **Upgrades + 📌-hide (`UnpinnedOnly`) → "No unpinned packages with upgrades found."**; Upgrades + 📌-only → "No pinned packages with upgrades found."; Upgrades + all → "All packages are up to date!"; an active local filter that hides everything → 'No packages match "<text>".'. Toggle the pin filter (`P`) in Upgrades and type a non-matching filter (`/`) to exercise each.

## P2 — Review-flagged real-Windows concerns & measurements

- [ ] **Shared `PackageManager` thread-agility.** The backend reuses one `PackageManager` across operations invoked from background/threadpool (MTA) threads. Watch for `RPC_E_WRONG_THREAD` or intermittent COM errors under rapid search/typing or back-to-back ops. **If seen → switch to a fresh `PackageManager` per operation** (currently shared as a perf choice). *(Open from review pass 2.)*
- [ ] **Unhealthy source + `All`.** Disable/break the `msstore` source, then do a default (All) search. Does the all-or-nothing composite connect fail the whole query? Confirm the documented workaround — pressing `f` to narrow to winget-only — recovers. *(Open from review pass 1.)*
- [ ] **Pinning on the COM backend.** Pin (`p`), unpin, and pin annotations (📌) work — these delegate to `winget.exe`, so they need winget on PATH even on the COM backend. Confirm pin state shows in Installed/Upgrades and pin/unpin succeed.
- [ ] **Same-id-across-catalogs** (rare): if a package id exists in multiple sources, operations resolve the first match. Only worth checking if you hit an odd case.
- [ ] **CLI-backend cancel** (`--cli`, then Esc mid-install): confirm it stops watching but does **not** kill `winget.exe` (the install continues) — documented, lower priority.
- [ ] **Measure the AOT binary size** of the COM (Windows) build and compare to the CLI/mock build, to budget the COM backend's cost. *(Open spike question.)*
- [ ] **(Optional) win-arm64**: repeat the P0 smoke on an arm64 host or arm64 cross-target.
- [ ] **Terminal.Gui bump `2.4.3-develop.9` → `2.4.7-develop.1`.** Spot-check the Windows-only input/render fixes that landed in the 2.4.4 release line (can't be verified from Linux): (a) type a **non-ASCII search query** (e.g. accented chars / IME) into `/` search and confirm it renders correctly (Windows VT input encoding fix #5453); (b) **paste** a Unicode string into search via bracketed paste and confirm no mojibake (clipboard fixes #5449/#5451); (c) **resize the terminal** mid-use and confirm no garbled frame at the wrong dimensions (#5461). No app code changed — these are upstream fixes the bump picks up for free.

---

### Notes
- Spike repro for the COM-in-AOT mechanics lives in `spikes/ComBackendSpike/` (`Run-AotSpike.ps1`, `SPIKE-RESULTS.md`) — already validated; useful if a low-level COM/AOT question resurfaces.
- This checklist is maintained as the canonical "verify on Windows" list for the COM work; new COM-backend changes that can't be checked from Linux should add an item here.
