# Ripgrep Search — C# WinForms

This is the C# version of the PowerShell ripgrep search tool.

## Features

- Remembers the last 10 folders.
- Remembers the last 20 search patterns.
- Supports regular expressions such as `word1|word2`.
- Before/after context controls with counts remembered between sessions.
- Optional file sorting by path, modified, accessed, or created, in ascending (`--sort`) or descending (`--sortr`) order. Sorting preferences are remembered.
- Optional rg/rga command display.
- Independent **Show search options** and **Show command** checkboxes below the search inputs. Hiding search options collapses the context, rga, and sorting controls while keeping their selected values active. The visibility choices are remembered.
- Optional ripgrep-all (`rga`) mode for PDFs, Office documents, archives, e-books, and other converted formats.
- Dark result pane with colored filenames, line numbers, and matches.
- Double-click a blue filename to open it in its default Windows application.
- Export results to HTML with the displayed highlighting and file links, or to Markdown with linked filenames and code blocks.
- Stores settings in `%APPDATA%\RipgrepSearch\settings.json`.

## Requirements

- Windows 10 or Windows 11.
- `rg.exe` installed and available on PATH.
- Optional: `rga.exe` installed and available on PATH to search Office documents, PDFs, archives, e-books, and other supported formats. Some rga adapters require external converter tools.
- To build: .NET 8 SDK.

## Export search results

After a search, choose **File → Export Results…** (or press **Ctrl+S**) and select HTML or Markdown in the save dialog. Each report includes the folder, search pattern, command, search start time, and completion status from that search, even if you have since changed the controls.

HTML keeps the dark results background, colors, bold matches, and clickable filenames. Markdown uses linked filename headings and plain code blocks, without the exact colors. Both formats preserve result order and context lines, and support Unicode text.

Links point to the original files and require those paths to remain accessible. Browsers and Markdown viewers may restrict local file links or open files differently from the application. The export contains the displayed results, rather than copies of the searched files.

## Create the executable

Double-click:

`publish-single-exe.bat`

The finished standalone executable will be written to:

`bin\Release\net8.0-windows\win-x64\publish\RipgrepSearch.exe`

Because the publish script uses `--self-contained true`, the target computer does not need the .NET runtime installed. It still needs access to `rg.exe`. To use the document-search checkbox, it also needs access to `rga.exe` and any converter programs required by the selected file formats.

## Open in Visual Studio

Open `RipgrepSearch.csproj` in Visual Studio 2022 and run or publish it as a WinForms project.

## Launch from the Windows Explorer address bar

The application now uses the current working folder when no folder argument is supplied. After adding the folder containing `RipgrepSearch.exe` to your user `PATH`, open any folder in Explorer, type:

`RipgrepSearch`

in the address bar, and press Enter. The application opens with that Explorer folder selected.

You can also pass a folder explicitly:

`RipgrepSearch "C:\Projects\Reports"`

## Optional Explorer right-click menu

After publishing the executable, double-click:

`install-explorer-context-menu.bat`

This adds **Search with RipgrepSearch** when right-clicking:

- A folder.
- Empty space inside a folder.
- A drive.

The installer writes only to the current user's registry (`HKCU`), so administrator rights are not normally required.

To remove the entries, run:

`uninstall-explorer-context-menu.bat`


## Application icon

The project includes `RipgrepSearch.ico`. The icon is embedded into the executable and is also used by the main window, so it appears in the title bar, taskbar, Explorer, and shortcuts. Re-run `publish-single-exe.bat` after changing the icon.

## Search Office documents and PDFs

Enable **Search Office/PDF files with rga** to run the same search through `rga` instead of `rg`. The generated command preview changes accordingly. Disable the checkbox for the fastest normal text-file search.

The checkbox choice is remembered in `%APPDATA%\RipgrepSearch\settings.json`. If `rga.exe` is not on PATH, the application displays a specific error explaining what is missing.
