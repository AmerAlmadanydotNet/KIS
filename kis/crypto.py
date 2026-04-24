"""
Cryptographic primitives for the KIS archive format.

Key derivation: PBKDF2-HMAC-SHA256 with 600,000 iterations.
Encryption:     AES-256-GCM (authenticated encryption).
"""

import os

from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.pbkdf2 import PBKDF2HMAC
from cryptography.hazmat.primitives import hashes

SALT_SIZE = 32          # bytes — random salt for PBKDF2
NONCE_SIZE = 12         # bytes — random nonce for AES-GCM
KEY_SIZE = 32           # bytes — 256-bit AES key
KDF_ITERATIONS = 600_000


def derive_key(password: str | bytes, salt: bytes) -> bytes:
    """Derive a 256-bit encryption key from *password* and *salt*."""
    if isinstance(password, str):
        password = password.encode()
    kdf = PBKDF2HMAC(
        algorithm=hashes.SHA256(),
        length=KEY_SIZE,
        salt=salt,
        iterations=KDF_ITERATIONS,
    )
    return kdf.derive(password)


def generate_salt() -> bytes:
    """Return a cryptographically random salt."""
    return os.urandom(SALT_SIZE)


def generate_nonce() -> bytes:
    """Return a cryptographically random GCM nonce."""
    return os.urandom(NONCE_SIZE)


def encrypt(key: bytes, nonce: bytes, plaintext: bytes) -> bytes:
    """AES-256-GCM encrypt *plaintext*.

    Returns *ciphertext || tag* (16-byte tag appended by the library).
    """
    aesgcm = AESGCM(key)
    return aesgcm.encrypt(nonce, plaintext, None)


def decrypt(key: bytes, nonce: bytes, ciphertext: bytes) -> bytes:
    """AES-256-GCM decrypt *ciphertext* (which includes the 16-byte tag).

    Raises ``cryptography.exceptions.InvalidTag`` on authentication failure.
    """
    aesgcm = AESGCM(key)
    return aesgcm.decrypt(nonce, ciphertext, None)
