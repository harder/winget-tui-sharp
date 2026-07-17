# Terminal.Gui Feature Gaps vs. shanselman/winget-tui (Rust + Ratatui)

This document tracks the API and behavior gaps in **Terminal.Gui v2** that came up while
building this winget-tui port. Each entry is the kind of thing that should drive a
Terminal.Gui issue, PR, or wishlist discussion — not a complaint about Terminal.Gui being
incomplete. Where this port works around a gap, the workaround is noted.

The list is anchored to **Terminal.Gui 2.4.17-develop.6** (the version in `WingetTuiSharp.csproj`).
If Terminal.Gui upgrades close any of these, please send a PR removing the entry — and
ideally adding a test in `tests/ParserTests.cs § Terminal.Gui compatibility` that would
have caught a regression.

## A. TableView gaps

### A1. Per-cell foreground color in standard table source
Ratatui's `Cell::new(...).style(...)` lets the upstream app color the selection marker, pin
emoji, source ("winget" in blue vs "msstore" in amber), and version cells independently.
Terminal.Gui's `TableView` exposes `CellColorGetter`/`RowColorGetter` only via
`ColumnStyle`, and they return a whole `Scheme`, not a per-grapheme attribute. Inline
coloring of mixed-style text within a cell (e.g. a pinned package showing 📌 in accent
color followed by the name in normal color) requires writing a custom `ITableSource` +
drawing override.

**Workaround:** the Source column uses a per-row `ColumnStyle.ColorGetter` to pick the
foreground based on the cell value (winget → info-blue, msstore → accent). Per-grapheme
inline styling within the Name column isn't done.

> Suggested: add a per-cell `Attribute[]` representation (or accept a `Markup` string) so
> hosts can paint partial-cell styles without subclassing TableView.

### A2. No built-in column sort indicator
Upstream renders `↑` / `↓` next to the sorted column header by reconstructing the header
string. Terminal.Gui has no first-class "sort by column" concept on `TableView`. A
`ColumnStyle.SortIndicator` with click-to-sort is the natural fix.

**Workaround:** `App.HeaderWithSort` appends ` ↑`/` ↓` to the header string at table-build
time. Crude but works.

### A3. Header click → sort
Ratatui dispatches header-region clicks to cycle the sort column. Terminal.Gui's
`TableView` does not raise a click event scoped to the header row; you have to override
`OnMouseEvent` and compute column boundaries yourself.

**Status:** click-to-sort is not implemented in this port. Sort is keyboard-only (`S`).

### A4. Multi-select markers (checkbox column)
Upstream renders `[x]` / `[ ]` prefixes in the Name column for batch selection. Terminal.Gui
has `CheckBoxTableSourceWrapper`, but it commandeers an entire leading column and re-flows
selection semantics. A "multi-select set" overlaid on existing rows (without taking
column 0) would be more useful.

**Workaround:** `App.RefreshTable` prefixes `[x] ` / `    ` into the Name cell text in the
Upgrades view based on `AppState.BatchSelected`.

### A5. Pin/state glyph rendering width
Ratatui counts emoji 📌 as width 2 cells and aligns columns accordingly. Terminal.Gui's
table column widths reserve `ColumnStyle.MaxWidth` in display cells, but the table source
returns a `string`/object — measurement happens via `string.GetColumns()` only on render,
which can desync from manually computed widths if you split cells yourself.

### A6. `ColumnStyle.RepresentationGetter` lacks row context
The lambda receives only the cell value (`Func<object, string>`). To prefix the cursor row
with a `●` marker (like upstream does for accessibility / color-blind users), we had to
write `MarkedTableSource` (nested in `App.cs`) that injects a column-0 marker and tracks
the cursor row externally via `ValueChanged`. A `Func<CellRepresentationArgs, string>`
overload exposing `RowIndex`, `ColumnIndex`, and `IsCursorRow` would have made this a
one-liner without a wrapper type.

