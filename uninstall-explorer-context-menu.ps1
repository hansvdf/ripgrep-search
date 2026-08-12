$ErrorActionPreference = 'Stop'

$paths = @(
    'HKCU:\Software\Classes\Directory\shell\RipgrepSearch',
    'HKCU:\Software\Classes\Directory\Background\shell\RipgrepSearch',
    'HKCU:\Software\Classes\Drive\shell\RipgrepSearch'
)

foreach ($path in $paths) {
    if (Test-Path $path) {
        Remove-Item -Path $path -Recurse -Force
    }
}

Write-Host 'Explorer context-menu entries removed.' -ForegroundColor Green
