# Phase 5.3 — Search options, report capture, command preview and View menu

Implemented on `feature/avalonia-migration`, following committed Phase 5.2 (`770b204`), on 2026-11-09. Stable WinForms remains v0.10.0/net8.0-windows; Desktop remains v0.10.0/net10.0 and Core targets net8.0;net10.0. No versions, tags, packages, releases or publish scripts changed. No commits/pushes, installers or bundled executables were created. Export/save workflows and Help/dependency dialogs remain outside this phase.

## Implemented options and defaults

Actual WinForms BuildInterface/BuildCurrentArguments are the reference. Its GUI exposes the following options, now present in Desktop with compiled bindings:

| Option | Defaults and behavior |
| --- | --- |
| Before / After | Independent integers 0–100; default 0/0. Core omits the corresponding flag for zero. UI uses integer parsing and bounded numeric controls; loaded/out-of-range model values are clamped as in WinForms. |
| Search Office/PDF files with rga | Default false. Selects rg or rga through the same Core executable resolver. No silent fallback. Optional rga requires rg and whatever converters its adapters need. |
| Enable sorting | Default false. Field/order remain stored when disabled and are not passed to the engine until enabled. |
| Sort by | path/modified/accessed/created; default retained field path. Unknown loaded/model values fall back to path. |
| Sort order | Ascending/Descending; default Ascending. Core generates --sort or --sortr. |
| View: Show Search Options | Default true; check state tracks the visible section, saves immediately and does not change option values. |
| View: Show Command | Default false; independent from options visibility, saves immediately and does not stop preview updates. |

No case, literal/regex-mode, whole-word, include/exclude, hidden-file or arbitrary-extra-arguments controls were introduced: they do not exist in the reference WinForms UI. Regex semantics and inline flags remain the engine's responsibility. Engine tests that invoke additional switches directly do not create new GUI parity obligations.

Phase 5.2 profiles, startup policies, 10/20-entry histories and asynchronous save ordering remain. Each option/View change now saves the current settings snapshot and nonblank folder/pattern text, matching WinForms SaveCurrentSettings admission timing. Initialization restores controls without triggering a save. Mutations first create an independent settings snapshot, so a pending write cannot observe later changes to its object. Folder/pattern editing still updates the preview without saving until a search, option/View change or close.

The entire options panel is disabled while searching; sort field/order additionally require sorting to be enabled. Search cannot overlap an active search. View remains available during a search, as in WinForms. Hidden context/engine/sort values stay active. Existing selection, scrolling, palette, Unicode offsets and incremental editor insertion are retained. Hiding sections reclaims results space; it does not rebuild the document. The minimum window size is now 780×680 to accommodate the new sections, readable numeric fields and a results area with both sections shown; default size remains 1080×740.

## Search integration and command preview

Desktop constructs arguments exclusively with SearchRunner.BuildArguments. Each valid execution captures one argument list before settings/UI callbacks can change state. That same list is passed through ArgumentList to the resolved executable and formatted for the captured report command. There is no shell invocation and no separate Avalonia regex/search implementation.

The read-only, selectable command preview uses the same builder and selected command name, rg/rga. SearchCommandFormatter contains the original WinForms BuildDisplayCommand/QuoteForDisplay behavior: spaces between arguments, quoting empty/whitespace/quote-containing values, escaping backslashes/quotes inside displayed quotes. The extracted quoting method body was compared against committed WinForms source and is unchanged. WinForms' private BuildDisplayCommand now delegates to this helper; no other Windows UI/settings/search behavior was refactored.

The preview is intentionally **display-only**, not shell escaping. The UI says so. It shows the selected rg/rga command name, as WinForms does, rather than dumping PATH/environment or repeatedly resolving full executable paths on every keypress. Actual launch resolution still uses Core's current-PATH/platform rules, including Windows .exe precedence and Unix extensionless executables. A found-but-unlaunchable candidate reports a process failure; it is not replaced silently.

The preview updates on folder, pattern, context, engine and sort changes even while hidden. Before a valid folder is entered, it can show incomplete/invalid input for inspection; validation still prevents execution. Folder normalization is pure path handling, not a directory probe in the preview getter.

Desktop continues Phase 5.2's normalized absolute execution path and search-folder working directory. Therefore a relative typed input is displayed as its effective absolute folder in the preview and generally yields absolute result headings. WinForms can retain a relative folder spelling in its command/headings. Display quoting is shared, but that already-established host path-policy difference is explicitly retained. Navigation metadata is always separate from display text. No environment refresh, shell-profile discovery or configured tool paths were added.

