# Handoff — Windows COM-backend verification

**Latest:** session 4 · 2026-06-27 · branch `main` (PR #11 merged) · Windows 11 **ARM64**, App Installer `1.29.250.0`.
**Earlier:** session 3 · 2026-06-13 · branch `main`.

Session 3 was fully autonomous (no human at the TUI); it resolved the headline "does COM activate under
Native AOT" question and exercised the COM backend via read-only diagnostics rather than the interactive
flows. **Session 4 was autonomous *with* the agent driving the interactive TUI itself** (GUI automation on
the AOT build), closing most of the interactive-render gaps — see the session-4 block immediately below.

Sessions 1–2 (human-in-the-loop debugging of the original "COM doesn't activate under AOT" bug, before the
session-3 fix) have been removed from this file now that the fix is stable and verified across two further
sessions — see `WINDOWS-TESTING.md`'s P0 section for the condensed resolution summary. Remaining open work
lives in `WINDOWS-TESTING.md`'s unchecked boxes, not here.

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

The temp `#if WINGET_COM` diagnostics (`--comshow`/`--comsmoke`/`--comverify`/`--comop`) were
**removed before commit**; `--comdiag` was kept permanently in `Program.cs` (see `WINDOWS-TESTING.md`).
Test dir `bin\jit-x64-test\` and `bin\…\publish\` are build outputs (gitignored).
