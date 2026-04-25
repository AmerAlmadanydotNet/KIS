// Round-trip test harness for the KES archive format.
//
// This file mirrors the on-disk layout of the UWP `KesFile` project
// (Models/KesHeader.cs, Models/KesEntryInfo.cs,
//  Services/KesArchiveService.cs, Services/KesEncryptionService.cs,
//  Services/KesCompressionService.cs) using portable .NET 8 BCL APIs.
//
// Goals:
//   1. Confirm the documented format is internally consistent
//      (create -> open -> extract round-trip yields identical bytes).
//   2. Exercise every advertised feature combination:
//        none + deflate, none + lzma, aes + deflate, aes + lzma,
//        aes + lzma + encrypted filenames.
//   3. Provide negative tests: wrong password, tampered byte.
//
// PBKDF2-SHA256, AES-256-CBC PKCS7, HMAC-SHA256 are deterministic across
// WinRT and BCL given identical inputs, so any archive created by this
// harness can be opened by the UWP app and vice-versa.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace KesRoundTrip;

internal static class Program
{
    private const string Magic = "KESF";
    private const int    HeaderSize = 56;
    private const int    SaltSize = 32;
    private const int    IvSize = 16;
    private const int    HmacSize = 32;
    private const int    KeySize = 32;
    private const int    HmacKeySize = 32;
    private const int    KdfIterations = 600_000;

    [Flags]
    public enum KesFlags : uint
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

    // ─────────────────────────────────────────────────────────────────────
    //  Test runner
    // ─────────────────────────────────────────────────────────────────────
    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "kes_roundtrip_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        Console.WriteLine($"Workspace: {root}");