## Structured report lifecycle

Desktop now uses the existing Core SearchReport model, rather than recreating an export model from editor text:

1. **Before any executed search:** CurrentReport is null. Option/input changes alone do not manufacture a report.
2. **Validation failure:** A blank pattern, invalid folder or missing selected engine does not start a new search or clear old output. The previous report, if any, remains associated with its original display/metadata, matching WinForms. The input-error status is separate from the old report's completion status.
3. **Valid start:** Create a fresh report with the normalized folder, unchanged pattern, exact display command built from the captured execution arguments, and DateTimeOffset start time. Replace CurrentReport and clear the results document. Older report instances are never appended to by the new search.
4. **Streaming:** Core supplies ordered structured batches through the existing bounded channel. A worker formats one batch with one per-search SearchResultFormatter, producing the same ResultSpan objects used for report capture. One awaited background-priority UI operation appends the document, adds those raw lines/spans to the local report, then updates counts/status. A cancelled/skipped UI batch is not captured as displayed output. Counts remain matching lines, excluding context and counting multiple occurrences on one line once.
5. **Finalization:** Preserve completion/partial status and append no-match/reason/stderr spans to both document and report. No-match presentation now exactly uses `No matches found.` without the prototype's extra trailing CRLF, and failure status includes the exit code when available, matching the reference. Warnings are retained even with successful completion. Cancelled, failed and limited searches retain the lines actually displayed and explicitly partial status.
6. **After finalization:** Later input/option/View edits change the next-search preview/settings, not this report. A new executed search replaces it. View-only changes do not reorder results or rebuild report/editor data.

SearchReport has captured init-only metadata and mutable result/span lists/status, as before. It is populated on the UI thread and is not an immutable/thread-safe object. Phase 5.4 must export the captured report after the active search settles, not treat it as finalized while IsSearching is true or use current input controls as metadata. Export availability and file/dialog workflows are deliberately not implemented yet.

The raw Results list and formatted Spans list preserve **engine/display order**, including rg/rga sorting, file grouping and context gaps. There is no client-side sort/view transform. Consequently future exports should reflect the same presentation/order the user saw, as WinForms exports do. Raw results retain original text/ranges; presentation includes visible NUL substitution, styles and navigation metadata.

Memory is bounded by the unchanged Core limits. Raw lines are retained by reference, not cloned; the same formatted span objects are added to the report. The viewer index continues to hold offsets/styles/navigation rather than a second collection of span text. Temporary per-batch arrays/text are not retained as a separate whole-search buffer, and the entire document is not concatenated on each append. Retaining raw results plus presentation strings necessarily uses more memory than the Phase 4 viewer-only prototype; see measurements below.

## View menu and phase boundaries

Only View→Show Search Options / Show Command is introduced. There are no new display-formatting commands, grouping controls or result-sort actions; the reference View menu has none. The menu uses toolkit mnemonics/check items, with independent two-way state and all four layout combinations tested. It stays usable while search controls are busy. Toggling it preserves the existing document and cross-line selection.

File→Export Results and save shortcuts/dialogs remain Phase 5.4. Help→Check Dependencies/About, diagnostic copying and native application-menu policy remain Phase 5.5. The View menu is attached to the window as an Avalonia NativeMenu. macOS exports it to the screen menu bar; the regular in-window menu is shown only when NativeMenu.IsNativeMenuExported is false (including headless tests and platforms without global menu support). Both menus bind to the same view-model state and persistence, and native activation toggles that state once. Application.Name is Ripgrep Search for the standard macOS application menu label. No empty File/Help menus were added as placeholders. Existing result F4, Escape cancellation and history keyboard routing remain.

### Native-menu follow-up

After feedback about macOS menu placement, a native View menu and export-aware managed fallback were added without changing Core or WinForms. The Desktop suite now passes **220 checks** (six additional native model/binding/activation/fallback/shutdown checks). A separate opt-in `--native-menu-smoke` test opens a brief real macOS window using injected test settings and verifies native export plus fallback hiding, then closes and awaits settings cleanup. It does not synthesize OS input or touch the user's profile. The menu's screen placement is confirmed through the actual native exporter; manual pointer/keyboard acceptance is still outstanding.

```sh
dotnet run --project tests/RipgrepSearch.Desktop.Tests/RipgrepSearch.Desktop.Tests.csproj -c Release -- --native-menu-smoke
```

