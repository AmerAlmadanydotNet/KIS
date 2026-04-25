# Install-KisShell.ps1
# Registers Windows Explorer right-click context-menu entries for KIS.
#
#   Right-click on any file/folder  ->  "Compress to .kis"
#                                       "Compress to .kis (encrypted)..."
#   Right-click on a .kis / .kes    ->  "Extract here"
#                                       "Extract to subfolder"
#                                       "Verify integrity"
#                                       "Show info"
#   Right-click empty folder space  ->  "New KIS archive here..."
#
# Per-user install (no admin) writes to HKCU\Software\Classes.
# Run with -Uninstall to remove all KIS shell entries.

[CmdletBinding()]
param(
    [string]$KisExe,
    [switch]$Uninstall,
    [switch]$AllUsers
)

$ErrorActionPreference = 'Stop'
$root = if ($AllUsers) { 'HKLM:\Software\Classes' } else { 'HKCU:\Software\Classes' }
$verbsDir = Join-Path $PSScriptRoot 'verbs'

function Set-Key {
    param($Path, $Default = $null, [hashtable]$Values = @{})
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
    if ($null -ne $Default) {
        New-ItemProperty -Path $Path -Name '(default)' -Value $Default -PropertyType String -Force | Out-Null
    }
    foreach ($k in $Values.Keys) {
        New-ItemProperty -Path $Path -Name $k -Value $Values[$k] -PropertyType String -Force | Out-Null
    }
}

function Remove-KeyTree {
    param($Path)
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
}

if ($Uninstall) {
    Write-Host "Removing KIS shell integration from $root ..."
    Remove-KeyTree "$root\*\shell\KIS.Compress"
    Remove-KeyTree "$root\*\shell\KIS.CompressEnc"
    Remove-KeyTree "$root\Directory\shell\KIS.Compress"
    Remove-KeyTree "$root\Directory\shell\KIS.CompressEnc"
    Remove-KeyTree "$root\Directory\Background\shell\KIS.CompressHere"
    Remove-KeyTree "$root\KIS.Archive"
    Write-Host "Removed." -ForegroundColor Green
    return
}

if (-not $KisExe) {
    $candidates = @(
        (Join-Path $PSScriptRoot 'kis.exe'),
        (Join-Path $PSScriptRoot '..\kis-cli\publish\kis.exe'),
        (Join-Path $PSScriptRoot '..\kis-cli\bin\Release\net8.0\win-x64\publish\kis.exe'),
        (Join-Path $PSScriptRoot '..\kis-cli\bin\Release\net8.0\kis.exe'),
        (Join-Path $PSScriptRoot '..\kis-cli\bin\Debug\net8.0\kis.exe')
    )
    $KisExe = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $KisExe -or -not (Test-Path -LiteralPath $KisExe)) {
    throw "Could not find kis.exe. Run 'dotnet publish -c Release -r win-x64 --self-contained false -o publish' in tools/kis-cli first, or pass -KisExe <path>."
}
$KisExe = (Resolve-Path -LiteralPath $KisExe).Path
Write-Host "Using kis.exe at:  $KisExe"

# Stage kis.exe alongside the verb scripts so they all live in one place.
Copy-Item -LiteralPath $KisExe -Destination (Join-Path $verbsDir 'kis.exe') -Force
$icon = '"' + (Join-Path $verbsDir 'kis.exe') + '"'

function VerbCmd {
    param($scriptName, $argRef)
    $script = Join-Path $verbsDir $scriptName
    $ps     = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    return ('"{0}" -NoProfile -ExecutionPolicy Bypass -File "{1}" "{2}"' -f $ps, $script, $argRef)
}

# ProgID for .kis archives + verbs
Set-Key "$root\KIS.Archive" -Default 'KIS Archive' -Values @{ FriendlyTypeName = 'KIS Archive' }
Set-Key "$root\KIS.Archive\DefaultIcon" -Default $icon

Set-Key "$root\KIS.Archive\shell\KIS.ExtractHere" -Default 'Extract here' -Values @{ Icon = $icon }
Set-Key "$root\KIS.Archive\shell\KIS.ExtractHere\command" -Default (VerbCmd 'extract-here.ps1' '%1')

Set-Key "$root\KIS.Archive\shell\KIS.ExtractTo"   -Default 'Extract to subfolder' -Values @{ Icon = $icon }
Set-Key "$root\KIS.Archive\shell\KIS.ExtractTo\command"   -Default (VerbCmd 'extract-to.ps1'   '%1')

Set-Key "$root\KIS.Archive\shell\KIS.Verify" -Default 'Verify integrity' -Values @{ Icon = $icon }
Set-Key "$root\KIS.Archive\shell\KIS.Verify\command" -Default (VerbCmd 'verify.ps1' '%1')

Set-Key "$root\KIS.Archive\shell\KIS.Info" -Default 'Show info' -Values @{ Icon = $icon }
Set-Key "$root\KIS.Archive\shell\KIS.Info\command" -Default (VerbCmd 'info.ps1' '%1')

Set-Key "$root\.kis" -Default 'KIS.Archive' -Values @{
    'Content Type'  = 'application/x-kis'
    'PerceivedType' = 'compressed'
}
Set-Key "$root\.kis\OpenWithProgids" -Values @{ 'KIS.Archive' = '' }

if (-not (Test-Path -LiteralPath "$root\.kes")) {
    Set-Key "$root\.kes" -Default 'KIS.Archive'
}
Set-Key "$root\.kes\OpenWithProgids" -Values @{ 'KIS.Archive' = '' }

# "Compress to .kis" on any file or folder
foreach ($t in @('*', 'Directory')) {
    Set-Key "$root\$t\shell\KIS.Compress" -Default 'Compress to .kis' -Values @{
        Icon             = $icon
        MultiSelectModel = 'Player'
    }
    Set-Key "$root\$t\shell\KIS.Compress\command" -Default (VerbCmd 'compress.ps1' '%1')

    Set-Key "$root\$t\shell\KIS.CompressEnc" -Default 'Compress to .kis (encrypted)...' -Values @{
        Icon             = $icon
        MultiSelectModel = 'Player'
    }
    Set-Key "$root\$t\shell\KIS.CompressEnc\command" -Default (VerbCmd 'compress-encrypted.ps1' '%1')
}

# "New KIS archive here" on folder background
Set-Key "$root\Directory\Background\shell\KIS.CompressHere" -Default 'New KIS archive here...' -Values @{
    Icon = $icon
}
Set-Key "$root\Directory\Background\shell\KIS.CompressHere\command" -Default (VerbCmd 'compress-here.ps1' '%V')

Write-Host ''
Write-Host "Installed KIS shell integration under $root" -ForegroundColor Green
Write-Host ''
Write-Host "Try it:"
Write-Host "  - Right-click any file/folder  ->  'Compress to .kis'"
Write-Host "  - Right-click any .kis archive ->  'Extract here' / 'Verify integrity' / 'Show info'"
Write-Host "  - Right-click empty folder     ->  'New KIS archive here...'"
Write-Host ''
Write-Host "On Windows 11 these appear under 'Show more options' (or Shift+F10)."
Write-Host "Uninstall:  Install-KisShell.ps1 -Uninstall"
