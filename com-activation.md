# COM activation under Native AOT

How `winget-tui-sharp` reaches the WinGet COM API, why it's harder than it looks, and the options
for distributing it. This is the architectural companion to [code-signing.md](code-signing.md)
(which covers the signing/packaging mechanics referenced below).

## The problem in one paragraph

The WinGet COM API (`Microsoft.Management.Deployment.PackageManager`) is served by an
**out-of-process server** (`WindowsPackageManagerServer.exe`) that ships in-box with **App
Installer** — so the engine is already on every Windows 10/11 machine. The catch: `new
PackageManager()` is a **WinRT activation**, which requires the calling process to have **package
identity**. An unpackaged process has none, so activation throws `0x80073D54`
(`APPMODEL_ERROR_NO_PACKAGE`). Under JIT this is normally bridged (the old `winrtact.dll`
manual-activation shim, or CsWinRT's runtime fallback); under **Native AOT** none of those bridges
exist, so the shipped AOT build can't reach the out-of-process server at all.

## What the spike measured

All rows tested on a Windows 11 ARM64 host against the real installed winget server.

| Build | Package identity | Ships beyond the exe | `new PackageManager()` | classic `CoCreateInstance` (OOP) |
|---|---|---|---|---|
| **JIT**, portable | none | nothing | — | ✅ works (this is the UniGetUI path) |
| **AOT**, portable | none | nothing | ❌ `0x80073D54` | ❌ `0x80073D54` |
| **AOT**, portable | none | bare `app.manifest` (supportedOS only) | — | ❌ `0x80073D54` |
| **AOT**, packaged | ✅ | nothing | ❌ `0x8000000F` | ❌ `0x8000000F` |
| **AOT**, packaged | ✅ | **+ 61 KB `Microsoft.Management.Deployment.winmd`** | ✅ works | ✅ works |
| **AOT**, portable (current) | none | **+ ~7 MB in-process engine** | ✅ (in-proc) | n/a |

Two non-obvious conclusions:

1. **The "single portable exe + out-of-process COM, ship nothing" idea is impossible under AOT.**
   Even the classic `CoCreateInstance` trick that UniGetUI uses (`WindowsPackageManagerStandardFactory`
   with `CLSCTX_ALLOW_LOWER_TRUST_REGISTRATION`) fails with the *same* `0x80073D54` — because
   UniGetUI is JIT/ReadyToRun, and **no shipping app has ever done winget-COM-under-AOT**. So
   shipping the in-process engine in the portable build is genuinely required, not an oversight.

2. **Package identity rescues AOT — cheaply.** Identity clears `0x80073D54`, surfaces a second gate
   (`0x8000000F = RO_E_METADATA_NAME_NOT_FOUND` — AOT carries no WinRT type metadata), and dropping
   the **61 KB `.winmd`** into the package clears that too. A packaged AOT exe then talks to the
   already-installed server, shipping ~61 KB of metadata instead of the ~7 MB engine.

## The two viable activation strategies (and the dead ends)

**Viable**

- **In-process server** (current portable build). Bundle `Microsoft.WindowsPackageManager.InProcCom`
  (native `WindowsPackageManager.dll` ~7 MB + `Microsoft.Management.Deployment.InProc.dll`) and a
  registration-free WinRT `app.manifest` that routes the activatable classes in-process. No identity,
  no out-of-process server, no signing required → a true portable exe (+ engine). Also sidesteps the
  out-of-process server-wedge failure mode entirely. Cost: ~7 MB and you own keeping the engine current.
- **Package identity** (MSIX / sparse package). Give the app identity and ship the 61 KB `.winmd`;
  `new PackageManager()` (or `CoCreateInstance`) then reaches the in-box out-of-process server. Tiny
  payload, but requires an MSIX + a signing cert, and reintroduces the out-of-process dependency
  (server health, cross-process progress-callback marshaling — which does work; the download path
  exercised it).

**Dead ends (don't retry)**

- Classic `CoCreateInstance` from an unpackaged AOT process — `0x80073D54`.
- A bare `app.manifest` (supportedOS/longPathAware) without in-proc routing or identity — no effect.
- Identity without the `.winmd` — `0x8000000F`.
- `Microsoft.Windows.CsWinRT 3.x` AOT-first projection — breaks at its own codegen.

## How this maps to distribution (the two-download model)

- **Download A — portable `.exe`, CLI backend.** AOT single file, no install, no signing, no COM.
  Lowest friction; the obvious default.
- **Download B — signed MSIX, COM backend.** One installed app. Two internal flavors:
  - **B-identity** — AOT exe + 61 KB `.winmd`, package identity → out-of-process COM. Minimal payload;
    needs the signing cert; leans on the machine's winget server. Built via
    `WingetComMode=Identity` (see below).
  - **B-inproc** — AOT exe + the 7 MB in-process engine inside the MSIX; identity not required for
    activation, so the MSIX is purely for signing/clean-install/SmartScreen. Bigger but self-contained
    and dodges the server-wedge bug. Built via the default `WingetComMode=InProc`.

### The `WingetComMode` build switch

The project selects the activation strategy at build time (Windows TFM only):

- `WingetComMode=InProc` (default) — references `InProcCom` + the in-proc `app.manifest`. Portable
  build; what the zip/standalone exe ships.
- `WingetComMode=Identity` — drops `InProcCom` and the in-proc manifest routing, copies the `.winmd`
  next to the exe, and relies on package identity. What the MSIX (download B-identity) ships.

```powershell
# portable / in-proc (default)
dotnet publish -c Release -f net10.0-windows10.0.26100.0 -r win-x64

# identity build for the MSIX
dotnet publish -c Release -f net10.0-windows10.0.26100.0 -r win-x64 -p:WingetComMode=Identity
```

## Recommendation

Ship **A (portable CLI exe)** as the default and **B-identity (signed MSIX, winmd-only, OOP COM)** as
the full-COM experience — the cleanest realization of "signed installer → single app → COM," validated
by the spike. Keep the in-proc zip as a no-install fallback if a third option is wanted. Packaging and
signing mechanics, costs, and the release-workflow wiring live in [code-signing.md](code-signing.md);
the MSIX manifest + build script live under [`packaging/`](packaging/).
