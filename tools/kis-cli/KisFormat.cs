// Portable .NET 8 implementation of the KIS archive format.
// Identical on-disk layout to the UWP KesFile project so archives are
// fully interoperable between the CLI and the GUI app.

using System.Security.Cryptography;
using System.Text;

namespace KIS.Cli;

[Flags]
public enum KisFlags : uint
{
    None             = 0,
    HasChecksums     = 1 << 0,
    IsSplit          = 1 << 1,
    EncryptFileNames = 1 << 2,
    PreserveMetadata = 1 << 3,
}

public enum CompressionType : byte { None = 0, Deflate = 1, Lzma = 2 }
public enum EncryptionType  : byte { None = 0, Aes256CbcHmac = 1 }
public enum EntryType       : byte { File = 0, Directory = 1 }

public sealed class EntryInfo
{
    public EntryType       Kind        = EntryType.File;
    public string          Path        = "";
    public ulong           OriginalSize;
    public ulong           StoredSize;
    public long            DataOffset;
    public long            ModifiedTicks;
    public long            CreatedTicks;
    public uint            Attributes;
    public byte[]          Sha256 = new byte[32];
    public CompressionType Compression = CompressionType.Deflate;
}

public sealed class ArchiveHeader
{
    public CompressionType DefaultCompression;
    public EncryptionType  Encryption;
    public KisFlags        Flags;
    public uint            EntryCount;
    public ulong           OriginalTotal;
    public ulong           CompressedTotal;
    public long            CreatedTicks;
    public long            EntryTableOffset;
    public byte[]?         Salt;
    public string          PasswordHint = "";
    public bool IsEncrypted          => Encryption != EncryptionType.None;
    public bool HasEncryptedNames    => (Flags & KisFlags.EncryptFileNames) != 0;
    public bool HasChecksums         => (Flags & KisFlags.HasChecksums) != 0;
}

public static class KisFormat
{
    public const string Magic         = "KESF";
    public const int    HeaderSize    = 56;
    public const int    SaltSize      = 32;
    public const int    IvSize        = 16;
    public const int    HmacSize      = 32;
    public const int    KeySize       = 32;
    public const int    HmacKeySize   = 32;
    public const int    KdfIterations = 600_000;

    // ─── Create ──────────────────────────────────────────────────────────
    public sealed class CreateOptions
    {
        public CompressionType Compression  = CompressionType.Lzma;
        public string?         Password;
        public bool            EncryptNames;
        public string          PasswordHint = "";
        public Action<string>? Log;
    }

