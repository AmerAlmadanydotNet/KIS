using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using SharpCompress.Compressors.LZMA;

static string Hex(byte[] data, int max = -1)
{
    if (data == null) return "<null>";
    int n = (max < 0 || max > data.Length) ? data.Length : max;
    return Convert.ToHexString(data, 0, n);
}

var sharpPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", "sharpcompress", "0.37.2", "lib", "netstandard2.0", "SharpCompress.dll");
Console.WriteLine($"SharpCompress path: {sharpPath}");
if (!File.Exists(sharpPath)) throw new FileNotFoundException("SharpCompress.dll not found.", sharpPath);
Assembly.LoadFrom(sharpPath);

byte[] sample = Encoding.UTF8.GetBytes("KesFile-LZMA sample bytes 0123456789");
using var input = new MemoryStream(sample);
using var output = new MemoryStream();

var props = new LzmaEncoderProperties(eos: false, dictionary: 1 << 23, numFastBytes: 128);
using (var encoder = new LzmaStream(props, false, output))
{
    input.CopyTo(encoder);
    encoder.Dispose();

    byte[] compressed = output.ToArray();
    byte[] encProps = encoder.Properties;

    Console.WriteLine($"Sample length: {sample.Length}");
    Console.WriteLine($"Compressed length: {compressed.Length}");
    Console.WriteLine($"encoder.Properties length: {encProps?.Length ?? -1}");
    Console.WriteLine($"encoder.Properties hex: {Hex(encProps)}");
    Console.WriteLine($"Compressed first16 hex: {Hex(compressed, Math.Min(16, compressed.Length))}");

    bool startsWith = encProps != null && compressed.Length >= encProps.Length && compressed.Take(encProps.Length).SequenceEqual(encProps);
    Console.WriteLine($"Compressed starts with encoder.Properties: {startsWith}");

    bool decodeAOk = false, decodeAMatch = false;
    int decodeALen = -1;
    try
    {
        byte[] propsA = compressed.Take(5).ToArray();
        using var msA = new MemoryStream(compressed);
        msA.Position = 5;
        using var decA = new LzmaStream(propsA, msA, sample.Length);
        using var outA = new MemoryStream();
        decA.CopyTo(outA);
        byte[] bytesA = outA.ToArray();
        decodeAOk = true;
        decodeALen = bytesA.Length;
        decodeAMatch = bytesA.SequenceEqual(sample);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Decode A error: {ex.Message}");
    }
    Console.WriteLine($"Decode A success: {decodeAOk}; len={decodeALen}; contentMatch={decodeAMatch}");

    bool decodeBOk = false, decodeBMatch = false;
    int decodeBLen = -1;
    try
    {
        using var msB = new MemoryStream(compressed);
        using var decB = new LzmaStream(encProps, msB, sample.Length);
        using var outB = new MemoryStream();
        decB.CopyTo(outB);
        byte[] bytesB = outB.ToArray();
        decodeBOk = true;
        decodeBLen = bytesB.Length;
        decodeBMatch = bytesB.SequenceEqual(sample);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Decode B error: {ex.Message}");
    }
    Console.WriteLine($"Decode B success: {decodeBOk}; len={decodeBLen}; contentMatch={decodeBMatch}");
}
