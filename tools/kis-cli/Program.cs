// KIS — command-line tool for the KIS archive format.
// Usage:
//   kis create  <archive.kis> <files...>     [-p PASSWORD] [-c lzma|deflate|none] [--encrypt-names] [--hint TEXT]
//   kis extract <archive.kis> [-o OUTDIR]    [-p PASSWORD] [--force]
//   kis list    <archive.kis>                [-p PASSWORD]
//   kis verify  <archive.kis>                [-p PASSWORD]
//   kis info    <archive.kis>
//   kis help

using System.Globalization;

namespace KIS.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0])) { PrintHelp(); return 0; }
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "create" or "c" or "a" => CmdCreate(args[1..]),
                "extract" or "x" or "e" => CmdExtract(args[1..]),
                "list" or "ls" or "l"  => CmdList(args[1..]),
                "verify" or "v" or "t" => CmdVerify(args[1..]),
                "info" or "i"          => CmdInfo(args[1..]),
                _ => Fail($"Unknown command '{args[0]}'. Try 'kis help'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
    }

    // ─── create ─────────────────────────────────────────────────────────
    private static int CmdCreate(string[] args)
    {
        if (args.Length < 2) return Fail("usage: kis create <archive.kis> <files...> [options]");
        string archive = args[0];
        var positional = new List<string>();
        var opts = new KisFormat.CreateOptions { Compression = CompressionType.Lzma, Log = Console.WriteLine };
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-p" or "--password":     opts.Password     = NextArg(args, ref i, "--password"); break;
                case "-c" or "--compression":  opts.Compression  = ParseComp(NextArg(args, ref i, "--compression")); break;
                case "--encrypt-names":        opts.EncryptNames = true; break;
                case "--hint":                 opts.PasswordHint = NextArg(args, ref i, "--hint"); break;
                case "-q" or "--quiet":        opts.Log = null; break;
                default:
                    if (args[i].StartsWith('-')) return Fail($"unknown option '{args[i]}'");
                    positional.Add(args[i]); break;
            }
        }
        if (positional.Count == 0) return Fail("no input files specified.");
        if (opts.EncryptNames && string.IsNullOrEmpty(opts.Password))
            return Fail("--encrypt-names requires --password.");

        // Expand directories into (full,rel) pairs.
        var pairs = new List<(string Full, string Rel)>();
        foreach (var p in positional)
        {
            string normalized = Path.GetFullPath(p);
            if (Directory.Exists(normalized))
            {
                string baseName = new DirectoryInfo(normalized).Name;
                foreach (var f in Directory.EnumerateFiles(normalized, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.Combine(baseName, Path.GetRelativePath(normalized, f))
                                     .Replace('\\', '/');
                    pairs.Add((f, rel));
                }
            }
            else if (File.Exists(normalized))
            {
                pairs.Add((normalized, Path.GetFileName(normalized)));
            }
            else
            {
                return Fail($"input not found: {p}");
            }
        }
        if (pairs.Count == 0) return Fail("no files to archive.");

        Console.WriteLine($"Creating {archive} ({pairs.Count} entries, {opts.Compression}{(opts.Password != null ? ", encrypted" : "")}{(opts.EncryptNames ? " + names" : "")})");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        KisFormat.Create(pairs, archive, opts);
        sw.Stop();

        var fi = new FileInfo(archive);
        long origTotal = pairs.Sum(p => new FileInfo(p.Full).Length);
        double ratio = origTotal == 0 ? 0 : (1.0 - (double)fi.Length / origTotal) * 100;
        Console.WriteLine();
        Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F2}s. Original {Human(origTotal)} → archive {Human(fi.Length)} ({ratio:F1}% smaller)");
        return 0;
    }

    // ─── extract ────────────────────────────────────────────────────────
    private static int CmdExtract(string[] args)
    {
        if (args.Length < 1) return Fail("usage: kis extract <archive.kis> [-o OUTDIR] [-p PASSWORD] [--force]");
        string archive = args[0];
        string outDir = ".";
        string? pw = null;
        bool force = false;
        bool quiet = false;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" or "--output":   outDir = NextArg(args, ref i, "--output"); break;
                case "-p" or "--password": pw = NextArg(args, ref i, "--password"); break;
                case "--force" or "-f":    force = true; break;
                case "-q" or "--quiet":    quiet = true; break;
                default: return Fail($"unknown option '{args[i]}'");
            }
        }
        if (pw == null && IsEncrypted(archive)) pw = PromptPassword();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n = KisFormat.Extract(archive, outDir, pw, overwrite: force,
            log: quiet ? null : Console.WriteLine);
        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"Extracted {n} file(s) to {Path.GetFullPath(outDir)} in {sw.Elapsed.TotalSeconds:F2}s.");
        return 0;
    }

    // ─── list ───────────────────────────────────────────────────────────
    private static int CmdList(string[] args)
    {
        if (args.Length < 1) return Fail("usage: kis list <archive.kis> [-p PASSWORD]");
        string archive = args[0];
        string? pw = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-p" or "--password": pw = NextArg(args, ref i, "--password"); break;
                default: return Fail($"unknown option '{args[i]}'");
            }
        }
        if (pw == null && IsEncrypted(archive)) pw = PromptPassword();

        var (hdr, entries) = KisFormat.Open(archive, pw);
        if (hdr.HasEncryptedNames && entries.Count == 0)
        {
            Console.WriteLine("(filenames are encrypted; provide --password to list)");
            return 0;
        }
        Console.WriteLine($"{"Original",12}  {"Stored",12}  {"Ratio",6}  {"Modified",-19}  Name");
        Console.WriteLine(new string('─', 80));
        foreach (var e in entries)
        {
            double ratio = e.OriginalSize == 0 ? 0 : (1.0 - (double)e.StoredSize / e.OriginalSize) * 100;
            string mtime = new DateTime(e.ModifiedTicks, DateTimeKind.Utc)
                .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            Console.WriteLine($"{Human((long)e.OriginalSize),12}  {Human((long)e.StoredSize),12}  {ratio,5:F1}%  {mtime,-19}  {e.Path}");
        }
        Console.WriteLine(new string('─', 80));
        Console.WriteLine($"{entries.Count} entries  ·  {Human((long)hdr.OriginalTotal)} → {Human((long)hdr.CompressedTotal)}");
        return 0;
    }

    // ─── verify ─────────────────────────────────────────────────────────
    private static int CmdVerify(string[] args)
    {
        if (args.Length < 1) return Fail("usage: kis verify <archive.kis> [-p PASSWORD]");
        string archive = args[0];
        string? pw = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-p" or "--password": pw = NextArg(args, ref i, "--password"); break;
                default: return Fail($"unknown option '{args[i]}'");
            }
        }
        if (pw == null && IsEncrypted(archive)) pw = PromptPassword();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = KisFormat.Verify(archive, pw, Console.WriteLine);
        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"{res.Ok}/{res.Total} entries OK ({sw.Elapsed.TotalSeconds:F2}s).");
        if (res.Failed > 0)
        {
            Console.WriteLine($"{res.Failed} entries failed integrity check.");
            return 3;
        }
        return 0;
    }

    // ─── info ───────────────────────────────────────────────────────────
    private static int CmdInfo(string[] args)
    {
        if (args.Length < 1) return Fail("usage: kis info <archive.kis>");
        string archive = args[0];
        var (hdr, entries) = KisFormat.Open(archive, password: null);
        var fi = new FileInfo(archive);
        Console.WriteLine($"File:               {fi.FullName}");
        Console.WriteLine($"On-disk size:       {Human(fi.Length)} ({fi.Length:N0} bytes)");
        Console.WriteLine($"Format:             KIS v1.0  (magic 'KESF')");
        Console.WriteLine($"Default compression:{hdr.DefaultCompression}");
        Console.WriteLine($"Encryption:         {(hdr.IsEncrypted ? "AES-256-CBC + HMAC-SHA256" : "none")}");
        Console.WriteLine($"Encrypted names:    {(hdr.HasEncryptedNames ? "yes" : "no")}");
        Console.WriteLine($"Per-file checksums: {(hdr.HasChecksums ? "SHA-256" : "none")}");
        Console.WriteLine($"Entry count:        {hdr.EntryCount}");
        Console.WriteLine($"Original total:     {Human((long)hdr.OriginalTotal)} ({hdr.OriginalTotal:N0} bytes)");
        Console.WriteLine($"Compressed total:   {Human((long)hdr.CompressedTotal)} ({hdr.CompressedTotal:N0} bytes)");
        if (hdr.OriginalTotal > 0)
        {
            double ratio = (1.0 - (double)hdr.CompressedTotal / hdr.OriginalTotal) * 100;
            Console.WriteLine($"Compression ratio:  {ratio:F1}% smaller than uncompressed");
        }
        if (hdr.IsEncrypted && !string.IsNullOrEmpty(hdr.PasswordHint))
            Console.WriteLine($"Password hint:      {hdr.PasswordHint}");
        if (entries.Count > 0)
        {
            var byComp = entries.GroupBy(e => e.Compression)
                                .Select(g => $"{g.Key}={g.Count()}")
                                .ToArray();
            Console.WriteLine($"Per-entry algos:    {string.Join(", ", byComp)}");
        }
        return 0;
    }

    // ─── helpers ────────────────────────────────────────────────────────
    private static bool IsHelp(string a) =>
        a is "-h" or "--help" or "/?" or "help" or "/h";

    private static string NextArg(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"missing value for {name}");
        return args[++i];
    }

    private static CompressionType ParseComp(string s) => s.ToLowerInvariant() switch
    {
        "none"             => CompressionType.None,
        "deflate" or "zip" => CompressionType.Deflate,
        "lzma" or "xz"     => CompressionType.Lzma,
        _ => throw new ArgumentException($"unknown compression '{s}' (use lzma|deflate|none)")
    };

    private static bool IsEncrypted(string archive)
    {
        try { return KisFormat.Open(archive, null).header.IsEncrypted; }
        catch { return false; }
    }

    private static string PromptPassword()
    {
        Console.Write("Password: ");
        var sb = new System.Text.StringBuilder();
        ConsoleKeyInfo k;
        while ((k = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (k.Key == ConsoleKey.Backspace && sb.Length > 0) sb.Length--;
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    private static string Human(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {u[i]}";
    }

    private static int Fail(string msg) { Console.Error.WriteLine(msg); return 1; }

    private static void PrintHelp()
    {
        Console.WriteLine("KIS — command-line archiver for the KIS format (.kis / .kes)");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  kis create  <archive.kis> <files...>  [options]");
        Console.WriteLine("  kis extract <archive.kis> [-o DIR]    [options]");
        Console.WriteLine("  kis list    <archive.kis>             [options]");
        Console.WriteLine("  kis verify  <archive.kis>             [options]");
        Console.WriteLine("  kis info    <archive.kis>");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -p, --password TEXT      Password for encryption / decryption");
        Console.WriteLine("  -c, --compression ALGO   lzma (default) | deflate | none");
        Console.WriteLine("      --encrypt-names      Also encrypt the entry table (paths/sizes)");
        Console.WriteLine("      --hint TEXT          Password hint (stored next to the salt)");
        Console.WriteLine("  -o, --output DIR         Output directory (extract; default: .)");
        Console.WriteLine("  -f, --force              Overwrite existing files when extracting");
        Console.WriteLine("  -q, --quiet              Suppress per-file logging");
        Console.WriteLine("  -h, --help               Show this help");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  kis create photos.kis ./vacation -c lzma -p \"sunset!\" --hint \"summer\"");
        Console.WriteLine("  kis list   photos.kis -p \"sunset!\"");
        Console.WriteLine("  kis verify photos.kis -p \"sunset!\"");
        Console.WriteLine("  kis extract photos.kis -o ./restored -p \"sunset!\" --force");
    }
}
