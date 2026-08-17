param()

Set-StrictMode -Version Latest

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$check = Join-Path $projectRoot "tools\check-all.fsx"
$sources = @(
    (Join-Path $projectRoot "src")
    (Join-Path $projectRoot "contracts")
)

foreach ($source in $sources) {
    & dotnet fsi $check -- $source

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

Write-Host "Source checks passed."
