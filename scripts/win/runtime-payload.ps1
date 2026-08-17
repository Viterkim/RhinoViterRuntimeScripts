function Get-RuntimePayloadFiles {
    param(
        [Parameter(Mandatory)] [string] $BuildOutput,
        [Parameter(Mandatory)] [string] $AssetsFile,
        [Parameter(Mandatory)] [string] $TargetFramework,
        [switch] $IncludeSymbols
    )

    $assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json
    $targets = @($assets.targets.PSObject.Properties | Where-Object { $_.Name -eq $TargetFramework -or $_.Name -like "$TargetFramework[0-9]*" })
    if ($targets.Count -ne 1) { throw "Expected one dependency target for $TargetFramework in '$AssetsFile'." }
    $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void] $files.Add('RhinoViterRuntimeScripts.rhp')
    [void] $files.Add('FSharp.Core.dll')
    $hostAssemblies = @('RhinoCommon.dll', 'Rhino.UI.dll', 'Eto.dll', 'Ed.Eto.dll')

    foreach ($library in $targets[0].Value.PSObject.Properties) {
        foreach ($kind in @('runtime', 'native', 'runtimeTargets', 'resource')) {
            $group = $library.Value.PSObject.Properties[$kind]
            if ($null -eq $group) { continue }
            foreach ($asset in $group.Value.PSObject.Properties) {
                $name = [IO.Path]::GetFileName($asset.Name)
                if ($name -eq '_._' -or $name -in $hostAssemblies) { continue }
                $relative = switch ($kind) {
                    'resource' { "$($asset.Value.locale)/$name" }
                    'runtimeTargets' { $asset.Name }
                    default { $name }
                }
                [void] $files.Add($relative)
            }
        }
    }

    if ($TargetFramework -ne 'net48') { [void] $files.Add('RhinoViterRuntimeScripts.deps.json') }
    foreach ($name in @('RhinoViterRuntimeScripts.runtimeconfig.json', 'RhinoViterRuntimeScripts.rhp.config')) {
        if (Test-Path -LiteralPath (Join-Path $BuildOutput $name)) { [void] $files.Add($name) }
    }
    if ($IncludeSymbols -and (Test-Path -LiteralPath (Join-Path $BuildOutput 'RhinoViterRuntimeScripts.pdb'))) {
        [void] $files.Add('RhinoViterRuntimeScripts.pdb')
    }

    foreach ($relative in $files) {
        $path = [IO.Path]::GetFullPath((Join-Path $BuildOutput $relative))
        $prefix = [IO.Path]::GetFullPath($BuildOutput).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid runtime asset '$relative'." }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing runtime dependency '$path'." }
        $relative
    }
}

function Copy-RuntimePayload {
    param(
        [Parameter(Mandatory)] [string] $BuildOutput,
        [Parameter(Mandatory)] [string] $Destination,
        [Parameter(Mandatory)] [string] $AssetsFile,
        [Parameter(Mandatory)] [string] $TargetFramework,
        [switch] $IncludeSymbols
    )

    $files = @(Get-RuntimePayloadFiles -BuildOutput $BuildOutput -AssetsFile $AssetsFile -TargetFramework $TargetFramework -IncludeSymbols:$IncludeSymbols)
    foreach ($relative in $files) {
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $BuildOutput $relative) -Destination $target -Force
    }
}
