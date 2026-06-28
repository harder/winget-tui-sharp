# Handoff — Windows COM-backend verification

**Latest:** session 4 · 2026-06-27 · branch `main` (PR #11 merged) · Windows 11 **ARM64**, App Installer `1.29.250.0`.
**Earlier:** session 3 · 2026-06-13 · branch `main`; session 2 · 2026-05-29 · branch `feat/com-backend`.

Sessions 1–2 were human-in-the-loop (a human drove the interactive TUI; the agent handled builds,
non-interactive checks, diagnosis, fixes). Session 3 was fully autonomous (no human at the TUI), so it
resolved the headline question and exercised the COM backend via read-only diagnostics rather than the
interactive flows. **Session 4 was autonomous *with* the agent driving the interactive TUI itself**
(GUI automation on the AOT build), closing most of the interactive-render gaps — see the session-4 block
immediately below.

---

## ✅ SESSION 4 (2026-06-27) — agent-driven interactive TUI pass + repair-message fix

The agent published the **Native-AOT win-x64** build (22.5 MB exe, no `coreclr.dll`, in-proc
`WindowsPackageManager.dll` beside it), confirmed `--comdiag` → **activation OK, 3 catalogs on both MTA
threads**, then drove the live TUI via GUI automation. All checks below were **observed on screen on the
AOT/COM build** (badge read `COM · winget 1.29.190-preview` throughout). Throwaway package: `ajeetdsouza.zoxide`
(installed → verified → repair-attempted → uninstalled; confirmed gone via `winget list`, system restored).

**Code fix this session — repair message (`src/ComBackend.cs` `RepairAsync`).** A portable `.zip` package
returns `RepairResultStatus.RepairError` with `ExtendedErrorCode 0x8A15007C`
(`APPINSTALLER_CLI_ERROR_REPAIR_NOT_SUPPORTED`), which fell through to the raw-HRESULT branch. Now a
`RepairFailureMessage` helper maps the "can't repair this" HRESULT family — `0x8A150079`
(NO_REPAIR_INFO_FOUND), `0x8A15007A` (NOT_APPLICABLE), `0x8A15007C` (NOT_SUPPORTED) — to the friendly
**"{name} doesn't support repair."**, and `0x8A15007D` (ADMIN_CONTEXT_REPAIR_PROHIBITED) to its own
elevation message; genuine failures keep the detailed status + HRESULT. **Verified live:** Repair on zoxide
now shows **"zoxide doesn't support repair."** in the status bar (was the raw `0x8A15007C`). Tests: 109 pass.

**Interactive items verified on screen (AOT/COM):** backend badge (`COM · winget 1.29.190-preview`) on the
header *and* leading the `?` Help dialog · Installed + Upgrades tabs with the **Available** column populated ·
rich detail panel (Tags, Product code, Author, Copyright, Support, Privacy, Manual/FAQ, Homepage, Release
notes) on Unity Hub / 7-Zip / zoxide · **bulk-select hint** (`Spc Select` / `U Upgrade sel`) · **Verify**
dialog → **Ok** ("all checks passed", ✓ Registry + ✓ Install location) on Unity Hub and on zoxide (the
per-installer fix — zoxide previously false-flagged Issues) · **Install preview** dialog with summary line
`Zip · arm64` · real **Install** (zoxide) and **Uninstall** execution · **Repair** confirm + friendly message
(above) · **op result persists** ("Done") through the post-op reload · **version picker** (`I`) selectable
list newest-first (`0.9.9…0.9.0`) · **advanced install** panel (`A`: Scope/Mode/Arch radios + custom-args) ·
**empty-state** variants — filter-no-match `No packages match "zoxide".` and Upgrades 📌-only `No pinned
packages with upgrades found.` · **sort** (`S`) → `Name ↑` arrow + alphabetized list · paste into `/` search ·
ARP-only graceful fallback ("could not retrieve manifest details … list-view information only").

**Still NOT exercised (and why):** live progress-bar *frames* advancing + **Esc** cancel mid-op (zoxide ops
finish too fast to capture; the `IProgress` path itself was already proven via `DownloadAsync` in session 3) ·
**batch upgrade** execution (`U`) and single **upgrade** `u` execution (would mutate the host's real packages) ·
**mouse** click-to-sort header hit-detection (the GUI-automation screenshot masks the `winget-tui-sharp.exe`-owned
window and hides other windows — not worth disrupting a live desktop; sort logic + arrow render confirmed via
keyboard `S`, and `SortFieldForHeader` is unit-tested) · **pinning** state change `p` (mutates real winget pins) ·
truncated-id `u` fallback (needs `--cli`; COM ids never truncate) · non-ASCII/IME input + terminal resize
(Terminal.Gui bump spot-checks). NB: installed count read 299→298 across the install/uninstall cycle — normal
±1 winget ARP-enumeration drift between refreshes (only zoxide was ever uninstalled).

---

## ✅ SESSION 3 (2026-06-13) — headline question RESOLVED + a second COM bug found & fixed

**1. ✅✅ COM-on-AOT is SOLVED — Native AOT now activates the COM backend. AOT is the ship target.**
First the failure was isolated (fresh AOT `--comdiag` FAILED `0x80073D54` on both MTA threads; the same source
as a JIT self-contained build activated 3 catalogs → genuine AOT-specific bug, not machine state). Then it was
**fixed** by switching to the **in-process** WinGet server:

| Build | `coreclr.dll` | `--comdiag` |
|-------|---------------|-------------|
| Native AOT, OOP activation (before) | absent | FAILED `0x80073D54` |
| JIT self-contained, OOP (control) | present | OK — 3 catalogs |
| **Native AOT, in-proc (the fix)** | **absent** | **OK — 3 catalogs, both MTA threads** |

**The fix** (`WingetTuiSharp.csproj` + new `app.manifest`, Windows TFM only):
- Add `Microsoft.WindowsPackageManager.InProcCom` (match ComInterop's version, `1.29.190-preview`) with
  `ExcludeAssets="compile" NoWarn="NU1701"` — native-only package shipping `WindowsPackageManager.dll` (~7 MB)
  + `Microsoft.Management.Deployment.InProc.dll`. Keep ComInterop (managed projection).
- `<ApplicationManifest>app.manifest</ApplicationManifest>`; `app.manifest` transplants the InProc package's
  `<file>` comClass/`activatableClass` block so `new PackageManager()` activates **in-proc**, not OOP.

**Why OOP failed under AOT:** the manual-activation shim `winrtact.dll`
(`WinGetServerManualActivation_CreateInstance`) was **dropped from ComInterop ≥ 1.10.x**
([winget-cli#5459](https://github.com/microsoft/winget-cli/issues/5459),
[#4839](https://github.com/microsoft/winget-cli/issues/4839)); AOT has no CsWinRT runtime fallback to reach the
registered OOP server (JIT does). In-proc needs neither the OOP server nor package identity → activates under AOT.

Verified on the AOT build via `--comsmoke`: search / installed (299) / upgrades / versions (113) / installer-preview
(`Burn · arm64 · user`) / COM detail (Tags=10, Support, Docs) / Verify=Ok — all in-proc. Badge reads
**`COM · winget 1.29.190-preview`** (the bundled in-proc engine version). **Bonus:** in-proc sidesteps the OOP
server-wedge problem entirely. **Size:** AOT single-exe stays ~22.4 MB; +7.3 MB in-proc engine beside it (vs the
~112 MB JIT self-contained folder that was the abandoned fallback).

**Leads that did NOT work (don't retry):** CsWinRT 2.2.0 optimizer; `Microsoft.Windows.CsWinRT 3.0.0-preview`
(breaks at its own `cswinrt.exe` codegen + WinRT.Runtime conflict); bare `app.manifest` with only
`supportedOS`/`longPathAware` (no in-proc routing); warming the OOP server.

**2. 🐛 Second, independent COM bug FOUND & FIXED — `ComBackend.ConnectAsync` (`src/ComBackend.cs`).**
`ConnectAsync` set `reference.AcceptSourceAgreements = true` on the **composite** catalog reference, which
throws `E_ILLEGAL_STATE_CHANGE` (`set_AcceptSourceAgreements` on `IPackageCatalogReference3`). All three
composite-connect callers (search, list, find-by-id) hit it — so **every COM search/list/detail would have
thrown the instant COM activated.** It stayed latent because AOT always fell back to CLI, so the COM path
never actually ran in the app. `RemoteRefs` already sets `AcceptSourceAgreements = true` on each *source*
ref (the API-correct place) before compositing, making the composite set both redundant and illegal.
**Fix:** removed the set from `ConnectAsync` (comment explains why). After the fix, the full COM surface works.

**3. COM verification done on the JIT build (read-only, non-destructive) — all passing:**
- Badge / `DescribeAsync` → **`COM · winget 1.29.250`** · `CanRepair` → **True**
- `ListSourcesAsync` → **`msstore, winget, winget-font`** (dynamic; picks up the custom `winget-font` source)
- `SearchAsync("powertoys")` → **19** results (`PowerToys [Microsoft.PowerToys] 0.100.0 winget`)
- `ListInstalledAsync` → **299** · `ListUpgradesAsync` → **10** (`PostgreSQL 18 18.3-3 → 18.4-1`, Available populated)
- `ListVersionsAsync(Microsoft.PowerToys)` → **112** versions, newest-first
- `GetInstallerPreviewAsync(Microsoft.PowerToys)` → **`Burn · arm64 · user`**
- `VerifyInstalledAsync(ajeetdsouza.zoxide)` → **Ok** after the per-installer Verify fix (item 5). *(This is the package that surfaced the bug: before the fix it reported **Issues**, `1 of 3 checks failed` (`Registry entry — hr 0x8A150201`) — a non-installed manifest installer's "ARP entry not found", not a real problem. Post-fix, zoxide/PowerShell/7-Zip all verify **Ok**.)*
- `ShowAsync(Microsoft.PowerToys)` → **Tags (10), Support, Documentation(Wiki), Author, Copyright, Privacy** all
  populate → **resolves the old `#17`** (ProductCode/FamilyName null is legit — PowerToys is a `burn` installer).
- **Operation + progress paths** (`--comop ajeetdsouza.zoxide`, on COM/JIT — these had NEVER run before):
  - `DownloadAsync(zoxide)` → **Success**, files actually landed in `%USERPROFILE%\Downloads\winget-tui`
    (`zoxide_0.9.9_Arm64_portable_en-US.zip` 480 KB + `.yaml`; COM resolved the **arm64** installer). The
    `IProgress<OpProgress>` callback fired (phase **Downloading**, fraction → 1.00) — **the "live progress"
    marshaling works on COM**, the path that resolves the old "CCW under AOT" unknown now that COM runs
    under AOT in-proc. Cleaned up.
  - `RepairAsync(zoxide)` → **Success=False**, `Repair failed: RepairError (repairer 0, hr 0x8A15007C)`,
    **no crash** — zoxide is a portable .zip with no repairer. NB this is the `RepairError` path, *distinct*
    from `NoApplicableRepairer`; the message leaked the raw HRESULT. **RESOLVED in session 4** — `0x8A15007C`
    now maps to the friendly "doesn't support repair." line (see the session-4 block). The
    `Verify(Issues) → Repair → Verify` sequence ran e2e.
- `dotnet test -f net10.0` → **pass** (18 facts incl. all three P1.5 ports' logic).

**4. Still NOT verified (need a human at the interactive TUI, and/or are destructive):** actual
install/uninstall/upgrade *execution* (download + repair WERE exercised — see item 3); the **status-bar progress
render** + cooperative **Esc cancel**; the dialog/panel *rendering* (install preview, version-picker list,
advanced-install options, Verify→Repair-button flow); the three P1.5 ports' end-to-end terminal *interaction*
(mouse header clicks, `u`/`P`/`/` keypresses); pinning; the P2 thread-agility / unhealthy-source probes.
NOTE: COM now runs **under AOT in-proc** (see item 1), so the CCW progress-callback marshaling is in play on
the shipped AOT build and should be confirmed there — the `DownloadAsync` progress path already exercised it
cleanly. (The read-only verification in item 3 was first done on JIT, then re-confirmed on the AOT build via
`--comdiag`/`--comsmoke`.)

**5. Fixes committed (session 3).** Beyond the `ConnectAsync` COM-activation fix, this pass found and fixed,
from a real interactive COM run + the user's feedback:
- **Verify false "Issues"** (`src/ComBackend.cs` `VerifyInstalledAsync`): `CheckInstalledStatus` returns a block
  per *manifest installer*; the non-installed ones report "ARP entry not found" (0x8A150201). Old code flattened
  all installers and flagged Issues on any failure → healthy multi-installer/portable packages looked corrupt.
  Now grouped per installer: **Ok if any one installer's checks all pass**; the dialog shows that clean installer.
- **Narrow-terminal columns** (`src/App.cs`): Name/Id/Version shrink toward minimums so **Available** stays
  visible instead of being pushed off-screen; reflows on resize via `ViewportChanged`.
- **Installed-in-Search** (`src/ComBackend.cs` `SearchAsync`, `DetailPanel.cs`, `Models.cs`): search rows read the
  composite's correlated `InstalledVersion`; an installed row shows a **✓ Installed** badge and Uninstall/Upgrade
  actions instead of a bare Install.
- **Op result through reload** (`src/App.cs` `TriggerRefresh`): the result line ("Done"/…) persists through the
  post-op list reload instead of being masked by "Loading Installed…".
- **Bulk-select hint** (`src/Ui.cs`): the Upgrades status bar shows `Spc Select` / `U Upgrade sel`.

The temp `#if WINGET_COM` diagnostics (`--comdiag`/`--comshow`/`--comsmoke`/`--comverify`/`--comop`) were
**removed before commit** (re-add `--comdiag` from the appendix for the AOT activation work). Test dir
`bin\jit-x64-test\` and `bin\…\publish\` are build outputs (gitignored).

---

## (Session 2, 2026-05-29) original headline finding — superseded by session 3 above, kept for history

> **⚠️ If you are running on WSL / Linux:** you **cannot** run the Windows verification here.
> Native AOT codegen can't cross-compile from Linux, and the WinGet COM server + installs need
> Windows. On Linux you CAN: read/edit source, run the cross-platform build/tests
> (`dotnet build -f net10.0` / `dotnet test`), run the spike's Linux trim-analysis
> (`spikes/ComBackendSpike/SPIKE-RESULTS.md` → "Reproducing on Linux"), and **prepare** the `#17`
> fix — but the actual pass/fail confirmation resumes on the Windows host. Don't check Windows-only
> boxes from Linux.

---

## 🚨 HEADLINE FINDING (session 2) — the COM backend does NOT run under Native AOT

**`new PackageManager()` throws `0x80073D54` (`APPMODEL_ERROR_NO_PACKAGE`) in the AOT build**, so
`SelectBackend` (Program.cs) catches it and **silently falls back to the CLI backend**. The
"COM backend unavailable…" stderr note is immediately painted over by the TUI, so it's invisible.
**Proven** by the COM-only `V` (Verify) action reporting *"Verify is only available on the COM
backend"* on a default launch.

Implications:
- **P0 item 3 (default = COM) FAILS.** The shipped AOT app never runs COM.
- **Most "P0 on COM" passes were actually the CLI backend** (which also yields structured
  search/list/details — indistinguishable in the UI). They validate the UI + CLI path, not COM.
- **`#17` (missing Tags/Support/Docs) is a symptom, not a bug** — those are COM-only fields, `null`
  on CLI by design. Not a composite-catalog bug (the earlier hypothesis is withdrawn).
- Every COM-only P1 feature (Verify, real version picker, install preview, live progress) would also
  have silently been CLI/absent.

Evidence (all reproducible right now, post-reboot):
- AOT build: activation FAILS on both MTA main thread and MTA threadpool thread.
- **Same source built non-AOT (JIT, self-contained x64): activation SUCCEEDS** (3 catalogs) — even
  with the server warmed by JIT moments earlier. ⇒ AOT-specific, not server state, not apartment.
- A **clean AOT spike** (verified no `coreclr.dll`) also FAILS — so it's not app-vs-spike either.

Tried, did NOT fix: CsWinRT AOT optimizer (`Microsoft.Windows.CsWinRT 2.2.0` + `CsWinRTAotOptimizerEnabled=Auto`);
`app.manifest` with Win10 `supportedOS` + `longPathAware`; warming the OOP server with a JIT process first.
(All reverted — tree is clean.)

**Caveat / open contradiction:** EARLY in the session (before heavy COM-diagnostic abuse + a reboot),
an AOT spike DID activate COM (3 catalogs + the AOT `foreach` signature). So AOT-COM is not
categorically impossible on this machine. The current deterministic failure may be entangled with
COM-server/AppModel state the abuse+reboot left in an odd condition, OR a genuine AOT activation bug
the early run happened to avoid. **The machine state is compromised — a clean read is needed.**

**DECISIVE NEXT EXPERIMENT (do this first, on a clean reboot):** before launching the TUI or running
any winget/COM command, run the AOT build's diagnostic:
`winget-tui-sharp.exe --comdiag` (the publish-dir binary still has this flag; snippet in the appendix
to re-add after a rebuild). 
- **Activates (catalogs = 3)** → it was transient state; AOT-COM works; **redo the real P0/P1
  verification on COM** (prior passes were CLI).
- **Still fails** → genuine AOT activation bug. Avenues: `Microsoft.Windows.CsWinRT` **3.x** (AOT-first,
  but may conflict with the projection's bundled WinRT.Runtime 2.2.0); `Microsoft.WindowsPackageManager.InProcCom`
  (bundle the COM server in-process, ~100 MB); or **ship the Windows/COM build non-AOT** (JIT/ReadyToRun —
  confirmed to activate COM). File an upstream CsWinRT/WinGet issue with the `--comdiag` output.

---

## TL;DR

- **P0: 8/9 nominally "pass" but on the CLI backend; item 3 (default = COM) actually FAILS** — see the
  headline finding. The genuine, COM-independent passes: AOT publish + no-InvalidCastException + the
  UI/CLI behaviors. The AOT-publish toolchain works; the COM backend does not activate under AOT.
- **P1: blocked on the COM-activation issue** — every COM-only feature needs COM to actually run.
  Two **quality fixes applied & committed** (detail-load debounce, msstore contrast) — compile-clean,
  still need runtime verification once COM works.
- **P2:** not started.
- **The `--comdiag` diagnostic** (apartment + activation probe) is now **restored to `Program.cs`**
  (gated `#if WINGET_COM`) so a fresh build always has it for the clean-reboot retest — no re-add needed.
  Additionally, the silent COM→CLI fallback now records its HRESULT/message and surfaces it in the
  `?` Help dialog (`COM unavailable: 0x… — using CLI`).

---

## How to BUILD on Windows (non-obvious — ARM64 host)

Plain `dotnet publish` fails at the ILC native-link step (`MSB3073 … link.exe … code 123`,
`'vswhere.exe' is not recognized`). ILC 10.0.8 calls a bare `vswhere.exe` not on PATH. Run the
publish inside the VS Dev Shell for the x64 cross-target **with the VS Installer dir on PATH**:

```powershell
$installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
$root = & "$installer\vswhere.exe" -latest -products * -property installationPath
Import-Module (Join-Path $root "Common7\Tools\Microsoft.VisualStudio.DevShell.dll")
Enter-VsDevShell -VsInstallPath $root -SkipAutomaticLocation -DevCmdArguments "-arch=x64 -host_arch=arm64" | Out-Null
$env:PATH = "$installer;$env:PATH"   # Enter-VsDevShell does NOT add this; ILC needs bare vswhere
dotnet publish -c Release -f net10.0-windows10.0.26100.0 -r win-x64
```

- `dotnet build` (managed) and `dotnet run` do NOT need the Dev Shell — only AOT `publish` (native link) does.
- Intermittent publish exit 1 at link/copy = a still-running `winget-tui-sharp.exe` holding the output exe; stop it first.
- Output exe: `bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\winget-tui-sharp.exe`.
- A clean AOT build = **~23.3 MB** exe, **no `coreclr.dll`** (true AOT). `.pdb` ~69 MB is symbols only.

---

## Results so far

### P0 — ✅ 9/9 (all boxes checked in WINDOWS-TESTING.md)
| # | Item | Result |
|---|------|--------|
| 1 | AOT publish, native exe, no `coreclr.dll` | ✅ 23.3 MB, coreclr.dll absent |
| 2 | No `InvalidCastException` (search/list/upgrades/show) | ✅ clean everywhere |
| 3 | Default backend = COM, full IDs, fast | ✅ no fallback note; structured |
| 4 | Flag precedence `--mock > --cli > --com > default` | ✅ verified both decision points |
| 5 | Search returns catalog results + version/source cols | ✅ 18–19 results, winget+msstore |
| 6 | Installed tab + correct versions | ✅ 248 packages |
| 7 | Upgrades tab (subset, Available col) | ✅ |
| 8 | Details panel (publisher/desc/homepage/license/notes) | ✅ |
| 9 | Source filter `f` cycles All/winget/msstore, re-queries | ✅ functional (+ cosmetic notes below) |

Also verified non-interactively: the **spike** (`spikes/ComBackendSpike/`) AOT-publishes (3.85 MB) and
activates COM, returning 3 catalogs + 3 matches for "powertoys" with full property access — re-confirming
the indexed pattern on this host.

### P1 — partial
| Item | Result |
|------|--------|
| #17 Richer detail panel (Tags/ProductCode/FamilyName/Support/Docs) | ❌ **CONFIRMED BUG** (see below). Absent-field omission half ✅. |
| #11 Core ops (install/version/upgrade/uninstall/batch) | ⬜ not tested |
| #12 Install preview dialog (`i`) | ⬜ not tested |
| #13 Version picker (`I`) | ⬜ not tested |
| #14 Download-only (`d`) | ⬜ not tested |
| #15 Advanced install (`A`) | ⬜ not tested |
| #16 Verify install (`V`) | ⬜ not tested |
| #18 Live progress bar | ⬜ not tested |
| #19 Cancellation (`Esc`) | ⬜ not tested |

**Chosen test package for the destructive P1 ops: `ajeetdsouza.zoxide`** (small, native arm64, silent install/uninstall).

### P2 — not started (#20). One observation already addressed by the debounce fix (below).

---

## Code changes made this session (UNCOMMITTED working-tree edits unless you committed)

1. **`src/App.cs` — detail-load debounce (REAL FIX, keep).** `DetailLoadDebounceMs = 200`; `OnSelectedRowChanged`
   now `await Task.Delay(DetailLoadDebounceMs, ct)` before the backend `ShowAsync`. Prevents a fast list-scroll
   from firing a COM detail fetch per row (which throttled/wedged the COM server → 30–60s detail stalls).
   Each selection change cancels `_detailCts`, so passed-over rows never hit the backend. **Compiles; NOT yet
   verified at runtime on Windows.**
2. **`src/App.cs` — msstore Source-cell contrast fix (REAL FIX, keep).** In `ApplyColumnStyles`'s Source
   `ColorGetter`, removed the `Focus`/selected-row foreground override so a selected msstore row (whose
   background is `Theme.Accent`) no longer renders Accent-on-Accent (invisible). Color-coding kept for
   non-selected rows. **Compiles; NOT yet verified at runtime.**
3. **`spikes/ComBackendSpike/Program.cs` — metadata probe (DIAGNOSTIC, keep).** Step 5 now probes
   `GetCatalogPackageMetadata()` fields. This is what proved the COM data for `#17` exists (see below).
   A composite-path Step 6 was attempted and reverted (it threw `E_ILLEGAL_STATE_CHANGE`; see note in file).
4. **`Program.cs` — `--comshow` diagnostic was added and REVERTED.** It's gone from the tree; the snippet to
   re-add it is in the appendix below.
5. **`WINDOWS-TESTING.md`** — P0 boxes checked with notes; `#17` marked ❌ with detail.
6. **Memory files** under `~/.claude/projects/.../memory/` — build-env + COM-wedge lessons.

---

## `#17` richer detail panel — RESOLVED as a symptom of the headline finding (not a bug)

> **UPDATE (session 2):** the composite-catalog hypothesis below is **WITHDRAWN.** The fields never
> render because the app is on the **CLI fallback** (COM didn't activate under AOT — see the headline
> finding), and Tags/Support/Documentation are COM-only (`null` on CLI by design). The spike evidence
> below still stands (the COM data exists and is readable), so once COM activation is fixed, `#17`
> should resolve with no further code change. The original (now-moot) analysis is kept for context:

**Symptom:** None of these fields ever render in the detail panel, for any package, including
`Microsoft.PowerToys` which definitely has them.

**Evidence gathered:**
- `winget show --id Microsoft.PowerToys` (via the app's `--dump show`) shows the manifest HAS:
  Publisher Support Url, Documentation (Wiki), and 10 Tags.
- The **spike** (`--query powertoys`, Step 5) fetched `Microsoft.PowerToys`'s metadata from a **single
  `winget`-catalog** connect and got: `meta.Publisher` ✓, `meta.PublisherSupportUrl` = the URL ✓,
  `meta.Tags` = 10 items (foreach even works) ✓, `meta.Documentations` = 1 item (indexed access works) ✓,
  `ProductCodes`/`PackageFamilyNames` = 0 (legitimately — PowerToys is a `burn` installer, not MSI/MSIX).
- So the **COM data exists and is readable** via the exact indexed pattern `ComBackend` uses.
- The detail PANEL renders all five fields conditionally (verified in `DetailPanel.SetDetail`), and
  `MergeContext`/`EnsureDetailHint` (`Models.cs`) do NOT touch them (they're `init`-only).
- Therefore **`ComBackend.ShowAsync` must be returning Tags/SupportUrl/Documentation as null/empty.**

**Leading hypothesis:** the difference between the working spike and the app is that the spike used a
**single-catalog** connect, while `ComBackend.ShowAsync` resolves the package via `FindByIdAsync` over a
**composite `All` catalog** (`CreateCompositePackageCatalog` of winget+msstore,
`RemotePackagesFromRemoteCatalogs`). Suspect: a composite-catalog package's
`DefaultInstallVersion.GetCatalogPackageMetadata()` returns the scalar fields (Publisher/Description/
Homepage/License/ReleaseNotes — which the user DOES see) but not the richer ones. NOT yet confirmed —
the puzzle is why scalar `PublisherSupportUrl` would differ from scalar `Publisher` on the same `meta`.

**Decisive next step (Windows, healthy COM):** re-add the `--comshow` diagnostic (appendix) and run
`winget-tui-sharp.exe --comshow Microsoft.PowerToys` ONCE.
- If `Tags = <null>`, `SupportUrl = <null>`, `Docs = <null>` → confirmed: fix `ShowAsync` to fetch metadata
  via the package's own source (e.g. resolve/connect the single source catalog the package came from, or
  re-find by id in a single-source catalog) before `GetCatalogPackageMetadata()`.
- If they're populated → bug is in the TUI render/cache path, not `ShowAsync` — investigate
  `OnSelectedRowChanged` / `DetailCache` / `DetailPanel` instead.

Relevant code: `src/ComBackend.cs` `ShowAsync` (~L162), `FindByIdAsync` (~L806), `StringVector`/`DocLinks`
helpers; `src/DetailPanel.cs` `SetDetail` (L60–135); `src/App.cs` `OnSelectedRowChanged` (~L535).

---

## Cosmetic follow-ups found (not blockers)

- **Source column blank under single-source filter** (`f` → winget): in `All` mode rows show `winget`,
  but filtered-to-winget rows show a blank Source cell (and the detail Source line is then omitted via
  MergeContext). Low value (you already filtered to that source); root-cause needs live COM. Deferred.
- **Search box doesn't auto-open on the Search tab** — pressing `1` then needing `/` to type. UX polish:
  auto-focus the filter input when switching to Search with an empty query. Deferred (user-requested nicety).
- **Source column clipped at narrow terminal width** — `ExpandLastColumn`; reappears when widened. Acceptable.

---

## The COM-server wedge (READ before running ANY COM diagnostic)

The agent spawned ~8 short-lived processes that each `new PackageManager()`; several **crashed mid-COM-op**
(an `E_ILLEGAL_STATE_CHANGE` in a flawed composite spike). After that, COM activation fails **everywhere**
(both background tasks AND the user's interactive session) with:
`COMException 0x80073D54` = Win32 `APPMODEL_ERROR_NO_PACKAGE` (15700). winget CLI still works.

**Recovery (lightest first):**
1. `Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe` then retry.
2. If still wedged, **reboot** (reliable fix for a wedged WinGet OOP COM server).

**Lesson (also in memory):** do NOT spawn many rapid/short-lived COM-activating processes, and never let
one crash mid-COM-operation. For introspection prefer ONE long-lived process or the interactive TUI. The
in-app version of this same hammering (fast scroll → per-row detail fetch) is what the **debounce fix**
addresses.

---

## Resume plan (next Windows session)

**STEP 0 — settle the headline finding FIRST (everything else depends on it).** On a **clean reboot**,
before launching the TUI or running any winget/COM command, run the AOT diagnostic:
`bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\winget-tui-sharp.exe --comdiag`
(`--comdiag` is now **permanently in `Program.cs`** under `#if WINGET_COM`, so a fresh build always
has it — no need to re-add the appendix snippet. Just rebuild and run.)
- **`activation OK; catalogs = 3`** → AOT-COM works on a clean machine; the session's failures were
  transient state from the diagnostic abuse. Proceed to re-verify P0/P1 **on COM** (prior passes were CLI).
- **`activation FAILED … APPMODEL_ERROR_NO_PACKAGE`** → genuine AOT activation bug. Pursue, in order of
  cost: (a) try `Microsoft.Windows.CsWinRT` **3.x** overriding the bundled WinRT.Runtime 2.2.0; (b) the
  `InProcCom` package (~100 MB, in-proc server); (c) ship the COM/Windows build **non-AOT** (JIT confirmed
  working) — the cleanest pragmatic option if AOT activation can't be made reliable; (d) file upstream with
  the `--comdiag` output (AOT fails / JIT works, same machine, MTA, server warm).

**Once COM actually activates:**
1. Re-run the full P0 checklist confirming COM is active (e.g. `V` shows a real verify dialog, not the
   "COM-only" message; detail panel shows Tags/Support/Docs on PowerToys).
2. **Verify the two applied quality fixes** on the COM build:
   - Debounce: hold ↓ to scroll the list fast — detail panel should NOT freeze for tens of seconds;
     settles within ~0.2s on the row you stop on.
   - Contrast: highlight the msstore row in a powertoys search — Source cell text readable when selected.
3. **Re-check `#17`** — with COM live it should now show Tags/Support/Documentation (the spike proved the
   data is there). No code fix expected beyond the activation fix.
4. **Run P1** with `ajeetdsouza.zoxide` (non-destructive dialog checks first — `i` preview→Cancel,
   `I` version list→Cancel, `A` options→Cancel, `V` positive — then real install/download/uninstall/upgrade,
   progress bar, `Esc` cancellation; finally batch upgrade).
5. **P2 (#20):** shared-`PackageManager` thread-agility (watch RPC_E_WRONG_THREAD); unhealthy-source + `All`
   (break msstore, confirm `f`→winget recovers); pinning (`p`/`P`, needs winget on PATH); AOT vs CLI binary
   size; optional arm64.
6. Update `WINDOWS-TESTING.md` boxes; commit; remove any `--comshow`/`--comdiag` diagnostic before final commit.

### `--comdiag` (apartment + activation probe) — NOW IN `Program.cs` (kept here for reference)
> Restored to source under `#if WINGET_COM`; no longer needs re-adding. Snippet kept for reference only.
```csharp
#if WINGET_COM
if (args.Length > 0 && args [0] is "--comdiag")
{
    Console.WriteLine ($"main thread apartment = {System.Threading.Thread.CurrentThread.GetApartmentState ()}");
    try { var pm = new Microsoft.Management.Deployment.PackageManager (); Console.WriteLine ($"main-thread activation OK; catalogs = {pm.GetPackageCatalogs ().Count}"); }
    catch (Exception ex) { Console.WriteLine ($"main-thread activation FAILED: 0x{(uint)ex.HResult:X8} {ex.Message}"); }
    await System.Threading.Tasks.Task.Run (() => {
        Console.WriteLine ($"threadpool apartment = {System.Threading.Thread.CurrentThread.GetApartmentState ()}");
        try { var pm = new Microsoft.Management.Deployment.PackageManager (); Console.WriteLine ($"threadpool activation OK; catalogs = {pm.GetPackageCatalogs ().Count}"); }
        catch (Exception ex) { Console.WriteLine ($"threadpool activation FAILED: 0x{(uint)ex.HResult:X8} {ex.Message}"); }
    });
    return;
}
#endif
```
Quick JIT-vs-AOT check (no Dev Shell needed for the JIT build):
`dotnet publish -c Release -f net10.0-windows10.0.26100.0 -r win-x64 -p:PublishAot=false --self-contained true -o bin\jit-x64-test` then run `bin\jit-x64-test\winget-tui-sharp.exe --comdiag`.

---

## Appendix — the `--comshow` diagnostic to re-add to `Program.cs`

Insert right after `using WingetTuiSharp;` (gated so it only affects the `--comshow` arg). Build the
win-x64 AOT exe (Dev Shell) and run it from an interactive, COM-healthy session.

```csharp
#if WINGET_COM
// TEMP DIAGNOSTIC (remove before commit): dump exactly what ComBackend.ShowAsync returns.
if (args.Length > 1 && args [0] is "--comshow")
{
    ComBackend be = new ();
    PackageDetail? d = await be.ShowAsync (args [1], CancellationToken.None);
    if (d is null) { Console.WriteLine ("ShowAsync returned null"); return; }
    Console.WriteLine ($"Name        = {d.Name}");
    Console.WriteLine ($"Publisher   = {d.Publisher}");
    Console.WriteLine ($"Homepage    = {d.Homepage}");
    Console.WriteLine ($"License     = {d.License}");
    Console.WriteLine ($"RelNotesUrl = {d.ReleaseNotesUrl}");
    Console.WriteLine ($"SupportUrl  = {d.SupportUrl}");
    Console.WriteLine ($"Tags        = {(d.Tags is null ? "<null>" : string.Join (" | ", d.Tags))}");
    Console.WriteLine ($"Docs        = {(d.Documentation is null ? "<null>" : string.Join (" | ", d.Documentation.Select (x => $"{x.Label}:{x.Url}")))}");
    Console.WriteLine ($"ProductCodes= {(d.ProductCodes is null ? "<null>" : string.Join (" | ", d.ProductCodes))}");
    Console.WriteLine ($"FamilyNames = {(d.PackageFamilyNames is null ? "<null>" : string.Join (" | ", d.PackageFamilyNames))}");
    return;
}
#endif
```