Additional files for this follow-up: `src/RipgrepSearch.Desktop/App.axaml`, `src/RipgrepSearch.Desktop/MainWindow.axaml.cs`, and `tests/RipgrepSearch.Desktop.Tests/NativeMenuSmoke.cs`; existing test runner/menu tests and README/parity notes were updated. The Phase 5.3 performance table below remains the earlier recorded run, not a new native performance measurement.

## Core API changes

- **SearchReport** becomes public, with its existing shape/behavior retained, so Desktop can capture the same raw results, presentation spans, metadata and status used by the existing exporter. No broad friend access is added.
- **SearchCommandFormatter.Format** is a new small public presentation helper extracted from WinForms. Both hosts reuse identical quoting; the WinForms private forwarding method remains.
- SearchRunner, RipgrepJsonParser, SearchResultFormatter, AppSettings schema/store and platform PATH policy algorithms are unchanged. ResultsExporter and dependency services remain internal until their owning phases.

## Builds and tests actually run

Environment: macOS ARM64, SDK 10.0.403, runtime 10.0.14, rg 15.2.0, rga 0.10.10. No tools/dependencies were installed. Existing restored assets were used. As in prior phases, builds/tests ran outside the execution sandbox with shared compilation disabled.

| Check | Result |
| --- | --- |
| Core Release build | PASS, net8.0 and net10.0; zero warnings/errors. |
| Core regression suite on .NET 10 | PASS, **138 checks** (133 existing + 5 display-quoting cases); existing report/export compatibility fixtures still pass. |
| Standalone Desktop Release build | PASS, zero warnings/errors. |
| Desktop headless/integration suite | PASS, **214 checks** (98 existing + 116 option/report/View checks and added assertions). Uses the actual XAML view, headless windowing and Skia. |
| Real rg behavior | PASS: Unicode/spaced paths, real before/after context, multiple occurrences per matching line, gaps/grouping, descending path order and every timestamp field in both orders on this Mac. |
| Real rga integration | PASS: actual Unicode ZIP containing a text file, with context/sorting, correct navigation metadata, captured rga command and matching-line count. A test launcher only redirects rga's conversion cache into ignored output. No bundled binary or application cache override was added. |
| Missing selected rga | PASS: actionable missing-engine status, no fallback/no new report on validation failure. |
| WinForms compatibility evidence | Extracted quoting body unchanged; Core net8 builds; root project still net8.0-windows/version 0.10.0 and excludes Desktop sources. Only its command-display forwarding method changed. |
| Windows regression/native GUI | Not run on macOS; existing 590 Windows checks must be run on Windows. No fresh native Windows result is claimed. |
| .NET 8 test execution | Not run: ARM64 .NET 8 runtime remains unavailable. Core net8 compilation passes; .NET 10 execution is validated. |
| Native Avalonia GUI acceptance | Initial Phase 5.3 used headless layout checks. The subsequent native-menu follow-up also passed a real macOS backend smoke check: NativeMenu.IsNativeMenuExported=true and in-window fallback hidden, using isolated test settings. Manual menu clicks/keyboard, OS clipboard and broader GUI acceptance remain pending. |

Focused tests cover all rg/rga × sort-field × direction combinations with context and quote/space/Unicode arguments transported to a real child process, preview/actual-argument consistency, defaults/clamping, disabled-sort retention, settings restoration without initialization writes, hidden preview updates, all four View states/reflow, checked-state/pointer menu activation, selection/document preservation and option/View persistence. Report checks cover captured timestamps/metadata, live-edit independence, successive searches, retained prior report on validation failure, no matches, regex errors, partial process failure, warnings, launch failure, cancellation while streaming, rejected overlap, both main display budgets, and report-text equality with the document. Earlier history/startup and process-tree cleanup tests continue to pass.

Core's five new quoting checks exercise plain values, empty arguments, whitespace/quotes/backslashes, Unicode, shell metacharacters as display text and the historical trailing space for an empty argument list. They do not claim the preview is shell-safe.

Commands:

```sh
dotnet build src/RipgrepSearch.Core/RipgrepSearch.Core.csproj -c Release --no-restore -p:UseSharedCompilation=false
dotnet run --project tests/RipgrepSearch.Core.Tests/RipgrepSearch.Core.Tests.csproj -c Release -f net10.0 --no-restore -p:UseSharedCompilation=false
dotnet build src/RipgrepSearch.Desktop/RipgrepSearch.Desktop.csproj -c Release --no-restore -p:UseSharedCompilation=false
dotnet run --project tests/RipgrepSearch.Desktop.Tests/RipgrepSearch.Desktop.Tests.csproj -c Release --no-restore -p:UseSharedCompilation=false
```

## Performance observations and limitations

