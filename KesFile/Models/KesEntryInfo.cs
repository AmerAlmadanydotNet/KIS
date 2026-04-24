using System;
using System.IO;

namespace KesFile.Models
{
    /// <summary>Type of entry stored in the archive.</summary>
    public enum KesEntryType : byte
    {
        File      = 0,
        Directory = 1
    }

    /// <summary>
    /// Metadata record for a single file or directory inside a KesFile archive.
    /// Serialised into the entry table (written at the end of the .kes file).
    /// </summary>
    public class KesEntryInfo
    {
        public KesEntryType       EntryType       { get; set; } = KesEntryType.File;
        /// <summary>Relative path inside the archive (forward-slash separated).</summary>
        public string             Path            { get; set; } = string.Empty;
        /// <summary>Size of the original, uncompressed data in bytes.</summary>
        public ulong              OriginalSize    { get; set; }
        /// <summary>
        /// Size of the stored data block in bytes (after compression + optional encryption overhead).
        /// When encrypted, this includes: IV(16) + HMAC(32) + ciphertext length.
        /// </summary>
        public ulong              StoredSize      { get; set; }
        /// <summary>Absolute byte offset of the data block within the .kes file.</summary>
        public long               DataOffset      { get; set; }
        public long               ModifiedUtcTicks{ get; set; }
        public long               CreatedUtcTicks { get; set; }
        public uint               FileAttributes  { get; set; }
        /// <summary>SHA-256 hash of the original (uncompressed, unencrypted) data.</summary>
        public byte[]             Sha256Hash      { get; set; } = new byte[32];
        /// <summary>Per-entry compression override (defaults to the archive-level setting).</summary>
        public KesCompressionType CompressionType { get; set; } = KesCompressionType.Deflate;

        // ─── Convenience Properties ──────────────────────────────────────────

        public string   FileName      => System.IO.Path.GetFileName(Path);
        public string   DirectoryPath => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
        public DateTime ModifiedUtc   => new DateTime(ModifiedUtcTicks, DateTimeKind.Utc);
        public DateTime CreatedUtc    => new DateTime(CreatedUtcTicks,  DateTimeKind.Utc);

        /// <summary>Space saved as a fraction of original size (0–1).</summary>
        public double CompressionRatio =>
            OriginalSize > 0 ? Math.Max(0, 1.0 - (double)StoredSize / OriginalSize) : 0;

        public string CompressionRatioText =>
            $"{CompressionRatio:P1}";

        public string OriginalSizeText  => FormatSize(OriginalSize);
        public string StoredSizeText    => FormatSize(StoredSize);

        private static string FormatSize(ulong bytes)
        {
            if (bytes < 1024)        return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        // ─── Serialisation ───────────────────────────────────────────────────

        public void Serialize(BinaryWriter writer)
        {
            byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(Path);
            writer.Write((byte)EntryType);              // 1
            writer.Write((ushort)pathBytes.Length);     // 2
            writer.Write(pathBytes);                    // variable
            writer.Write(OriginalSize);                 // 8
            writer.Write(StoredSize);                   // 8
            writer.Write(DataOffset);                   // 8
            writer.Write(ModifiedUtcTicks);             // 8
            writer.Write(CreatedUtcTicks);              // 8
            writer.Write(FileAttributes);               // 4
            writer.Write(Sha256Hash, 0, 32);            // 32
            writer.Write((byte)CompressionType);        // 1
        }

        public static KesEntryInfo Deserialize(BinaryReader reader)
        {
            var e = new KesEntryInfo();
            e.EntryType        = (KesEntryType)reader.ReadByte();
            ushort pathLen     = reader.ReadUInt16();
            byte[] pathBytes   = reader.ReadBytes(pathLen);
            e.Path             = System.Text.Encoding.UTF8.GetString(pathBytes);
            e.OriginalSize     = reader.ReadUInt64();
            e.StoredSize       = reader.ReadUInt64();
            e.DataOffset       = reader.ReadInt64();
            e.ModifiedUtcTicks = reader.ReadInt64();
            e.CreatedUtcTicks  = reader.ReadInt64();
            e.FileAttributes   = reader.ReadUInt32();
            e.Sha256Hash       = reader.ReadBytes(32);
            e.CompressionType  = (KesCompressionType)reader.ReadByte();
            return e;
        }
    }
}
