# KIS — Keep It Simple archive format

KIS is a modern archive format built from the ground up to answer every
weakness of ZIP.

| Feature | ZIP | **KIS** |
|---|---|---|
| Compression | Deflate | **LZMA** (30–50 % smaller) |
| Encryption | ZipCrypto (crackable in seconds) | **AES-256-GCM** |
| Key derivation | None / weak | **PBKDF2-HMAC-SHA256, 600 000 iterations** |
| Encrypted filenames | ✗ — anyone can see the file list | **✓ — complete black box** |

## Installation

```bash
pip install .          # from the repository root
```

Python ≥ 3.11 and the [`cryptography`](https://cryptography.io) package are
required.

## CLI usage

```
kis create  <archive.kis> <file|dir> [<file|dir> ...]
kis extract <archive.kis> [-d OUTPUT_DIR]
kis list    <archive.kis>
```

The password is read from the `KIS_PASSWORD` environment variable when set,
otherwise prompted interactively (without echo).  You can also pass it with
`--password` / `-p`.

### Examples

```bash
# Create an archive
KIS_PASSWORD=secret kis create backup.kis ~/documents/

# List contents (filenames only visible after decryption)
KIS_PASSWORD=secret kis list backup.kis

# Extract
KIS_PASSWORD=secret kis extract backup.kis -d ./restored
```

## Python API

```python
from kis import create, extract, list_contents

# Create
create("backup.kis", ["docs/", "report.pdf"], password="secret")

# List
for entry in list_contents("backup.kis", password="secret"):
    print(entry["name"], entry["size"], entry["compressed_size"])

# Extract
extracted = extract("backup.kis", password="secret", output_dir="./out")
```

## File format

```
[Magic    4 bytes]  b'KIS\x00'
[Version  1 byte ]  0x01
[Salt    32 bytes]  random PBKDF2 salt
[Nonce   12 bytes]  random AES-GCM nonce
[Payload variable]  AES-256-GCM( index-JSON || LZMA-file-data )
```

Because *everything* after the 49-byte header is encrypted, an attacker
learns nothing about the archive contents — not filenames, directory
structure, individual file sizes, or file data — without the password.

## Running tests

```bash
pip install pytest
pytest tests/ -v
```
