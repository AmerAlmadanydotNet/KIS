# KesFile - Setup Script
# Run this script ONCE before opening the project in Visual Studio.
# It generates the required placeholder PNG assets for the UWP app.

param([string]$ProjectRoot = $PSScriptRoot)

$assetsDir = Join-Path $ProjectRoot "KesFile\Assets"
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null

Add-Type -AssemblyName System.Drawing

function New-PlaceholderPng {
    param([string]$Path, [int]$Width, [int]$Height, [string]$Label)

    $bmp = New-Object System.Drawing.Bitmap($Width, $Height)
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)

    # Background gradient-like fill
    $bgBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(80, 100, 212))
    $gfx.FillRectangle($bgBrush, 0, 0, $Width, $Height)

    # Draw "K" letter
    $font  = New-Object System.Drawing.Font("Segoe UI", [Math]::Max(10, $Height / 3), [System.Drawing.FontStyle]::Bold)
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $fmt   = New-Object System.Drawing.StringFormat
    $fmt.Alignment          = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment      = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF(0, 0, $Width, $Height)
    $gfx.DrawString("K", $font, $brush, $rect, $fmt)

    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $gfx.Dispose(); $bmp.Dispose()
    Write-Host "  Created: $Path ($Width x $Height)" -ForegroundColor Green
}

Write-Host ""
Write-Host "KesFile — Generating placeholder assets..." -ForegroundColor Cyan

New-PlaceholderPng -Path "$assetsDir\StoreLogo.scale-200.png"        -Width 100  -Height 100 -Label "K"
New-PlaceholderPng -Path "$assetsDir\Square150x150Logo.scale-200.png" -Width 300  -Height 300 -Label "K"
New-PlaceholderPng -Path "$assetsDir\Square44x44Logo.scale-200.png"   -Width 88   -Height 88  -Label "K"
New-PlaceholderPng -Path "$assetsDir\SplashScreen.scale-200.png"      -Width 1240 -Height 600 -Label "K"
New-PlaceholderPng -Path "$assetsDir\Wide310x150Logo.scale-200.png"   -Width 620  -Height 300 -Label "K"
New-PlaceholderPng -Path "$assetsDir\LockScreenLogo.scale-200.png"    -Width 48   -Height 48  -Label "K"

Write-Host ""
Write-Host "Assets generated successfully." -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Open KesFile.sln in Visual Studio 2022 (with UWP workload installed)"
Write-Host "  2. Right-click the solution > Restore NuGet Packages"
Write-Host "  3. Build and Deploy (x86 or x64 Debug mode)"
Write-Host ""