### A8. Column widths shift with viewport contents
`TableView.CalculateMaxCellWidth` scans only the **visible** rows and picks the max cell
content width per column. When the cursor moves or the viewport scrolls, columns can
visibly jump width as new rows enter the viewport. Not a parity issue with Ratatui, but a
real UX paper cut.

**Workaround:** every column in `ApplyColumnStyles` sets `MinWidth = MaxWidth` to pin
column widths regardless of viewport contents. Trades short data padding for stable
layout — the right tradeoff for this app.

> Suggested: `ColumnStyle.WidthMode` with values `Fixed | FitVisible | FitAll`. Fixed
> widths pin to MaxWidth; FitVisible is today's behavior; FitAll scans the whole source
> once at bind time (best for stable layouts on bounded data).

## B. Layout / styling gaps

### B1. Tabs widget API mismatch
`Terminal.Gui.Views.Tabs` is **focus-driven** — the focused subview is the active tab.
That clashes with Ratatui-style tabs where the active tab is a logical state independent
of focus (you want focus to stay on the package list while the active tab is "Installed").
We had to hand-roll a `TabBar` widget (in `Ui.cs`) for winget-tui-style behavior.

> Suggested: a `TabSelector` (or `Tabs.Mode = Active|Focused`) for headless tabs that
> drive a *content swap* in another view rather than swapping focus.

### B2. View `Border.SchemeName` not assignable
You can pass `BorderStyle = LineStyle.Rounded` and the border picks up the parent view's
scheme — there is no `Border.SchemeName` property to tint borders independently.

**Workaround:** `Theme.cs` defines `FrameFocusedSchemeName` and `FrameUnfocusedSchemeName`.
`App.ApplyFocusStyle` swaps the whole frame's `SchemeName` on focus change AND toggles
`BorderStyle` between `Heavy` and `Rounded` to make the focus state visually obvious. The
inner content (TableView, DetailPanel) sets its own `SchemeName` separately so the focus
swap doesn't repaint the body. Works but is more code than it should be.

### B3. No semantic "FrameView with title-aligned right text"
Upstream's panel title is `" Installed (42) • 📌 only "` with the count right-aligned.
The `FrameView.Title` API is a single string with `TitleAlignment` — no inline split for
left-and-right segments.

**Workaround:** we encode counts and pin-filter state inline in the title string. No
right-alignment.

### B4. No first-class status bar with right-aligned shortcut hints
Terminal.Gui has `StatusBar` and `Shortcut`, but they assume each shortcut is a focusable
widget tied to a `Command`. The Ratatui status bar is purely informational with colored
badges (filter, pin, message, hints) and right-alignment fall-off.

**Workaround:** `Ui.cs`'s `StatusBar` is a custom `View` with hand-written
`OnDrawingContent` that draws the source/pin badges left, hotkey hints right (with
ellipsis truncation from the left when space is tight), and the status message in the
middle. ~150 LOC.

### B5. Inline color "badge" (chip) primitive missing
Source-filter / pin-filter chips ( ` Winget `, ` 📌 only `), the action-key chips in the
detail panel ( ` i ` ` u ` ` x ` etc.), and the tab pills are all variations of "padded
text with a Scheme." A `Chip` / `Badge` widget would dedupe ~80 LOC across status bar,
detail panel, and tabs.

### B6. No rich-text span primitive
The detail panel needs inline-styled text: accent-bold labels, info-blue underlined URLs,
chip-style action keys, normal-text descriptions — all mixed on a single line. Terminal.Gui's
`Label` and `TextView` render a single attribute across their entire content.

**Workaround:** `DetailPanel.cs` mostly does direct drawing via `OnDrawingContent`
with a local `Span` record type holding `(Text, Attribute)`. Walks spans, accumulates
display width, line-wraps at word boundaries. The homepage / release-notes rows now use
tiny embedded `Markdown` views to get native hyperlink behavior without rewriting the
whole panel. A real span primitive would still remove ~250 LOC of custom layout/rendering.

