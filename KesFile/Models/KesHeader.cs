using System;
using System.IO;

namespace KesFile.Models
{
    // ─── Enumerations ────────────────────────────────────────────────────────

    /// <summary>Compression algorithm used in the archive.</summary>
    public enum KesCompressionType : byte
    {
        None    = 0,
        Deflate = 1,   // System.IO.Compression – good speed/ratio balance
        Lzma    = 2    // SharpCompress LZMA – best ratio, ~30-50% better than Deflate
    }

    /// <summary>Encryption algorithm used in the archive.</summary>
    public enum KesEncryptionType : byte
    {
        None         = 0,
        Aes256CbcHmac = 1   // AES-256-CBC + HMAC-SHA256 (Encrypt-then-MAC)
    }

    /// <summary>Bit-flags stored in the archive header.</summary>
    [Flags]
    public enum KesArchiveFlags : uint
    {
        None              = 0,
        HasChecksums      = 1 << 0,   // SHA-256 per-file checksums
        IsSplit           = 1 << 1,   // Multi-part split archive
        EncryptFileNames  = 1 << 2,   // File paths are encrypted
        PreserveMetadata  = 1 << 3,   // File timestamps / attributes stored
    }

    // ─── KesHeader ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fixed-size 56-byte header written at the very start of every .kes file.
    /// </summary>
    public class KesHeader
    {
        // Magic signature "KESF" (0x4B 0x45 0x53 0x46)
        public static readonly byte[] MagicBytes = { 0x4B, 0x45, 0x53, 0x46 };
        public const int HeaderSize = 56;

        public byte               VersionMajor      { get; set; } = 1;
        public byte               VersionMinor      { get; set; } = 0;
        public KesCompressionType CompressionType   { get; set; } = KesCompressionType.Deflate;
        public KesEncryptionType  EncryptionType    { get; set; } = KesEncryptionType.None;
        public KesArchiveFlags    Flags             { get; set; } = KesArchiveFlags.HasChecksums | KesArchiveFlags.PreserveMetadata;
        public uint               EntryCount        { get; set; }
        public ulong              OriginalTotalSize  { get; set; }
        public ulong              CompressedTotalSize{ get; set; }
        public long               CreatedUtcTicks   { get; set; } = DateTime.UtcNow.Ticks;
        public long               EntryTableOffset  { get; set; }   // Seek here to read entry table
        public ushort             SplitPartNumber   { get; set; }   // 0 = not split
        public ushort             SplitTotalParts   { get; set; }

        // Convenience helpers
        public bool IsEncrypted       => EncryptionType != KesEncryptionType.None;
        public bool IsSplit           => (Flags & KesArchiveFlags.IsSplit)          != 0;
        public bool HasChecksums      => (Flags & KesArchiveFlags.HasChecksums)     != 0;
        public bool HasEncryptedNames => (Flags & KesArchiveFlags.EncryptFileNames) != 0;

        /// <summary>Serialise the header to the writer (56 bytes).</summary>
        public void Serialize(BinaryWriter writer)
        {
            writer.Write(MagicBytes);               // 4
            writer.Write(VersionMajor);             // 1
            writer.Write(VersionMinor);             // 1
            writer.Write((byte)CompressionType);    // 1
            writer.Write((byte)EncryptionType);     // 1
            writer.Write((uint)Flags);              // 4
            writer.Write(EntryCount);               // 4
            writer.Write(OriginalTotalSize);        // 8
            writer.Write(CompressedTotalSize);      // 8
            writer.Write(CreatedUtcTicks);          // 8
            writer.Write(EntryTableOffset);         // 8
            writer.Write(SplitPartNumber);          // 2
            writer.Write(SplitTotalParts);          // 2
            // Padding to 56 bytes (4 reserved bytes)
            writer.Write((uint)0);                  // 4 reserved
        }

        /// <summary>Read and validate a header from the reader.</summary>
        public static KesHeader Deserialize(BinaryReader reader)
        {
            byte[] magic = reader.ReadBytes(4);
            if (magic.Length < 4
                || magic[0] != MagicBytes[0]
                || magic[1] != MagicBytes[1]
                || magic[2] != MagicBytes[2]
                || magic[3] != MagicBytes[3])
            {
                throw new InvalidDataException("Not a valid KesFile archive (invalid magic signature).");
            }

            var h = new KesHeader
            {
                VersionMajor       = reader.ReadByte(),
                VersionMinor       = reader.ReadByte(),
                CompressionType    = (KesCompressionType)reader.ReadByte(),
                EncryptionType     = (KesEncryptionType)reader.ReadByte(),
                Flags              = (KesArchiveFlags)reader.ReadUInt32(),
                EntryCount         = reader.ReadUInt32(),
                OriginalTotalSize  = reader.ReadUInt64(),
                CompressedTotalSize= reader.ReadUInt64(),
                CreatedUtcTicks    = reader.ReadInt64(),
                EntryTableOffset   = reader.ReadInt64(),
                SplitPartNumber    = reader.ReadUInt16(),
                SplitTotalParts    = reader.ReadUInt16(),
            };
            reader.ReadUInt32(); // consume reserved bytes
            return h;
        }
    }

    // ─── EncryptionBlock ─────────────────────────────────────────────────────

    /// <summary>
    /// Written immediately after the header when encryption is enabled.
    /// Contains the per-archive salt for PBKDF2 key derivation.
    /// Size: 32 bytes salt + 2-byte hint length + N-byte UTF-8 hint.
    /// </summary>
    public class KesEncryptionBlock
    {
        public byte[] Salt        { get; set; } = new byte[32];   // PBKDF2 salt
        public string PasswordHint{ get; set; } = string.Empty;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(Salt);                                    // 32 bytes
            byte[] hintBytes = System.Text.Encoding.UTF8.GetBytes(PasswordHint ?? string.Empty);
            ushort hintLen = (ushort)Math.Min(hintBytes.Length, 512);
            writer.Write(hintLen);                                 // 2 bytes
            writer.Write(hintBytes, 0, hintLen);
        }

        public static KesEncryptionBlock Deserialize(BinaryReader reader)
        {
            var block = new KesEncryptionBlock
            {
                Salt = reader.ReadBytes(32)
            };
            ushort hintLen = reader.ReadUInt16();
            byte[] hintBytes = reader.ReadBytes(hintLen);
            block.PasswordHint = System.Text.Encoding.UTF8.GetString(hintBytes);
            return block;
        }
    }
}
