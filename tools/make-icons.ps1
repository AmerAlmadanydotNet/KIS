# Generate proper KIS-branded icons (PNG + ICO) from scratch using System.Drawing.
# Produces:
#   KesFile/Assets/Square44x44Logo.png  (44x44, taskbar/start)
#   KesFile/Assets/Square150x150Logo.png
#   KesFile/Assets/StoreLogo.png        (50x50)
#   KesFile/Assets/SplashScreen.png     (620x300, KIS centered)
#   KesFile/Assets/Wide310x150Logo.png
#   KesFile/Assets/LockScreenLogo.png   (24x24)
#   KesFile/Assets/KisFileIcon.png      (256x256, file association)
#   tools/kis-shellext-cpp/kis.ico      (multi-size ICO for shell menu)
#   tools/kis-shellext/layout/Assets/KIS.ico
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'KesFile\Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null

function New-KisBitmap {
    param([int]$W,[int]$H,[switch]$Wide)
    $bmp = New-Object System.Drawing.Bitmap($W,$H,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode    = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode= [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint= [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    # Rounded square background — KIS brand blue gradient
    $rect = New-Object System.Drawing.Rectangle(0,0,$W,$H)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 30, 90, 200),
        [System.Drawing.Color]::FromArgb(255, 10, 50, 140),
        45.0)
    $radius = [Math]::Min($W,$H) * 0.18
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0,         0,         $d,$d, 180, 90)
    $path.AddArc($W-$d-1,   0,         $d,$d, 270, 90)
    $path.AddArc($W-$d-1, $H-$d-1,     $d,$d,   0, 90)
    $path.AddArc(0,         $H-$d-1,   $d,$d,  90, 90)
    $path.CloseFigure()
    $g.FillPath($brush, $path)

    # "KIS" lettering
    $text = "KIS"
    $fontSize = if ($Wide) { $H * 0.55 } else { $H * 0.50 }
    $font = New-Object System.Drawing.Font("Segoe UI", $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment     = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    # subtle shadow
    $shadow = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(120,0,0,0))
    $g.DrawString($text, $font, $shadow, (New-Object System.Drawing.RectangleF(2,3,$W,$H)), $sf)
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.DrawString($text, $font, $white,  (New-Object System.Drawing.RectangleF(0,0,$W,$H)), $sf)

    $g.Dispose(); $brush.Dispose(); $font.Dispose(); $sf.Dispose(); $shadow.Dispose(); $white.Dispose(); $path.Dispose()
    return $bmp
}

function Save-Png {
    param($Bitmap,[string]$Path)
    $Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $Bitmap.Dispose()
    Write-Host "  $Path  ($([Math]::Round((Get-Item $Path).Length/1KB,1)) KB)"
}

Write-Host "Generating PNG assets..." -ForegroundColor Cyan
Save-Png (New-KisBitmap  44  44)        (Join-Path $assets 'Square44x44Logo.png')
Save-Png (New-KisBitmap 150 150)        (Join-Path $assets 'Square150x150Logo.png')
Save-Png (New-KisBitmap  50  50)        (Join-Path $assets 'StoreLogo.png')
Save-Png (New-KisBitmap 620 300 -Wide)  (Join-Path $assets 'SplashScreen.png')
Save-Png (New-KisBitmap 310 150 -Wide)  (Join-Path $assets 'Wide310x150Logo.png')
Save-Png (New-KisBitmap  24  24)        (Join-Path $assets 'LockScreenLogo.png')
Save-Png (New-KisBitmap 256 256)        (Join-Path $assets 'KisFileIcon.png')

# ICO file for shell extension (multi-resolution)
Write-Host "Generating ICO..." -ForegroundColor Cyan
function Write-Ico {
    param([string]$Path,[int[]]$Sizes)
    $bitmaps = @()
    foreach ($s in $Sizes) { $bitmaps += ,(New-KisBitmap $s $s) }

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # ICONDIR
    $bw.Write([uint16]0)                    # reserved
    $bw.Write([uint16]1)                    # type = icon
    $bw.Write([uint16]$bitmaps.Count)       # count

    # Pre-encode each bitmap as PNG (allowed inside ICO since Vista)
    $pngs = @()
    foreach ($b in $bitmaps) {
        $m = New-Object System.IO.MemoryStream
        $b.Save($m, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += ,$m.ToArray()
        $m.Dispose()
    }

    $offset = 6 + (16 * $bitmaps.Count)
    for ($i = 0; $i -lt $bitmaps.Count; $i++) {
        $sz = $bitmaps[$i].Width
        $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))   # width
        $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))   # height
        $bw.Write([byte]0)                  # color count
        $bw.Write([byte]0)                  # reserved
        $bw.Write([uint16]1)                # planes
        $bw.Write([uint16]32)               # bits per pixel
        $bw.Write([uint32]$pngs[$i].Length) # size of image data
        $bw.Write([uint32]$offset)          # offset
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
    foreach ($b in $bitmaps) { $b.Dispose() }
    $bw.Dispose(); $ms.Dispose()
    Write-Host "  $Path  ($([Math]::Round((Get-Item $Path).Length/1KB,1)) KB)"
}

$icoOut = Join-Path $root 'tools\kis-shellext-cpp\kis.ico'
Write-Ico $icoOut @(16,24,32,48,64,128,256)

# Mirror the ICO into the shell-extension layout so the menu can reference it
$layoutAssets = Join-Path $root 'tools\kis-shellext\layout\Assets'
New-Item -ItemType Directory -Path $layoutAssets -Force | Out-Null
Copy-Item $icoOut (Join-Path $layoutAssets 'KIS.ico') -Force

Write-Host "Done." -ForegroundColor Green
