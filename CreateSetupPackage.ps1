#Requires -Version 5.0
# ============================================================
#  CreateSetupPackage.ps1
#
#  Run this script (from the project root) to assemble the
#  distributable  KISfile-Setup  folder ready to zip and send.
#
#  Usage:
#    cd c:\Files\Projects\KesFile
#    .\CreateSetupPackage.ps1
#
#  Output:  .\KISfile-Setup\   (zip this and share)
# ============================================================

$ErrorActionPreference = "Stop"
$scriptDir    = $PSScriptRoot
$certThumbprint = "780E0142856EBEF98CC2ECDAC027017D8211D870"

# --- Auto-detect MSBuild.exe (any VS 2022 edition: Enterprise/Professional/Community/BuildTools) ---
function Find-MSBuild {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
                            -find "MSBuild\**\Bin\MSBuild.exe" 2>$null | Select-Object -First 1
        if ($found -and (Test-Path $found)) { return $found }
    }
    $candidates = @(
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\*\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\*\MSBuild\Current\Bin\MSBuild.exe"
    )
    foreach ($pattern in $candidates) {
        $hit = Get-ChildItem $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

# --- Auto-detect signtool.exe (latest Windows 10/11 SDK) ---
function Find-SignTool {
    $patterns = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe",
        "${env:ProgramFiles}\Windows Kits\10\bin\*\x64\signtool.exe"
    )
    foreach ($pattern in $patterns) {
        $hit = Get-ChildItem $pattern -ErrorAction SilentlyContinue |
               Sort-Object FullName -Descending | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

$msbuild  = Find-MSBuild
$signtool = Find-SignTool
if (-not $msbuild)  { Write-Error "MSBuild.exe not found. Install Visual Studio 2022 or Build Tools." }
if (-not $signtool) { Write-Error "signtool.exe not found. Install Windows 10/11 SDK." }
Write-Host "MSBuild : $msbuild"  -ForegroundColor DarkGray
Write-Host "SignTool: $signtool" -ForegroundColor DarkGray

$csprojPath   = Join-Path $scriptDir "KesFile\KesFile.csproj"
$outputDir    = Join-Path $scriptDir "KISfile-Setup"
$exeOutput    = Join-Path $scriptDir "KISfile-Setup.exe"

function Write-Step { param([string]$msg)
    Write-Host "`n>> $msg" -ForegroundColor Cyan
}
function Write-OK   { Write-Host "   Done." -ForegroundColor Green }

#  Clean output folder --------------------------------------
Write-Step "Cleaning output folder..."
if (Test-Path $outputDir) { Remove-Item $outputDir -Recurse -Force }
New-Item $outputDir -ItemType Directory | Out-Null
Write-OK

#  Build Release --------------------------------------------
Write-Step "Building Release x64..."
$buildArgs = @(
    $csprojPath,
    "/p:Platform=x64",
    "/p:Configuration=Release",
    "/p:VisualStudioVersion=17.0",
    "/p:TargetPlatformVersion=10.0.22621.0",
    "/t:Build",
    "/v:minimal"
)
& $msbuild @buildArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "   Release build failed. Falling back to existing Debug build." -ForegroundColor Yellow
    $Configuration = "Debug"
} else {
    $Configuration = "Release"
    Write-OK
}

#  Locate the MSIX -----------------------------------------
Write-Step "Locating MSIX package ($Configuration)..."
$appPackagesDir = Join-Path $scriptDir "KesFile\AppPackages"
$testFolder     = Get-ChildItem $appPackagesDir -Directory |
                  Where-Object { $_.Name -like "*${Configuration}*" -or
                                 ($Configuration -eq "Release" -and $_.Name -notlike "*Debug*") } |
                  Sort-Object LastWriteTime -Descending |
                  Select-Object -First 1

if (-not $testFolder) {
    Write-Error "Cannot find AppPackages test folder for configuration '$Configuration'."
}
$msixFile = Get-ChildItem $testFolder.FullName -Filter "*.msix" | Select-Object -First 1
if (-not $msixFile) { Write-Error "No .msix file found in $($testFolder.FullName)" }
Write-Host "   Found: $($msixFile.Name)" -ForegroundColor White
Write-OK

#  Sign the MSIX --------------------------------------------
Write-Step "Signing MSIX with developer certificate..."
$certInStore = Get-ChildItem Cert:\CurrentUser\My,Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
               Where-Object { $_.Thumbprint -eq $certThumbprint } | Select-Object -First 1
if ($certInStore) {
    & $signtool sign /sha1 $certThumbprint /fd SHA256 /q $msixFile.FullName
    if ($LASTEXITCODE -ne 0) { Write-Error "signtool failed." }
    Write-OK
} else {
    Write-Host "   Cert thumbprint $certThumbprint not in store - using existing signature from build." -ForegroundColor Yellow
    # Verify the MSIX is already signed
    $verifyOut = & $signtool verify /pa $msixFile.FullName 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Error "MSIX is not signed and no signing cert is available. Build in Visual Studio first."
    }
    Write-Host "   MSIX already signed by build process - OK." -ForegroundColor Green
}

