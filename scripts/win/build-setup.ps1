param(
    [int] $RhinoVersion = 0,
    [switch] $Quiet,
    [switch] $MatrixOnly
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $projectRoot "RhinoViterRuntimeScripts.fsproj"

function Get-RhinoBuildProperties {
    param([int] $Major)

    $arguments = @(
        "msbuild"
        $project
        "-nologo"
        "-verbosity:quiet"
        "-getProperty:RhinoMajorVersion"
        "-getProperty:RhinoSupportedVersions"
        "-getProperty:RhinoExperimentalVersions"
        "-getProperty:RhinoBuildVersions"
        "-getProperty:RhinoReleaseVersions"
        "-getProperty:TargetFramework"
        "-getProperty:RhinoCommonPackageVersion"
    )

    if ($Major -ne 0) {
        $arguments += "-p:RhinoMajorVersion=$Major"
    }

    $output = & dotnet @arguments

    if ($LASTEXITCODE -ne 0) {
        throw "Could not read the Rhino build matrix from '$project'."
    }

    try {
        return (($output -join [Environment]::NewLine) | ConvertFrom-Json).Properties
    }
    catch {
        throw "Could not parse the Rhino build matrix from '$project': $($_.Exception.Message)"
    }
}

function Convert-VersionList {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return @()
    }

    return @(
        $Value.Split(';', [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { [int] $_.Trim() }
    )
}

function Find-RhinoInstallation {
    param([int] $Major)

    $installPath = $null
    $registry = "HKLM:\SOFTWARE\McNeel\Rhinoceros\$Major.0\Install"

    if ($null -ne (Get-PSDrive -Name HKLM -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath $registry)) {
        $installPath = (Get-ItemProperty -LiteralPath $registry -ErrorAction SilentlyContinue).InstallPath
    }

    if (-not $installPath) {
        $candidate = Join-Path $env:ProgramFiles "Rhino $Major"
        if (Test-Path -LiteralPath $candidate) { $installPath = $candidate }
    }

    if ($installPath) {
        $executable = Join-Path $installPath "System\Rhino.exe"

        if (Test-Path -LiteralPath $executable) {
            return [PSCustomObject]@{
                Major = $Major
                Install = $installPath
            }
        }
    }

    return $null
}

function Get-RunningRhinoProcess {
    param([string] $ExecutablePath)

    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
        return @()
    }

    $targetPath = [IO.Path]::GetFullPath($ExecutablePath)

    return @(
        Get-Process -Name "Rhino" -ErrorAction SilentlyContinue |
            Where-Object {
                $processPath = $null

                try {
                    $processPath = $_.Path
                }
                catch {
                    $processPath = $null
                }

                if ([string]::IsNullOrWhiteSpace($processPath)) {
                    try {
                        $processPath = $_.MainModule.FileName
                    }
                    catch {
                        $processPath = $null
                    }
                }

                -not [string]::IsNullOrWhiteSpace($processPath) -and
                    [string]::Equals(
                        [IO.Path]::GetFullPath($processPath),
                        $targetPath,
                        [StringComparison]::OrdinalIgnoreCase
                    )
            }
    )
}

$requestedVersion = $RhinoVersion

if (-not $MatrixOnly -and $requestedVersion -eq 0 -and -not [string]::IsNullOrWhiteSpace($env:RHINO_VITER_RUNTIME_SCRIPTS_RHINO_VERSION)) {
    $environmentVersion = 0

    if (-not [int]::TryParse($env:RHINO_VITER_RUNTIME_SCRIPTS_RHINO_VERSION.Trim(), [ref] $environmentVersion)) {
        throw "RHINO_VITER_RUNTIME_SCRIPTS_RHINO_VERSION must be an integer, not '$($env:RHINO_VITER_RUNTIME_SCRIPTS_RHINO_VERSION)'."
    }

    $requestedVersion = $environmentVersion
}

$matrixMajor = if ($MatrixOnly) { 0 } else { $requestedVersion }
$matrixProperties = Get-RhinoBuildProperties -Major $matrixMajor
$SupportedRhinoVersions = Convert-VersionList $matrixProperties.RhinoSupportedVersions
$ExperimentalRhinoVersions = Convert-VersionList $matrixProperties.RhinoExperimentalVersions
$BuildRhinoVersions = Convert-VersionList $matrixProperties.RhinoBuildVersions
$ReleaseRhinoVersions = Convert-VersionList $matrixProperties.RhinoReleaseVersions

if ($MatrixOnly) {
    return
}

if ($requestedVersion -eq 0) {
    foreach ($major in @($BuildRhinoVersions | Sort-Object -Descending)) {
        if ($null -ne (Find-RhinoInstallation -Major $major)) {
            $requestedVersion = $major
            break
        }
    }
}

if ($requestedVersion -eq 0) {
    $requestedVersion = [int] $matrixProperties.RhinoMajorVersion
}

$properties =
    if ([int] $matrixProperties.RhinoMajorVersion -eq $requestedVersion) {
        $matrixProperties
    }
    else {
        Get-RhinoBuildProperties -Major $requestedVersion
    }
$resolvedVersion = [int] $properties.RhinoMajorVersion

if ($resolvedVersion -notin $BuildRhinoVersions) {
    $allowed = $BuildRhinoVersions -join ", "
    throw "RhinoVersion must be one of $allowed, not '$resolvedVersion'."
}

$resolvedTargetFramework = [string] $properties.TargetFramework
$resolvedPackageVersion = [string] $properties.RhinoCommonPackageVersion

if ([string]::IsNullOrWhiteSpace($resolvedTargetFramework)) {
    throw "The Rhino $resolvedVersion build profile has no target framework."
}

if ([string]::IsNullOrWhiteSpace($resolvedPackageVersion)) {
    throw "The Rhino $resolvedVersion build profile has no RhinoCommon package version."
}

$rhino = Find-RhinoInstallation -Major $resolvedVersion

if ($null -eq $rhino) {
    $rhino = [PSCustomObject]@{
        Major = $resolvedVersion
        Install = ""
    }
}

$resolvedYakPath = ""

if (-not [string]::IsNullOrWhiteSpace($rhino.Install)) {
    $candidateYak = Join-Path $rhino.Install "System\Yak.exe"
    if (Test-Path -LiteralPath $candidateYak) { $resolvedYakPath = $candidateYak }
}

if ([string]::IsNullOrWhiteSpace($resolvedYakPath)) {
    foreach ($major in @($BuildRhinoVersions | Sort-Object -Descending)) {
        $yakRhino = Find-RhinoInstallation -Major $major

        if ($null -ne $yakRhino) {
            $candidateYak = Join-Path $yakRhino.Install "System\Yak.exe"

            if (Test-Path -LiteralPath $candidateYak) {
                $resolvedYakPath = $candidateYak
                break
            }
        }
    }
}

$RhinoMajorVersion = $resolvedVersion
$RhinoInstallDir = [string] $rhino.Install
$RhinoCommonPackageVersion = $resolvedPackageVersion
$TargetFramework = $resolvedTargetFramework
$YakPath = $resolvedYakPath

if (-not $Quiet) {
    Write-Host "Configured Rhino $RhinoMajorVersion"
    Write-Host "RhinoCommon: NuGet $RhinoCommonPackageVersion"
    Write-Host "Target: $TargetFramework"
}

