param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$kis = Join-Path $PSScriptRoot 'kis.exe'
$out = if (Test-Path -LiteralPath $Path -PathType Container) {
    $Path.TrimEnd('\') + '.kis'
} else {
    [System.IO.Path]::ChangeExtension($Path, '.kis')
}
$pw = Read-Host 'Password' -AsSecureString
$bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($pw)
try {
    $plain = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    $hint = Read-Host 'Password hint (optional, stored in plaintext)'
    if ($hint) {
        & $kis create $out $Path -c lzma -p $plain --encrypt-names --hint $hint
    } else {
        & $kis create $out $Path -c lzma -p $plain --encrypt-names
    }
} finally {
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) | Out-Null
}
Write-Host ''
Read-Host 'Done. Press Enter to close'
