# Phase 5.2 — Settings, histories and startup policies

Implemented on `feature/avalonia-migration`, 2026-11-09. WinForms remains the stable Windows v0.10.0 application. Its source, settings location/schema/store behavior, project target, scripts and version are unchanged. No commits, pushes, tags, package changes, installers or bundled tools were introduced. Phase 5.3 search options and later features remain unimplemented.

## Settings isolation and lifecycle

The Desktop host selects its own profile:

| Platform | Desktop settings file | Stable WinForms settings |
| --- | --- | --- |
| Windows | `%APPDATA%\RipgrepSearch.Desktop\settings.json` | Still `%APPDATA%\RipgrepSearch\settings.json`. Separate directories; no import/migration or shared writes. |
| macOS | `~/Library/Application Support/RipgrepSearch.Desktop/settings.json` | Not read or written. |
| Linux | `$XDG_CONFIG_HOME/RipgrepSearch.Desktop/settings.json`; fallback `~/.config/RipgrepSearch.Desktop/settings.json` | Not read or written. |

Unset, empty and relative XDG_CONFIG_HOME values use the fallback, as required by the [XDG Base Directory specification](https://specifications.freedesktop.org/basedir/latest/). Paths are never based on the executable/current directory. If no absolute configuration root can be selected, persistence fails best-effort without writing into the checkout. Host path selection resides in DesktopEnvironment, not Core.

The existing ten JSON properties, defaults and indented format are reused. Loading runs on a worker after the window opens; inputs/Search remain disabled until initialization completes. It does not create, migrate, rewrite or save a file. The window can continue processing dispatcher work while the load is pending. Corrupt JSON, wrong field types and unreadable files fall back through the existing Core store. Desktop additionally normalizes null history lists/items, blanks, duplicates and excess entries in memory, clamps context values to 0–100 and defaults an unknown sort field to path. Normalization does not rewrite the file on startup.

Histories save after folder/nonblank-pattern/executable validation at search startup, before the engine outcome is known. A later regex/process failure still enters history, matching WinForms. Validation failures or a missing executable do not trigger a search-start save. Normal close also captures nonblank currently typed values, even if never searched. Selecting/typing/Browse alone does not write settings immediately, matching the existing ComboBox behavior.

Saves execute off the UI thread in order, using separate settings snapshots so an older pending save cannot overwrite a newer one within this window. Window close and the desktop Quit/shutdown request path wait asynchronously for initialization, active-search cleanup and the final queued write. Repeated close requests are guarded and Search stays disabled during shutdown. Save failures remain nonfatal. The shared Core store remains synchronous/non-atomic for its existing callers; no cross-process merge/atomic-write redesign was added. Separate Desktop instances still have the store's existing last-writer behavior, but neither reads/writes the stable Windows profile.

All existing option values survive Desktop load/save. Context, engine, sorting and visibility controls are not added or activated in Phase 5.2: searches still request standard rg, zero context and no sorting. Save-on-option-change wiring belongs to Phase 5.3.

## Histories and UI interaction

- Folder history: latest first, maximum 10, OrdinalIgnoreCase comparison on Windows and Ordinal comparison on macOS/Linux. Unix comparison is deliberately conservative for case-sensitive volumes. It can retain different spellings on a case-insensitive Mac volume.
- Pattern history: latest first, maximum 20, exact ordinal comparison on every OS. Case variants and significant trailing whitespace are retained. Whitespace-only entries are omitted.
- A new/promoted entry appears first and duplicates are removed. A startup folder absent from the choices is offered without saving or reordering the stored MRU; choices remain capped at 10. WinForms can temporarily offer an extra startup item above its persisted cap; Desktop keeps the requested 10-item limit.
- Editable ComboBoxes replace the two plain TextBoxes. Mouse selection, Alt+Down/F4, arrows, Enter and Escape are tested headlessly. Enter commits an open history choice before it reaches the default Search button; it does not run a search. Outside the dropdown, Enter retains its normal Search behavior. F4 in the results viewer retains file navigation.
- Automatic text completion is disabled so typing does not silently change a regex. Current input is preserved when history choices are refreshed. The pattern starts empty, rather than automatically selecting the most recent search, just as WinForms LoadHistory only populates its pattern list.
- Browse suggests the current valid folder through the storage provider, including a relative input resolved against the captured launch directory. Cancel/provider errors retain the existing behavior.

## Startup policy

1. A valid first command-line folder wins. Surrounding quote characters are removed as in the Windows launcher. Relative arguments resolve against the captured launch working directory; invalid/missing directories fall through.
2. On Windows, a valid launch working directory is next, preserving the Explorer/terminal startup behavior of WinForms. Then use the newest folder history entry, or home when neither is available.
3. On macOS/Linux, use the newest folder history entry, otherwise home. A GUI launch's `/` or bundle cwd is not selected automatically. An explicit relative argument still uses the captured working directory.
4. As in WinForms, a remembered folder is not removed just because it no longer exists. Searching validates it and reports a recoverable error.
5. Relative typed folder inputs also resolve against that captured launch directory. Windows trims surrounding input whitespace; Unix preserves literal path characters. Execution continues to receive the normalized absolute path, retaining Phase 4's result-path rendering behavior. Display-command/path presentation policy remains for Phase 5.3.

No search runs during startup. After load, the pattern input receives focus and the normal Search/Cancel state is available. Settings-path/startup directory checks and settings parsing occur on the initialization worker; constructors do not load settings. No shell profiles, registry PATH refresh or launch integrations were added.

## Core API and architecture

Only two existing settings types become public: **AppSettings** and **AppSettingsStore**. Their properties, defaults, Load/Save signatures and existing failure handling remain unchanged. No new friend assembly was added.

AppSettings gains one portable, I/O-free method: `WithRecentSearch(folder, pattern, folderComparer)`. It returns an independent snapshot, preserving all scalar preferences and copying/normalizing the two history lists. Empty supplied values only normalize existing history. A caller-supplied comparer keeps OS path policy in the host; pattern comparison remains ordinal. WinForms keeps its existing history code and never calls this new method, so no stable UI behavior was refactored. Desktop reuses Core's serializer/store instead of implementing a second JSON format.

DesktopEnvironment owns configuration-path conventions, startup precedence and folder-input policy. SearchViewModel owns the async settings lifecycle, UI history lists and ordered writes. MainWindow owns picker seeding, editable dropdown routing, focus and async close. SearchRunner, RipgrepJsonParser, ResultsDocument, ResultColorizer and RTF rendering were not changed.

## Validation performed

SDK 10.0.403, runtime 10.0.14, macOS ARM64, installed rg 15.2.0. Existing restored dependencies were used. Builds/tests ran outside the execution sandbox with shared compilation disabled, as in prior validation; no new dependency was needed.

| Check | Actual result |
| --- | --- |
| Core Release build, net8.0 and net10.0 | PASS, zero warnings/errors. |
| Portable Core suite, net10.0 | PASS, **133 checks**: previous 122 plus 11 MRU/snapshot cases. |
| Desktop Release build, net10.0 | PASS, zero warnings/errors. |
| Desktop headless/integration suite | PASS, **98 checks**: previous 30 plus 68 focused settings/startup/history checks. Uses the actual view with headless Skia and real macOS rg. |
| WinForms target/source isolation | Root project remains net8.0-windows, version 0.10.0; Windows source/settings code and compile boundary unchanged. Windows execution is not claimed on macOS. |
| .NET 8 test execution | Not run: ARM64 .NET 8 runtime remains absent. Both Core target frameworks build; .NET 10 tests pass. |
| Native GUI acceptance | Not performed; headless evidence is reported separately below. |

Commands:

```sh
dotnet build src/RipgrepSearch.Core/RipgrepSearch.Core.csproj -c Release --no-restore -p:UseSharedCompilation=false
dotnet run --project tests/RipgrepSearch.Core.Tests/RipgrepSearch.Core.Tests.csproj -c Release -f net10.0 --no-restore -p:UseSharedCompilation=false
dotnet build src/RipgrepSearch.Desktop/RipgrepSearch.Desktop.csproj -c Release --no-restore -p:UseSharedCompilation=false
dotnet run --project tests/RipgrepSearch.Desktop.Tests/RipgrepSearch.Desktop.Tests.csproj -c Release --no-restore -p:UseSharedCompilation=false
```

Focused automated coverage includes:

- Windows/Mac/Linux settings-path policy, XDG fallback rules and no cwd writes; a simulated stable profile remains byte-for-byte unchanged after Desktop operations and independent writes do not leak between profiles.
- New-view-model restart through actual on-disk JSON, ordered overlapping saves, MRU promotion/caps/case variants, significant pattern whitespace and independent snapshot lists.
- Missing, malformed, wrong-type and null-list/item settings; normalization without rewriting input; unwritable directory-as-file target and nonfatal close.
- All existing defaults, preserved future option values, unknown-sort/context normalization, no initialization save, invalid/relative/quoted/Unicode startup arguments and platform-specific cwd/history/home precedence.
- Actual editable controls with typed regex text, pointer-selected folder/pattern entries, Alt+Down/F4/arrow/Enter/Escape routing, current-text preservation during search-start history refresh, failed validation versus later-engine-error admission, relative-folder real rg execution and unsearched text saved on close.
- A deliberately delayed settings load running on a different thread while the dispatcher and window remain responsive; close during initialization waits, writes once initialization completes and cannot re-enable Search.
- All earlier real-search, Unicode/highlighting, selection/copy, batching, cancellation/descendant cleanup, close-during-search and result-limit scenarios remain passing. Performance output from the existing runner is headless smoke evidence, not a new native performance acceptance claim.

All test settings paths are injected under ignored build output; no real user profile or registry is used. Pure Windows/Linux path-policy branches are tested on macOS, not on NTFS or a Linux desktop. The existing Desktop process-launcher fixtures remain Unix-specific.

## Remaining native acceptance

- Launch the native app with no argument, valid/invalid absolute and relative arguments, from terminal and desktop; confirm Windows cwd versus macOS history/home defaults and initial editable-input focus.
- Choose recent values using mouse and keyboard, including popup focus, Tab, Enter, Escape and F4; verify selecting history never starts a search and normal Enter still does. Test real platform copy/paste/edit behavior inside editable ComboBoxes.
- Open Browse with an existing/relative folder, cancel, choose a spaced/Unicode folder and confirm the suggested start location. Actual picker behavior is provider-specific and is not proved by headless tests.
- Quit and relaunch the actual application after search, typing without search, and while a search/load/save is active. Confirm persisted MRUs and no stale processes. Automated restart is a fresh view-model/window lifecycle against disk, not an OS process restart.
- On Windows, run stable WinForms and Desktop side by side and run the existing Windows regression suite before merge. Confirm profile separation and that stable Explorer registrations still launch WinForms. No Windows native or Linux GUI test was run here.

## Files changed

Created: `src/RipgrepSearch.Desktop/DesktopEnvironment.cs`, `tests/RipgrepSearch.Core.Tests/HistoryTests.cs`, `tests/RipgrepSearch.Desktop.Tests/SettingsHistoryTests.cs`, and this document.

Modified: `src/RipgrepSearch.Core/AppSettings.cs`, `src/RipgrepSearch.Core/AppSettingsStore.cs`, `src/RipgrepSearch.Desktop/App.axaml.cs`, `src/RipgrepSearch.Desktop/MainWindow.axaml`, `src/RipgrepSearch.Desktop/MainWindow.axaml.cs`, `src/RipgrepSearch.Desktop/SearchViewModel.cs`, the Core/Desktop test runner Program.cs files, `README.md`, `docs/core-boundaries.md`, and `docs/phase5-feature-parity.md`.

Stop after Phase 5.2 for review. Context/sort/rga controls, View menus, command preview, report/export capture, dependency UI and native packaging remain deferred.
