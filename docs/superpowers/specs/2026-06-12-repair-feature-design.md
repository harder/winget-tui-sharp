# Repair feature — design

- **Date:** 2026-06-12
- **Status:** Approved (design); pending implementation plan
- **Branch:** `feat/com-backend`

## Summary

Add a **Repair** action that re-runs a package's installer in repair mode to fix a
damaged/corrupt install, backed by the WinGet COM `PackageManager.RepairPackageAsync`
API (contract 11). Repair complements the existing **Verify** action: Verify *detects*
a broken install (`CheckInstalledStatus`); Repair *fixes* it. Today the app only does
the detection half.

## Motivation

`RepairPackageAsync` is the one genuinely *missing capability* in our COM surface (the
other gaps are enrichments). It pairs naturally with Verify — when Verify reports the
`Issues` outcome, the obvious next step is to repair — and it reuses the app's existing
operation/progress infrastructure almost entirely.

## Goals

- A standalone **`R`** action to repair the selected installed package.
- When **Verify** finds issues, offer **Repair** directly from its result dialog.
- Determinate progress, Esc-to-cancel, and status reporting consistent with
  install/upgrade/uninstall.

## Non-goals

- **No advanced repair-options dialog.** Repair runs as a plain confirm → silent
  repair. (Repair exposes far fewer meaningful knobs than install; this matches the
  Upgrade/Uninstall confirm-then-run pattern.)
- **No CLI repair.** Repair is **COM-only**, like Verify. The CLI backend reports it
  unavailable; the mock synthesizes it for Linux dev iteration. (We deliberately do
  *not* shell `winget repair`, even though that command exists.)
- No source/scope selection, no batch repair, no "repair all".

## UX design

### Trigger

Two entry points, sharing one `RunRepair(Package)` core so confirmation isn't doubled:

1. **Standalone `R` key** — handled in `App.OnKeyDown`, active only in **Installed**
   and **Upgrades** modes (repair acts on installed packages; not offered in Search).
   `R` is currently unbound (`r` is refresh). Calls `AskRepair(p)`, which gates on
   `CanRepair`, shows the **confirm** dialog, then `RunRepair(p)`.
2. **Verify → Repair offer** — `App.ShowVerifyResult` currently shows the verify result
   via `MessageBox.Query(..., "_OK")`. When the outcome is `VerifyOutcome.Issues`, it
   instead offers `"_Repair"` and `"_Close"`. Choosing Repair calls `RunRepair(p)`
   **directly, with no second confirm** — the user is already in a dialog reporting the
   broken install and explicitly clicked Repair, which is itself the confirmation. This
   path is only reachable on COM (Verify is COM-only), so the `CanRepair` gate is moot.

This splits responsibilities cleanly:
- `AskRepair(p)` = `CanRepair` gate (neutral message if false) + confirm + `RunRepair(p)`.
- `RunRepair(p)` = `GuardTruncatedId(p, "repair")` + `RunOperation(...)`.

### Availability gate

`AskRepair` checks `IBackend.CanRepair` **before** confirming. If false, it shows a
neutral (non-error) status message *"Repair is only available on the COM backend."* —
mirroring exactly how `V` degrades on the CLI backend. This means the `R` action and the
detail-panel "Actions" entry are always visible, but gracefully no-op with an
explanation on backends that can't repair.

### Confirm + run

On a repair-capable backend, `AskRepair(p)`:

1. Gate on `CanRepair` (neutral message if false).
2. `Confirm("Repair", $"Repair {p.Name}? This re-runs the installer's repair to fix a
   damaged install.")`.
3. `RunRepair(p)`.

`RunRepair(p)`:

1. `GuardTruncatedId(p, "repair")` — a winget-truncated id can't be matched, same guard
   as install/uninstall.
2. `RunOperation($"Repairing {p.Name}", (prog, ct) => _state.Backend.RepairAsync(p.Id, prog, ct))`.

`RunOperation` already provides: the one-operation-at-a-time gate, `_opCts` Esc-to-cancel,
the determinate status-bar progress bar, detail-cache invalidation on success, and the
post-op list refresh. Repair needs no new orchestration.

### Detail panel & help

- `DetailPanel.SetDetail` adds `AddAction("R", "Repair install")` after the existing
  `V Verify install` entry, in the **Installed** and **Upgrades** action lists.
- `HelpDialog.HelpText` gains a line under Actions: `R   Repair install (re-run repair)`.
- Status-bar hint pairs are left unchanged (curated/space-limited; Repair is discoverable
  via the detail panel and help).

## Backend contract (`IBackend`)

Two additions:

```csharp
// True when this backend can repair an installed package (COM only). The UI gates the
// Repair action on this and shows a neutral "only available on the COM backend" message
// otherwise — mirroring how Verify degrades.
bool CanRepair { get; }

// Repair an installed package by re-running its installer in repair mode. Reports progress
// like install/upgrade. Only meaningful when CanRepair is true.
Task<OpResult> RepairAsync (string id, IProgress<OpProgress>? progress, CancellationToken ct);
```

