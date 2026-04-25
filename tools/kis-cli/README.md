# `kis` — KIS Archive CLI

A small, scriptable command-line tool for the **KIS** archive format
(`.kis` / `.kes`) — fully interoperable with the KIS desktop app.

* Single self-contained `kis.exe` (built on .NET 8)
* Same on-disk format as the GUI — archives created here open in the app and
  vice-versa
* AES-256-CBC + HMAC-SHA256 authenticated encryption (PBKDF2-SHA256, 600 000 iters)
* Optional **encrypted entry table** so even file names / sizes are hidden
* LZMA (default), Deflate, or stored; per-file SHA-256 integrity

## Build

```powershell
cd tools/kis-cli
dotnet build -c Release
# binary lands in: bin/Release/net8.0/kis.exe
```

## Quick reference

| Command   | Purpose                                            |
|-----------|----------------------------------------------------|
| `create`  | Pack files / folders into a `.kis`                 |
| `extract` | Unpack a `.kis` back to disk                       |
| `list`    | Show entries + sizes + ratios                      |
| `verify`  | Re-decrypt + re-decompress + check every SHA-256   |
| `info`    | Show header summary (works without password)       |

### Examples

```powershell
# Pack a folder with strong LZMA + password + hidden filenames
kis create vacation.kis ./photos -c lzma -p "sunset!" --encrypt-names --hint "summer"

# Inspect any archive (header info works even when names are encrypted)
kis info  vacation.kis

# List entries (will prompt for password if needed)
kis list  vacation.kis -p "sunset!"

# Re-verify every SHA-256
kis verify vacation.kis -p "sunset!"

# Extract
kis extract vacation.kis -o ./restored -p "sunset!" --force
```

### Exit codes

| Code | Meaning                              |
|------|--------------------------------------|
| 0    | success                              |
| 1    | usage error (bad arguments)          |
| 2    | runtime error (I/O, wrong password)  |
| 3    | `verify` found integrity failures    |

## Format notes

* Magic: `KESF`, version 1.0, fixed 56-byte header
* Salt (32 B) + UTF-8 password hint immediately follow the header when encrypted
* Each file block = `IV(16) || HMAC(32) || AES-CBC(PKCS7 ciphertext)`
* Entry table is appended at the end; with `--encrypt-names` it is itself
  AES-encrypted + HMAC'd, so listing requires the password
