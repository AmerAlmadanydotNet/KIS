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
