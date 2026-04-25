param([Parameter(Mandatory)][string]$Dir)
$ErrorActionPreference = 'Stop'
$kis = Join-Path $PSScriptRoot 'kis.exe'
$name = if ($env:KIS_TEST_NAME) { $env:KIS_TEST_NAME } else { Read-Host 'Archive name (without .kis extension)' }
if (-not $name) { return }
$out = Join-Path $Dir ($name + '.kis')
$items = Get-ChildItem -LiteralPath $Dir -Force | Where-Object { $_.FullName -ne $out } | ForEach-Object { $_.FullName }
if (-not $items) { Write-Host 'Folder is empty.'; Read-Host 'Press Enter'; return }
& $kis create $out @items -c lzma
Write-Host ''
if (-not $env:KIS_NO_PAUSE) { Read-Host 'Done. Press Enter to close' }