> Suggested: a `Span` / `Markup` value type accepted by `Label.Text` and `TextView.Text`,
> backed by an `IList<(string Text, Attribute Attr)>`. Ratatui's `Line::from(vec![Span])`
> is the model.

## C. Input gaps

### C1. Modifier-aware single keystroke dispatch
The Rust app distinguishes `p` vs `P` purely by `KeyEvent.code`. In Terminal.Gui, `KeyCode.P`
plus `ShiftMask` is correct, but routing through `KeyDown` mixes the rune (via
`Key.AsRune.Value`) with the `KeyCode` enum. We use `Key.AsRune.Value` for all letter
actions because it's the only reliable case-sensitive path. A dedicated `Key.Character`
property (case-correct, modifier-aware) would help.

### C2. `Esc` ambiguity
Ratatui's input handler decides whether `Esc` clears a filter, closes the help overlay, or
quits, based on a single state machine. Terminal.Gui routes Esc to focus-up navigation
and modal-dismiss by default; suppressing those defaults across nested views (TextField
→ list → window) is non-trivial.

**Workaround:** `App.OnKeyDown` and `App.OnFilterKeyDown` intercept Esc explicitly based
on `_state.InputMode` before any other handling.

## D. Async + threading

### D1. No bundled generation-counter pattern for cancelling stale results
The Rust app uses a `tokio::mpsc` channel and drains it inside the render loop, with
explicit generation counters on the app state to discard stale responses. Terminal.Gui's
equivalent is `Application.Invoke(Action)`, but there's no built-in helper for the
generation-counter pattern.

**Workaround:** `AppState` carries `view_generation` and `detail_generation`, each
incremented before a fetch. The async callback checks `gen != _state.ViewGeneration`
before mutating UI state. Pattern works fine but is hand-rolled per call site.

> Suggested: a `Task<T>` extension `WithGeneration(Func<int> currentGen)` or an
> `IApplication.InvokeIfCurrent` helper that takes a guard predicate.

### D2. `IApplication.Invoke` re-entrance guarantees
We rely on `App?.Invoke(...)` from background threads. The documentation is light on
whether nested Invokes from inside an existing Invoke are safe / queued / synchronous. For
an app this size it works, but a clearer contract (e.g. "always queued to next iteration")
would ease porting from Ratatui's pull model.

## E. Performance hypotheses to validate

Unmeasured but were noticeable while building the port — items to put under a benchmark:

1. **Full table redraw on filter change.** Ratatui re-renders only changed rows.
   Terminal.Gui's `TableView.Update()` invalidates the whole content area. With 5,000
   installed packages and active sort, scroll responsiveness should be measured side by
   side.
2. **Status-bar spinner timer at 100 ms.** We invalidate the entire status bar each tick.
   Ratatui only repaints the spinner glyph cell.
3. **Process+parse cost.** Both apps spend most time inside `winget`. The .NET `Process`
   invocation startup overhead vs Rust's `std::process::Command` should be measured.
4. **Mouse hit-testing in TabBar.** Hand-rolled linear scan; cheap, but compare to
   whatever Ratatui does in its tabs widget.
5. **Emoji / wide-grapheme rendering.** Each cell with 📌 forces a `string.GetColumns()`
   call per draw. Cache column widths per row in `EnumerableTableSource`?

## F. Things Terminal.Gui v2 has done well

Counterweight to all the above — these came as pleasant surprises:

- **`Rune.GetColumns()` + `string.GetColumns()`** correctly handle grapheme clusters and
  clamp emoji widths. Backed by the `Wcwidth` package; functionally equivalent to Rust's
  `unicode-width` crate. Our display-width column slicing in `CliBackend.SliceColumn`
  depends on this, and it Just Works for CJK package names.
