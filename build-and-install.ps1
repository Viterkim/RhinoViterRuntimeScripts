param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [switch] $Clean,
    [int] $RhinoVersion = 9
)

$ErrorActionPreference = "Stop"
$scripts = Join-Path $PSScriptRoot "scripts\win"
$build = Join-Path $scripts "build.ps1"
$installer = Join-Path $scripts "install-plugin.ps1"
$buildSetup = Join-Path $scripts "build-setup.ps1"

. $buildSetup -Quiet -RhinoVersion $RhinoVersion

& $build `
    -Configuration $Configuration `
    -Clean:$Clean.IsPresent `
    -RhinoVersion $RhinoVersion `
    -SkipSetup `
    -Quiet

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $installer `
    -Configuration $Configuration `
    -RhinoVersion $RhinoVersion `
    -SkipBuild `
    -SkipSetup

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$rhinoExecutable = Join-Path $RhinoInstallDir "System\Rhino.exe"

if (-not (Test-Path -LiteralPath $rhinoExecutable)) {
    throw "The build was installed, but Rhino.exe was not found at '$rhinoExecutable'."
}

Write-Host "Starting Rhino $RhinoMajorVersion."
Start-Process `
    -FilePath $rhinoExecutable `
    -WorkingDirectory (Split-Path -Parent $rhinoExecutable)
