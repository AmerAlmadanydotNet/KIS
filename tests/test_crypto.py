"""Tests for kis.crypto"""

import os

import pytest
from cryptography.exceptions import InvalidTag

from kis.crypto import (
    derive_key,
    encrypt,
    decrypt,
    generate_salt,
    generate_nonce,
    KEY_SIZE,
    KDF_ITERATIONS,
    NONCE_SIZE,
    SALT_SIZE,
)


class TestGenerateHelpers:
    def test_salt_length(self):
        salt = generate_salt()
        assert len(salt) == SALT_SIZE

    def test_salt_random(self):
        # Two salts should almost certainly differ
        assert generate_salt() != generate_salt()

    def test_nonce_length(self):
        nonce = generate_nonce()
        assert len(nonce) == NONCE_SIZE

    def test_nonce_random(self):
        assert generate_nonce() != generate_nonce()


class TestDeriveKey:
    def test_output_length(self):
        key = derive_key("secret", generate_salt())
        assert len(key) == KEY_SIZE

    def test_deterministic_given_same_inputs(self):
        salt = generate_salt()
        assert derive_key("password", salt) == derive_key("password", salt)

    def test_different_passwords_differ(self):
        salt = generate_salt()
        assert derive_key("password1", salt) != derive_key("password2", salt)

    def test_different_salts_differ(self):
        assert derive_key("password", generate_salt()) != derive_key("password", generate_salt())

    def test_bytes_password(self):
        salt = generate_salt()
        key_str = derive_key("hello", salt)
        key_bytes = derive_key(b"hello", salt)
        assert key_str == key_bytes

    def test_iterations(self):
        assert KDF_ITERATIONS == 600_000


class TestEncryptDecrypt:
    def _setup(self):
        key = derive_key("test-key", generate_salt())
        nonce = generate_nonce()
        return key, nonce

    def test_round_trip(self):
        key, nonce = self._setup()
        plaintext = b"Hello, KIS!"
        ciphertext = encrypt(key, nonce, plaintext)
        assert decrypt(key, nonce, ciphertext) == plaintext

    def test_ciphertext_differs_from_plaintext(self):
        key, nonce = self._setup()
        plaintext = b"sensitive data"
        ciphertext = encrypt(key, nonce, plaintext)
        assert ciphertext != plaintext

    def test_wrong_key_raises(self):
        key1, nonce = self._setup()
        key2 = derive_key("other-password", generate_salt())
        ciphertext = encrypt(key1, nonce, b"data")
        with pytest.raises(InvalidTag):
            decrypt(key2, nonce, ciphertext)

    def test_wrong_nonce_raises(self):
        key, nonce1 = self._setup()
        nonce2 = generate_nonce()
        ciphertext = encrypt(key, nonce1, b"data")
        with pytest.raises(InvalidTag):
            decrypt(key, nonce2, ciphertext)

    def test_tampered_ciphertext_raises(self):
        key, nonce = self._setup()
        ciphertext = bytearray(encrypt(key, nonce, b"data"))
        ciphertext[0] ^= 0xFF
        with pytest.raises(InvalidTag):
            decrypt(key, nonce, bytes(ciphertext))

    def test_empty_plaintext(self):
        key, nonce = self._setup()
        ciphertext = encrypt(key, nonce, b"")
        assert decrypt(key, nonce, ciphertext) == b""

    def test_large_plaintext(self):
        key, nonce = self._setup()
        plaintext = os.urandom(1024 * 1024)
        ciphertext = encrypt(key, nonce, plaintext)
        assert decrypt(key, nonce, ciphertext) == plaintext