## Models

- `OperationKind.Repair` — new enum member.
- `OpPhase.Repairing` — new enum member; `OpProgress.Label` maps it to `"Repairing"`.

## COM implementation (`ComBackend`)

- `CanRepair => true`.
- `RepairAsync`:
  1. `FindByIdAsync(id, null, installedContext: true)` — installed context so the package
     carries its installed version (what repair targets). Null → not installed → clean
     failure `"Installed package '{id}' not found."`.
  2. `RepairOptions options = new () { PackageRepairMode = PackageRepairMode.Silent };`
  3. `var asyncOp = _pm.RepairPackageAsync(pkg, options);`
     `asyncOp.Progress = (_, p) => progress?.Report(MapRepair(p));`
     `RepairResult result = await asyncOp.AsTask(ct);`
  4. Map `result.Status`:
     - `RepairResultStatus.Ok` → success, append `" (reboot required)"` when
       `result.RebootRequired`.
     - `RepairResultStatus.NoApplicableRepairer` → friendly failure *"{name} doesn't
       support repair."*
     - otherwise → `$"Repair failed: {result.Status} (repairer {result.RepairerErrorCode},
       hr 0x{HResultOf(result.ExtendedErrorCode):X8})"`, consistent with `DescribeInstall`.
- `MapRepair(RepairProgress p)`:
  - `Queued → OpPhase.Queued`, `Repairing → OpPhase.Repairing`,
    `PostRepair → OpPhase.Finalizing`, `Finished → OpPhase.Done`, default → `Repairing`.
  - fraction: `p.State == Finished ? 1.0 : p.RepairCompletionProgress`.

## CLI implementation (`CliBackend`)

- `CanRepair => false`.
- `RepairAsync` returns a safety-net failure `OpResult` *"Repair is only available on the
  COM backend."* — never reached through the UI because the `CanRepair` gate stops first,
  but implemented to satisfy the interface.

## Mock implementation (`MockBackend`)

- `CanRepair => true` (so the flow is exercisable on Linux, as Verify's mock is).
- `RepairAsync` synthesizes a `Repairing` progress ramp (reusing/paralleling
  `SimulateProgressAsync`) and returns success `"[mock] Repaired {id}"`.

## Error handling & edge cases

| Case | Behavior |
| --- | --- |
| Backend can't repair (CLI) | Neutral status: "Repair is only available on the COM backend." (no red error) |
| Truncated id | `GuardTruncatedId` blocks with an explanatory message |
| Package not installed / not found | Failure OpResult: "Installed package '{id}' not found." |
| Package has no repairer | Friendly failure: "{name} doesn't support repair." |
| Esc during repair | Cooperative cancel via shared `_opCts`; "Cancelled". (Per IDL, cancel during the Repairing phase may not roll back — same caveat as Installing.) |
| Reboot required | "(reboot required)" appended to the success message |
| Second op while one runs | Ignored by `RunOperation`'s one-op gate (unchanged) |

## Files touched

- `src/Models.cs` — `OperationKind.Repair`, `OpPhase.Repairing` + `Label`.
- `src/Backend.cs` — `CanRepair`, `RepairAsync` on `IBackend`.
- `src/ComBackend.cs` — `CanRepair`, `RepairAsync`, `MapRepair`.
- `src/CliBackend.cs` — `CanRepair`, `RepairAsync` (unavailable).
- `src/MockBackend.cs` — `CanRepair`, `RepairAsync` (synth).
- `src/App.cs` — `R` key → `AskRepair`; `AskRepair`; Verify→Repair offer in
  `ShowVerifyResult`.
- `src/DetailPanel.cs` — `R Repair install` action (Installed + Upgrades).
- `src/Ui.cs` — Help text line.
- `tests/` — mock repair success path; `CliBackend.CanRepair == false`.
- `WINDOWS-TESTING.md` — COM repair verification items.

## Testing

- **Unit (Linux):** mock `CanRepair == true` and `RepairAsync` returns a successful
  `OpResult` after a progress ramp; assert `CliBackend.CanRepair == false`. If the existing
  `AppBehaviorTests` drive operation flows, add a repair-flow case there.
- **Windows (manual, COM):** new `WINDOWS-TESTING.md` items —
  - `R` on a healthy installed package repairs it; progress bar advances through a
    **Repairing** phase; "Done" (or "(reboot required)").
  - Break an install, `V` → **Issues** → the dialog offers **Repair**; choosing it repairs.
  - A package with no repairer reports the friendly "doesn't support repair" message.
  - Esc during a repair cancels cooperatively.
  - On the CLI fallback backend, `R` shows "Repair is only available on the COM backend."

## Open questions

None. The three design choices that needed sign-off — simple confirm (no advanced
options), the `R` keybinding, and Installed+Upgrades-only scope — are all confirmed.
