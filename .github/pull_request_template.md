<!--
Quick PR template. Keep entries terse — bullet points are fine. Delete sections that
don't apply. The CI workflow (build + 245+ tests + mock-run smoke on Windows) must pass
before merge.
-->

## Summary

<!-- 1-3 sentences. What does this PR change and why? -->

## Type

<!-- Pick one or more. -->

- [ ] Bug fix
- [ ] Parser hardening — new edge case in winget output
- [ ] Terminal.Gui version bump or compatibility fix
- [ ] Docs / build / CI only

## Verification

<!-- How did you confirm this works? -->

- [ ] `dotnet test --project tests/WinGetScout.Tests.csproj` — all 245+ tests pass
- [ ] Added test(s) for the changed behavior (specify which below if so)
- [ ] Ran `dotnet run -- --mock` (UI iteration sanity)
- [ ] Ran AOT publish on Windows: `dotnet publish -c Release -r win-x64`
- [ ] Manual smoke on real `winget` (Windows host)

## Notes

<!-- Anything reviewers should know: tradeoffs, follow-ups, screenshots for UI changes. -->
