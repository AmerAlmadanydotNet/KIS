@echo off
:: KIS file – Setup Launcher
:: Double-click this file to install KIS file on your PC.

title KIS file Setup

:: Run Install.ps1 as Administrator
powershell.exe -ExecutionPolicy Bypass -NoProfile -File "%~dp0Install.ps1"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo  Installation failed. Please run as Administrator.
    pause
)
