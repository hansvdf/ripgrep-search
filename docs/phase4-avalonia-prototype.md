# Phase 4: Avalonia shell and results-viewer prototype

Implementation and validation on 2026-11-09, on `feature/avalonia-migration`. The working Windows application remains version **0.10.0**, targeting **net8.0-windows**. Core still targets **net8.0;net10.0**. No tags, releases, commits, native packaging, or bundled tools were created. Native GUI acceptance remains pending; the automated results below establish a working headless prototype and real macOS search integration.

## Implementation

`src/RipgrepSearch.Desktop` is a separate **net10.0** desktop executable with compiled XAML bindings and a small `INotifyPropertyChanged` view model. Direct dependencies are pinned to stable [Avalonia.Desktop 12.1.5](https://www.nuget.org/packages/Avalonia.Desktop/12.1.5), [Avalonia.Themes.Fluent 12.1.5](https://www.nuget.org/packages/Avalonia.Themes.Fluent/12.1.5), and [Avalonia.AvaloniaEdit 12.0.0](https://www.nuget.org/packages/Avalonia.AvaloniaEdit/12.0.0). The test runner adds Avalonia.Headless 12.1.5. There is no MVVM framework, service container, TextMate integration, or settings dependency.

The interface provides folder/pattern inputs, native folder picking through StorageProvider, Search/Cancel commands, matching-line count, status, and a single read-only AvaloniaEdit document. Enter starts a search; Escape cancels. Inputs are disabled while searching. Window close cancels and asynchronously awaits the search before closing. The desktop lifetime's Quit/shutdown request routes active searches through the same window-close path.

Executable lookup uses Core's current-process PATH resolver, with extensionless `rg` on Unix. No shell profile or registry lookup is introduced. The initial folder is the user's home, or an explicit absolute command-line folder; searches use that folder as their working directory. Spaces and Unicode are transported through Core's shell-free `ArgumentList` implementation. Only standard `rg` is exposed in this phase.

SearchRunner and RipgrepJsonParser algorithms are unchanged. A bounded channel of two batches joins Core's producer to a worker consumer. The consumer formats ordered structured results with one SearchResultFormatter per search and awaits one background-priority dispatcher append per batch. Core's 128-line/approximately 65K-character batching, quiet flushing, cancellation, diagnostic limits, and original display budgets are retained. A cancellation received while queued UI batches remain is explicitly reported as partial, even if the process already exited successfully.

`ResultsDocument` appends once per batch and retains a sorted offset index of the Core spans. It keeps CRLF and UTF-16 offsets intact and disables undo storage. `ResultColorizer` binary-searches that index and styles only runs intersecting the editor's visible lines. It preserves Core's RGB colors, bold/underline decorations, filename grouping, context separators and visible NUL substitution. There is no control per result line, regex re-highlighting, or complete-document rebuild per batch. Existing selection and viewport are not reset on each append.

AvaloniaEdit provides cross-line selection, platform copy gestures and non-wrapped horizontal scrolling. Automatic URL/email links in arbitrary result text are disabled. Only Core's file-heading navigation metadata is used: double-click or F4 requests opening a local file through Avalonia's storage/launcher APIs. Actual OS launching still needs native acceptance testing.

The WinForms project already excludes `src/**/*.cs` and `tests/**/*.cs`; no root project edit was necessary. MSBuild evaluation confirmed its Compile items remain only DependencyDialog.cs, Program.cs, ResultRtfBuilder.cs, WindowsDependencies.cs and WindowsPathEnvironment.cs, and its target/version remain unchanged.

## Core API

Eleven existing types become public, with their namespace, signatures and behavior retained:

| Types | Reason |
| --- | --- |
| SearchRunner, SearchLimits, SearchOutcome, SearchCompletion | Execute and cancel the existing bounded search pipeline and interpret completion. |
| SearchResultLine, MatchRange | Consume structured match/context lines and their UTF-16 ranges. |
| SearchResultFormatter, ResultSpan, ResultColor, ResultTextStyle | Render the existing presentation metadata without reproducing formatting rules. Formatter instances are stateful and per-search. Palette enum values are RGB. |
| ExecutableResolver | Share the existing platform-aware current-PATH search-tool lookup. Its explicit-PATH overload remains internal. |

RipgrepJsonParser, SearchReport, ResultPalette, settings types, exporters, dependency probes and diagnostic helpers remain internal. No new Core friend assembly is introduced for Desktop. Existing friend access for WinForms and existing tests is retained. Desktop grants its own tests internal access to the span index and measurements, without adding those diagnostics to Core's public API.

## Reproduce

Run from the repository root using the .NET 10 SDK and a terminal with `rg` on PATH:

```sh
dotnet build src/RipgrepSearch.Core/RipgrepSearch.Core.csproj -c Release
dotnet run --project tests/RipgrepSearch.Core.Tests/RipgrepSearch.Core.Tests.csproj -c Release -f net10.0
dotnet build src/RipgrepSearch.Desktop/RipgrepSearch.Desktop.csproj -c Release
dotnet run --project tests/RipgrepSearch.Desktop.Tests/RipgrepSearch.Desktop.Tests.csproj -c Release
dotnet run --project src/RipgrepSearch.Desktop/RipgrepSearch.Desktop.csproj -c Release -- "/absolute/folder"
```

Environment: Apple Silicon macOS 26.5, SDK 10.0.403, runtime 10.0.14, installed `/opt/homebrew/bin/rg` 15.2.0. NuGet restore required network access outside the execution sandbox. As in earlier baseline work, sandbox compilation sometimes stalled/exited without compiler diagnostics; unrestricted builds with shared compilation disabled completed. No SDK or search tool was installed.

## Validation results

| Category | Result |
| --- | --- |
| Core Release build | PASS, net8.0 and net10.0; zero warnings/errors. |
| Core portable suite on .NET 10.0.14 | PASS, all 122 existing checks. |
| Desktop Release build | PASS, net10.0; zero warnings/errors. |
| Desktop automated suite | PASS, 30 checks using the actual view, headless windowing and Skia rendering. |
| Real macOS rg integration | PASS within the headless suite: Unicode/spaced folder and filename, emoji pattern, matching-line counts, no matches and invalid-regex diagnostics. |
| Headless rendering/input | PASS: orange-red match glyph runs, bold/underlined headings, UTF-16 emoji offsets, context/grouping/NUL presentation, cross-line selection surviving append, platform copy gesture and clipboard content (platform newline normalization), large selection, horizontal scrolling. |
| Lifecycle/process tests | PASS: streaming before process exit, busy input/command states, Escape cancellation, window-close wait, and termination of both a fixture process and its spawned descendant. Also covers cancellation during queued result consumption. |
| Error/limit tests | PASS: invalid folder, empty pattern, missing executable, real regex error, 10,000 matching lines, approximately 2M-character budget, and oversized-line diagnostic. |
| Native launch | Executable launched and entered the macOS native event loop. The computer-use connector could not locate the unbundled executable by path/name, so no native window interaction or visual acceptance is claimed. Diagnostic processes were stopped after inspection. |
| .NET 8 portable execution | Not run in Phase 4: ARM64 .NET 8 runtime is absent. Both target frameworks compile. |
| Windows application/regression suite | Cannot execute on macOS; pending Windows validation. Historical 590-check Windows results are in the baseline/boundary documents. |

The Desktop runner uses Unix launcher fixtures and requires installed `rg`; it is currently intended for macOS/Linux. Test settings are never loaded or saved. Fixtures reside in ignored build output; the many-file benchmark uses a temporary directory and removes it after a successful run.

## Performance observations

These are single-run, warmed **headless Skia** measurements, not native frame-rate, startup, or human input-latency benchmarks. The timer and render callbacks run on the same dispatcher as search consumption. Absolute results vary with JIT, filesystem cache, fonts and other host activity.

| Scenario | Observed |
| --- | --- |
| 200 matching files, 11,000 input lines, stopped at 10,000 | 79 incremental batches; 253,148 displayed UTF-16 characters. |
| First append / first headless rendered frame | 25.5 ms / 89.6 ms from starting the search. |
| Search through final layout | 172.1 ms; maximum measured document append 3.91 ms (excludes later layout/rendering). |
| Dispatcher heartbeat while streaming | Eight 16 ms timer ticks; largest observed tick interval 23.8 ms. |
| Thirty viewport jumps across the result document, with Skia frames | 210.9 ms total. This is not a continuous-scroll FPS measurement. |
| Select entire 10K-result document and render | 4.5 ms. |
| Memory after 10K results | Managed retained delta 6,044,120 bytes; whole test-process working set 157,204,480 bytes (~150 MiB). Includes Avalonia, runtime, and earlier tests. |
| Approximately 2M-character scenario, 16,000-character source lines | 1,969,209 displayed characters; first append 36.6 ms, maximum append 1.71 ms; process working set 255,950,848 bytes (~244 MiB). Core's budget includes per-line paths and allowance, so output need not equal exactly 2M characters. |
| Quiet-process cancellation through cleanup | 36.2 ms, including process-tree termination. |

AvaloniaEdit 12.0.0's `ScrollToHorizontalOffset` convenience method is a stub in [the upstream implementation](https://github.com/AvaloniaUI/AvaloniaEdit/blob/12.0.0/src/AvaloniaEdit/TextEditor.cs). The test manipulates the actual ScrollViewer offset and confirms nonzero horizontal scrolling. The application relies on the editor's scrollbar/input handling and does not call the stub.

Headless evidence supports continuing the AvaloniaEdit experiment. No requirement has yet justified replacing it. Native scrolling/selection, cold startup and standalone-app memory still need measurement before making a final renderer choice.

## Native acceptance and remaining scope

The following still require a native desktop session, beyond headless controls:

- Visually verify initial window layout, focus, Enter/Tab keyboard use and match/Unicode rendering on macOS.
- Open Browse, select a folder with spaces/Unicode, and search with the installed rg.
- Confirm progressive display, trackpad/scrollbar scrolling, dragging selections across lines, and Cmd+C paste into another application, both during and after a large search.
- Double-click/F4 a filename and verify the default application opens the intended file.
- Cancel, close the window and use macOS Quit during an active search; verify no process remains. Automated headless process-tree cleanup already passes, but native lifecycle dispatch is separate.
- Measure cold/warm startup, native first-visible-result latency, sustained scrolling/selection, and memory for a standalone process at both limits. Validate high-DPI rendering and accessibility.
- Run the existing Windows build and 590-check suite before merging; test Windows/Linux Desktop behavior separately.

Full menus, history/settings persistence, sort/context controls, exports, dependency-checker UI, rga/converters, signing/notarization/installers and bundled binaries are deliberately outside this phase. Context rendering is exercised through structured fixtures; the search UI currently requests zero context lines.

After Phase 4 review, Phase 5 should first complete native acceptance and Windows regression checks, then decide platform policies for settings locations, folder-history case comparison and GUI-launch PATH discovery. Keep AvaloniaEdit provisionally; review native latency and memory evidence before approving a renderer change or broader feature migration. No further phase was started.

## Files created and changed

Created:

- `src/RipgrepSearch.Desktop/RipgrepSearch.Desktop.csproj`
- `src/RipgrepSearch.Desktop/Program.cs`
- `src/RipgrepSearch.Desktop/App.axaml`
- `src/RipgrepSearch.Desktop/App.axaml.cs`
- `src/RipgrepSearch.Desktop/MainWindow.axaml`
- `src/RipgrepSearch.Desktop/MainWindow.axaml.cs`
- `src/RipgrepSearch.Desktop/SearchViewModel.cs`
- `src/RipgrepSearch.Desktop/ResultsDocument.cs`
- `src/RipgrepSearch.Desktop/ResultColorizer.cs`
- `tests/RipgrepSearch.Desktop.Tests/RipgrepSearch.Desktop.Tests.csproj`
- `tests/RipgrepSearch.Desktop.Tests/Program.cs`
- `docs/phase4-avalonia-prototype.md`

Modified:

- `src/RipgrepSearch.Core/ExecutableResolver.cs`
- `src/RipgrepSearch.Core/SearchResults.cs`
- `src/RipgrepSearch.Core/SearchRunner.cs`
- `src/RipgrepSearch.Core/SearchResultFormatter.cs`
- `src/RipgrepSearch.Core/SearchReport.cs`
- `README.md`
- `docs/core-boundaries.md`
