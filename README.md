# Ripgrep Search

**Ripgrep Search** is a lightweight Windows desktop application for searching files through a graphical interface. It uses [ripgrep (`rg`)](https://github.com/BurntSushi/ripgrep) for fast text searches and optionally [ripgrep-all (`rga`)](https://github.com/phiresky/ripgrep-all) to search inside PDFs, Office documents, archives, and other supported formats.

## Features

- **Flexible searching:** Search folders using text or regular expressions (for example, `word1|word2`), with configurable lines of context before and after matches.
- **Document searching:** Switch to `rga` with the **Search Office/PDF files with rga** checkbox.
- **Readable results:** Dark results pane with highlighted matches, colored filenames and line numbers; double-click a filename to open it in its default Windows application.
- **Sorting:** Optionally sort files by path, modification time, access time, or creation time, in ascending (`--sort`) or descending (`--sortr`) order.
- **Export:** Save search results as HTML (with highlighting and file links) or Markdown.
- **View menu:** Show or hide search options and the generated command independently. Hidden options retain their selected values and remain active.
- **Saved preferences:** Remember the last 10 folders, last 20 search patterns, context counts, sorting choices, visibility settings, and `rga` mode.
- **Explorer integration:** Launch searches from the Windows Explorer address bar or an optional right-click menu.
- **Dependency checker:** Verify installed tools, versions, and executable paths through **Help → Check Dependencies**.

## Requirements

- Windows 10 or Windows 11.

## External Dependencies

- **ripgrep (`rg`):** Required for standard text and regex searches. It must be accessible through `PATH`.
- **ripgrep-all (`rga`):** Optional. Enable **Search Office/PDF files with rga** to search supported document and archive formats. This mode requires `rga`, the `rg` backend it invokes, and any converters needed for the selected formats (for example, `pandoc` for DOCX or `pdftotext` for PDF). Supported formats depend on the installed rga adapters and converters.

These tools are external to Ripgrep Search and are not bundled by the publish script. The application currently uses PATH-based resolution; it has no separate configured tool paths.

### Check dependencies in the application

Choose **Help → Check Dependencies**. The dialog checks `rg` and `rga` in the background and displays each tool's status, detected version, and full resolved executable path:

- **Installed:** The executable ran successfully with `--version` and returned recognizable version information for that tool.
- **Not found:** The application's executable lookup could not find the tool on its current `PATH`.
- **Error:** A path was found, but the executable could not run, returned a failure exit code or invalid version output, or timed out. The dialog keeps the resolved path visible and explains the error.

Searches and dependency checks share the same executable resolver. If a package manager provides a shim or launcher, its path is shown because that is the executable the application invokes. A version check times out after five seconds. The checker verifies the two commands themselves; it does not verify every document converter or guarantee that every file format can be searched.

When both tools are available, the dialog shows a single confirmation. Missing tools or errors show a summary with **Show details** to expand the diagnostic messages. Use **Refresh** to rerun the checks. Select a path cell and press **Ctrl+C** to copy the full path. An installation link appears only when its corresponding tool is **Not found** and opens that tool's official instructions; the checker does not install, download, or modify dependencies.

Use **Copy Diagnostics** to copy a plain-text troubleshooting report with the application and Windows versions, plus each tool's latest checked status, version, executable path, and any diagnostic messages. Paths under your Windows user profile are replaced with `%USERPROFILE%` in the copied report, including paths in diagnostic messages. The dialog still displays full paths. Copying does not rerun checks or include an environment-variable dump. The button becomes available after the first completed check and uses the last completed results while a refresh is running.

Opening the checker or clicking **Refresh** reloads the saved Windows system and current-user `PATH`, in that order. Checks and subsequent searches use the updated path without restarting Ripgrep Search. Environment-variable references in those paths are expanded using the latest saved values. Only this application's process environment is updated; Windows settings and other running applications are not modified.

Custom variables referenced by `PATH` must be present in the saved system or user settings. Deleted or unknown custom references remain unresolved rather than using stale inherited values. Windows-generated variables such as `USERPROFILE` and `SystemRoot` remain available as fallbacks.

Refreshing replaces any session-only PATH customizations inherited from a launching terminal or IDE with the saved Windows PATH. If reading or rebuilding that path fails, the dialog reports the error and the application's previous PATH is kept.

### Install ripgrep (`rg`)

Open PowerShell or Command Prompt and run:

```powershell
winget install BurntSushi.ripgrep.MSVC
```

Alternatively, download a Windows build from the [ripgrep releases](https://github.com/BurntSushi/ripgrep/releases).

### Install ripgrep-all (`rga`) — optional

On Windows, the [ripgrep-all installation documentation](https://github.com/phiresky/ripgrep-all#installation) recommends Chocolatey or Scoop so that dependencies are included. If you already use Chocolatey, run:

```powershell
choco install ripgrep-all
```

Or, if you use Scoop, run:

```powershell
scoop install rga
```

Windows binaries are also available from the [ripgrep-all releases](https://github.com/phiresky/ripgrep-all/releases), but manual downloads do not include the external dependencies. If installing manually, extract the archive and keep `rga.exe` and its accompanying files in a permanent directory, such as `C:\Tools\ripgrep-all`, then install the converters required for your document formats.

Some document formats require external converters. See the [ripgrep-all documentation](https://github.com/phiresky/ripgrep-all) for format-specific requirements.

### Add manually installed tools to `PATH`

Tools installed through WinGet may already be on `PATH`. For manually installed executables:

1. In the Windows Start menu, search for **Edit environment variables for your account**.
2. Under **User variables**, select **Path** and click **Edit**.
3. Click **New** and enter the **folder** containing `rg.exe` or `rga.exe` (not the full executable path).
4. Save the changes. In Ripgrep Search, choose **Help → Check Dependencies**, then click **Refresh**. Open a new terminal for command-line verification.

Open a **new** PowerShell or Command Prompt window to verify:

```powershell
rg --version
rga --version
```

The second command is only needed if you installed `rga`. Normal `rg` searches still work without it.

## Install Ripgrep Search

1. Open the repository's [Releases page](https://github.com/hansvdf/ripgrep-search/releases).
2. If a prebuilt Windows x64 ZIP archive is available, download it. Otherwise, follow **Build from source** below to create the executable.
3. Extract it to a permanent folder, such as `C:\Tools\RipgrepSearch`.
4. Install `rg` as described above. Install `rga` too if you want to search inside supported document formats.
5. Run `RipgrepSearch.exe`.

The published **self-contained** executable does not require a separate .NET runtime installation. `rg` and `rga` are external tools and are not included in the application executable.

## Use Ripgrep Search

Select a folder, enter a search pattern, and start the search. Regular expressions such as `word1|word2` are supported. Use the optional **Before** and **After** controls to include surrounding lines, and enable sorting if you want results ordered by file path or timestamps.

Select **Search Office/PDF files with rga** to use `rga` instead of `rg`. Leave it unchecked for normal text-file searches. If `rga` is unavailable, the application displays an error rather than switching silently to `rg`.

Use **View → Show Search Options** and **View → Show Command** to toggle the two sections independently. A checkmark indicates a visible section, and the results area expands when a section is hidden. Search options are shown and the command is hidden by default. The command preview continues to update as search settings change, including while hidden.

Highlights use the match positions reported by `rg` or `rga` through `--json`, including case-insensitive and Unicode matches. The application does not run the search pattern through a second regex engine. Zero-length matches count as matching lines but do not color an unrelated character.

During a search, **Search** becomes **Cancel**. Cancelling or closing the window stops the search and its converter processes. Results appear in batches while the interface remains available.

Small batches are also flushed while the search process pauses between results. Embedded NUL characters are displayed as **␀** in results and exports; the structured search data keeps the original characters and match positions.

The completion count is the number of matching output lines, not the number of individual occurrences. Multiple matches on one line count once. For `rga`, these are lines in the converted document text.

To keep large searches manageable, the application stops at 10,000 result lines (including context), an approximately 2,000,000-character result budget (including paths and formatting allowance), a result line longer than 16,384 characters, or a JSON message longer than 4,194,304 characters. When a limit is reached, the displayed count is explicitly partial. Refine the search to reduce the result set.

Cancelled or failed searches retain the results already shown and include a diagnostic message. Invalid UTF-8 text is displayed using replacement characters; file paths that cannot be decoded safely are displayed without an active file link.

Choose **Help → About** to see the application icon and version number.

### Export results

After a search, choose **File → Export Results…** or press **Ctrl+S**, then select HTML or Markdown.

Exports preserve the results and search details captured when that search ran, even if you subsequently change the input controls. HTML retains the displayed highlighting and file links; Markdown contains linked filename headings and code blocks. Both preserve result order and context lines.

File links refer to the original paths; they may stop working if files move, and some browsers or Markdown viewers restrict local file links. Exported reports do not contain copies of the source files.

Exports of cancelled, limited, or failed searches contain the results actually displayed and retain the incomplete-search status. They do not imply a full-search total.

## Windows Explorer integration

Explorer integration is optional. It opens Ripgrep Search with the selected folder or drive already entered in the Folder input, ready for you to enter a pattern and search. It does not run a search automatically.

The address-bar method and the right-click menu are independent: the address-bar method uses `PATH`, while the right-click menu registers the executable's full path for the current Windows user.

### Launch from the address bar

Add the directory containing `RipgrepSearch.exe` to your user `PATH` using the same procedure described above. Then open a folder in Windows Explorer, type `RipgrepSearch` in the address bar, and press Enter. The application opens with that folder selected.

You can also provide an explicit folder argument:

```cmd
RipgrepSearch "C:\Projects\Reports"
```

### Optional right-click menu

Use the integration scripts from a source checkout. First publish the application with `publish-single-exe.bat` and keep the executable at its intended location. The installer checks that the specified executable exists; `rg` and optional `rga` still need to be installed as described above.

#### Install

Run `install-explorer-context-menu.bat` from the source checkout. By default, it registers the executable at `bin\Release\net8.0-windows\win-x64\publish\RipgrepSearch.exe` relative to the scripts.

If you keep the executable elsewhere, open PowerShell in the directory containing the installer scripts and supply its full path:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install-explorer-context-menu.ps1 -ExecutablePath "C:\Tools\RipgrepSearch\RipgrepSearch.exe"
```

Keep the executable at the registered location. The installer adds **Search with RipgrepSearch** to the context menu for folders, folder backgrounds, and drives.

The installer writes to the current user's registry (`HKCU\Software\Classes`) and normally does not require administrator privileges. The batch wrapper invokes its accompanying PowerShell script; keep both files together when using the wrapper. If you move the executable, rerun the installer with its new full path.

#### Uninstall

Run `uninstall-explorer-context-menu.bat` from the source checkout, or run the PowerShell script directly:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\uninstall-explorer-context-menu.ps1
```

The uninstaller removes the three current-user context-menu registrations. It does not delete the application, saved settings, or external dependencies, and normally does not require administrator privileges. To remove address-bar launching as well, remove the application's directory from your user `PATH`.

## Settings

Preferences and recent search history are saved in:

```text
%APPDATA%\RipgrepSearch\settings.json
```

The View menu saves its choices immediately using the existing `ShowSearchOptions` and `ShowCommand` settings. Existing saved choices are preserved; when preferences are missing, search options are shown and the command is hidden.

## Build from source

These steps are for developers. If you downloaded a published executable, you do **not** need the .NET SDK or Visual Studio.

**Requirements:** .NET 10 SDK to build this migration checkout, using an IDE that supports that SDK or the command line. The existing Windows Forms application still targets .NET 8; its shared Core library targets both .NET 8 and .NET 10. Published application runtime requirements are unchanged.

`src/RipgrepSearch.Core` contains search models, JSON parsing, process execution, portable result formatting/report models, HTML/Markdown exports, settings serialization/storage, executable lookup, dependency probes, and diagnostic report formatting. The Windows Forms application retains its interface and RTF renderer, Windows settings location and PATH-refresh policy, OS integration, and publish scripts. See the [v0.10.0 migration baseline](docs/v0.10.0-baseline.md) for initial validation and the [Core boundaries](docs/core-boundaries.md) for the current extraction and compatibility checks.

Open `RipgrepSearch.csproj` in an IDE supporting the required SDK, or run the included `publish-single-exe.bat` to create a self-contained Windows x64 executable.

For development and testing, open a terminal in the project directory and run:

```powershell
dotnet run
```

This builds and launches the application without publishing it. To build without launching, run `build.bat` or `dotnet build -c Release`.

The publish script writes the executable to:

```text
bin\Release\net8.0-windows\win-x64\publish\RipgrepSearch.exe
```

The project includes `RipgrepSearch.ico`, embedded in the executable and used by the application's window and taskbar icon.

### Tests

Run the package-free regression suite on Windows:

```powershell
.\run-tests.ps1
```

The script runs from the project directory and returns the test command's exit code. The equivalent command is:

```powershell
dotnet run --project tests/RipgrepSearch.Tests/RipgrepSearch.Tests.csproj -c Release
```

`-c Release` selects the Release build configuration, which enables compiler optimizations. The command builds and runs the tests; it does not publish the application.

The suite requires `rg` and `rga` on `PATH`, plus the rga converters for DOCX and PDF (`pandoc` and `pdftotext`). It checks parsing and Unicode offsets, engine compatibility, matching-line count equivalence with the previous text output, sorting, exports, cancellation, process failures, display limits, and offscreen WinForms rendering. Dependency-checker tests cover installed and missing tools, unusable executables, invalid versions, failures, timeouts, refresh, path copying, and dialog closure. PATH-refresh tests simulate saved Windows settings to check additions, removals, ordering, environment-variable expansion, child-process inheritance, and refresh failures without modifying Windows environment settings. Test inputs and application settings use isolated folders under the test build output; tests do not change the normal application settings or install tools.

To run only the dependency-checker scenarios:

```powershell
dotnet run --project tests/RipgrepSearch.Tests/RipgrepSearch.Tests.csproj -c Release -- --dependencies-only
```

The shared Core also has a package-free portable regression runner. It references only Core, requires no Windows Forms or installed search tools, and uses its own child processes to exercise output streaming and failures. It checks formatting and complete exports against captured v0.10.0 fixtures, settings compatibility, diagnostic reports/redaction, and dependency resolution/probes:

```powershell
dotnet run --project tests/RipgrepSearch.Core.Tests/RipgrepSearch.Core.Tests.csproj -c Release -f net8.0
dotnet run --project tests/RipgrepSearch.Core.Tests/RipgrepSearch.Core.Tests.csproj -c Release -f net10.0
```

These portable checks supplement the existing real rg/rga integration and Windows UI tests; they do not establish macOS/Linux compatibility by themselves.

## Avalonia desktop preview (Phase 4)

The working Windows release remains **v0.10.0**. A separate, experimental .NET 10 desktop host now lives in `src/RipgrepSearch.Desktop`. It uses Avalonia **12.1.5** and AvaloniaEdit **12.0.0**, sharing the existing Core search pipeline. It is not yet a replacement for the Windows Forms application.

On macOS Apple Silicon, use the .NET 10 SDK and install `rg` separately. Verify `rg --version` in the terminal used to launch the preview, then run from the repository root:

```sh
dotnet run --project src/RipgrepSearch.Desktop/RipgrepSearch.Desktop.csproj -c Release
# Optional absolute starting folder:
dotnet run --project src/RipgrepSearch.Desktop/RipgrepSearch.Desktop.csproj -c Release -- "/path/with spaces/café"
```

The preview loads its own preferences asynchronously after opening the window. A valid first folder argument wins (relative arguments resolve against the launch directory). Windows then uses a valid launch working directory, followed by folder history and home; macOS/Linux use folder history and home without treating a GUI launch's working directory as the default. The pattern starts empty, with the last 20 patterns available in its editable dropdown. Folder history keeps 10 entries. Alt+Down or F4 opens a history dropdown; arrows and Enter choose an entry, Escape dismisses it. Selection does not start a search. Browse uses the native picker and suggests the current valid folder.

Enter outside a history dropdown or Search starts a search; Cancel or Escape stops it. Results stream into a read-only, black-background editor with Core's colors and highlighting. Select and copy across lines using the platform keyboard shortcuts. Double-click a filename, or put the caret in its heading and press F4, to request opening it in the default application.

Desktop settings are isolated from WinForms: `%APPDATA%\RipgrepSearch.Desktop\settings.json` on Windows, `~/Library/Application Support/RipgrepSearch.Desktop/settings.json` on macOS, and `$XDG_CONFIG_HOME/RipgrepSearch.Desktop/settings.json` (default `~/.config/RipgrepSearch.Desktop/settings.json`) on Linux. Relative XDG directories are ignored. No stable Windows settings are imported or changed. Histories are saved at validated search startup and normal close, including typed-but-unsearched values on close. Folder deduplication ignores case on Windows and is ordinal on Unix; patterns use exact ordinal comparison everywhere. See [Phase 5.2 settings/history validation](docs/phase5.2-settings-history.md).

The preview uses its inherited process `PATH`; it does not read shell profiles or refresh Windows registry settings. If `rg` is missing, launch from a terminal with the correct `PATH`. It does not use `rga` or provide menus, sorting controls, context controls, exports, or dependency-checker UI yet. Saved values for those existing preferences are retained for later phases but do not activate those features in this preview.

Build and validate on macOS:

```sh
dotnet build src/RipgrepSearch.Core/RipgrepSearch.Core.csproj -c Release
dotnet run --project tests/RipgrepSearch.Core.Tests/RipgrepSearch.Core.Tests.csproj -c Release -f net10.0
dotnet build src/RipgrepSearch.Desktop/RipgrepSearch.Desktop.csproj -c Release
dotnet run --project tests/RipgrepSearch.Desktop.Tests/RipgrepSearch.Desktop.Tests.csproj -c Release
```

The Desktop runner requires installed `rg`. It exercises the actual view using headless Skia, real searches, and Unix process fixtures, and prints performance observations. It currently targets macOS/Linux validation; its process-launcher fixtures are Unix-specific. Headless checks do not establish native GUI behavior. See [Phase 4 implementation and validation](docs/phase4-avalonia-prototype.md) for measured results, the public Core API, and the native acceptance checklist. The existing Windows regression suite must still run on Windows.

## License

Ripgrep Search is licensed under the MIT License.
See the [LICENSE](LICENSE) file for details.
