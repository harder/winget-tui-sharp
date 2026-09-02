# Contributing

winget-tui-sharp is a daily-usable winget TUI and an ongoing benchmark of Terminal.Gui v2 against Ratatui via its port of [shanselman/winget-tui](https://github.com/shanselman/winget-tui). Contributions that close parity gaps against upstream, fix bugs in the existing surface, sharpen the test suite, surface Terminal.Gui findings, or add new features beyond what upstream does are all welcome.

## Dev setup

```bash
git clone https://github.com/harder/winget-tui-sharp
cd winget-tui-sharp
dotnet test --project tests/WingetTuiSharp.Tests.csproj   # 245+ tests
dotnet run -- --mock                       # UI iteration, any host
```

Building the actual AOT binary requires a **Windows host** with Visual Studio Build Tools (C++ workload). See [README § Building](README.md#building).

## Working on a change

1. **Add a test first** when the change touches parser behavior, model semantics, or anything covered by `tests/ParserTests.cs`. Every existing test is anchored to a real bug — please keep that pattern.
2. **Compare against upstream** when changing winget parsing logic. The Rust source at <https://github.com/shanselman/winget-tui/tree/main/src> is the behavioral spec. Note divergences in [feature-gaps.md](feature-gaps.md).
3. **Run the suite** before opening a PR: `dotnet test --project tests/WingetTuiSharp.Tests.csproj`.
4. **Check in before large new-feature PRs.** New features beyond upstream parity are welcome, but open an issue or discuss the approach first for anything sizable so the design lands before the code does. Packaging, distribution, and signing are being actively pursued (see [code-signing.md](code-signing.md)) — coordinate there rather than opening a competing effort.

The repository selects Microsoft.Testing.Platform in `global.json`. Pass the project with
`--project` as shown above; the older positional `dotnet test tests/...csproj` form is not
accepted by this runner. IDE test discovery likewise requires Microsoft.Testing.Platform support.

## Filing issues

- **Bugs**: include the failing scenario, OS + architecture (x64 vs arm64), and where possible a `--dump` trace (e.g. `winget-tui-sharp --dump search vscode > dump.txt`).
- **Parity gaps**: link to the upstream Rust code that does it differently.
- **Terminal.Gui regressions**: include the version you upgraded from and to. The Terminal.Gui compatibility tests in `tests/ParserTests.cs` should ideally catch these — if a regression slipped through, an extra test for it is highly welcome.

## Code style

Mostly follow standard C# / .NET conventions. The project loosely mirrors [Terminal.Gui's style](https://github.com/gui-cs/Terminal.Gui/blob/develop/.claude/rules/formatting.md) — notable points:

- Space before parens: `Method ()`, `array [i]`, `if (...)`.
- Braces on next line (Allman style).
- `var` only for built-in types (`int`, `string`, `bool`, etc.). Explicit type for everything else.
- Blank line before `return` / `break` / `continue`, after control blocks.

These aren't CI-enforced; just match the surrounding code.

## License

By contributing you agree your work is licensed under the project's [MIT license](LICENSE).
