param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [switch] $Clean,
    [switch] $SkipBuild,
    [switch] $SkipSetup,
    [int] $RhinoVersion = 0
)

$ErrorActionPreference = "Stop"

$pluginName = "RhinoViterRuntimeScripts"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$buildSetup = Join-Path $PSScriptRoot "build-setup.ps1"
$buildScript = Join-Path $PSScriptRoot "build.ps1"
$runtimeBuildScript = Join-Path $PSScriptRoot "build-runtime.ps1"
$assemblyInfo = Join-Path $repoRoot "src\AssemblyInfo.fs"
$pluginIdMatches = @(
    Select-String -Path $assemblyInfo -Pattern 'assembly:\s*Guid\("([0-9A-Fa-f-]+)"\)' -AllMatches |
        ForEach-Object { $_.Matches }
)

if ($pluginIdMatches.Count -ne 1) {
    throw "Expected one plug-in GUID in '$assemblyInfo', found $($pluginIdMatches.Count)."
}

$pluginId = $pluginIdMatches[0].Groups[1].Value

$setupParameters = @{ Quiet = $true }

if ($PSBoundParameters.ContainsKey("RhinoVersion")) {
    $setupParameters.RhinoVersion = $RhinoVersion
}

if (-not $SkipSetup) {
    . $buildSetup @setupParameters
}

if (-not [string]::IsNullOrWhiteSpace($RhinoInstallDir)) {
    $rhinoExecutable = Join-Path $RhinoInstallDir "System\Rhino.exe"
    $runningRhino = @(Get-RunningRhinoProcess -ExecutablePath $rhinoExecutable)

    if ($runningRhino.Count -gt 0) {
        throw "Rhino $RhinoMajorVersion is running and may have the plug-in file locked. Save your work, close Rhino $RhinoMajorVersion, then run build-and-install.ps1 again."
    }
}

$buildParameters = @{
    Configuration = $Configuration
    Clean = $Clean.IsPresent
    RhinoVersion = [int] $RhinoMajorVersion
    SkipSetup = $true
    Quiet = $true
}

if (-not $SkipBuild) {
    & $buildScript @buildParameters
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ([string]::IsNullOrWhiteSpace($RhinoInstallDir)) {
    throw "Rhino $RhinoMajorVersion is not installed. The plug-in was built, but direct installation requires Rhino $RhinoMajorVersion."
}

$buildOutput = Join-Path $repoRoot "bin\rh$RhinoMajorVersion\$Configuration\$TargetFramework"
$builtPluginFile = Join-Path $buildOutput "RhinoViterRuntimeScripts.rhp"
$devInstallRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "bin\RhinoViterRuntimeScriptsDev"))
$devInstallDirectory = [IO.Path]::GetFullPath((Join-Path $devInstallRoot "rh$RhinoMajorVersion"))
$pluginFile = Join-Path $devInstallDirectory "RhinoViterRuntimeScripts.rhp"
$registryPath = "HKCU:\Software\McNeel\Rhinoceros\$RhinoMajorVersion.0\Plug-ins\$pluginId"
$pluginRegistryPath = Join-Path $registryPath "PlugIn"
$commandListRegistryPath = Join-Path $registryPath "CommandList"
$machineRegistryPath = "HKLM:\Software\McNeel\Rhinoceros\$RhinoMajorVersion.0\Plug-ins\$pluginId"
$machinePluginRegistryPath = Join-Path $machineRegistryPath "PlugIn"
$machineCommandListRegistryPath = Join-Path $machineRegistryPath "CommandList"

if (-not (Test-Path -LiteralPath $builtPluginFile)) {
    throw "The build succeeded but '$builtPluginFile' was not found."
}

$expectedInstallPrefix = $devInstallRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar

