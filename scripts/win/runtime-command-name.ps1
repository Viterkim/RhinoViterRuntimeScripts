function Resolve-RuntimeCommandName {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $trimmedName = $Name.Trim()
    $scriptName =
        if ($trimmedName.StartsWith("Rss", [StringComparison]::OrdinalIgnoreCase)) {
            $trimmedName.Substring(3)
        }
        else {
            $trimmedName
        }

    if ([string]::IsNullOrWhiteSpace($scriptName)) {
        throw "Enter a script name such as BingoManden. The Rss prefix is automatic."
    }

    $scriptName = $scriptName.Substring(0, 1).ToUpperInvariant() + $scriptName.Substring(1)

    if (-not [regex]::IsMatch($scriptName, '^[A-Z][A-Za-z0-9_]{0,59}$')) {
        throw "Use at most 60 letters, numbers, or underscores, starting with a letter."
    }

    [pscustomobject]@{
        Name = "Rss$scriptName"
        ScriptName = $scriptName
    }
}
