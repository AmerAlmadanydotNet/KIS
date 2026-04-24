namespace KesFile.Models
{
    /// <summary>Compression speed / ratio trade-off.</summary>
    public enum CompressionSpeed
    {
        Fastest = 0,
        Fast    = 1,
        Normal  = 2,
        Maximum = 3
    }

    /// <summary>Options used when creating a new KesFile archive.</summary>
    public class ArchiveOptions
    {
        // ─── Compression ─────────────────────────────────────────────────────
        public KesCompressionType CompressionType  { get; set; } = KesCompressionType.Lzma;
        public CompressionSpeed   CompressionSpeed { get; set; } = CompressionSpeed.Normal;

        // ─── Encryption ──────────────────────────────────────────────────────
        public bool   EnableEncryption  { get; set; } = false;
        public string Password          { get; set; } = string.Empty;
        public string PasswordHint      { get; set; } = string.Empty;
        /// <summary>
        /// When true, file paths inside the entry table are also encrypted,
        /// making the archive completely opaque to external scanners.
        /// </summary>
        public bool   EncryptFileNames  { get; set; } = false;

        // ─── Split archive ────────────────────────────────────────────────────
        public bool   EnableSplit       { get; set; } = false;
        /// <summary>Maximum size per part in bytes (default 100 MB).</summary>
        public ulong  SplitSizeBytes    { get; set; } = 100UL * 1024 * 1024;

        // ─── Integrity & metadata ─────────────────────────────────────────────
        public bool   HasChecksums      { get; set; } = true;
        public bool   PreserveMetadata  { get; set; } = true;

        // ─── Helpers ─────────────────────────────────────────────────────────
        public KesArchiveFlags BuildFlags()
        {
            var flags = KesArchiveFlags.None;
            if (HasChecksums)     flags |= KesArchiveFlags.HasChecksums;
            if (EnableSplit)      flags |= KesArchiveFlags.IsSplit;
            if (EncryptFileNames) flags |= KesArchiveFlags.EncryptFileNames;
            if (PreserveMetadata) flags |= KesArchiveFlags.PreserveMetadata;
            return flags;
        }
    }
}