    /// <summary>Create an archive at <paramref name="destPath"/> from a flat
    /// list of (absolute path, archive-relative path) pairs.</summary>
    public static void Create(IList<(string Full, string Rel)> files, string destPath, CreateOptions opts)
    {
        byte[]? salt = null, encKey = null, hmacKey = null;
        bool hasEnc = !string.IsNullOrEmpty(opts.Password);
        if (hasEnc)
        {
            salt = RandomNumberGenerator.GetBytes(SaltSize);
            (encKey, hmacKey) = DeriveKeys(opts.Password!, salt);
        }
        if (opts.EncryptNames && !hasEnc)
            throw new ArgumentException("Encrypted filenames require a password.");

        using var fs = File.Open(destPath, FileMode.Create, FileAccess.ReadWrite);
        using var w  = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

        var flags = KisFlags.HasChecksums | KisFlags.PreserveMetadata;
        if (opts.EncryptNames) flags |= KisFlags.EncryptFileNames;

        WriteHeader(w, opts.Compression,
            hasEnc ? EncryptionType.Aes256CbcHmac : EncryptionType.None,
            flags, (uint)files.Count, 0, 0, DateTime.UtcNow.Ticks, 0);

        if (hasEnc)
        {
            w.Write(salt!);
            byte[] hintBytes = Encoding.UTF8.GetBytes(opts.PasswordHint ?? "");
            w.Write((ushort)hintBytes.Length);
            if (hintBytes.Length > 0) w.Write(hintBytes);
        }

        var entries = new List<EntryInfo>(files.Count);
        ulong origTotal = 0, compTotal = 0;
        int idx = 0;

        foreach (var (full, rel) in files)
        {
            idx++;
            byte[] raw = File.ReadAllBytes(full);
            byte[] sha = SHA256.HashData(raw);

            var (compressed, actualType) = Compress(raw, opts.Compression);
            byte[] stored = hasEnc ? Encrypt(compressed, encKey!, hmacKey!) : compressed;

            long offset = fs.Position;
            w.Write(stored);

            entries.Add(new EntryInfo
            {
                Kind          = EntryType.File,
                Path          = rel,
                OriginalSize  = (ulong)raw.Length,
                StoredSize    = (ulong)stored.Length,
                DataOffset    = offset,
                ModifiedTicks = File.GetLastWriteTimeUtc(full).Ticks,
                CreatedTicks  = DateTime.UtcNow.Ticks,
                Attributes    = 0,
                Sha256        = sha,
                Compression   = actualType,
            });
            origTotal += (ulong)raw.Length;
            compTotal += (ulong)stored.Length;

            opts.Log?.Invoke($"  [{idx,4}/{files.Count}]  {actualType,-7}  {raw.Length,12:N0}  →  {stored.Length,12:N0}  {rel}");
        }

        // Entry table (sealed if EncryptFileNames)
        long tableOffset = fs.Position;
        using (var tableMs = new MemoryStream())
        using (var tw = new BinaryWriter(tableMs, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var e in entries) WriteEntry(tw, e);
            tw.Flush();
            byte[] tableBytes = tableMs.ToArray();
            if (opts.EncryptNames)
            {
                byte[] sealedTable = Encrypt(tableBytes, encKey!, hmacKey!);
                w.Write(sealedTable.Length);
                w.Write(sealedTable);
            }
            else
            {
                w.Write(tableBytes);
            }
        }

        // Rewrite header with totals & table offset
        fs.Seek(0, SeekOrigin.Begin);
        WriteHeader(w, opts.Compression,
            hasEnc ? EncryptionType.Aes256CbcHmac : EncryptionType.None,
            flags, (uint)entries.Count, origTotal, compTotal,
            DateTime.UtcNow.Ticks, tableOffset);
        w.Flush();
    }

    // ─── Open / Peek ────────────────────────────────────────────────────
    /// <summary>Read the header and (if not name-encrypted) the entry table.
    /// If <paramref name="password"/> is null and the archive is encrypted,
    /// the header is returned but entries will be empty.</summary>
    public static (ArchiveHeader header, IList<EntryInfo> entries) Open(string archivePath, string? password)
    {
        using var fs = File.OpenRead(archivePath);
        using var r  = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        var hdr = ReadHeader(r);
        byte[]? encKey = null, hmacKey = null;
        if (hdr.IsEncrypted)
        {
            hdr.Salt = r.ReadBytes(SaltSize);
            ushort hintLen = r.ReadUInt16();
            hdr.PasswordHint = hintLen > 0
                ? Encoding.UTF8.GetString(r.ReadBytes(hintLen))
                : "";
            if (!string.IsNullOrEmpty(password))
                (encKey, hmacKey) = DeriveKeys(password!, hdr.Salt);
        }

        var entries = new List<EntryInfo>();
        // If filenames are encrypted and we don't have a password, we can't read the table.
        if (hdr.HasEncryptedNames && encKey == null) return (hdr, entries);

        fs.Seek(hdr.EntryTableOffset, SeekOrigin.Begin);
        if (hdr.HasEncryptedNames)
        {
            int sealedLen = r.ReadInt32();
            byte[] sealedTable = r.ReadBytes(sealedLen);
            byte[] table = Decrypt(sealedTable, encKey!, hmacKey!);
            using var ms = new MemoryStream(table);
            using var tr = new BinaryReader(ms);
            for (uint i = 0; i < hdr.EntryCount; i++) entries.Add(ReadEntry(tr));
        }
        else
        {
            for (uint i = 0; i < hdr.EntryCount; i++) entries.Add(ReadEntry(r));
        }
        return (hdr, entries);
    }

