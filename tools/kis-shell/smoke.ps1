$ErrorActionPreference = 'Stop'
$env:KIS_NO_PAUSE = '1'
$verbs = "d:\Personal Projects\KIS\tools\kis-shell\verbs"
$work  = "$env:TEMP\kis-shell-smoke"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$src = Join-Path $work 'demo'
New-Item -ItemType Directory -Path $src -Force | Out-Null
'hello from kis shell' | Set-Content (Join-Path $src 'a.txt')
'second file ' * 100   | Set-Content (Join-Path $src 'b.txt')

Write-Host "`n=== compress.ps1 (folder) ===" -ForegroundColor Cyan
& "$verbs\compress.ps1" -Path $src
$archive = "$src.kis"
if (-not (Test-Path $archive)) { throw "archive not created" }
Write-Host "archive: $archive  ($([math]::Round((Get-Item $archive).Length/1KB,2)) KB)"

Write-Host "`n=== verify.ps1 ===" -ForegroundColor Cyan
& "$verbs\verify.ps1" -Path $archive

Write-Host "`n=== info.ps1 ===" -ForegroundColor Cyan
& "$verbs\info.ps1" -Path $archive

Write-Host "`n=== extract-here.ps1 ===" -ForegroundColor Cyan
$dest = Join-Path $work 'extracted'
New-Item -ItemType Directory -Path $dest -Force | Out-Null
$tmpArchive = Join-Path $dest 'demo.kis'
Copy-Item $archive $tmpArchive
& "$verbs\extract-here.ps1" -Path $tmpArchive
Get-ChildItem $dest -Recurse -File | ForEach-Object { Write-Host "  $($_.FullName.Substring($dest.Length+1))  ($($_.Length) B)" }

Write-Host "`n=== extract-to.ps1 ===" -ForegroundColor Cyan
& "$verbs\extract-to.ps1" -Path $archive
if (Test-Path (Join-Path (Split-Path $archive) 'demo')) {
    Write-Host "  -> auto subfolder created"
}

Write-Host "`n=== compress-here.ps1 (folder background) ===" -ForegroundColor Cyan
$bg = Join-Path $work 'bg'
New-Item -ItemType Directory -Path $bg -Force | Out-Null
'one' | Set-Content (Join-Path $bg 'one.txt')
'two' | Set-Content (Join-Path $bg 'two.txt')
$env:KIS_TEST_NAME = 'my-archive'
& "$verbs\compress-here.ps1" -Dir $bg
Remove-Item Env:\KIS_TEST_NAME
if (Test-Path (Join-Path $bg 'my-archive.kis')) {
    Write-Host "  -> created my-archive.kis"
}

Write-Host "`n=== ALL VERBS OK ===" -ForegroundColor Green
