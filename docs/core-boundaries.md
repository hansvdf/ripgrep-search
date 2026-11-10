# Core extraction: presentation, settings, and platform policy

This phase builds on commit `231cb81` (the initial Core extraction) on `feature/avalonia-migration`. The working application remains version 0.10.0 and targets `net8.0-windows`. Core targets `net8.0;net10.0`. No Avalonia or third-party dependencies are introduced, no tools are bundled, and no release tags are modified.

## Responsibilities

| Core | Windows Forms host |
| --- | --- |
| Unchanged search result model, JSON parser, and search runner | Search orchestration, control state, count/status display, UI lifetime |
| ResultSpan/SearchReport, fixed RGB palette and portable bold/underline flags, SearchResultFormatter | RichTextBox and ResultRtfBuilder, fonts, hit testing, navigation and dialogs |
| HTML and Markdown generation from the same formatted report data | Save dialog, export file writing and feedback |
| AppSettings and explicit-path AppSettingsStore | `%APPDATA%\RipgrepSearch\settings.json` selection, updating settings from controls and history deduplication |
| ExecutableResolver and asynchronous DependencyChecker | WindowsDependencies wrapper supplies the existing WindowsPathEnvironment.RefreshProcessPath callback |
| DependencyDiagnostics builds/redacts plain text from supplied metadata and checked results | Application/Windows version lookup, profile selection, cached results, clipboard, feedback and icons |

