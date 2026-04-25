param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$kis = Join-Path $PSScriptRoot 'kis.exe'
$dest = [System.IO.Path]::Combine(
    [System.IO.Path]::GetDirectoryName($Path),
    [System.IO.Path]::GetFileNameWithoutExtension($Path))
& $kis extract $Path -o $dest --force
Write-Host ''
if (-not $env:KIS_NO_PAUSE) { Read-Host 'Done. Press Enter to close' }
