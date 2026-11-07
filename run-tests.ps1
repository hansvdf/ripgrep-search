$ErrorActionPreference = 'Stop'

Push-Location -LiteralPath $PSScriptRoot
try {
    dotnet run --project tests/RipgrepSearch.Tests/RipgrepSearch.Tests.csproj -c Release
    $testExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

exit $testExitCode
