param(
    [string]$ExecutablePath = (Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\win-x64\publish\RipgrepSearch.exe')
)

$ErrorActionPreference = 'Stop'
$ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)

if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw "RipgrepSearch.exe was not found at:`n$ExecutablePath`n`nRun publish-single-exe.bat first."
}

$menuLocations = @(
    @{ Path = 'HKCU:\Software\Classes\Directory\shell\RipgrepSearch'; Argument = '%1' },
    @{ Path = 'HKCU:\Software\Classes\Directory\Background\shell\RipgrepSearch'; Argument = '%V' },
    @{ Path = 'HKCU:\Software\Classes\Drive\shell\RipgrepSearch'; Argument = '%1' }
)

foreach ($menu in $menuLocations) {
    New-Item -Path $menu.Path -Force | Out-Null
    Set-ItemProperty -Path $menu.Path -Name '(default)' -Value 'Search with RipgrepSearch'
    Set-ItemProperty -Path $menu.Path -Name 'Icon' -Value $ExecutablePath

    $commandPath = Join-Path $menu.Path 'command'
    New-Item -Path $commandPath -Force | Out-Null
    Set-ItemProperty -Path $commandPath -Name '(default)' -Value ('"{0}" "{1}"' -f $ExecutablePath, $menu.Argument)
}

Write-Host 'Explorer context-menu entries installed successfully.' -ForegroundColor Green
Write-Host 'You can now right-click a folder, drive, or empty folder background.'
