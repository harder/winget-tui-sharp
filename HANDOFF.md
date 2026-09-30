# Handoff — Windows COM-backend verification

**Status (2026-09-30):** P0 (COM activation under Native AOT) remains verified, including a native
ARM64 publish and live COM startup. The compact `winget pin list` parser failure with winget
1.30.140-preview is fixed and the pin/unpin flow passed on the native ARM64 COM build. The checklist
still records an ARM64/x64 advanced-install preview mismatch. Rapid COM navigation passed. The remaining interactive parity checks, unhealthy-
source recovery, and a conclusive CLI cancellation test are still open; the checklist has the exact
results and cleanup from this session.

The current native ARM64 COM install path also has a newly reproduced `0xC0000005` crash during
zoxide installation (WinGet COM 1.29.380). The crash reproduces from an unmodified-HEAD native
AOT build, so the compact-header work did not introduce it. Direct WinGet and the app's CLI backend
installed and uninstalled zoxide successfully; it is absent after cleanup. See the open P1 regression item in
`WINDOWS-TESTING.md` before shipping the COM mutation path.

This file exists for the non-obvious findings from getting P0/P1 there in the first place — the fix
mechanism, the dead ends, and a couple of gotchas worth not re-discovering. It is not a task list; for
what's still open, see `WINDOWS-TESTING.md`.

---

## Gotchas worth remembering

**In-proc COM is required under Native AOT — out-of-process activation does not work.** The manual-
activation shim (`winrtact.dll` / `WinGetServerManualActivation_CreateInstance`) was dropped from
`ComInterop ≥ 1.10.x` ([winget-cli#5459](https://github.com/microsoft/winget-cli/issues/5459),
[#4839](https://github.com/microsoft/winget-cli/issues/4839)); AOT has no CsWinRT runtime fallback to
reach a registered OOP server (JIT does, which is why a JIT build activates fine but AOT doesn't without
the fix). The fix — bundling `Microsoft.WindowsPackageManager.InProcCom` + a reg-free `app.manifest`
routing activation to it — is already in `WingetTuiSharp.csproj`; see `WINDOWS-TESTING.md`'s P0 section
for the full mechanism if this ever needs revisiting.

**Approaches that did NOT work for the AOT-activation problem (don't retry):** the CsWinRT AOT optimizer
(2.2.0, or `Microsoft.Windows.CsWinRT 3.0.0-preview` — breaks at its own `cswinrt.exe` codegen + conflicts
with the projection's bundled WinRT.Runtime); a bare `app.manifest` with only `supportedOS`/
`longPathAware` (no in-proc routing); warming the OOP server first.

**Never set `AcceptSourceAgreements` on a composite catalog reference** (`ComBackend.ConnectAsync`) — it
throws `E_ILLEGAL_STATE_CHANGE` on `IPackageCatalogReference3`. Set it on each *source* ref before
compositing instead (see the comment in `ConnectAsync`). This bug was latent for a long time because AOT
always fell back to CLI, so the COM path never actually ran until the in-proc fix landed — it broke every
COM search/list/detail the instant COM activated.

---

## Session history (condensed)

**Session 4 (2026-06-27)** — agent-driven interactive TUI pass on the published AOT/COM build (GUI
automation driving the live app, not just diagnostics). Confirmed the P0/P1 items now checked in
`WINDOWS-TESTING.md` actually render and behave correctly on screen, not just at the data layer. Also
fixed the Repair-failure message for installers with no repair support (`RepairFailureMessage` in
`src/ComBackend.cs`) — see git history for the fix itself.

**Session 3 (2026-06-13)** — resolved the P0 headline finding (COM wasn't activating under Native AOT) via
the in-proc-server fix described above, and found + fixed the composite-`AcceptSourceAgreements` bug also
described above. Read-only diagnostic verification only (no human at the TUI) — session 4 covered the
interactive-rendering gap this left.

**2026-07-16** — full manual P1 pass on Windows by the user; all P1 items in `WINDOWS-TESTING.md` now
checked and verified end-to-end.
