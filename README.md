# Ripgrep Search

**Ripgrep Search** is a lightweight Windows desktop application for searching files through a graphical interface. It uses [ripgrep (`rg`)](https://github.com/BurntSushi/ripgrep) for fast text searches and optionally [ripgrep-all (`rga`)](https://github.com/phiresky/ripgrep-all) to search inside PDFs, Office documents, archives, and other supported formats.

## Features

- **Flexible searching:** Search folders using text or regular expressions (for example, `word1|word2`), with configurable lines of context before and after matches.
- **Document searching:** Switch to `rga` with the **Search Office/PDF files with rga** checkbox.
- **Readable results:** Dark results pane with highlighted matches, colored filenames and line numbers; double-click a filename to open it in its default Windows application.
- **Sorting:** Optionally sort files by path, modification time, access time, or creation time, in ascending (`--sort`) or descending (`--sortr`) order.
- **Export:** Save search results as HTML (with highlighting and file links) or Markdown.
- **Convenient controls:** Show or hide search options and the generated command independently. Hidden options retain their selected values and remain active.
- **Saved preferences:** Remember the last 10 folders, last 20 search patterns, context counts, sorting choices, visibility settings, and `rga` mode.
- **Explorer integration:** Launch searches from the Windows Explorer address bar or an optional right-click menu.

## Requirements

- Windows 10 or Windows 11.
- **Required:** `rg.exe` accessible through `PATH` for normal searches.
- **Optional:** `rga.exe` accessible through `PATH` for document searches. Some `rga` formats require additional converter programs.

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
4. Save the changes and restart open terminals and Ripgrep Search.

Open a **new** PowerShell or Command Prompt window to verify:

```powershell
rg --version
rga --version
```

The second command is only needed if you installed `rga`. Normal `rg` searches still work without it.

## Install Ripgrep Search

1. Open the repository's [Releases page](https://github.com/hansvdf/Ripgrep_Search/releases).
2. If a prebuilt Windows x64 ZIP archive is available, download it. Otherwise, follow **Build from source** below to create the executable.
3. Extract it to a permanent folder, such as `C:\Tools\RipgrepSearch`.
4. Install `rg` as described above. Install `rga` too if you want to search inside supported document formats.
5. Run `RipgrepSearch.exe`.

The published **self-contained** executable does not require a separate .NET runtime installation. `rg` and `rga` are external tools and are not included in the application executable.

## Use Ripgrep Search

Select a folder, enter a search pattern, and start the search. Regular expressions such as `word1|word2` are supported. Use the optional **Before** and **After** controls to include surrounding lines, and enable sorting if you want results ordered by file path or timestamps.

Select **Search Office/PDF files with rga** to use `rga` instead of `rg`. Leave it unchecked for normal text-file searches. If `rga` is unavailable, the application displays an error rather than switching silently to `rg`.

Use **Show search options** and **Show command** to control how much space the options and generated command occupy. The command preview updates as search settings change.

Highlights use the match positions reported by `rg` or `rga` through `--json`, including case-insensitive and Unicode matches. The application does not run the search pattern through a second regex engine. Zero-length matches count as matching lines but do not color an unrelated character.

During a search, **Search** becomes **Cancel**. Cancelling or closing the window stops the search and its converter processes. Results appear in batches while the interface remains available.

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

### Launch from the address bar

Add the directory containing `RipgrepSearch.exe` to your user `PATH` using the same procedure described above. Then open a folder in Windows Explorer, type `RipgrepSearch` in the address bar, and press Enter. The application opens with that folder selected.

You can also provide an explicit folder argument:

```cmd
RipgrepSearch "C:\Projects\Reports"
```

### Optional right-click menu

After publishing from a source checkout, run `install-explorer-context-menu.bat`. By default, it registers the executable at `bin\Release\net8.0-windows\win-x64\publish\RipgrepSearch.exe` relative to the scripts.

If you keep the executable elsewhere, open PowerShell in the directory containing the installer scripts and supply its full path:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install-explorer-context-menu.ps1 -ExecutablePath "C:\Tools\RipgrepSearch\RipgrepSearch.exe"
```

Keep the executable at the registered location. The installer adds **Search with RipgrepSearch** to the context menu for folders, folder backgrounds, and drives.

The installer uses the current user's registry (`HKCU`) and normally does not require administrator privileges. To remove the entries, run `uninstall-explorer-context-menu.bat` from the same package or checkout.

## Settings

Preferences and recent search history are saved in:

```text
%APPDATA%\RipgrepSearch\settings.json
```

## Build from source

These steps are for developers. If you downloaded a published executable, you do **not** need the .NET SDK or Visual Studio.

**Requirements:** .NET 8 SDK (or Visual Studio 2022 with the appropriate .NET desktop development tooling).

Open `RipgrepSearch.csproj` in Visual Studio 2022, or run the included `publish-single-exe.bat` to create a self-contained Windows x64 executable.

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

The suite requires `rg` and `rga` on `PATH`, plus the rga converters for DOCX and PDF (`pandoc` and `pdftotext`). It checks parsing and Unicode offsets, engine compatibility, matching-line count equivalence with the previous text output, sorting, exports, cancellation, process failures, display limits, and offscreen WinForms rendering. Test inputs and application settings use isolated folders under the test build output; tests do not change the normal application settings.

## License

Ripgrep Search is licensed under the MIT License.
See the [LICENSE](LICENSE) file for details.
