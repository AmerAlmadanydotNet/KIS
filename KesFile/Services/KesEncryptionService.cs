using System;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage.Streams;
using SysBuffer = System.Buffer;

namespace KesFile.Services
{
    /// <summary>
    /// Provides AES-256-CBC + HMAC-SHA256 authenticated encryption (Encrypt-then-MAC).
    ///
    /// Why this is far stronger than ZIP encryption:
    ///   • Key derivation: PBKDF2-SHA256 with 600 000 iterations (ZIP uses ~1 iteration)
    ///   • Algorithm: AES-256-CBC (ZIP's legacy encryption is 96-bit PKWARE)
    ///   • Authentication: HMAC-SHA256 prevents tampering (ZIP has none)
    ///   • Random 32-byte salt + random 16-byte IV per operation
    ///
    /// Stored blob layout (returned by Encrypt):
    ///   [IV 16 bytes][HMAC 32 bytes][ciphertext …]
    /// </summary>
    public class KesEncryptionService
    {
        private const int SaltSize      = 32;
        private const int IvSize        = 16;
        private const int HmacSize      = 32;
        private const int KeySize       = 32;   // AES-256
        private const int HmacKeySize   = 32;   // HMAC-SHA256
        private const int KdfIterations = 600_000;

        // ─── Key Derivation ──────────────────────────────────────────────────

        /// <summary>Generate a fresh random 32-byte salt.</summary>
        public static byte[] GenerateSalt()
        {
            IBuffer buf = CryptographicBuffer.GenerateRandom(SaltSize);
            CryptographicBuffer.CopyToByteArray(buf, out byte[] salt);
            return salt;
        }

        /// <summary>
        /// Derives a 64-byte key from password + salt using PBKDF2-SHA256.
        /// First 32 bytes → AES encryption key.
        /// Last  32 bytes → HMAC authentication key.
        /// </summary>
        public static (byte[] encKey, byte[] hmacKey) DeriveKeys(string password, byte[] salt)
        {
            var provider = KeyDerivationAlgorithmProvider.OpenAlgorithm(
                KeyDerivationAlgorithmNames.Pbkdf2Sha256);

            IBuffer passwordBuf = CryptographicBuffer.ConvertStringToBinary(
                password, BinaryStringEncoding.Utf8);
            IBuffer saltBuf = CryptographicBuffer.CreateFromByteArray(salt);

            var kdfParams = KeyDerivationParameters.BuildForPbkdf2(saltBuf, KdfIterations);
            CryptographicKey kdfKey = provider.CreateKey(passwordBuf);

            IBuffer derivedBuf = CryptographicEngine.DeriveKeyMaterial(
                kdfKey, kdfParams, (uint)(KeySize + HmacKeySize));

            CryptographicBuffer.CopyToByteArray(derivedBuf, out byte[] derived);

            byte[] encKey  = new byte[KeySize];
            byte[] hmacKey = new byte[HmacKeySize];
            SysBuffer.BlockCopy(derived, 0,       encKey,  0, KeySize);
            SysBuffer.BlockCopy(derived, KeySize, hmacKey, 0, HmacKeySize);
            return (encKey, hmacKey);
        }

        // ─── Encrypt ─────────────────────────────────────────────────────────

        /// <summary>
        /// Encrypts plaintext and returns: IV(16) + HMAC(32) + ciphertext.
        /// </summary>
        public static byte[] Encrypt(byte[] plaintext, byte[] encKey, byte[] hmacKey)
        {
            // 1. Generate random IV
            IBuffer ivBuf = CryptographicBuffer.GenerateRandom(IvSize);
            CryptographicBuffer.CopyToByteArray(ivBuf, out byte[] iv);

            // 2. AES-256-CBC encrypt
            var aesProvider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(
                SymmetricAlgorithmNames.AesCbcPkcs7);
            IBuffer encKeyBuf = CryptographicBuffer.CreateFromByteArray(encKey);
            CryptographicKey aesKey = aesProvider.CreateSymmetricKey(encKeyBuf);

            IBuffer plainBuf = CryptographicBuffer.CreateFromByteArray(plaintext);
            IBuffer cipherBuf = CryptographicEngine.Encrypt(aesKey, plainBuf, ivBuf);
            CryptographicBuffer.CopyToByteArray(cipherBuf, out byte[] ciphertext);

            // 3. HMAC-SHA256 over IV + ciphertext (Encrypt-then-MAC)
            byte[] macInput = new byte[IvSize + ciphertext.Length];
            SysBuffer.BlockCopy(iv,         0, macInput, 0,      IvSize);
            SysBuffer.BlockCopy(ciphertext, 0, macInput, IvSize, ciphertext.Length);

            var hmacProvider = MacAlgorithmProvider.OpenAlgorithm(
                MacAlgorithmNames.HmacSha256);
            IBuffer hmacKeyBuf = CryptographicBuffer.CreateFromByteArray(hmacKey);
            CryptographicKey hmacCryptoKey = hmacProvider.CreateKey(hmacKeyBuf);
            IBuffer macInputBuf = CryptographicBuffer.CreateFromByteArray(macInput);
            IBuffer hmacBuf = CryptographicEngine.Sign(hmacCryptoKey, macInputBuf);
            CryptographicBuffer.CopyToByteArray(hmacBuf, out byte[] hmac);

            // 4. Pack: IV | HMAC | ciphertext
            byte[] blob = new byte[IvSize + HmacSize + ciphertext.Length];
            SysBuffer.BlockCopy(iv,         0, blob, 0,                   IvSize);
            SysBuffer.BlockCopy(hmac,       0, blob, IvSize,              HmacSize);
            SysBuffer.BlockCopy(ciphertext, 0, blob, IvSize + HmacSize,   ciphertext.Length);
            return blob;
        }

