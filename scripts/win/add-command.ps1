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
$addFileScript = Join-Path $PSScriptRoot "add-file.ps1"
$relativeCommandPath = "src/Commands/Rss/$scriptName.fs"
$commandPath = Join-Path $repoRoot $relativeCommandPath

if (Test-Path -LiteralPath $commandPath) {
    throw "Runtime command file already exists: '$commandPath'."
}

$projectContent = [IO.File]::ReadAllText($projectPath)
$commandListContent = [IO.File]::ReadAllText($commandListPath)

if ([regex]::IsMatch($commandListContent, "(?m)^\s*command\s+`"$([regex]::Escape($Name))`"\s")) {
    throw "Runtime command '$Name' is already registered."
}

$guid = [guid]::NewGuid().ToString().ToUpperInvariant()
$newline = if ($commandListContent.Contains("`r`n")) { "`r`n" } else { "`n" }
$source = @"
module RhinoViterRuntimeScripts.Commands.Rss.$scriptName

open global.RhinoViterRuntimeScripts
open Rhino
open Rhino.Commands

let run (_document: RhinoDoc) =
    RhinoApp.WriteLine "$Name ran."
    Result.Success
"@
$registration = "       command `"$Name`" `"$guid`" Commands.Rss.$scriptName.run"
$listEnd = $commandListContent.LastIndexOf("|]", [StringComparison]::Ordinal)

if ($listEnd -lt 0) {
    throw "CommandList.fs has no command array terminator."
}

$beforeListEnd = $commandListContent.Substring(0, $listEnd).TrimEnd()
$afterListEnd = $commandListContent.Substring($listEnd)
$updatedCommandList = $beforeListEnd + $newline + $registration + " " + $afterListEnd
$utf8WithoutBom = [Text.UTF8Encoding]::new($false)
$projectUpdated = $false

try {
    New-Item -ItemType Directory -Path (Split-Path -Parent $commandPath) -Force | Out-Null
    [IO.File]::WriteAllText($commandPath, $source.Trim() + $newline, $utf8WithoutBom)
    & $addFileScript -Name $relativeCommandPath -Before "src/Commands/CommandList.fs"
    $projectUpdated = $true
    [IO.File]::WriteAllText($commandListPath, $updatedCommandList, $utf8WithoutBom)
}
catch {
    if ($projectUpdated) {
        [IO.File]::WriteAllText($projectPath, $projectContent, $utf8WithoutBom)
    }

    [IO.File]::WriteAllText($commandListPath, $commandListContent, $utf8WithoutBom)

    if (Test-Path -LiteralPath $commandPath) {
        Remove-Item -LiteralPath $commandPath -Force
    }

    throw
}

Write-Host "Created $relativeCommandPath"
Write-Host "Registered Rhino command $Name with GUID $guid."
Write-Host "Edit the run function in that file."
