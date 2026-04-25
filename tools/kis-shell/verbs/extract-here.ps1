param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$kis = Join-Path $PSScriptRoot 'kis.exe'
$dir = [System.IO.Path]::GetDirectoryName($Path)
Push-Location $dir
try {
    & $kis extract $Path -o '.' --force
    Write-Host ''
    if (-not $env:KIS_NO_PAUSE) { Read-Host 'Done. Press Enter to close' }
} finally { Pop-Location }
