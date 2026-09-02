<!--
Quick PR template. Keep entries terse — bullet points are fine. Delete sections that
don't apply. The CI workflow (build + 245+ tests + mock-run smoke on Windows) must pass
before merge.
-->

## Summary

<!-- 1-3 sentences. What does this PR change and why? -->

## Type

<!-- Pick one. Bug fixes against upstream parity are the most common kind. -->

- [ ] Bug fix — closes a gap against upstream `shanselman/winget-tui` behavior
- [ ] Parser hardening — new edge case in winget output
- [ ] Terminal.Gui version bump or compatibility fix
- [ ] Docs / build / CI only

## Verification

<!-- How did you confirm this works? -->

- [ ] `dotnet test --project tests/WingetTuiSharp.Tests.csproj` — all 245+ tests pass
- [ ] Added test(s) for the changed behavior (specify which below if so)
- [ ] Ran `dotnet run -- --mock` (UI iteration sanity)
- [ ] Ran AOT publish on Windows: `dotnet publish -c Release -r win-x64`
- [ ] Manual smoke on real `winget` (Windows host)

## Upstream parity

<!-- For parser / behavior changes, link the upstream Rust source that motivated this.
     Skip if the change is purely additive (e.g. a new Terminal.Gui-only test). -->

Upstream reference: `src/cli_backend.rs::<function>`

## Notes

<!-- Anything reviewers should know: tradeoffs, follow-ups, screenshots for UI changes. -->