At the end of this extraction, internal types stayed in the `RipgrepSearch` namespace, with existing friend-assembly access. Phase 4 subsequently exposes the small search/formatting API described in [the Avalonia prototype notes](phase4-avalonia-prototype.md#core-api). [Phase 5.2](phase5.2-settings-history.md) additionally exposes AppSettings/AppSettingsStore and a pure WithRecentSearch snapshot method for portable MRU handling; the existing store methods and WinForms call sites are unchanged. Other types retain their existing visibility. No UI toolkit or registry types enter Core. The existing path-provider delegate is sufficient for dependency checks; no service container or new interface hierarchy is needed.

[Phase 5.3](phase5.3-search-options-report-view.md) makes the existing SearchReport public for Desktop report capture and adds SearchCommandFormatter, extracted from the WinForms display quoting. WinForms delegates only its command-display formatting to that identical implementation; its search, settings and UI behavior are retained. ResultsExporter and dependency infrastructure remain internal pending their owning phases. SearchRunner and RipgrepJsonParser algorithms are unchanged.

## Compatibility decisions

- **Presentation:** The fixed v0.10.0 palette is represented by ResultColor values containing RGB data; ResultTextStyle records regular/bold/underline decorations. HTML retains its original named-color spelling. RTF consumes the same spans and palette. Grouping, separators, merged adjacent matches, zero-length semantics, CRLF output, visible NUL substitution, report ordering and navigation metadata are retained. Raw results remain separate from presentation spans.
- **Settings:** Property names, declaration order, defaults, indented JSON output and best-effort failure handling are unchanged. Load never creates, migrates, or rewrites a file. Save is called at the same existing application events; no user settings are accessed by the portable tests. The store accepts an explicit path and has no platform-specific location policy. It preserves the current synchronous/non-atomic write behavior; concurrency and alternate preview settings locations remain future work.
- **Resolution:** On Windows, lookup still searches every PATH directory for `.exe` first, then performs the extensionless fallback. A found but unusable first candidate is not silently replaced by a later executable. File existence alone is not an Installed result: probes must execute and return a recognized version. Unix hosts use extensionless rg/rga names; actual Unix execution is not yet verified.
- **PATH policy:** Core's default checker reads the current process PATH without refreshing it. The Windows application explicitly uses WindowsDependencies, which supplies the unchanged registry refresh function on a worker thread. System/user ordering, deleted-variable behavior, global process-PATH update, subsequent search inheritance, timeout handling, and error reporting remain unchanged. Core does not interpret shell profiles or discover Unix login environments.
- **Diagnostics:** Core receives application/OS/profile metadata from the host and collects no additional system information. The Windows dialog retains `Windows version`, `%USERPROFILE%`, case-insensitive matching, escaped separators, and omission of redundant success messages. A future host can supply another OS label, profile placeholder and case policy. Copy still uses cached completed results; displayed paths remain unredacted.
- **Scope:** SearchRunner, RipgrepJsonParser, SearchResults, WindowsPathEnvironment, command generation, packaging, and existing settings locations are not redesigned.

## Tests

Run from the repository root with the .NET 10 SDK:

```powershell
dotnet build src/RipgrepSearch.Core/RipgrepSearch.Core.csproj -c Release
dotnet build RipgrepSearch.csproj -c Release
dotnet run --project tests/RipgrepSearch.Tests/RipgrepSearch.Tests.csproj -c Release
dotnet run --project tests/RipgrepSearch.Core.Tests/RipgrepSearch.Core.Tests.csproj -c Release -f net8.0
dotnet run --project tests/RipgrepSearch.Core.Tests/RipgrepSearch.Core.Tests.csproj -c Release -f net10.0
```

The existing 590 Windows checks are retained, with references adapted to portable span types and extracted diagnostics. Fake dependency fixtures now include Core's assembly. These checks continue to exercise actual rg/rga, converters, RTF rendering, settings initialization, Windows PATH refresh and dialog behavior.

The portable runner adds focused coverage for full formatted spans and exports, all palette colors, NULs/Unicode/zero-length matches, settings defaults/property names/round trips/load-without-write/failures, diagnostic text and redaction policies, executable precedence, probe statuses/timeouts/errors/cancellation and host-supplied refresh policy.

`tests/RipgrepSearch.Core.Tests/Fixtures/v0.10.0-*` were generated using the pre-phase formatter/exporter source from commit `231cb81`, compiled in ignored build output. They contain the same v0.10.0 implementation as the original release baseline. Tests compare complete output, normalizing only the absolute fixture file URI and platform line endings. No real account paths are stored. These fixtures are compatibility references and should not be regenerated merely to make a changed exporter pass.

## Validation results

Validation on Windows x64 with SDK 10.0.403:

| Check | Result |
| --- | --- |
| Core and portable test Release build, both net8.0 and net10.0 | PASS; 0 warnings, 0 errors |
| WinForms Release build, net8.0-windows | PASS; 0 warnings, 0 errors |
| Existing Windows regression suite | PASS; all 590 checks |
| Portable Core runner, .NET 8.0.33 | PASS; all 122 checks (47 previous + 75 focused checks) |
| Portable Core runner, .NET 10.0.14 | PASS; all 122 checks |
| Complete span/HTML/Markdown fixture comparisons | PASS on both target frameworks |
| SearchRunner/RipgrepJsonParser/SearchResults/WindowsPathEnvironment comparison | Unchanged from the start of this phase |

The sandbox build exited without a compiler diagnostic, as during the earlier SDK validation; builds succeeded outside the sandbox. The Windows regression suite ran outside the sandbox so installed rga converters could execute. Portable runner executions succeeded in the sandbox and require no installed search tools. No SDKs/tools were installed. Fixtures and test settings are isolated under ignored build output. Native macOS/Linux execution, startup/memory benchmarks, manual native UI acceptance, and published-package launch were not performed on this Windows host.

## Remaining limits and next phase

macOS/Linux execution and native integration remain untested. Settings directory selection, folder-history case rules, runtime metadata, clipboard, OS file/URL launch, RTF/RichTextBox behavior, registry PATH refresh, Explorer scripts and packaging remain in the Windows host. Timestamp sorting and process-tree cleanup still require validation on each target OS. A .NET 10 SDK is needed for normal multi-target builds; published WinForms runtime requirements remain .NET 8.

The fixed palette and report spans are ready for another renderer. The next proposed phase is an Avalonia shell and bounded result-view prototype, including cross-line selection, Unicode highlighting and large-result performance tests. Implement that only after review of this phase.

The subsequent implementation and macOS .NET 10 validation are recorded in [Phase 4](phase4-avalonia-prototype.md). The validation results above remain the historical Windows extraction results.