- **Bracketed-paste pipeline** (PR #5277, in `2.2.2-develop.3`): `TextField` has `Pasted`
  and `Pasting` events on the `Command.Paste` pipeline. We use `Pasted` on the search
  field to auto-fire the search backend without waiting for Enter. Terminal handles the
  CSI 2004h wrap; we don't see any of that.
- **Native AOT compatibility.** The current `develop` builds AOT cleanly with
  `<PublishAot>true</PublishAot>` + `InvariantGlobalization=true`. The only manual change
  we needed was converting a runtime `Regex` to source-generated via `[GeneratedRegex]`.
  Earlier versions had AOT trim issues; this is largely solved as of 2.2.x.
- **`Scheme` value type with derived roles.** Setting only `Normal` gives a reasonable
  `Focus`/`Active`/`HotNormal` automatically. Most of our scheme definitions in `Theme.cs`
  only set 2–3 roles explicitly.
- **`HasFocusChanged` event.** Used for the focus-driven border weight swap. Clean
  callback model, no polling.

## G. Items resolved by recent Terminal.Gui versions or upstream parity work

These gaps were on this list at some point during development but are now closed —
preserved here as a changelog so anyone bumping Terminal.Gui versions can sanity-check
whether they've regressed.

- ~~**Bracketed paste support**~~ — Terminal.Gui's PR #5277 added the full pipeline.
  See section F above.
- ~~**Markdown view auto-focuses last link table on initial render**~~ — Fixed upstream in
  PR #5370 (released in 2.4.x). Our `DetailPanel` still sets `CanFocus = false` on the
  embedded `Markdown` views as a defense-in-depth, but the framework no longer steals
  focus on `Add()`.
- ~~**Redraw fan-out from ancestor-only layout propagation**~~ — Fixed upstream in
  PR #5373 (#5357). Our many `SetNeedsLayout`/`SetNeedsDraw` calls in `App.cs` and
  `DetailPanel.cs` no longer trigger subtree-wide invalidations when only ancestor
  layout changes.
- ~~**Duplicate `ScreenChanged` from `Application.Screen` setter**~~ — Fixed upstream in
  PR #5404 (#5274). Halves the redraw cost on terminal resize.
- ~~**No `TableStyle.HeaderColorGetter` (only per-column), A7**~~ — Closed by 2.4.17's
  `TableStyle.HeaderScheme`: a base `Scheme` applied to all column headers, falling back to
  the view's scheme if `null`, with `ColumnStyle.HeaderColorGetter` still available for
  per-column overrides. `App.ApplyColumnStyles` (`src/App.cs`) still loops over every column
  to assign the same `HeaderColorGetter` on every `RefreshTable` call — that loop can be
  replaced with a single `TableStyle.HeaderScheme` assignment as a follow-up simplification.

## H. Wishlist — items that would have made the port noticeably shorter

Prioritized by LOC saved if Terminal.Gui implemented them:

| Wishlist item | Saves ~LOC | Closes gap |
|---|---|---|
| `Span`/`Markup` rich-text primitive for `Label`/`TextView` | ~250 (DetailPanel rewrite) | A1, B6 |
| `Chip` / `Badge` widget | ~80 (status bar, detail, tabs) | B5 |
| `Border.SchemeName` settable | ~25 (Theme.cs duplicate schemes + ApplyFocusStyle) | B2 |
| `Tabs.Mode = Headless` + click handlers | ~110 (entire TabBar implementation) | B1 |
| `TableView.OnHeaderClicked(int col)` + sort cycling | ~30 (header parsing, sort wiring) | A2, A3 |
| `Key.Character` modifier-aware property | ~5 per handler site | C1 |
| `Application.InvokeIfCurrent(guard, action)` | ~5 per async call site | D1 |
| `ColumnStyle.WidthMode { Fixed, FitVisible, FitAll }` | ~3 per column | A8 |