#  Export certificate ---------------------------------------
Write-Step "Exporting signing certificate..."
$certOutDir = Join-Path $outputDir "cert"
New-Item $certOutDir -ItemType Directory | Out-Null
$certOutPath = Join-Path $certOutDir "KISfile.cer"

$cert = Get-ChildItem "Cert:\CurrentUser\My" |
        Where-Object { $_.Thumbprint -eq $certThumbprint } |
        Select-Object -First 1

if (-not $cert) {
    # Try LocalMachine
    $cert = Get-ChildItem "Cert:\LocalMachine\My" |
            Where-Object { $_.Thumbprint -eq $certThumbprint } |
            Select-Object -First 1
}
if (-not $cert) {
    # Try from file if already exported
    $candidates = @(
        (Join-Path $scriptDir "KesFile\cert\KesFileDev.cer"),
        "C:\Temp\KesFileDev.cer"
    )
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($found) {
        Copy-Item $found $certOutPath
        Write-Host "   Used pre-exported cert: $found" -ForegroundColor White
    } else {
        # Last resort: extract the cert from the signed MSIX itself
        try {
            $msixCert = (Get-AuthenticodeSignature $msixFile.FullName).SignerCertificate
            if ($msixCert) {
                $bytes = $msixCert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
                [System.IO.File]::WriteAllBytes($certOutPath, $bytes)
                Write-Host "   Extracted cert from MSIX signature." -ForegroundColor White
            } else {
                Write-Error "No certificate available."
            }
        } catch {
            Write-Error "Certificate with thumbprint $certThumbprint not found and no fallback worked."
        }
    }
} else {
    $certBytes = $cert.Export(
        [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    [System.IO.File]::WriteAllBytes($certOutPath, $certBytes)
    Write-Host "   Exported from certificate store." -ForegroundColor White
}
Write-OK

#  Copy MSIX ------------------------------------------------
Write-Step "Copying MSIX to output..."
Copy-Item $msixFile.FullName (Join-Path $outputDir "KISfile.msix")
Write-OK

#  Copy Dependencies ----------------------------------------
Write-Step "Copying framework dependencies..."
$depsSource = Join-Path $testFolder.FullName "Dependencies\x64"
if (Test-Path $depsSource) {
    $depsTarget = Join-Path $outputDir "Dependencies\x64"
    New-Item $depsTarget -ItemType Directory | Out-Null
    Get-ChildItem $depsSource -Filter "*.appx" | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $depsTarget $_.Name)
        Write-Host "   $($_.Name)" -ForegroundColor White
    }
} else {
    Write-Host "   No x64 dependencies folder found - skipping." -ForegroundColor Yellow
}
Write-OK

#  Copy shell extension ------------------------------------
Write-Step "Copying KIS shell extension (context menu)..."
$shellExtLayout = Join-Path $scriptDir "tools\kis-shellext\layout"
if (Test-Path $shellExtLayout) {
    $shellExtTarget = Join-Path $outputDir "ShellExt"
    Copy-Item $shellExtLayout $shellExtTarget -Recurse -Force
    Write-OK
} else {
    Write-Host "   Shell extension layout not found at $shellExtLayout - skipping." -ForegroundColor Yellow
    Write-Host "   Run tools\kis-shellext\Install-KisShellExt.ps1 once to build the layout." -ForegroundColor Yellow
}

#  Copy Installer scripts -----------------------------------
Write-Step "Copying installer scripts..."
$installerSrc = Join-Path $scriptDir "Installer"
Copy-Item (Join-Path $installerSrc "Install.ps1") (Join-Path $outputDir "Install.ps1")
Copy-Item (Join-Path $installerSrc "Install.bat") (Join-Path $outputDir "Install.bat")
Write-OK

#  Copy Release Notes ---------------------------------------
Write-Step "Copying release notes..."
$rnSrc = Join-Path $scriptDir "ReleaseNotes.html"
if (Test-Path $rnSrc) {
    Copy-Item $rnSrc (Join-Path $outputDir "ReleaseNotes.html")
    Write-OK
} else {
    Write-Host "   ReleaseNotes.html not found - skipping." -ForegroundColor Yellow
}

#  Write README ---------------------------------------------
Write-Step "Writing README..."
$readme = @"
KIS file v1.0.0 - Setup Package
================================
Built by Ahmad Madany - 2026

HOW TO INSTALL
--------------
1. Double-click  Install.bat
   (It will ask for Administrator permission -- click Yes.)

2. Wait for all steps to complete.

3. Find "KIS file" in your Start menu.

