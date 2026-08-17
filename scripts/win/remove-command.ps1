[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Name
)

$ErrorActionPreference = "Stop"
$nameHelper = Join-Path $PSScriptRoot "runtime-command-name.ps1"
. $nameHelper
$resolvedName = Resolve-RuntimeCommandName $Name
$Name = $resolvedName.Name
$scriptName = $resolvedName.ScriptName
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$projectPath = Join-Path $repoRoot "runtime\RhinoViterRuntimeScripts.Payload.fsproj"
$commandListPath = Join-Path $repoRoot "src\Commands\CommandList.fs"
$relativeCommandPath = "src/Commands/Rss/$scriptName.fs"
$commandPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $relativeCommandPath))
$commandRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "src\Commands\Rss"))
$commandPrefix = $commandRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar

if (-not $commandPath.StartsWith($commandPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove a file outside '$commandRoot'."
}

if (-not (Test-Path -LiteralPath $commandPath -PathType Leaf)) {
    throw "Runtime command source was not found: '$relativeCommandPath'."
}

$projectContent = [IO.File]::ReadAllText($projectPath)
$commandListContent = [IO.File]::ReadAllText($commandListPath)
$projectInclude = "../$relativeCommandPath"
$compilePattern =
    '(?m)^[ \t]*<Compile\s+Include="' + [regex]::Escape($projectInclude) + '"\s*/>[ \t]*(?:\r?\n)?'
$compileMatches = [regex]::Matches($projectContent, $compilePattern)

if ($compileMatches.Count -ne 1) {
    throw "Expected one '$projectInclude' compile entry, found $($compileMatches.Count)."
}

$entryPattern = 'command\s+"(?<name>[^"]+)"\s+"(?<id>[^"]+)"\s+(?<run>[A-Za-z0-9_.]+)'
$entries = @([regex]::Matches($commandListContent, $entryPattern))
$targetEntries = @($entries | Where-Object { $_.Groups['name'].Value -eq $Name })

if ($targetEntries.Count -ne 1) {
    throw "Expected one $Name entry in CommandList.fs, found $($targetEntries.Count)."
}

$remainingEntries = @($entries | Where-Object { $_.Groups['name'].Value -ne $Name })
$newline = if ($commandListContent.Contains("`r`n")) { "`r`n" } else { "`n" }

$definitions =
    if ($remainingEntries.Count -eq 0) {
        "let definitions = [||]"
    }
    else {
        $lines = @($remainingEntries | ForEach-Object { $_.Value.Trim() })
        $body = "    [| " + $lines[0]

        if ($lines.Count -gt 1) {
            $body += $newline + (($lines[1..($lines.Count - 1)] | ForEach-Object { "       $_" }) -join $newline)
        }

        "let definitions =$newline$body |]"
    }

$definitionsPattern = '(?ms)^let definitions\s*=.*?\|\][ \t]*(?:\r?\n)?$'

if (-not [regex]::IsMatch($commandListContent, $definitionsPattern)) {
    throw "CommandList.fs has no generated definitions array."
}

$updatedProject = [regex]::Replace($projectContent, $compilePattern, '', 1)
$updatedCommandList = [regex]::Replace($commandListContent, $definitionsPattern, $definitions + $newline, 1)
$utf8WithoutBom = [Text.UTF8Encoding]::new($false)

try {
    [IO.File]::WriteAllText($projectPath, $updatedProject, $utf8WithoutBom)
    [IO.File]::WriteAllText($commandListPath, $updatedCommandList, $utf8WithoutBom)
    Remove-Item -LiteralPath $commandPath -Force
}
catch {
    [IO.File]::WriteAllText($projectPath, $projectContent, $utf8WithoutBom)
    [IO.File]::WriteAllText($commandListPath, $commandListContent, $utf8WithoutBom)
    throw
}

Write-Host "Removed $relativeCommandPath"
Write-Host "Removed runtime command $Name."
