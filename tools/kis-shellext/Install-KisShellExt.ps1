# Install-KisShellExt.ps1
# Builds and registers the KIS top-level Explorer context-menu handler.
#
# This packages the IExplorerCommand handler as a sparse MSIX package and
# registers it for the current user — no admin required, but Developer Mode
# (or sideloading enabled) IS required.
#
# Run with -Uninstall to remove.

[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root      = $PSScriptRoot
$pkgName   = 'KIS.ShellExt_1.0.0.0_x64__kisshellext'   # not strictly used, see below
$identity  = 'KIS.ShellExt'

# ─── uninstall ───────────────────────────────────────────────────────────
if ($Uninstall) {
    $existing = Get-AppxPackage -Name $identity -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "Removing $($existing.PackageFullName) ..."
        Remove-AppxPackage -Package $existing.PackageFullName
        Write-Host "Removed." -ForegroundColor Green
    } else {
        Write-Host "Nothing to remove."
    }
    return
}

# ─── 1) Build CLI + shell extension ──────────────────────────────────────
if (-not $NoBuild) {
    Write-Host "Publishing kis.exe ..." -ForegroundColor Cyan
    & dotnet publish "$root\..\kis-cli\KIS.Cli.csproj" -c Release -r win-x64 --self-contained false -o "$root\..\kis-cli\publish" -nologo | Out-Null

    Write-Host "Building C++ shell extension ..." -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$root\..\kis-shellext-cpp\build.ps1" | Out-Null
}

# ─── 2) Stage layout ─────────────────────────────────────────────────────
$layout = Join-Path $root 'layout'
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Path $layout, "$layout\Assets", "$layout\verbs" -Force | Out-Null

# Copy manifest
Copy-Item "$root\AppxManifest.xml" "$layout\AppxManifest.xml"

# Copy native shell-ext build output
Copy-Item "$root\..\kis-shellext-cpp\build\KIS.ShellExt.dll" "$layout\KIS.ShellExt.dll"

# Copy the branded icon used by the top-level menu entry
if (Test-Path "$root\..\kis-shellext-cpp\kis.ico") {
    Copy-Item "$root\..\kis-shellext-cpp\kis.ico" "$layout\KIS.ico" -Force
}

# Copy the .kis file-type icon into Assets/ for the FTA registration
if (Test-Path "$root\..\..\KesFile\Assets\KisFileIcon.png") {
    Copy-Item "$root\..\..\KesFile\Assets\KisFileIcon.png" "$layout\Assets\KisFileIcon.png" -Force
}

# Copy CLI exe (the handler invokes it)
Copy-Item "$root\..\kis-cli\publish\kis.exe" "$layout\kis.exe"

# Copy the verb scripts (re-used for password prompts and pause-after-output)
Copy-Item "$root\..\kis-shell\verbs\*.ps1" "$layout\verbs\" -Force

# Tiny placeholder PNGs for the manifest assets (1x1 transparent)
$png1x1 = [byte[]]@(
    0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,0x00,0x00,0x00,0x0D,0x49,0x48,0x44,0x52,
    0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x01,0x08,0x06,0x00,0x00,0x00,0x1F,0x15,0xC4,
    0x89,0x00,0x00,0x00,0x0D,0x49,0x44,0x41,0x54,0x78,0x9C,0x63,0xF8,0x0F,0x00,0x00,
    0x01,0x01,0x00,0x01,0x5B,0xCC,0x69,0xB6,0x00,0x00,0x00,0x00,0x49,0x45,0x4E,0x44,
    0xAE,0x42,0x60,0x82
)
foreach ($n in 'StoreLogo.png','Square150x150Logo.png','Square44x44Logo.png','Wide310x150Logo.png') {
    [System.IO.File]::WriteAllBytes("$layout\Assets\$n", $png1x1)
}

# ─── 3) Register the layout as a sparse package (no signing needed when registered by path) ──
Write-Host "Registering package from layout ..." -ForegroundColor Cyan

# Remove any previous registration first
$existing = Get-AppxPackage -Name $identity -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "  removing previous: $($existing.PackageFullName)"
    Remove-AppxPackage -Package $existing.PackageFullName
}

Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml') -ForceUpdateFromAnyVersion

$pkg = Get-AppxPackage -Name $identity
if (-not $pkg) { throw "Registration failed." }

Write-Host ""
Write-Host "✓ Installed: $($pkg.PackageFullName)" -ForegroundColor Green
Write-Host "  InstallLocation: $($pkg.InstallLocation)"
Write-Host ""
Write-Host "Restart Explorer for the menu to refresh:" -ForegroundColor Yellow
Write-Host "  Stop-Process -Name explorer -Force"
Write-Host ""
Write-Host "Then right-click any file or folder — you'll see 'KIS' near the top of the menu."
Write-Host "Uninstall:  .\Install-KisShellExt.ps1 -Uninstall"