CONTENTS
--------
  Install.bat          - Double-click launcher
  Install.ps1          - Installer script (PowerShell)
  KISfile.msix         - Application package
  ShellExt\            - Context menu (right-click) integration
  cert\KISfile.cer     - Signing certificate
  Dependencies\x64\   - Required framework packages
  ReleaseNotes.html    - Release notes (open in any browser)

REQUIREMENTS
------------
  - Windows 10 version 1809 (build 17763) or later
  - x64 processor
  - Administrator rights for installation

NOTES
-----
  The installer automatically:
    - Enables app sideloading
    - Installs the signing certificate (trusted locally)
    - Installs required framework dependencies
    - Installs KIS file and registers the .kes file type
    - Registers the KIS right-click context menu
"@
$readme | Set-Content (Join-Path $outputDir "README.txt") -Encoding UTF8
Write-OK

#  Compile SFX stub ----------------------------------------
Write-Step "Compiling self-extracting installer stub..."
$sfxCsPath  = Join-Path $scriptDir "Installer\SfxStub.cs"
$sfxExeTemp = Join-Path $env:TEMP ("KISfileStub_" + [System.IO.Path]::GetRandomFileName().Replace('.','') + ".exe")

Add-Type -AssemblyName System.IO.Compression
$compressionDll = [System.IO.Compression.ZipArchive].Assembly.Location

$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
$cparms   = New-Object System.CodeDom.Compiler.CompilerParameters
$cparms.OutputAssembly      = $sfxExeTemp
$cparms.GenerateExecutable  = $true
$cparms.CompilerOptions     = "/target:exe /optimize+"
$cparms.ReferencedAssemblies.Add($compressionDll) | Out-Null
$cparms.ReferencedAssemblies.Add("System.dll")    | Out-Null
$cparms.ReferencedAssemblies.Add("System.Security.dll") | Out-Null

$stubCode = [System.IO.File]::ReadAllText($sfxCsPath)
$result   = $provider.CompileAssemblyFromSource($cparms, $stubCode)
if ($result.Errors.HasErrors) {
    $result.Errors | Where-Object { -not $_.IsWarning } | ForEach-Object {
        Write-Host "   ERROR: $_" -ForegroundColor Red
    }
    Write-Error "SfxStub.cs compilation failed."
}
Write-OK

#  Build SFX EXE (stub + payload ZIP + trailer) ------------
Write-Step "Building self-extracting EXE installer..."

# Create payload ZIP in TEMP
$payloadZip = Join-Path $env:TEMP ("KISfile_payload_" + [System.IO.Path]::GetRandomFileName().Replace('.','') + ".zip")
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $outputDir, $payloadZip,
    [System.IO.Compression.CompressionLevel]::Optimal, $false)

# Combine: [stub bytes] + [zip bytes] + [int64 zipStart] + ["KISFILE_SFX"]
$stubBytes = [System.IO.File]::ReadAllBytes($sfxExeTemp)
$zipBytes  = [System.IO.File]::ReadAllBytes($payloadZip)
$zipStart  = [long]$stubBytes.Length

if (Test-Path $exeOutput) { Remove-Item $exeOutput -Force }
$outStream = [System.IO.File]::Open($exeOutput, [System.IO.FileMode]::Create)
try {
    $outStream.Write($stubBytes, 0, $stubBytes.Length)
    $outStream.Write($zipBytes,  0, $zipBytes.Length)
    $outStream.Write([BitConverter]::GetBytes($zipStart), 0, 8)
    $outStream.Write([System.Text.Encoding]::ASCII.GetBytes("KISFILE_SFX"), 0, 11)
} finally {
    $outStream.Dispose()
}

# Cleanup temp files
Remove-Item $payloadZip  -Force -ErrorAction SilentlyContinue
Remove-Item $sfxExeTemp  -Force -ErrorAction SilentlyContinue

$exeSizeMB = [math]::Round((Get-Item $exeOutput).Length / 1MB, 1)
Write-Host "   Output: $exeOutput  ($exeSizeMB MB)" -ForegroundColor White
Write-OK

#  Summary -------------------------------------------------
Write-Host ""
Write-Host "=================================================" -ForegroundColor Green
Write-Host "  Setup package created successfully!" -ForegroundColor Green
Write-Host "=================================================" -ForegroundColor Green
Write-Host "  Folder : KISfile-Setup\" -ForegroundColor White
Write-Host "  EXE    : KISfile-Setup.exe  ($exeSizeMB MB)" -ForegroundColor White
Write-Host ""
Write-Host "  Share KISfile-Setup.exe with other people." -ForegroundColor White
Write-Host "  They double-click it to install KIS file." -ForegroundColor White
Write-Host "  (No extraction needed - single file setup)" -ForegroundColor White
Write-Host "=================================================" -ForegroundColor Green
Write-Host ""
