param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [switch] $Clean,
    [switch] $SkipBuild,
    [ValidateSet("", "A", "B")]
    [string] $Slot = "",
    [int] $RhinoVersion = 9
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$buildSetup = Join-Path $PSScriptRoot "build-setup.ps1"
$payloadProject = Join-Path $projectRoot "runtime\RhinoViterRuntimeScripts.Payload.fsproj"

function Invoke-DotNetQuiet {
    param([string[]] $Arguments)

    $output = & dotnet @Arguments 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        $output | ForEach-Object { Write-Host $_ }
        exit $exitCode
    }
}

. $buildSetup -Quiet -RhinoVersion $RhinoVersion

$properties = @(
    "-p:RhinoMajorVersion=$RhinoMajorVersion"
    "-p:TargetFramework=$TargetFramework"
    "-p:RhinoCommonPackageVersion=$RhinoCommonPackageVersion"
    "-p:BuildProjectReferences=false"
)

if (-not $SkipBuild) {
    if ($Clean) {
        $cleanArguments =
            @("clean", $payloadProject, "--configuration", $Configuration, "--nologo", "--verbosity", "quiet") +
            $properties

        Invoke-DotNetQuiet $cleanArguments
    }

    $buildArguments =
        @(
            "build"
            $payloadProject
            "--configuration"
            $Configuration
            "--no-restore"
            "--nologo"
            "--verbosity"
            "quiet"
        ) +
        $properties

    Invoke-DotNetQuiet $buildArguments
}

$payloadOutput = Join-Path $projectRoot "bin\rh$RhinoMajorVersion\Payload\$Configuration\$TargetFramework"
$payloadFile = Join-Path $payloadOutput "RhinoViterRuntimeScripts.Payload.dll"
$installDirectory = Join-Path $projectRoot "bin\RhinoViterRuntimeScriptsDev\rh$RhinoMajorVersion"
$installedHost = Join-Path $installDirectory "RhinoViterRuntimeScripts.rhp"

if (-not (Test-Path -LiteralPath $payloadFile)) {
    throw "The runtime build succeeded but '$payloadFile' was not found."
}

if (-not (Test-Path -LiteralPath $installedHost)) {
    throw "The runtime host is not installed at '$installedHost'. Close Rhino and run build-and-install.ps1 first."
}

$runtimeDirectory = [IO.Path]::GetFullPath((Join-Path $installDirectory "runtime"))
$activeMarker = Join-Path $runtimeDirectory "active-slot.txt"
$activeSlot =
    if (Test-Path -LiteralPath $activeMarker) {
        [IO.File]::ReadAllText($activeMarker).Trim().ToUpperInvariant()
    }
    else {
        ""
    }

$targetSlot =
    if (-not [string]::IsNullOrWhiteSpace($Slot)) {
        $Slot.ToUpperInvariant()
    }
    elseif ($activeSlot -eq "A") {
        "B"
    }
    elseif ($activeSlot -eq "B") {
        "A"
    }
    else {
        "A"
    }

$slotDirectory = [IO.Path]::GetFullPath((Join-Path $runtimeDirectory $targetSlot))
$runtimePrefix = $runtimeDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar

if (-not $slotDirectory.StartsWith($runtimePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to publish outside '$runtimeDirectory'."
}

$staging = [IO.Path]::GetFullPath((Join-Path $runtimeDirectory ".$targetSlot-$PID"))
$payloadArtifacts = @(
    "RhinoViterRuntimeScripts.Payload.dll"
    "RhinoViterRuntimeScripts.Payload.pdb"
    "RhinoViterRuntimeScripts.Payload.deps.json"
    "RhinoViterRuntimeScripts.Payload.runtimeconfig.json"
)

New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $staging -Force | Out-Null
$published = $false

try {
    foreach ($artifact in $payloadArtifacts) {
        $source = Join-Path $payloadOutput $artifact

        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination $staging -Force
        }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $staging "RhinoViterRuntimeScripts.Payload.dll"))) {
        throw "The staged runtime payload has no DLL."
    }

    if (Test-Path -LiteralPath $slotDirectory) {
        Remove-Item -LiteralPath $slotDirectory -Recurse -Force
    }

    Move-Item -LiteralPath $staging -Destination $slotDirectory
    $published = $true
}
finally {
    if (-not $published -and (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

Write-Host "Published runtime payload."
Write-Host "Run RuntimeScriptsReload in Rhino $RhinoMajorVersion."
