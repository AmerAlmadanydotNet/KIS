# build.ps1 — build the C++ shell extension DLL using the local MSVC toolchain.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Locate MSVC + Windows SDK
$vc = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\18\Insiders\VC\Tools\MSVC' -Directory | Sort-Object Name -Descending | Select-Object -First 1
if (-not $vc) { throw "MSVC not found." }
$cl = Join-Path $vc.FullName 'bin\Hostx64\x64\cl.exe'
$lib = Join-Path $vc.FullName 'lib\x64'
$vcInc = Join-Path $vc.FullName 'include'

$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$sdkVer  = (Get-ChildItem (Join-Path $sdkRoot 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
$sdkInc  = Join-Path $sdkRoot "Include\$sdkVer"
$sdkLib  = Join-Path $sdkRoot "Lib\$sdkVer"

$includes = @(
    "/I`"$vcInc`"",
    "/I`"$sdkInc\ucrt`"",
    "/I`"$sdkInc\um`"",
    "/I`"$sdkInc\shared`"",
    "/I`"$sdkInc\winrt`"",
    "/I`"$sdkInc\cppwinrt`""
)
$libs = @(
    "/LIBPATH:`"$lib`"",
    "/LIBPATH:`"$sdkLib\ucrt\x64`"",
    "/LIBPATH:`"$sdkLib\um\x64`""
)

$out = Join-Path $root 'build'
New-Item -ItemType Directory $out -Force | Out-Null
Push-Location $out
try {
    $args = @(
        '/nologo','/EHsc','/std:c++17','/MD','/O2','/DNDEBUG','/D_WINDOWS','/D_USRDLL',
        '/W3','/permissive-','/Zc:wchar_t'
    ) + $includes + @(
        "$root\KisShellExt.cpp",
        '/link','/DLL','/MACHINE:X64',
        "/DEF:$root\KisShellExt.def",
        '/OUT:KIS.ShellExt.dll'
    ) + $libs

    Write-Host "Compiling C++ shell extension..." -ForegroundColor Cyan
    & $cl @args
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed." }

    Write-Host ""
    Write-Host "Built: $(Resolve-Path .\KIS.ShellExt.dll)" -ForegroundColor Green
    Get-ChildItem .\KIS.ShellExt.dll | Select-Object Name, Length | Format-Table -AutoSize
} finally {
    Pop-Location
}
