using System;
using System.IO;
using System.Linq;
using KesFile.Models;
using KesFile.Services;
using SharpCompress.Compressors.LZMA;

internal static class Program
{
    private static int Main()
    {
        byte[] original = new byte[256 * 1024];
        Random.Shared.NextBytes(original);

        byte[] oldPayload = OldCompress(original);

        var compressionService = new KesCompressionService();
        byte[] decompressed = compressionService.Decompress(oldPayload, KesCompressionType.Lzma, (ulong)original.Length);

        bool pass = original.SequenceEqual(decompressed);

        Console.WriteLine($"OriginalLength={original.Length} CompressedLength={oldPayload.Length} DecompressedLength={decompressed.Length}");
        Console.WriteLine(pass ? "PASS: Legacy raw-LZMA payload decompressed correctly." : "FAIL: Legacy raw-LZMA payload mismatch.");

        return pass ? 0 : 1;
    }

    private static byte[] OldCompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var output = new MemoryStream();

        var props = new LzmaEncoderProperties(
            eos: false,
            dictionary: 1 << 23,
            numFastBytes: 128);

        using (var encoder = new LzmaStream(props, false, output))
        {
            input.CopyTo(encoder);
        }

        return output.ToArray();
    }
}