if (-not $devInstallDirectory.StartsWith($expectedInstallPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace unexpected development install path '$devInstallDirectory'."
}

if (Test-Path -LiteralPath $devInstallDirectory) {
    Remove-Item -LiteralPath $devInstallDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $devInstallDirectory | Out-Null

$developmentFiles = @(
    $builtPluginFile
    (Join-Path $buildOutput "RhinoViterRuntimeScripts.pdb")
    (Join-Path $buildOutput "RhinoViterRuntimeScripts.deps.json")
    (Join-Path $buildOutput "RhinoViterRuntimeScripts.runtimeconfig.json")
)

foreach ($file in $developmentFiles) {
    if (Test-Path -LiteralPath $file) {
        Copy-Item -LiteralPath $file -Destination $devInstallDirectory -Force
    }
}

foreach ($file in Get-ChildItem -LiteralPath $buildOutput -Filter "*.dll" -File) {
    Copy-Item -LiteralPath $file.FullName -Destination $devInstallDirectory -Force
}

foreach ($directory in Get-ChildItem -LiteralPath $buildOutput -Directory) {
    Copy-Item -LiteralPath $directory.FullName -Destination $devInstallDirectory -Recurse -Force
}

& $runtimeBuildScript -Configuration $Configuration -RhinoVersion $RhinoMajorVersion -SkipBuild
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$existingRegistration = Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue
$existingPluginFile = if ($null -eq $existingRegistration) { "" } else { [string] $existingRegistration.FileName }
$registrationComplete =
    $null -ne $existingRegistration -and
    (Test-Path -LiteralPath $pluginRegistryPath) -and
    (Test-Path -LiteralPath $commandListRegistryPath)
$machineRegistrationComplete =
    (Test-Path -LiteralPath $machineRegistryPath) -and
    (Test-Path -LiteralPath $machinePluginRegistryPath) -and
    (Test-Path -LiteralPath $machineCommandListRegistryPath)
$machinePluginFile = ""
$registrationScope = ""

if ($registrationComplete) {
    $registeredPluginFile =
        (Get-ItemProperty -LiteralPath $pluginRegistryPath -ErrorAction SilentlyContinue).FileName

    if (-not [string]::IsNullOrWhiteSpace($registeredPluginFile)) {
        $existingPluginFile = [string] $registeredPluginFile
    }
}

if ($machineRegistrationComplete) {
    $machinePluginFile =
        [string] (Get-ItemProperty -LiteralPath $machinePluginRegistryPath -ErrorAction SilentlyContinue).FileName
}

if (
    -not [string]::IsNullOrWhiteSpace($existingPluginFile) -and
    -not [string]::Equals($existingPluginFile, $pluginFile, [StringComparison]::OrdinalIgnoreCase)
) {
    Write-Warning "Replacing the existing RhinoViterRuntimeScripts registration at '$existingPluginFile'. Uninstall any Package Manager copy to prevent it from reclaiming this plugin GUID."
}

if ($registrationComplete) {
    New-ItemProperty -Path $registryPath -Name "Name" -Value $pluginName -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "LoadMode" -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $pluginRegistryPath -Name "FileName" -Value $pluginFile -PropertyType String -Force | Out-Null
    Remove-ItemProperty -Path $registryPath -Name "FileName" -ErrorAction SilentlyContinue
    $registrationScope = "current user"
}
elseif (
    $machineRegistrationComplete -and
    [string]::Equals($machinePluginFile, $pluginFile, [StringComparison]::OrdinalIgnoreCase)
) {
    if (Test-Path -LiteralPath $registryPath) {
        Write-Host "Removing incomplete per-user RhinoViterRuntimeScripts registration."
        Remove-Item -LiteralPath $registryPath -Recurse -Force
    }

    Write-Host "Using the complete machine-wide RhinoViterRuntimeScripts registration."
    $registrationScope = "machine-wide"
}
else {
    if (Test-Path -LiteralPath $registryPath) {
        Write-Host "Removing incomplete per-user RhinoViterRuntimeScripts registration."
        Remove-Item -LiteralPath $registryPath -Recurse -Force
    }

    throw "Rhino $RhinoMajorVersion has no completed RhinoViterRuntimeScripts registration. Open Rhino, install '$pluginFile' once through Options > Plug-ins, close Rhino, then run build-and-install.ps1 again."
}

Write-Host "Installed for Rhino $RhinoMajorVersion ($registrationScope): $pluginFile"
