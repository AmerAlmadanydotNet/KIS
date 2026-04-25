param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$kis = Join-Path $PSScriptRoot 'kis.exe'
& $kis info $Path
Write-Host ''
if (-not $env:KIS_NO_PAUSE) { Read-Host 'Press Enter to close' }
