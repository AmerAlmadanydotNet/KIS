#Requires -Version 5.0
# ============================================================
#  KIS file – Application Installer
#  Run as Administrator (the .bat launcher handles elevation)
# ============================================================

$ErrorActionPreference = "Stop"

# ── Re-launch as Administrator if needed ─────────────────────
if (-not ([Security.Principal.WindowsPrincipal]
          [Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator))
{
    Start-Process powershell.exe `
        -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`"" `
        -Verb RunAs
    exit
}

$scriptDir = Split-Path $PSCommandPath -Parent
$msixPath  = Join-Path $scriptDir "KISfile.msix"
$certPath  = Join-Path $scriptDir "cert\KISfile.cer"
$depsDir   = Join-Path $scriptDir "Dependencies\x64"

# ── Banner ────────────────────────────────────────────────────
function Show-Banner {
    Clear-Host
    Write-Host ""
    Write-Host "  ==========================================" -ForegroundColor Cyan
    Write-Host "      KIS file  -  Application Setup        " -ForegroundColor Cyan
    Write-Host "              Version  1.0.0                " -ForegroundColor Cyan
    Write-Host "          by  Anti-Moumen  (2026)           " -ForegroundColor Cyan
    Write-Host "  ==========================================" -ForegroundColor Cyan
    Write-Host ""
}

function Write-Step { param([int]$n,[int]$total,[string]$msg)
    Write-Host "  [$n/$total] $msg" -ForegroundColor Yellow
}
function Write-OK   { Write-Host "         → Done." -ForegroundColor Green }
function Write-Skip { param([string]$msg) Write-Host "         → $msg" -ForegroundColor DarkGray }
function Write-Fail { param([string]$msg) Write-Host "         → ERROR: $msg" -ForegroundColor Red }

Show-Banner

# Guard: check MSIX exists
if (-not (Test-Path $msixPath)) {
    Write-Host "  [ERROR] Cannot find KISfile.msix in the setup folder." -ForegroundColor Red
    Write-Host "          Expected path: $msixPath" -ForegroundColor Red
    Read-Host "`n  Press Enter to close"
    exit 1
}

# ── Step 1 – Enable app sideloading ──────────────────────────
Write-Step 1 4 "Enabling app sideloading (AllowAllTrustedApps)..."
try {
    $regKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock"
    if (-not (Test-Path $regKey)) { New-Item $regKey -Force | Out-Null }
    Set-ItemProperty $regKey "AllowAllTrustedApps"           1 -Type DWord -Force
    Set-ItemProperty $regKey "AllowDevelopmentWithoutDevLicense" 0 -Type DWord -Force
    Write-OK
} catch {
    Write-Skip "Could not set registry value: $($_.Exception.Message)"
}

# ── Step 2 – Install signing certificate ─────────────────────
Write-Step 2 4 "Installing signing certificate..."
if (-not (Test-Path $certPath)) {
    Write-Fail "cert\KISfile.cer not found. Cannot install without a trusted certificate."
    Read-Host "`n  Press Enter to close"
    exit 1
}
try {
    $cert   = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certPath)
    $thumbp = $cert.Thumbprint

    foreach ($storeName in @("Root","TrustedPeople")) {
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
                     $storeName,
                     [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        # Only add if not already present
        $existing = $store.Certificates | Where-Object { $_.Thumbprint -eq $thumbp }
        if (-not $existing) { $store.Add($cert) }
        $store.Close()
    }
    Write-OK
} catch {
    Write-Fail $_.Exception.Message
    Read-Host "`n  Press Enter to close"
    exit 1
}

# ── Step 3 – Install framework dependencies ──────────────────
Write-Step 3 4 "Installing framework dependencies..."
if (Test-Path $depsDir) {
    $appxFiles = Get-ChildItem $depsDir -Filter "*.appx"
    if ($appxFiles.Count -eq 0) {
        Write-Skip "No dependency files found in Dependencies\x64\."
    } else {
        foreach ($appx in $appxFiles) {
            Write-Host "         Installing $($appx.Name) ..." -NoNewline
            try {
                Add-AppxPackage -Path $appx.FullName -ErrorAction SilentlyContinue
                Write-Host " OK" -ForegroundColor Green
            } catch {
                Write-Host " skipped (already installed)" -ForegroundColor DarkGray
            }
        }
    }
} else {
    Write-Skip "No Dependencies folder found — skipping."
}

# ── Step 4 – Install KIS file ────────────────────────────────
Write-Step 4 4 "Installing KIS file..."
try {
    # Remove any old version first (ignore errors)
    Get-AppxPackage -Name "KesFile.Archiver" -ErrorAction SilentlyContinue |
        Remove-AppxPackage -ErrorAction SilentlyContinue

    Add-AppxPackage -Path $msixPath -ForceUpdateFromAnyVersion
    Write-OK
} catch {
    Write-Fail $_.Exception.Message
    Read-Host "`n  Press Enter to close"
    exit 1
}

# ── Done ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "  ==========================================" -ForegroundColor Green
Write-Host "   KIS file installed successfully!  OK     " -ForegroundColor Green
Write-Host "  ==========================================" -ForegroundColor Green
Write-Host ""
Write-Host "  You can now find 'KIS file' in your Start menu." -ForegroundColor White
Write-Host "  Double-click any .kes file to open it directly." -ForegroundColor White
Write-Host ""
Read-Host "  Press Enter to close"
