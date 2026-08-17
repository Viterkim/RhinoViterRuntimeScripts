[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string] $Name
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$rootPrefix = $repoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$projectPath = Join-Path $repoRoot "runtime\RhinoViterRuntimeScripts.Payload.fsproj"
$requestedPath = $Name.Trim()
$sourcePath =
    if ([IO.Path]::IsPathRooted($requestedPath)) {
        [IO.Path]::GetFullPath($requestedPath)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $repoRoot $requestedPath))
    }

if (-not $sourcePath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Source file must stay inside '$repoRoot'."
}

if (-not [string]::Equals([IO.Path]::GetExtension($sourcePath), ".fs", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Only .fs source files can be removed, not '$sourcePath'."
}

$repoRelative = $sourcePath.Substring($rootPrefix.Length).Replace('\', '/')
$projectInclude = "../$repoRelative"
$content = [IO.File]::ReadAllText($projectPath)
$compileMatches = [regex]::Matches($content, '(?m)^(?<indent>[ \t]*)<Compile\b[^>]*\/>[ \t]*$')
$sourceMatches = @(
    $compileMatches |
        Where-Object {
            $include = [regex]::Match($_.Value, 'Include="(?<path>[^"]+)"')

            $include.Success -and
            [string]::Equals(
                $include.Groups["path"].Value.Replace('\', '/'),
                $projectInclude,
                [StringComparison]::OrdinalIgnoreCase
            )
        }
)

if ($sourceMatches.Count -ne 1) {
    throw "Expected one '$projectInclude' compile entry in '$projectPath', found $($sourceMatches.Count)."
}

$compile = $sourceMatches[0]
$removeLength = $compile.Length
$afterCompile = $compile.Index + $compile.Length

if ($content.Substring($afterCompile).StartsWith("`r`n")) {
    $removeLength += 2
}
elseif ($content.Substring($afterCompile).StartsWith("`n")) {
    $removeLength += 1
}

$updated = $content.Remove($compile.Index, $removeLength)
$utf8WithoutBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($projectPath, $updated, $utf8WithoutBom)

if (Test-Path -LiteralPath $sourcePath -PathType Leaf) {
    Write-Host "Removed $repoRelative from the runtime payload. The source file remains on disk."
}
else {
    Write-Host "Removed $repoRelative from the runtime payload."
}