        int failed = 0;
        try
        {
            // Build sample tree
            string srcDir = Path.Combine(root, "src");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "readme.txt"),
                "Hello KIS file – round trip test. " + new string('A', 5_000));
            Directory.CreateDirectory(Path.Combine(srcDir, "sub"));
            File.WriteAllBytes(Path.Combine(srcDir, "sub", "binary.bin"),
                RandomBytes(123_456));
            File.WriteAllText(Path.Combine(srcDir, "sub", "notes.md"),
                "# Notes\n\n" + string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog\n", 200)));

            failed += Run(root, srcDir, "T1 plain  / deflate", CompressionType.Deflate, password: null,  encryptNames: false);
            failed += Run(root, srcDir, "T2 plain  / lzma   ", CompressionType.Lzma,    password: null,  encryptNames: false);
            failed += Run(root, srcDir, "T3 aes    / deflate", CompressionType.Deflate, password: "p@ss-Word-1!", encryptNames: false);
            failed += Run(root, srcDir, "T4 aes    / lzma   ", CompressionType.Lzma,    password: "p@ss-Word-1!", encryptNames: false);
            failed += Run(root, srcDir, "T5 aes    / lzma   + encrypted filenames", CompressionType.Lzma, password: "Hidden-Names-2!", encryptNames: true);
            failed += NegativeWrongPassword(root, srcDir);
            failed += NegativeTamper(root, srcDir);
            failed += NegativeFilenamesUnreadableWithoutPassword(root, srcDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL: " + ex);
            failed++;
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "ALL TESTS PASSED ✓" : $"FAILED: {failed}");
        return failed == 0 ? 0 : 1;
    }

    private static int Run(string root, string srcDir, string label, CompressionType comp, string? password, bool encryptNames)
    {
        string archive = Path.Combine(root, label.Replace(' ', '_').Replace('/', '_') + ".kes");
        string outDir  = archive + ".out";

        try
        {
            CreateArchive(srcDir, archive, comp, password, encryptNames);
            Extract(archive, outDir, password);

            bool ok = CompareTrees(srcDir, outDir);
            Console.WriteLine($"  {label}  size={new FileInfo(archive).Length,9}  {(ok ? "OK" : "FAIL")}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {label}  EX: {ex.Message}");
            return 1;
        }
    }

    private static int NegativeWrongPassword(string root, string srcDir)
    {
        string archive = Path.Combine(root, "neg_wrongpw.kes");
        CreateArchive(srcDir, archive, CompressionType.Lzma, "correct-pw", false);
        try
        {
            Extract(archive, archive + ".out", "wrong-pw");
            Console.WriteLine("  Negative wrong-password  FAIL (no exception thrown)");
            return 1;
        }
        catch (CryptographicException) { Console.WriteLine("  Negative wrong-password   OK (CryptographicException)"); return 0; }
        catch (InvalidOperationException) { Console.WriteLine("  Negative wrong-password   OK (HMAC mismatch)"); return 0; }
        catch (Exception ex) { Console.WriteLine("  Negative wrong-password   OK (" + ex.GetType().Name + ")"); return 0; }
    }

    private static int NegativeTamper(string root, string srcDir)
    {
        string archive = Path.Combine(root, "neg_tamper.kes");
        CreateArchive(srcDir, archive, CompressionType.Deflate, "tamper-pw", false);
        // Flip a byte in the first encrypted file payload (right after header + enc block)
        var bytes = File.ReadAllBytes(archive);
        bytes[HeaderSize + SaltSize + 2 + 8] ^= 0xFF; // somewhere in IV/HMAC of first entry
        File.WriteAllBytes(archive, bytes);
        try
        {
            Extract(archive, archive + ".out", "tamper-pw");
            Console.WriteLine("  Negative tamper           FAIL (no integrity error)");
            return 1;
        }
        catch (Exception ex) { Console.WriteLine("  Negative tamper           OK (" + ex.GetType().Name + ")"); return 0; }
    }

    private static int NegativeFilenamesUnreadableWithoutPassword(string root, string srcDir)
    {
        string archive = Path.Combine(root, "neg_names.kes");
        CreateArchive(srcDir, archive, CompressionType.Lzma, "names-pw", encryptNames: true);
        // Open the entry table without password; paths must NOT be plaintext.
        var raw = File.ReadAllBytes(archive);
        bool readmeVisible = ContainsAscii(raw, "readme.txt");
        bool notesVisible  = ContainsAscii(raw, "notes.md");
        bool ok = !readmeVisible && !notesVisible;
        Console.WriteLine($"  Encrypted filenames hidden {(ok ? "OK" : "FAIL (filenames leaked!)")}");
        return ok ? 0 : 1;
    }

    private static bool ContainsAscii(byte[] haystack, string needle)
    {
        var n = Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i + n.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < n.Length; j++) if (haystack[i + j] != n[j]) { match = false; break; }
            if (match) return true;
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Create
    // ─────────────────────────────────────────────────────────────────────
    private static void CreateArchive(string srcDir, string destPath, CompressionType comp, string? password, bool encryptNames)
    {
        var files = Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => (Full: p, Rel: Path.GetRelativePath(srcDir, p).Replace('\\', '/')))
            .ToList();

        byte[]? salt = null, encKey = null, hmacKey = null;
        bool hasEncryption = !string.IsNullOrEmpty(password);
        if (hasEncryption)
        {
            salt = RandomNumberGenerator.GetBytes(SaltSize);
            (encKey, hmacKey) = DeriveKeys(password!, salt);
        }
        if (encryptNames && !hasEncryption)
            throw new ArgumentException("Encrypted filenames require a password.");

        using var fs = File.Open(destPath, FileMode.Create, FileAccess.ReadWrite);
        using var w  = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

        var flags = KesFlags.HasChecksums | KesFlags.PreserveMetadata;
        if (encryptNames) flags |= KesFlags.EncryptFileNames;

        // 1. placeholder header
        WriteHeader(w, comp, hasEncryption ? EncryptionType.Aes256CbcHmac : EncryptionType.None,
                    flags, entryCount: (uint)files.Count, originalTotal: 0, compressedTotal: 0,
                    createdTicks: DateTime.UtcNow.Ticks, entryTableOffset: 0);

        // 2. encryption block
        if (hasEncryption)
        {
            w.Write(salt!);                 // 32
            w.Write((ushort)0);             // hint length 0
        }

        // 3. data blocks
        var entries = new List<EntryInfo>(files.Count);
        ulong origTotal = 0, compTotal = 0;

        foreach (var (full, rel) in files)
        {
            byte[] raw = File.ReadAllBytes(full);
            byte[] sha = SHA256.HashData(raw);

            var (compressed, actualType) = Compress(raw, comp);
            byte[] stored = hasEncryption ? Encrypt(compressed, encKey!, hmacKey!) : compressed;

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
        }

        // 4. entry table
        long tableOffset = fs.Position;

        // Build the entry table to a buffer first; if filenames are encrypted,
        // wrap the entire table in an Encrypt() blob. Length-prefix it so the
        // reader knows how many bytes to consume before decrypting.
        using (var tableMs = new MemoryStream())
        using (var tw = new BinaryWriter(tableMs, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var e in entries) WriteEntry(tw, e);
            tw.Flush();
            byte[] tableBytes = tableMs.ToArray();

            if (encryptNames)
            {
                byte[] sealedTable = Encrypt(tableBytes, encKey!, hmacKey!);
                w.Write(sealedTable.Length);   // int32 length
                w.Write(sealedTable);
            }
            else
            {
                w.Write(tableBytes);
            }
        }

        // 5. rewrite header
        fs.Seek(0, SeekOrigin.Begin);
        WriteHeader(w, comp, hasEncryption ? EncryptionType.Aes256CbcHmac : EncryptionType.None,
                    flags, (uint)entries.Count, origTotal, compTotal, DateTime.UtcNow.Ticks, tableOffset);
        w.Flush();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Extract
    // ─────────────────────────────────────────────────────────────────────
    private static void Extract(string archivePath, string destDir, string? password)
    {
        Directory.CreateDirectory(destDir);
        using var fs = File.OpenRead(archivePath);
        using var r  = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        var hdr = ReadHeader(r);
        byte[]? encKey = null, hmacKey = null;
        if (hdr.encType != EncryptionType.None)
        {
            byte[] salt = r.ReadBytes(SaltSize);
            ushort hintLen = r.ReadUInt16();
            r.ReadBytes(hintLen);
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password required.");
            (encKey, hmacKey) = DeriveKeys(password!, salt);
        }

        fs.Seek(hdr.tableOffset, SeekOrigin.Begin);

        List<EntryInfo> entries;
        if ((hdr.flags & KesFlags.EncryptFileNames) != 0)
        {
            int sealedLen = r.ReadInt32();
            byte[] sealedTable = r.ReadBytes(sealedLen);
            byte[] table = Decrypt(sealedTable, encKey!, hmacKey!);
            using var ms = new MemoryStream(table);
            using var tr = new BinaryReader(ms);
            entries = new List<EntryInfo>((int)hdr.entryCount);
            for (uint i = 0; i < hdr.entryCount; i++) entries.Add(ReadEntry(tr));
        }
        else
        {
            entries = new List<EntryInfo>((int)hdr.entryCount);
            for (uint i = 0; i < hdr.entryCount; i++) entries.Add(ReadEntry(r));
        }

        foreach (var e in entries)
        {
            fs.Seek(e.DataOffset, SeekOrigin.Begin);
            byte[] stored = r.ReadBytes((int)e.StoredSize);
            byte[] compressed = (encKey != null) ? Decrypt(stored, encKey, hmacKey!) : stored;
            byte[] raw = Decompress(compressed, e.Compression, (long)e.OriginalSize);
            if ((hdr.flags & KesFlags.HasChecksums) != 0)
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(raw), e.Sha256))
                    throw new InvalidDataException("Checksum mismatch on " + e.Path);

            string outPath = Path.Combine(destDir, e.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllBytes(outPath, raw);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Header / entry I/O (mirrors KesHeader.cs / KesEntryInfo.cs)
    // ─────────────────────────────────────────────────────────────────────
    private static void WriteHeader(BinaryWriter w, CompressionType comp, EncryptionType enc, KesFlags flags,
                                    uint entryCount, ulong originalTotal, ulong compressedTotal,
                                    long createdTicks, long entryTableOffset)
    {
        w.Write(Encoding.ASCII.GetBytes(Magic));   // 4
        w.Write((byte)1);                           // VersionMajor
        w.Write((byte)0);                           // VersionMinor
        w.Write((byte)comp);
        w.Write((byte)enc);
        w.Write((uint)flags);
        w.Write(entryCount);
        w.Write(originalTotal);
        w.Write(compressedTotal);
        w.Write(createdTicks);
        w.Write(entryTableOffset);
        w.Write((ushort)0); // SplitPartNumber
        w.Write((ushort)0); // SplitTotalParts
        w.Write((uint)0);   // reserved -> total = 56 bytes
    }

    private static (CompressionType comp, EncryptionType encType, KesFlags flags,
                    uint entryCount, ulong originalTotal, ulong compressedTotal,
                    long createdTicks, long tableOffset) ReadHeader(BinaryReader r)
    {
        var magic = r.ReadBytes(4);
        if (Encoding.ASCII.GetString(magic) != Magic) throw new InvalidDataException("Bad magic");
        r.ReadByte(); r.ReadByte();                                             // version major/minor
        var comp  = (CompressionType)r.ReadByte();
        var enc   = (EncryptionType)r.ReadByte();
        var flags = (KesFlags)r.ReadUInt32();
        var ec    = r.ReadUInt32();
        var ot    = r.ReadUInt64();
        var ct    = r.ReadUInt64();
        var ck    = r.ReadInt64();
        var to    = r.ReadInt64();
        r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt32();
        return (comp, enc, flags, ec, ot, ct, ck, to);
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
        var e = new EntryInfo();
        e.Kind = (EntryType)r.ReadByte();
        var n  = r.ReadUInt16();
        e.Path = Encoding.UTF8.GetString(r.ReadBytes(n));
        e.OriginalSize = r.ReadUInt64();
        e.StoredSize   = r.ReadUInt64();
        e.DataOffset   = r.ReadInt64();
        e.ModifiedTicks= r.ReadInt64();
        e.CreatedTicks = r.ReadInt64();
        e.Attributes   = r.ReadUInt32();
        e.Sha256       = r.ReadBytes(32);
        e.Compression  = (CompressionType)r.ReadByte();
        return e;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Crypto (deterministic with WinRT)
    // ─────────────────────────────────────────────────────────────────────
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
        aes.Key = encKey; aes.IV = iv;
        aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
        byte[] cipher = aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);

        byte[] macInput = new byte[IvSize + cipher.Length];
        Buffer.BlockCopy(iv, 0, macInput, 0, IvSize);
        Buffer.BlockCopy(cipher, 0, macInput, IvSize, cipher.Length);
        byte[] hmac = HMACSHA256.HashData(hmacKey, macInput);

        byte[] blob = new byte[IvSize + HmacSize + cipher.Length];
        Buffer.BlockCopy(iv,     0, blob, 0,                   IvSize);
        Buffer.BlockCopy(hmac,   0, blob, IvSize,              HmacSize);
        Buffer.BlockCopy(cipher, 0, blob, IvSize + HmacSize,   cipher.Length);
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
            throw new InvalidOperationException("HMAC mismatch (wrong password or corrupted).");

        return Aes.Create() is { } a ? Decrypt2(a, encKey, iv, cipher) : throw new Exception();
    }

    private static byte[] Decrypt2(Aes a, byte[] key, byte[] iv, byte[] cipher)
    {
        a.Key = key; a.IV = iv; a.Mode = CipherMode.CBC; a.Padding = PaddingMode.PKCS7;
        return a.DecryptCbc(cipher, iv, PaddingMode.PKCS7);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Compression (mirrors KesCompressionService.cs)
    // ─────────────────────────────────────────────────────────────────────
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
            CompressionType.Deflate => InflateDeflate(data, originalSize),
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

    private static byte[] InflateDeflate(byte[] data, long originalSize)
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

            // Mirror the original "use whichever is smaller" but report the truthful type.
            if (full.Length < data.Length) return (full, CompressionType.Lzma);
            return (Deflate(data), CompressionType.Deflate);   // ← BUGFIX: report Deflate, not Lzma
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
        using var input = new MemoryStream(payload);
        using var output = new MemoryStream();
        using var dec = new SharpCompress.Compressors.LZMA.LzmaStream(
            props, input, payload.LongLength, originalSize > 0 ? originalSize : -1);
        dec.CopyTo(output);
        return output.ToArray();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────
    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n]; new Random(42).NextBytes(b); return b;
    }

    private static bool CompareTrees(string a, string b)
    {
        var fa = Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(a, p).Replace('\\','/')).OrderBy(s => s).ToList();
        var fb = Directory.EnumerateFiles(b, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(b, p).Replace('\\','/')).OrderBy(s => s).ToList();
        if (!fa.SequenceEqual(fb)) { Console.WriteLine("    file lists differ"); return false; }
        foreach (var rel in fa)
        {
            var ba = File.ReadAllBytes(Path.Combine(a, rel));
            var bb = File.ReadAllBytes(Path.Combine(b, rel));
            if (!ba.SequenceEqual(bb)) { Console.WriteLine($"    bytes differ: {rel}"); return false; }
        }
        return true;
    }
}
