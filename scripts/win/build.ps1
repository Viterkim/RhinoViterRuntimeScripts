param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [switch] $Clean,
    [switch] $SkipChecks,
    [switch] $SkipSetup,
    [switch] $Quiet,
    [int] $RhinoVersion = 0
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$buildSetup = Join-Path $PSScriptRoot "build-setup.ps1"
$contractProject = Join-Path $projectRoot "contracts\RhinoViterRuntimeScripts.Contracts.fsproj"
$hostProject = Join-Path $projectRoot "RhinoViterRuntimeScripts.fsproj"
$payloadProject = Join-Path $projectRoot "runtime\RhinoViterRuntimeScripts.Payload.fsproj"

function Invoke-DotNet {
    param([string[]] $Arguments)

    if ($Quiet) {
        $output = & dotnet @Arguments 2>&1
        $exitCode = $LASTEXITCODE

        if ($exitCode -ne 0) {
            $output | ForEach-Object { Write-Host $_ }
        }
    }
    else {
        & dotnet @Arguments
        $exitCode = $LASTEXITCODE
    }

    if ($exitCode -ne 0) { exit $exitCode }
}

if (-not $SkipSetup) {
    $setupParameters = @{ Quiet = $Quiet.IsPresent }

    if ($PSBoundParameters.ContainsKey("RhinoVersion")) {
        $setupParameters.RhinoVersion = $RhinoVersion
    }

    . $buildSetup @setupParameters
}

$properties = @(
    "-p:RhinoMajorVersion=$RhinoMajorVersion"
    "-p:TargetFramework=$TargetFramework"
    "-p:RhinoCommonPackageVersion=$RhinoCommonPackageVersion"
)

if ($SkipChecks) {
    $properties += "-p:RunSourceChecks=false"
}

$verbosity = if ($Quiet) { @("--nologo", "--verbosity", "quiet") } else { @() }

if ($Clean) {
    Invoke-DotNet (@("clean", $payloadProject, "--configuration", $Configuration) + $properties + $verbosity)
    Invoke-DotNet (@("clean", $hostProject, "--configuration", $Configuration) + $properties + $verbosity)
    Invoke-DotNet (@("clean", $contractProject, "--configuration", $Configuration) + $properties + $verbosity)
}

Invoke-DotNet (@("build", $contractProject, "--configuration", $Configuration) + $properties + $verbosity)

$hostArguments =
    @("build", $hostProject, "--configuration", $Configuration, "-p:BuildProjectReferences=false") +
    $properties +
    $verbosity

Invoke-DotNet $hostArguments

$payloadArguments =
    @("build", $payloadProject, "--configuration", $Configuration, "-p:BuildProjectReferences=false") +
    $properties +
    $verbosity

Invoke-DotNet $payloadArguments

if (-not $Quiet) {
    $host = Join-Path $projectRoot "bin\rh$RhinoMajorVersion\$Configuration\$TargetFramework\RhinoViterRuntimeScripts.rhp"
    $payload = Join-Path $projectRoot "bin\rh$RhinoMajorVersion\Payload\$Configuration\$TargetFramework\RhinoViterRuntimeScripts.Payload.dll"
    Write-Host "Built host: $host"
    Write-Host "Built payload: $payload"
}
