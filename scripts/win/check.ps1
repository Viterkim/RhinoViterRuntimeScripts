param()

Set-StrictMode -Version Latest

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$check = Join-Path $projectRoot "tools\check-all.fsx"
$tools = Join-Path $projectRoot "tools"
$styleCheck = Join-Path $projectRoot "tools\check-source-style.fsx"
$sources = @(
    (Join-Path $projectRoot "src")
    (Join-Path $projectRoot "contracts")
)

Push-Location $projectRoot
try {
    foreach ($source in $sources) {
        & dotnet fsi $check -- $source

        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }

    & dotnet fsi $styleCheck -- $tools

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    Write-Host "Source checks passed."
}
finally {
    Pop-Location
}
