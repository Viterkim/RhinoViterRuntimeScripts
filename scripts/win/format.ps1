param([switch] $Check)

Set-StrictMode -Version Latest

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$source = Join-Path $projectRoot "src"
$contracts = Join-Path $projectRoot "contracts"
$tools = Join-Path $projectRoot "tools"

$arguments = @($source, $contracts, $tools)

if ($Check) {
    $arguments = @("--check") + $arguments
}

Push-Location $projectRoot

try {
    $restoreOutput = & dotnet tool restore --verbosity quiet 2>&1
    $restoreExitCode = $LASTEXITCODE

    if ($restoreExitCode -ne 0) {
        $restoreOutput | ForEach-Object { Write-Host $_ }
        exit $restoreExitCode
    }

    dotnet fantomas @arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
