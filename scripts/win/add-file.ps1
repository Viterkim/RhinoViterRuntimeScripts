[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string] $Name,

    [string] $Before = "src/Commands/CommandList.fs"
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$rootPrefix = $repoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$projectPath = Join-Path $repoRoot "runtime\RhinoViterRuntimeScripts.Payload.fsproj"

function Get-SourceDetails {
    param([string] $Requested)

    $trimmed = $Requested.Trim()
    $fullPath =
        if ([IO.Path]::IsPathRooted($trimmed)) {
            [IO.Path]::GetFullPath($trimmed)
        }
        else {
            [IO.Path]::GetFullPath((Join-Path $repoRoot $trimmed))
        }

    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Source file must stay inside '$repoRoot'."
    }

    if (-not [string]::Equals([IO.Path]::GetExtension($fullPath), ".fs", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Only .fs source files can be added, not '$fullPath'."
    }

    $repoRelative = $fullPath.Substring($rootPrefix.Length).Replace('\', '/')

    [pscustomobject]@{
        FullPath = $fullPath
        RepoRelative = $repoRelative
        ProjectInclude = "../$repoRelative"
    }
}

$source = Get-SourceDetails $Name
$beforeSource = Get-SourceDetails $Before

if (-not (Test-Path -LiteralPath $source.FullPath -PathType Leaf)) {
    throw "Source file was not found: '$($source.FullPath)'."
}

$content = [IO.File]::ReadAllText($projectPath)
$compileMatches = [regex]::Matches($content, '(?m)^(?<indent>[ \t]*)<Compile\b[^>]*\/>[ \t]*$')
$includes = @(
    $compileMatches | ForEach-Object {
        $match = [regex]::Match($_.Value, 'Include="(?<path>[^"]+)"')
        if ($match.Success) { $match.Groups["path"].Value.Replace('\', '/') }
    }
)

if ($includes -contains $source.ProjectInclude) {
    throw "'$($source.RepoRelative)' is already included in the runtime payload."
}

$beforeMatches = @(
    $compileMatches | Where-Object {
        $match = [regex]::Match($_.Value, 'Include="(?<path>[^"]+)"')
        $match.Success -and [string]::Equals(
            $match.Groups["path"].Value.Replace('\', '/'),
            $beforeSource.ProjectInclude,
            [StringComparison]::OrdinalIgnoreCase
        )
    }
)

if ($beforeMatches.Count -ne 1) {
    throw "Expected one '$($beforeSource.ProjectInclude)' compile entry in '$projectPath', found $($beforeMatches.Count)."
}

$newline = if ($content.Contains("`r`n")) { "`r`n" } else { "`n" }
$beforeCompile = $beforeMatches[0]
$escapedInclude = [Security.SecurityElement]::Escape($source.ProjectInclude)
$entry = "$($beforeCompile.Groups['indent'].Value)<Compile Include=`"$escapedInclude`" />$newline"
$updated = $content.Insert($beforeCompile.Index, $entry)
$utf8WithoutBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($projectPath, $updated, $utf8WithoutBom)

Write-Host "Added $($source.RepoRelative) before $($beforeSource.RepoRelative)."