Single warmed headless run from the final Desktop suite; these are not controlled A/B benchmarks or native first-visible-result/FPS figures. The new menu/options affect viewport size; the suite also executes more scenarios before measuring. Compare the [Phase 4 observations](phase4-avalonia-prototype.md#performance-observations) with those limitations.

| Scenario | Phase 5.3 observation |
| --- | --- |
| 10,000 matching lines across many files | 79 progressive batches, 252,966 displayed UTF-16 characters; first append 25.1 ms, first headless frame 37.1 ms, total through layout 201.8 ms, largest measured append/capture operation 4.10 ms. |
| Dispatcher work while streaming | Ten 16 ms timer ticks; largest observed interval 24.1 ms. |
| Thirty viewport jumps + Skia frames | 192.6 ms total. |
| Select-all + frame | 4.5 ms. |
| 10K memory | Managed retained delta 31,083,952 bytes; whole test-process working set 176,226,304 bytes (~168 MiB). |
| Near character budget | 1,969,209 displayed characters; first append 54.0 ms; max append/capture 1.58 ms; test-process working set 224,264,192 bytes (~214 MiB). |
| Quiet search cancellation including cleanup | 43.1 ms. |

Phase 4 recorded a 25.5 ms first append, 172.1 ms total, 36.2 ms quiet cancellation and whole-process working sets around 150/244 MiB at the two volumes. Full raw/presentation report retention adds managed data: the managed-delta increase is visible and should not be described as unchanged memory usage. In this run the 10K whole-process figure is higher and the near-character-limit figure is lower than Phase 4; GC/caching and the different preceding test workload prevent an isolated process-memory comparison. Full-text report/document assertion allocations run after each current-volume memory sample. Whole-process figures still include the runtime, rendering caches and earlier tests, so they do not isolate report memory. Batching, dispatcher progress, cancellation and limits still pass. Native latency, standalone memory and repeated comparable measurements remain acceptance work. Phase 5.4 must account for additional temporary HTML/Markdown output strings rather than relaxing limits or retaining another full result copy. No renderer replacement or performance SLA is justified by this single headless run.

## Remaining native/platform acceptance

- Keyboard-only use of numeric controls, engine/sort controls and View mnemonics on macOS/Windows; invalid numeric editing and focus/validation presentation.
- All visibility combinations and resizing on native DPI/font settings, selection/scroll preservation while toggling View and native menu pointer/keyboard interaction.
- Search with actual terminal/GUI PATH, missing/non-executable rg/rga, converter failures and no fallback. ZIP is tested on this Mac; PDF/DOCX conversions were not tested because pandoc/pdftotext were not found on PATH. No converter was installed. Converter availability is not certified by selecting rga.
- Actual rga converter descendant cleanup, OS Quit/close during search and persistence across a real application restart, in addition to passing headless process fixtures.
- Fresh WinForms build + full Windows regressions, including identical command-preview behavior after extraction. Linux GUI and Windows Desktop test-launcher portability remain future platform validation work.
- Native cold/warm startup, first visible result, scrolling/selection/copy, cancellation and memory with full report retention before final renderer/performance acceptance.

## Files created and modified

Created:

- `src/RipgrepSearch.Core/SearchCommandFormatter.cs`
- `tests/RipgrepSearch.Core.Tests/CommandPreviewTests.cs`
- `tests/RipgrepSearch.Desktop.Tests/SearchOptionsReportViewTests.cs`
- `docs/phase5.3-search-options-report-view.md`

Modified:

- `Program.cs` — only command-display formatter forwarding/extraction.
- `src/RipgrepSearch.Core/SearchReport.cs` — public visibility/documentation.
- `src/RipgrepSearch.Desktop/SearchViewModel.cs` — option state, preview, report lifecycle and execution wiring.
- `src/RipgrepSearch.Desktop/MainWindow.axaml` — controls, View menu, preview, minimum-size layout.
- `tests/RipgrepSearch.Core.Tests/Program.cs` — runs quoting checks.
- `tests/RipgrepSearch.Desktop.Tests/Program.cs` — runs focused checks, captures fixture arguments and checks reports in cancellation/limit scenarios.
- `tests/RipgrepSearch.Desktop.Tests/SettingsHistoryTests.cs` — engine-aware injected resolver in the existing test factory.
- `README.md`
- `docs/core-boundaries.md`
- `docs/phase5-feature-parity.md`

Stop after Phase 5.3 for review. Phase 5.4 can build exports from the captured report; no export menu, exporter API exposure, file writer or save-dialog workflow was implemented here.
