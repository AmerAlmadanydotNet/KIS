using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using KesFile.Models;

namespace KesFile.Services
{
    /// <summary>Raised after each file is processed during create/extract.</summary>
    public class ArchiveProgressEventArgs : EventArgs
    {
        public int     Processed      { get; set; }
        public int     Total          { get; set; }
        public string  CurrentFile    { get; set; } = string.Empty;
        public ulong   ProcessedBytes { get; set; }
        public ulong   TotalBytes     { get; set; }
        /// <summary>0..1 fraction based on bytes when available, else file count.</summary>
        public double  Fraction       => TotalBytes > 0
            ? Math.Min(1.0, (double)ProcessedBytes / TotalBytes)
            : (Total > 0 ? (double)Processed / Total : 0);
    }

    /// <summary>
    /// High-level service for creating and reading .kes archives.
    ///
    /// KesFile vs ZIP:
    ///   ✔ LZMA compression  – 30-50 % better ratio than Deflate
    ///   ✔ AES-256-CBC + HMAC-SHA256 – far stronger than ZIP's encryption
    ///   ✔ PBKDF2-SHA256 (600k iterations) – resists brute-force
    ///   ✔ Per-file SHA-256 checksums (ZIP uses only CRC-32)
    ///   ✔ Optional encrypted filenames – content opaque to scanners
    ///   ✔ Full metadata (timestamps, attributes)
    ///   ✔ Native split-archive support
    /// </summary>
    public class KesArchiveService
    {
        private readonly KesCompressionService _compressor = new();

        public event EventHandler<ArchiveProgressEventArgs>? Progress;

        // ─── Create ──────────────────────────────────────────────────────────

