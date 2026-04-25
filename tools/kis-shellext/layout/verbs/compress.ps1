param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$kis = Join-Path $PSScriptRoot 'kis.exe'
$out = if (Test-Path -LiteralPath $Path -PathType Container) {
    $Path.TrimEnd('\') + '.kis'
} else {
    [System.IO.Path]::ChangeExtension($Path, '.kis')
}
& $kis create $out $Path -c lzma
Write-Host ''
if (-not $env:KIS_NO_PAUSE) { Read-Host 'Done. Press Enter to close' }