        // ─── Decrypt ─────────────────────────────────────────────────────────

        /// <summary>
        /// Decrypts a blob produced by <see cref="Encrypt"/>.
        /// Throws <see cref="InvalidOperationException"/> if the HMAC is invalid.
        /// </summary>
        public static byte[] Decrypt(byte[] blob, byte[] encKey, byte[] hmacKey)
        {
            if (blob.Length < IvSize + HmacSize)
                throw new InvalidOperationException("Encrypted blob is too small.");

            // 1. Split IV / HMAC / ciphertext
            byte[] iv         = new byte[IvSize];
            byte[] storedHmac = new byte[HmacSize];
            int cipherLen     = blob.Length - IvSize - HmacSize;
            byte[] ciphertext = new byte[cipherLen];

            SysBuffer.BlockCopy(blob, 0,               iv,         0, IvSize);
            SysBuffer.BlockCopy(blob, IvSize,           storedHmac, 0, HmacSize);
            SysBuffer.BlockCopy(blob, IvSize + HmacSize,ciphertext, 0, cipherLen);

            // 2. Verify HMAC (constant-time via WinRT)
            byte[] macInput = new byte[IvSize + cipherLen];
            SysBuffer.BlockCopy(iv,         0, macInput, 0,      IvSize);
            SysBuffer.BlockCopy(ciphertext, 0, macInput, IvSize, cipherLen);

            var hmacProvider = MacAlgorithmProvider.OpenAlgorithm(MacAlgorithmNames.HmacSha256);
            IBuffer hmacKeyBuf = CryptographicBuffer.CreateFromByteArray(hmacKey);
            CryptographicKey hmacKey2 = hmacProvider.CreateKey(hmacKeyBuf);
            IBuffer macInputBuf = CryptographicBuffer.CreateFromByteArray(macInput);
            IBuffer computedHmacBuf = CryptographicEngine.Sign(hmacKey2, macInputBuf);
            CryptographicBuffer.CopyToByteArray(computedHmacBuf, out byte[] computedHmac);

            if (!ConstantTimeEquals(storedHmac, computedHmac))
                throw new InvalidOperationException("Archive integrity check failed. Wrong password or corrupted file.");

            // 3. AES-256-CBC decrypt
            var aesProvider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(
                SymmetricAlgorithmNames.AesCbcPkcs7);
            IBuffer encKeyBuf = CryptographicBuffer.CreateFromByteArray(encKey);
            CryptographicKey aesKey = aesProvider.CreateSymmetricKey(encKeyBuf);

            IBuffer ivBuf     = CryptographicBuffer.CreateFromByteArray(iv);
            IBuffer cipherBuf = CryptographicBuffer.CreateFromByteArray(ciphertext);
            IBuffer plainBuf  = CryptographicEngine.Decrypt(aesKey, cipherBuf, ivBuf);

            CryptographicBuffer.CopyToByteArray(plainBuf, out byte[] plaintext);
            return plaintext;
        }

        // ─── Checksum ────────────────────────────────────────────────────────

        /// <summary>Computes a SHA-256 hash of the data (used for per-file integrity).</summary>
        public static byte[] ComputeSha256(byte[] data)
        {
            var provider = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
            IBuffer dataBuf = CryptographicBuffer.CreateFromByteArray(data);
            IBuffer hashBuf = provider.HashData(dataBuf);
            CryptographicBuffer.CopyToByteArray(hashBuf, out byte[] hash);
            return hash;
        }

        /// <summary>Verifies a SHA-256 hash (constant-time comparison).</summary>
        public static bool VerifySha256(byte[] data, byte[] expectedHash)
        {
            byte[] actual = ComputeSha256(data);
            return ConstantTimeEquals(actual, expectedHash);
        }

        // ─── Helpers ─────────────────────────────────────────────────────────

        /// <summary>Timing-safe byte-array equality to prevent timing attacks.</summary>
        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
