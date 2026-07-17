# Handoff — Windows COM-backend verification

**Status (2026-07-16):** P0 (COM activates under Native AOT) and P1 (COM-backend operations) are both
resolved and fully verified — P0 via read-only diagnostics + interactive TUI passes (sessions 3–4 below),
P1 via a complete manual pass on Windows. Only **P2** (real-Windows review-flagged concerns: thread-
agility, unhealthy-source recovery, pinning, win-arm64, accumulated Terminal.Gui/WinGet-COM version
bumps) and two **P1.5** upstream-parity items (mouse click-to-sort, truncated-id fallback) remain open —
see `WINDOWS-TESTING.md`, which has a self-contained runbook for testing those with an agent driving the
interactive TUI on Windows.

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
