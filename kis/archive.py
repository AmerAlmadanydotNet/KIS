"""
KIS archive format — creation, extraction, and listing.

File format (binary, big-endian integers):
───────────────────────────────────────────
  Magic    4 bytes   b'KIS\\x00'
  Version  1 byte    0x01
  Salt     32 bytes  random PBKDF2 salt
  Nonce    12 bytes  random AES-GCM nonce
  Payload  variable  AES-256-GCM ciphertext (ciphertext || 16-byte tag)
───────────────────────────────────────────
Decrypted payload layout:
  Index length  4 bytes  big-endian uint32 — byte length of the JSON index
  Index JSON    variable UTF-8 JSON array of entry objects:
                  [{"name": str, "size": int, "compressed_size": int,
                    "offset": int}, ...]
                "offset" is relative to the start of the file-data section.
  File data     variable concatenated LZMA-compressed file bytes

Everything after the 37-byte header (magic + version + salt + nonce) is
encrypted, so filenames, directory structure, sizes, and content are all
hidden from an observer who does not know the password.
"""

import io
import json
import lzma
import os
import struct
from pathlib import Path
from typing import IO

from .crypto import (
    derive_key,
    encrypt,
    decrypt,
    generate_nonce,
    generate_salt,
    NONCE_SIZE,
    SALT_SIZE,
)

MAGIC = b"KIS\x00"
VERSION = 0x01
HEADER_SIZE = len(MAGIC) + 1 + SALT_SIZE + NONCE_SIZE  # 49 bytes

LZMA_FILTERS = [
    {"id": lzma.FILTER_LZMA2, "preset": lzma.PRESET_DEFAULT},
]


# ── Internal helpers ────────────────────────────────────────────────────────


def _compress(data: bytes) -> bytes:
    return lzma.compress(data, format=lzma.FORMAT_XZ, filters=LZMA_FILTERS)


def _decompress(data: bytes) -> bytes:
    return lzma.decompress(data, format=lzma.FORMAT_XZ)


def _build_payload(entries: list[dict], file_data: bytes) -> bytes:
    """Assemble the plaintext payload (index + file data)."""
    index_bytes = json.dumps(entries, separators=(",", ":")).encode()
    return struct.pack(">I", len(index_bytes)) + index_bytes + file_data


def _parse_payload(payload: bytes) -> tuple[list[dict], bytes]:
    """Split the payload back into the index and the raw file-data block."""
    if len(payload) < 4:
        raise ValueError("Payload too short — archive is corrupt.")
    (index_len,) = struct.unpack(">I", payload[:4])
    if len(payload) < 4 + index_len:
        raise ValueError("Payload too short for declared index length.")
    index = json.loads(payload[4 : 4 + index_len])
    file_data = payload[4 + index_len :]
    return index, file_data


# ── Public API ───────────────────────────────────────────────────────────────


def create(
    archive_path: str | os.PathLike,
    source_paths: list[str | os.PathLike],
    password: str | bytes,
    *,
    base_dir: str | os.PathLike | None = None,
) -> None:
    """Create a KIS archive at *archive_path* containing *source_paths*.

    Parameters
    ----------
    archive_path:
        Destination ``.kis`` file path (will be overwritten if it exists).
    source_paths:
        Files and/or directories to include.  Directories are walked
        recursively.
    password:
        Archive password (str or bytes).
    base_dir:
        Optional root used to compute relative names stored in the index.
        Defaults to the common parent of *source_paths* when omitted.
    """
    files: list[Path] = []
    for sp in source_paths:
        p = Path(sp)
        if p.is_dir():
            for f in sorted(p.rglob("*")):
                if f.is_file():
                    files.append(f)
        elif p.is_file():
            files.append(p)
        else:
            raise FileNotFoundError(f"Path not found: {sp}")

    if not files:
        raise ValueError("No files to archive.")

    if base_dir is None:
        # Derive a sensible common base so stored names are relative.
        common = Path(os.path.commonpath(files))
        base_dir = common if common.is_dir() else common.parent
    base_dir = Path(base_dir)

    entries: list[dict] = []
    file_data_parts: list[bytes] = []
    offset = 0

    for f in files:
        raw = f.read_bytes()
        compressed = _compress(raw)
        try:
            name = f.relative_to(base_dir).as_posix()
        except ValueError:
            name = f.name
        entry = {
            "name": name,
            "size": len(raw),
            "compressed_size": len(compressed),
            "offset": offset,
        }
        entries.append(entry)
        file_data_parts.append(compressed)
        offset += len(compressed)

    file_data = b"".join(file_data_parts)
    plaintext = _build_payload(entries, file_data)

    salt = generate_salt()
    nonce = generate_nonce()
    key = derive_key(password, salt)
    ciphertext = encrypt(key, nonce, plaintext)

    archive_path = Path(archive_path)
    archive_path.parent.mkdir(parents=True, exist_ok=True)
    with archive_path.open("wb") as fh:
        fh.write(MAGIC)
        fh.write(bytes([VERSION]))
        fh.write(salt)
        fh.write(nonce)
        fh.write(ciphertext)


def _read_and_decrypt(archive_path: Path, password: str | bytes) -> tuple[list[dict], bytes]:
    """Read *archive_path*, verify the header, decrypt, and return the index
    and the raw file-data block."""
    with archive_path.open("rb") as fh:
        _check_header(fh)
        salt = fh.read(SALT_SIZE)
        nonce = fh.read(NONCE_SIZE)
        ciphertext = fh.read()

    key = derive_key(password, salt)
    # InvalidTag is raised automatically if MAC verification fails.
    plaintext = decrypt(key, nonce, ciphertext)
    return _parse_payload(plaintext)


def _check_header(fh: IO[bytes]) -> None:
    magic = fh.read(len(MAGIC))
    if magic != MAGIC:
        raise ValueError("Not a KIS archive (magic bytes mismatch).")
    version = fh.read(1)
    if not version or version[0] != VERSION:
        raise ValueError(f"Unsupported KIS version: {version!r}.")


def extract(
    archive_path: str | os.PathLike,
    password: str | bytes,
    *,
    output_dir: str | os.PathLike = ".",
) -> list[str]:
    """Extract all entries from *archive_path* into *output_dir*.

    Returns the list of extracted file names (relative paths as stored in the
    archive index).

    Raises
    ------
    cryptography.exceptions.InvalidTag
        If the password is wrong or the archive has been tampered with.
    ValueError
        If the file is not a valid KIS archive.
    """
    archive_path = Path(archive_path)
    output_dir = Path(output_dir)
    index, file_data = _read_and_decrypt(archive_path, password)

    extracted: list[str] = []
    for entry in index:
        compressed = file_data[entry["offset"] : entry["offset"] + entry["compressed_size"]]
        raw = _decompress(compressed)
        dest = output_dir / entry["name"]
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(raw)
        extracted.append(entry["name"])

    return extracted


def list_contents(
    archive_path: str | os.PathLike,
    password: str | bytes,
) -> list[dict]:
    """Return the archive index (list of entry dicts) without extracting.

    Each dict has keys: ``name``, ``size``, ``compressed_size``, ``offset``.
    """
    archive_path = Path(archive_path)
    index, _ = _read_and_decrypt(archive_path, password)
    return index
