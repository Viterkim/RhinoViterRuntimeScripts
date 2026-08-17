param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [switch] $Clean
)

$ErrorActionPreference = "Stop"
$format = Join-Path $PSScriptRoot "format.ps1"
$check = Join-Path $PSScriptRoot "check.ps1"
$build = Join-Path $PSScriptRoot "build.ps1"
$buildSetup = Join-Path $PSScriptRoot "build-setup.ps1"

& $format -Check
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $check
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

. $buildSetup -Quiet -MatrixOnly

foreach ($rhinoVersion in $BuildRhinoVersions) {
    Write-Host "Building Rhino $rhinoVersion."
    & $build -Configuration $Configuration -Clean:$Clean.IsPresent -SkipChecks -Quiet -RhinoVersion $rhinoVersion

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$versions = $BuildRhinoVersions -join ", "
Write-Host "Built Rhino $versions."
