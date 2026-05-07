@echo off
:: Request elevation if not already admin
net session >nul 2>&1
if %errorLevel% == 0 goto :run
echo Requesting Administrator rights...
powershell -Command "Start-Process cmd -ArgumentList '/c \"%~f0\"' -Verb RunAs"
exit /b

:run
echo.
echo  ==========================================
echo      KIS file  -  Setup v1.0.0
echo      by Ahmad Madany (2026)
echo  ==========================================
echo.
echo  Installing KIS file...
echo.
powershell.exe -ExecutionPolicy Bypass -NoProfile -File "%~dp0Install.ps1"
echo.
pause