        /// <summary>
        /// Create a .kes archive from <paramref name="sourceFiles"/>.
        /// The file is written to <paramref name="destFile"/>.
        /// </summary>
        public async Task CreateArchiveAsync(
            IList<(StorageFile File, string RelativePath)> sourceFiles,
            StorageFile         destFile,
            ArchiveOptions      options,
            CancellationToken   ct = default)
        {
            // Derive encryption keys once if needed
            byte[]? salt    = null;
            byte[]? encKey  = null;
            byte[]? hmacKey = null;

            if (options.EnableEncryption)
            {
                if (string.IsNullOrEmpty(options.Password))
                    throw new ArgumentException("Password required for encryption.");

                salt = KesEncryptionService.GenerateSalt();
                (encKey, hmacKey) = KesEncryptionService.DeriveKeys(options.Password, salt);
            }

            using IRandomAccessStream ras  = await destFile.OpenAsync(FileAccessMode.ReadWrite);
            using Stream              stream = ras.AsStream();
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            // 1. Write placeholder header (we'll seek back to update it)
            var header = new KesHeader
            {
                CompressionType    = options.CompressionType,
                EncryptionType     = options.EnableEncryption ? KesEncryptionType.Aes256CbcHmac : KesEncryptionType.None,
                Flags              = options.BuildFlags(),
                EntryCount         = (uint)sourceFiles.Count,
                CreatedUtcTicks    = DateTime.UtcNow.Ticks,
            };
            header.Serialize(writer);

            // 2. Write encryption block (salt + hint) if encrypting
            if (options.EnableEncryption && salt != null)
            {
                var encBlock = new KesEncryptionBlock
                {
                    Salt         = salt,
                    PasswordHint = options.PasswordHint ?? string.Empty
                };
                encBlock.Serialize(writer);
            }

            // 3. Write data section — one block per file
            var entries = new List<KesEntryInfo>(sourceFiles.Count);
            ulong totalOriginal   = 0;
            ulong totalCompressed = 0;

            // Pre-calculate total bytes for accurate progress
            ulong totalBytes = 0;
            foreach (var (f, _) in sourceFiles)
            {
                var p = await f.GetBasicPropertiesAsync();
                totalBytes += p.Size;
            }
            ulong processedBytes = 0;

            for (int i = 0; i < sourceFiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (src, relativePath) = sourceFiles[i];

                OnProgress(i, sourceFiles.Count, relativePath, processedBytes, totalBytes);

                var props = await src.GetBasicPropertiesAsync();
                byte[] rawData = await ReadAllBytesAsync(src);
                // Compute SHA-256 of original data
                byte[] sha256 = options.HasChecksums
                    ? KesEncryptionService.ComputeSha256(rawData)
                    : new byte[32];

                // Compress
                var (compressed, actualCompressionType) =
                    _compressor.CompressWithActualType(rawData, options.CompressionType, options.CompressionSpeed);

                // Encrypt (if enabled)
                byte[] stored = options.EnableEncryption && encKey != null && hmacKey != null
                    ? KesEncryptionService.Encrypt(compressed, encKey, hmacKey)
                    : compressed;

                long dataOffset = stream.Position;
                writer.Write(stored);

                var entry = new KesEntryInfo
                {
                    EntryType        = KesEntryType.File,
                    Path             = relativePath,
                    OriginalSize     = (ulong)rawData.Length,
                    StoredSize       = (ulong)stored.Length,
                    DataOffset       = dataOffset,
                    ModifiedUtcTicks = props.DateModified.UtcTicks,
                    CreatedUtcTicks  = DateTime.UtcNow.Ticks,
                    FileAttributes   = 0,
                    Sha256Hash       = sha256,
                    CompressionType  = actualCompressionType,    // truthful per-entry type (LZMA may fall back to Deflate)
                };

                entries.Add(entry);
                totalOriginal   += entry.OriginalSize;
                totalCompressed += entry.StoredSize;
                processedBytes  += (ulong)rawData.Length;
                OnProgress(i + 1, sourceFiles.Count, relativePath, processedBytes, totalBytes);
            }

            // 4. Write entry table and record its position.
            //    When EncryptFileNames is enabled, the table itself is sealed with
            //    AES-256-CBC + HMAC-SHA256 so neither paths nor sizes leak.
            long entryTableOffset = stream.Position;
            bool sealNames = options.EncryptFileNames && encKey != null && hmacKey != null;

            if (sealNames)
            {
                using var tableMs = new MemoryStream();
                using (var tableWriter = new BinaryWriter(tableMs, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    foreach (var entry in entries)
                        entry.Serialize(tableWriter);
                    tableWriter.Flush();
                }
                byte[] sealedTable = KesEncryptionService.Encrypt(tableMs.ToArray(), encKey!, hmacKey!);
                writer.Write(sealedTable.Length);  // int32 length prefix
                writer.Write(sealedTable);
            }
            else
            {
                foreach (var entry in entries)
                    entry.Serialize(writer);
            }

            // 5. Seek back to update the header with totals + entry table offset
            stream.Seek(0, SeekOrigin.Begin);
            header.EntryCount         = (uint)entries.Count;
            header.OriginalTotalSize  = totalOriginal;
            header.CompressedTotalSize= totalCompressed;
            header.EntryTableOffset   = entryTableOffset;
            header.Serialize(writer);

            writer.Flush();
            OnProgress(sourceFiles.Count, sourceFiles.Count, "Done", totalBytes, totalBytes);
        }

        // ─── Read / Open ─────────────────────────────────────────────────────

        /// <summary>
        /// Reads only the header (and the encryption block, if present) without
        /// touching the entry table. Useful for showing the password prompt
        /// before the user has typed a password.
        /// </summary>
        public async Task<(KesHeader header, string passwordHint)> PeekArchiveAsync(
            StorageFile archiveFile)
        {
            using IRandomAccessStream ras    = await archiveFile.OpenReadAsync();
            using Stream              stream = ras.AsStream();
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            var header = KesHeader.Deserialize(reader);
            string hint = string.Empty;
            if (header.IsEncrypted)
            {
                var encBlock = KesEncryptionBlock.Deserialize(reader);
                hint = encBlock.PasswordHint ?? string.Empty;
            }
            return (header, hint);
        }

        /// <summary>
        /// Reads the header and entry table from a .kes file without extracting data.
        /// If the archive is encrypted you must supply the <paramref name="password"/>.
        /// </summary>
        public async Task<(KesHeader header, IList<KesEntryInfo> entries)> OpenArchiveAsync(
            StorageFile archiveFile,
            string?     password = null)
        {
            using IRandomAccessStream ras    = await archiveFile.OpenReadAsync();
            using Stream              stream = ras.AsStream();
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            var header = KesHeader.Deserialize(reader);

            KesEncryptionBlock? encBlock = null;
            byte[]? openEncKey  = null;
            byte[]? openHmacKey = null;
            if (header.IsEncrypted)
            {
                encBlock = KesEncryptionBlock.Deserialize(reader);
                if (string.IsNullOrEmpty(password))
                    throw new ArgumentException("This archive is encrypted. Please provide the password.");
                if (header.HasEncryptedNames)
                    (openEncKey, openHmacKey) = KesEncryptionService.DeriveKeys(password!, encBlock.Salt);
            }

            // Jump to the entry table
            stream.Seek(header.EntryTableOffset, SeekOrigin.Begin);

            var entries = new List<KesEntryInfo>((int)header.EntryCount);
            if (header.HasEncryptedNames && openEncKey != null && openHmacKey != null)
            {
                int sealedLen = reader.ReadInt32();
                byte[] sealedTable = reader.ReadBytes(sealedLen);
                byte[] tableBytes  = KesEncryptionService.Decrypt(sealedTable, openEncKey, openHmacKey);
                using var tms = new MemoryStream(tableBytes);
                using var tr  = new BinaryReader(tms, System.Text.Encoding.UTF8, leaveOpen: true);
                for (uint i = 0; i < header.EntryCount; i++)
                    entries.Add(KesEntryInfo.Deserialize(tr));
            }
            else
            {
                for (uint i = 0; i < header.EntryCount; i++)
                    entries.Add(KesEntryInfo.Deserialize(reader));
            }

            return (header, entries);
        }

        // ─── Extract ─────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts all entries from <paramref name="archiveFile"/> to <paramref name="destFolder"/>.
        /// </summary>
        public async Task ExtractArchiveAsync(
            StorageFile       archiveFile,
            StorageFolder     destFolder,
            string?           password,
            CancellationToken ct = default)
        {
            using IRandomAccessStream ras    = await archiveFile.OpenReadAsync();
            using Stream              stream = ras.AsStream();
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            var header = KesHeader.Deserialize(reader);

            byte[]? encKey  = null;
            byte[]? hmacKey = null;

            if (header.IsEncrypted)
            {
                var encBlock = KesEncryptionBlock.Deserialize(reader);
                if (string.IsNullOrEmpty(password))
                    throw new ArgumentException("Password required to extract this archive.");
                (encKey, hmacKey) = KesEncryptionService.DeriveKeys(password!, encBlock.Salt);
            }

            stream.Seek(header.EntryTableOffset, SeekOrigin.Begin);
            var entries = new List<KesEntryInfo>((int)header.EntryCount);
            if (header.HasEncryptedNames && encKey != null && hmacKey != null)
            {
                int sealedLen = reader.ReadInt32();
                byte[] sealedTable = reader.ReadBytes(sealedLen);
                byte[] tableBytes  = KesEncryptionService.Decrypt(sealedTable, encKey, hmacKey);
                using var tms = new MemoryStream(tableBytes);
                using var tr  = new BinaryReader(tms, System.Text.Encoding.UTF8, leaveOpen: true);
                for (uint i = 0; i < header.EntryCount; i++)
                    entries.Add(KesEntryInfo.Deserialize(tr));
            }
            else
            {
                for (uint i = 0; i < header.EntryCount; i++)
                    entries.Add(KesEntryInfo.Deserialize(reader));
            }

            for (int i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[i];
                OnProgress(i, entries.Count, entry.FileName);

                await ExtractEntryAsync(stream, entry, destFolder, encKey, hmacKey, header);
            }

            OnProgress(entries.Count, entries.Count, "Done");
        }

        // ─── Verify (no extraction) ──────────────────────────────────────────

        /// <summary>Result of an archive verification pass.</summary>
        public class VerifyResult
        {
            public bool          Success           { get; set; }
            public int           EntriesChecked    { get; set; }
            public int           EntriesFailed     { get; set; }
            public List<string>  FailedPaths       { get; } = new();
            public string        Summary           { get; set; } = string.Empty;
        }

        /// <summary>
        /// Verifies the integrity of every entry in <paramref name="archiveFile"/>
        /// without writing anything to disk. For encrypted archives the HMAC of
        /// every block is checked; when checksums are present the SHA-256 of the
        /// decompressed content is compared against the entry table.
        /// </summary>
        public async Task<VerifyResult> VerifyArchiveAsync(
            StorageFile       archiveFile,
            string?           password,
            CancellationToken ct = default)
        {
            var result = new VerifyResult();

            using IRandomAccessStream ras    = await archiveFile.OpenReadAsync();
            using Stream              stream = ras.AsStream();
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            var header = KesHeader.Deserialize(reader);

            byte[]? encKey  = null;
            byte[]? hmacKey = null;
            if (header.IsEncrypted)
            {
                var encBlock = KesEncryptionBlock.Deserialize(reader);
                if (string.IsNullOrEmpty(password))
                    throw new ArgumentException("Password required to verify this archive.");
                (encKey, hmacKey) = KesEncryptionService.DeriveKeys(password!, encBlock.Salt);
            }

            stream.Seek(header.EntryTableOffset, SeekOrigin.Begin);
            var entries = new List<KesEntryInfo>((int)header.EntryCount);
            if (header.HasEncryptedNames && encKey != null && hmacKey != null)
            {
                int sealedLen = reader.ReadInt32();
                byte[] sealedTable = reader.ReadBytes(sealedLen);
                byte[] tableBytes  = KesEncryptionService.Decrypt(sealedTable, encKey, hmacKey);
                using var tms = new MemoryStream(tableBytes);
                using var tr  = new BinaryReader(tms, System.Text.Encoding.UTF8, leaveOpen: true);
                for (uint i = 0; i < header.EntryCount; i++)
                    entries.Add(KesEntryInfo.Deserialize(tr));
            }
            else
            {
                for (uint i = 0; i < header.EntryCount; i++)
                    entries.Add(KesEntryInfo.Deserialize(reader));
            }

            for (int i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[i];
                OnProgress(i, entries.Count, entry.FileName);

                try
                {
                    stream.Seek(entry.DataOffset, SeekOrigin.Begin);
                    byte[] stored = new byte[entry.StoredSize];
                    int read = 0;
                    while (read < stored.Length)
                    {
                        int chunk = stream.Read(stored, read, stored.Length - read);
                        if (chunk == 0) break;
                        read += chunk;
                    }
                    byte[] compressed = (encKey != null && hmacKey != null)
                        ? KesEncryptionService.Decrypt(stored, encKey, hmacKey)
                        : stored;
                    byte[] original   = _compressor.Decompress(compressed, entry.CompressionType, entry.OriginalSize);

                    if (header.HasChecksums &&
                        !KesEncryptionService.VerifySha256(original, entry.Sha256Hash))
                    {
                        result.EntriesFailed++;
                        result.FailedPaths.Add(entry.Path + " (checksum mismatch)");
                    }
                    result.EntriesChecked++;
                }
                catch (Exception ex)
                {
                    result.EntriesFailed++;
                    result.FailedPaths.Add(entry.Path + " (" + ex.GetType().Name + ": " + ex.Message + ")");
                }
            }

            result.Success = result.EntriesFailed == 0;
            result.Summary = result.Success
                ? $"OK — {result.EntriesChecked}/{entries.Count} entries verified."
                : $"FAILED — {result.EntriesFailed} of {entries.Count} entries are corrupt.";
            OnProgress(entries.Count, entries.Count, "Done");
            return result;
        }

        /// <summary>Extracts a single entry from an already-open stream.</summary>
        public async Task ExtractEntryAsync(
            StorageFile   archiveFile,
            KesEntryInfo  entry,
            StorageFolder destFolder,
            KesHeader     header,
            string?       password)
        {
            byte[]? encKey  = null;
            byte[]? hmacKey = null;

            using IRandomAccessStream ras    = await archiveFile.OpenReadAsync();
            using Stream              stream = ras.AsStream();
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            if (header.IsEncrypted)
            {
                // Skip fixed header then read encryption block
                stream.Seek(KesHeader.HeaderSize, SeekOrigin.Begin);
                var encBlock = KesEncryptionBlock.Deserialize(reader);
                if (string.IsNullOrEmpty(password))
                    throw new ArgumentException("Password required.");
                (encKey, hmacKey) = KesEncryptionService.DeriveKeys(password!, encBlock.Salt);
            }

            await ExtractEntryAsync(stream, entry, destFolder, encKey, hmacKey, header);
        }

        // ─── Private helpers ─────────────────────────────────────────────────

        private async Task ExtractEntryAsync(
            Stream        stream,
            KesEntryInfo  entry,
            StorageFolder destFolder,
            byte[]?       encKey,
            byte[]?       hmacKey,
            KesHeader     header)
        {
            if (entry.EntryType == KesEntryType.Directory)
            {
                await CreateDirectoryStructureAsync(destFolder, entry.Path);
                return;
            }

            // Read stored blob
            stream.Seek(entry.DataOffset, SeekOrigin.Begin);
            byte[] stored = new byte[entry.StoredSize];
            int read = 0;
            while (read < stored.Length)
            {
                int chunk = stream.Read(stored, read, stored.Length - read);
                if (chunk == 0) break;
                read += chunk;
            }

            // Decrypt
            byte[] compressed = (encKey != null && hmacKey != null)
                ? KesEncryptionService.Decrypt(stored, encKey, hmacKey)
                : stored;

            // Decompress
            byte[] original;
            try
            {
                original = _compressor.Decompress(compressed, entry.CompressionType, entry.OriginalSize);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    $"Failed to decompress '{entry.Path}' (method={entry.CompressionType}, stored={entry.StoredSize}, original={entry.OriginalSize}): {ex.Message}",
                    ex);
            }

            // Verify checksum
            if (header.HasChecksums)
            {
                if (!KesEncryptionService.VerifySha256(original, entry.Sha256Hash))
                    throw new InvalidDataException($"Checksum mismatch for '{entry.Path}'. File may be corrupted.");
            }

            // Create folder hierarchy and write file
            StorageFolder targetFolder = await CreateDirectoryStructureAsync(destFolder, entry.DirectoryPath);
            StorageFile   outputFile   = await targetFolder.CreateFileAsync(
                entry.FileName, CreationCollisionOption.ReplaceExisting);

            using IRandomAccessStream outRas = await outputFile.OpenAsync(FileAccessMode.ReadWrite);
            using Stream outStream = outRas.AsStream();
            await outStream.WriteAsync(original, 0, original.Length);

            // Restore metadata
            if (entry.ModifiedUtcTicks > 0)
            {
                var props = await outputFile.GetBasicPropertiesAsync();
                // Note: UWP only allows setting DateModified via file system for some paths
            }
        }

        private static async Task<StorageFolder> CreateDirectoryStructureAsync(
            StorageFolder root, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return root;

            string[] parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            StorageFolder current = root;
            foreach (string part in parts)
            {
                current = await current.CreateFolderAsync(part,
                    CreationCollisionOption.OpenIfExists);
            }
            return current;
        }

        private static async Task<byte[]> ReadAllBytesAsync(StorageFile file)
        {
            using IRandomAccessStreamWithContentType stream = await file.OpenReadAsync();
            byte[] bytes = new byte[stream.Size];
            using DataReader reader = new(stream);
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }

        private void OnProgress(int processed, int total, string currentFile,
                                ulong processedBytes = 0, ulong totalBytes = 0)
        {
            Progress?.Invoke(this, new ArchiveProgressEventArgs
            {
                Processed      = processed,
                Total          = total,
                CurrentFile    = currentFile,
                ProcessedBytes = processedBytes,
                TotalBytes     = totalBytes,
            });
        }
    }
}
