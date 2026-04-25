# KIS Explorer Integration

Adds Windows Explorer right-click menu entries powered by the [`kis.exe`](../kis-cli/) CLI.

## What you get

| Right-click on…                  | Menu items                                                     |
|----------------------------------|----------------------------------------------------------------|
| Any file or folder               | **Compress to .kis** · **Compress to .kis (encrypted)…**      |
| A `.kis` / `.kes` archive        | **Extract here** · **Extract to subfolder** · **Verify integrity** · **Show info** |
| Empty space inside a folder      | **New KIS archive here…**                                      |

## Install (per-user, no admin)

```powershell
# 1. Build the CLI first (one-time)
cd ..\kis-cli
dotnet build -c Release

# 2. Install the shell integration
cd ..\kis-shell
.\Install-KisShell.ps1
```

The script auto-discovers `kis.exe` in the sibling `kis-cli\bin\Release\net8.0\` folder.
You can override with `-KisExe "C:\full\path\to\kis.exe"`.

## Install for all users (needs admin)

```powershell
# Run from an elevated PowerShell
.\Install-KisShell.ps1 -AllUsers -KisExe "C:\Program Files\KIS\kis.exe"
```

## Uninstall

```powershell
.\Install-KisShell.ps1 -Uninstall
# or, for HKLM install:
.\Install-KisShell.ps1 -Uninstall -AllUsers
```

## Notes

* On Windows 11, custom verbs appear under **Show more options** (Shift+F10) by
  default. To pin items to the top-level menu you'd need an `IExplorerCommand`
  COM handler — that's a follow-up task.
* The encrypted variant prompts for a password in a small console window
  (masked input) and uses `--encrypt-names` for maximum privacy.
* All verbs reuse the `kis.exe` icon, so they match your CLI build.