    // ─── Extract ────────────────────────────────────────────────────────
    public static int Extract(string archivePath, string destDir, string? password,
                              bool overwrite, Action<string>? log)
    {
        Directory.CreateDirectory(destDir);
        var (hdr, entries) = Open(archivePath, password);
        if (hdr.IsEncrypted && string.IsNullOrEmpty(password))
            throw new ArgumentException("This archive is encrypted. Provide --password.");

        byte[]? encKey = null, hmacKey = null;
        if (hdr.IsEncrypted)
            (encKey, hmacKey) = DeriveKeys(password!, hdr.Salt!);

        using var fs = File.OpenRead(archivePath);
        using var r  = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        int written = 0;
        foreach (var e in entries)
        {
            fs.Seek(e.DataOffset, SeekOrigin.Begin);
            byte[] stored     = r.ReadBytes((int)e.StoredSize);
            byte[] compressed = (encKey != null) ? Decrypt(stored, encKey, hmacKey!) : stored;
            byte[] raw        = Decompress(compressed, e.Compression, (long)e.OriginalSize);

            if (hdr.HasChecksums &&
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(raw), e.Sha256))
                throw new InvalidDataException("Checksum mismatch on " + e.Path);

            string outPath = Path.Combine(destDir, e.Path.Replace('/', Path.DirectorySeparatorChar));
            string? outDirectory = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDirectory)) Directory.CreateDirectory(outDirectory);
            if (File.Exists(outPath) && !overwrite)
            {
                log?.Invoke($"  SKIP (exists)  {e.Path}");
                continue;
            }
            File.WriteAllBytes(outPath, raw);
            try { File.SetLastWriteTimeUtc(outPath, new DateTime(e.ModifiedTicks, DateTimeKind.Utc)); } catch { }
            written++;
            log?.Invoke($"  ✓  {e.Path}");
        }
        return written;
    }

    // ─── Verify ─────────────────────────────────────────────────────────
    public sealed record VerifyResult(int Total, int Ok, int Failed, IList<string> Errors);

    public static VerifyResult Verify(string archivePath, string? password, Action<string>? log)
    {
        var (hdr, entries) = Open(archivePath, password);
        if (hdr.IsEncrypted && string.IsNullOrEmpty(password))
            throw new ArgumentException("This archive is encrypted. Provide --password.");

        byte[]? encKey = null, hmacKey = null;
        if (hdr.IsEncrypted)
            (encKey, hmacKey) = DeriveKeys(password!, hdr.Salt!);

        using var fs = File.OpenRead(archivePath);
        using var r  = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        var errs = new List<string>();
        int ok = 0;
        foreach (var e in entries)
        {
            try
            {
                fs.Seek(e.DataOffset, SeekOrigin.Begin);
                byte[] stored     = r.ReadBytes((int)e.StoredSize);
                byte[] compressed = (encKey != null) ? Decrypt(stored, encKey, hmacKey!) : stored;
                byte[] raw        = Decompress(compressed, e.Compression, (long)e.OriginalSize);
                if (hdr.HasChecksums &&
                    !CryptographicOperations.FixedTimeEquals(SHA256.HashData(raw), e.Sha256))
                {
                    errs.Add($"{e.Path}: SHA-256 mismatch");
                    log?.Invoke($"  ✗  {e.Path}  (SHA-256 mismatch)");
                    continue;
                }
                ok++;
                log?.Invoke($"  ✓  {e.Path}");
            }
            catch (Exception ex)
            {
                errs.Add($"{e.Path}: {ex.Message}");
                log?.Invoke($"  ✗  {e.Path}  ({ex.Message})");
            }
        }
        return new VerifyResult(entries.Count, ok, entries.Count - ok, errs);
    }

    // ─── Header / entry I/O ─────────────────────────────────────────────
    private static void WriteHeader(BinaryWriter w, CompressionType comp, EncryptionType enc, KisFlags flags,
                                    uint entryCount, ulong originalTotal, ulong compressedTotal,
                                    long createdTicks, long entryTableOffset)
    {
        w.Write(Encoding.ASCII.GetBytes(Magic));
        w.Write((byte)1); w.Write((byte)0);
        w.Write((byte)comp); w.Write((byte)enc);
        w.Write((uint)flags);
        w.Write(entryCount);
        w.Write(originalTotal);
        w.Write(compressedTotal);
        w.Write(createdTicks);
        w.Write(entryTableOffset);
        w.Write((ushort)0); w.Write((ushort)0); w.Write((uint)0);
    }

    private static ArchiveHeader ReadHeader(BinaryReader r)
    {
        var magic = r.ReadBytes(4);
        if (Encoding.ASCII.GetString(magic) != Magic)
            throw new InvalidDataException("Not a valid KIS archive (bad magic).");
        r.ReadByte(); r.ReadByte();
        var hdr = new ArchiveHeader
        {
            DefaultCompression = (CompressionType)r.ReadByte(),
            Encryption         = (EncryptionType)r.ReadByte(),
            Flags              = (KisFlags)r.ReadUInt32(),
            EntryCount         = r.ReadUInt32(),
            OriginalTotal      = r.ReadUInt64(),
            CompressedTotal    = r.ReadUInt64(),
            CreatedTicks       = r.ReadInt64(),
            EntryTableOffset   = r.ReadInt64(),
        };
        r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt32();
        return hdr;
    }

    private static void WriteEntry(BinaryWriter w, EntryInfo e)
    {
        var path = Encoding.UTF8.GetBytes(e.Path);
        w.Write((byte)e.Kind);
        w.Write((ushort)path.Length);
        w.Write(path);
        w.Write(e.OriginalSize);
        w.Write(e.StoredSize);
        w.Write(e.DataOffset);
        w.Write(e.ModifiedTicks);
        w.Write(e.CreatedTicks);
        w.Write(e.Attributes);
        w.Write(e.Sha256, 0, 32);
        w.Write((byte)e.Compression);
    }

    private static EntryInfo ReadEntry(BinaryReader r)
    {
        var e = new EntryInfo
        {
            Kind = (EntryType)r.ReadByte(),
        };
        ushort n = r.ReadUInt16();
        e.Path = Encoding.UTF8.GetString(r.ReadBytes(n));
        e.OriginalSize  = r.ReadUInt64();
        e.StoredSize    = r.ReadUInt64();
        e.DataOffset    = r.ReadInt64();
        e.ModifiedTicks = r.ReadInt64();
        e.CreatedTicks  = r.ReadInt64();
        e.Attributes    = r.ReadUInt32();
        e.Sha256        = r.ReadBytes(32);
        e.Compression   = (CompressionType)r.ReadByte();
        return e;
    }

    // ─── Crypto ─────────────────────────────────────────────────────────
    private static (byte[] enc, byte[] hmac) DeriveKeys(string password, byte[] salt)
    {
        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, KdfIterations, HashAlgorithmName.SHA256, KeySize + HmacKeySize);
        return (derived[..KeySize], derived[KeySize..]);
    }

    private static byte[] Encrypt(byte[] plain, byte[] encKey, byte[] hmacKey)
    {
        byte[] iv = RandomNumberGenerator.GetBytes(IvSize);
        using var aes = Aes.Create();
        aes.Key = encKey; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
        byte[] cipher = aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);
        byte[] macInput = new byte[IvSize + cipher.Length];
        Buffer.BlockCopy(iv, 0, macInput, 0, IvSize);
        Buffer.BlockCopy(cipher, 0, macInput, IvSize, cipher.Length);
        byte[] hmac = HMACSHA256.HashData(hmacKey, macInput);
        byte[] blob = new byte[IvSize + HmacSize + cipher.Length];
        Buffer.BlockCopy(iv,     0, blob, 0,                 IvSize);
        Buffer.BlockCopy(hmac,   0, blob, IvSize,            HmacSize);
        Buffer.BlockCopy(cipher, 0, blob, IvSize + HmacSize, cipher.Length);
        return blob;
    }

    private static byte[] Decrypt(byte[] blob, byte[] encKey, byte[] hmacKey)
    {
        if (blob.Length < IvSize + HmacSize) throw new InvalidOperationException("Blob too small.");
        byte[] iv     = blob[..IvSize];
        byte[] mac    = blob.AsSpan(IvSize, HmacSize).ToArray();
        byte[] cipher = blob.AsSpan(IvSize + HmacSize).ToArray();
        byte[] macInput = new byte[IvSize + cipher.Length];
        Buffer.BlockCopy(iv, 0, macInput, 0, IvSize);
        Buffer.BlockCopy(cipher, 0, macInput, IvSize, cipher.Length);
        byte[] expected = HMACSHA256.HashData(hmacKey, macInput);
        if (!CryptographicOperations.FixedTimeEquals(expected, mac))
            throw new CryptographicException("HMAC mismatch (wrong password or corrupted archive).");
        using var aes = Aes.Create();
        aes.Key = encKey; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
        return aes.DecryptCbc(cipher, iv, PaddingMode.PKCS7);
    }

    // ─── Compression ────────────────────────────────────────────────────
    private static (byte[] data, CompressionType actualType) Compress(byte[] data, CompressionType type)
    {
        if (data.Length == 0) return (data, type);
        return type switch
        {
            CompressionType.None    => (data, CompressionType.None),
            CompressionType.Deflate => (Deflate(data), CompressionType.Deflate),
            CompressionType.Lzma    => Lzma(data),
            _                       => (data, type),
        };
    }

    private static byte[] Decompress(byte[] data, CompressionType type, long originalSize)
    {
        if (data.Length == 0) return data;
        return type switch
        {
            CompressionType.None    => data,
            CompressionType.Deflate => InflateDeflate(data),
            CompressionType.Lzma    => InflateLzma(data, originalSize),
            _                       => data,
        };
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var d = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            d.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static byte[] InflateDeflate(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var d  = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
        var outMs = new MemoryStream();
        d.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static (byte[] data, CompressionType actualType) Lzma(byte[] data)
    {
        try
        {
            using var output = new MemoryStream();
            using var input  = new MemoryStream(data);
            var props = new SharpCompress.Compressors.LZMA.LzmaEncoderProperties(
                eos: false, dictionary: 1 << 23, numFastBytes: 128);
            byte[] propertyBytes;
            using (var enc = new SharpCompress.Compressors.LZMA.LzmaStream(props, false, output))
            {
                input.CopyTo(enc);
                propertyBytes = enc.Properties;
            }
            byte[] payload = output.ToArray();
            byte[] full = new byte[propertyBytes.Length + payload.Length];
            Buffer.BlockCopy(propertyBytes, 0, full, 0, propertyBytes.Length);
            Buffer.BlockCopy(payload, 0, full, propertyBytes.Length, payload.Length);
            if (full.Length < data.Length) return (full, CompressionType.Lzma);
            return (Deflate(data), CompressionType.Deflate);
        }
        catch
        {
            return (Deflate(data), CompressionType.Deflate);
        }
    }

    private static byte[] InflateLzma(byte[] data, long originalSize)
    {
        if (data.Length < 6) throw new InvalidDataException("LZMA data too short.");
        byte[] props   = data[..5];
        byte[] payload = data.AsSpan(5).ToArray();
        using var input  = new MemoryStream(payload);
        using var output = new MemoryStream();
        using var dec = new SharpCompress.Compressors.LZMA.LzmaStream(
            props, input, payload.LongLength, originalSize > 0 ? originalSize : -1);
        dec.CopyTo(output);
        return output.ToArray();
    }
}
