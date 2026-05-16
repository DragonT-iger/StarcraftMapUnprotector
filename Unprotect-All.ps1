param(
    [string]$Path = ".",
    [switch]$Recurse
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path -LiteralPath $Path
$option = if ($Recurse) { @{ Recurse = $true } } else { @{} }

Get-ChildItem -LiteralPath $root -File @option |
    Where-Object {
        $_.Extension -match '^\.(scx|scm)$' -and
        $_.Name -notmatch '\.unprotected\.(scx|scm)$' -and
        $_.Name -notmatch '^out_'
    } |
    ForEach-Object {
        $out = Join-Path $_.DirectoryName ($_.BaseName + ".unprotected" + $_.Extension)
        Write-Host "Unprotecting $($_.FullName)"
        & "$PSScriptRoot\StarcraftMapUnprotector.exe" $_.FullName $out
    }
