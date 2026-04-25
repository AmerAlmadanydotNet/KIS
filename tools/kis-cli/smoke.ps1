$ErrorActionPreference='Stop'
$kis  = "d:\Personal Projects\KIS\tools\kis-cli\bin\Release\net8.0\kis.exe"
$work = "$env:TEMP\kis-cli-smoke"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$src  = Join-Path $work 'src'
New-Item -ItemType Directory -Path $src,(Join-Path $src 'sub') -Force | Out-Null
('hello world ' * 200) | Set-Content -Path (Join-Path $src 'a.txt')
[System.IO.File]::WriteAllBytes((Join-Path $src 'sub\binary.bin'), ([byte[]]@(0..255) * 200))
('lorem ipsum dolor sit amet ' * 500) | Set-Content -Path (Join-Path $src 'sub\notes.md')

Write-Host "`n=== CREATE plain (LZMA) ===" -ForegroundColor Cyan
& $kis create "$work\plain.kis" "$src" -c lzma

Write-Host "`n=== LIST plain ===" -ForegroundColor Cyan
& $kis list "$work\plain.kis"

Write-Host "`n=== INFO plain ===" -ForegroundColor Cyan
& $kis info "$work\plain.kis"

Write-Host "`n=== VERIFY plain ===" -ForegroundColor Cyan
& $kis verify "$work\plain.kis"

Write-Host "`n=== EXTRACT plain ===" -ForegroundColor Cyan
& $kis extract "$work\plain.kis" -o "$work\out_plain" --force

Write-Host "`n=== CREATE encrypted (names hidden) ===" -ForegroundColor Cyan
& $kis create "$work\secret.kis" "$src" -c lzma -p "hunter2" --encrypt-names --hint "first pet"

Write-Host "`n=== LIST without password (should hide names) ===" -ForegroundColor Cyan
& $kis info "$work\secret.kis"

Write-Host "`n=== LIST with password ===" -ForegroundColor Cyan
& $kis list "$work\secret.kis" -p "hunter2"

Write-Host "`n=== VERIFY wrong password (should fail) ===" -ForegroundColor Cyan
& $kis verify "$work\secret.kis" -p "wrong"
Write-Host "exit code = $LASTEXITCODE"

Write-Host "`n=== EXTRACT with password ===" -ForegroundColor Cyan
& $kis extract "$work\secret.kis" -o "$work\out_secret" -p "hunter2" --force

Write-Host "`n=== ROUND-TRIP DIFFS ===" -ForegroundColor Cyan
$origHash    = Get-ChildItem $src -Recurse -File | Get-FileHash | Sort-Object Path
$plainHash   = Get-ChildItem (Join-Path $work 'out_plain\src')  -Recurse -File | Get-FileHash | Sort-Object Path
$secretHash  = Get-ChildItem (Join-Path $work 'out_secret\src') -Recurse -File | Get-FileHash | Sort-Object Path
$d1 = (Compare-Object $origHash $plainHash  -Property Hash).Count
$d2 = (Compare-Object $origHash $secretHash -Property Hash).Count
Write-Host "plain  diffs: $d1"
Write-Host "secret diffs: $d2"
if ($d1 -eq 0 -and $d2 -eq 0) { Write-Host "ALL ROUND-TRIPS OK" -ForegroundColor Green } else { Write-Host "FAILURES" -ForegroundColor Red; exit 1 }
