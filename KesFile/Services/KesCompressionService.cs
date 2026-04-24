using System;
using System.IO;
using System.IO.Compression;
using KesFile.Models;

namespace KesFile.Services
{
    /// <summary>
    /// Handles raw byte-array compression and decompression.
    /// Supports Deflate (built-in) and LZMA (via SharpCompress).
    /// LZMA typically achieves 30–50 % better ratio than Deflate.
    /// </summary>
    public class KesCompressionService
    {
        // ─── Public API ──────────────────────────────────────────────────────

        public byte[] Compress(byte[] data, KesCompressionType type, CompressionSpeed speed = CompressionSpeed.Normal)
        {
            if (data == null || data.Length == 0) return Array.Empty<byte>();

            return type switch
            {
                KesCompressionType.None    => data,
                KesCompressionType.Deflate => CompressDeflate(data, speed),
                KesCompressionType.Lzma    => CompressLzma(data, speed),
                _                          => data
            };
        }

        public byte[] Decompress(byte[] data, KesCompressionType type, ulong originalSize)
        {
            if (data == null || data.Length == 0) return Array.Empty<byte>();
            if (type == KesCompressionType.None)  return data;

            return type switch
            {
                KesCompressionType.Deflate => DecompressDeflate(data, (long)originalSize),
                KesCompressionType.Lzma    => DecompressLzma(data, (long)originalSize),
                _                          => data
            };
        }

        // ─── Deflate ─────────────────────────────────────────────────────────

        private static byte[] CompressDeflate(byte[] data, CompressionSpeed speed)
        {
            var level = speed switch
            {
                CompressionSpeed.Fastest => CompressionLevel.Fastest,
                CompressionSpeed.Fast    => CompressionLevel.Fastest,
                CompressionSpeed.Normal  => CompressionLevel.Optimal,
                CompressionSpeed.Maximum => CompressionLevel.Optimal,
                _                        => CompressionLevel.Optimal
            };

            using var ms = new MemoryStream();
            using (var deflate = new DeflateStream(ms, level, leaveOpen: true))
                deflate.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        private static byte[] DecompressDeflate(byte[] data, long originalSize)
        {
            using var compressed = new MemoryStream(data);
            using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);

            var output = new byte[originalSize];
            int offset = 0;
            while (offset < (int)originalSize)
            {
                int read = deflate.Read(output, offset, (int)originalSize - offset);
                if (read == 0) break;
                offset += read;
            }
            return output;
        }

        // ─── LZMA ────────────────────────────────────────────────────────────

        private static byte[] CompressLzma(byte[] data, CompressionSpeed speed)
        {
            try
            {
                using var output = new MemoryStream();
                using var input  = new MemoryStream(data);

                var props = new SharpCompress.Compressors.LZMA.LzmaEncoderProperties(
                    eos: false,
                    dictionary: SpeedToLzmaDictionary(speed),
                    numFastBytes: SpeedToLzmaFastBytes(speed));

                byte[] propertyBytes;
                using (var encoder = new SharpCompress.Compressors.LZMA.LzmaStream(props, false, output))
                {
                    input.CopyTo(encoder);
                    propertyBytes = encoder.Properties;
                }

                byte[] payload = output.ToArray();
                byte[] compressed = new byte[propertyBytes.Length + payload.Length];
                Buffer.BlockCopy(propertyBytes, 0, compressed, 0, propertyBytes.Length);
                Buffer.BlockCopy(payload, 0, compressed, propertyBytes.Length, payload.Length);

                // Only use LZMA result if it is actually smaller
                return compressed.Length < data.Length ? compressed : CompressDeflate(data, speed);
            }
            catch
            {
                // Fall back to Deflate if SharpCompress is unavailable or throws
                return CompressDeflate(data, speed);
            }
        }

        private static byte[] DecompressLzma(byte[] data, long originalSize)
        {
            try
            {
                if (data.Length < 6)
                    throw new InvalidDataException("LZMA data too short.");

                // Current format: 5-byte LZMA properties + raw LZMA payload.
                byte[]? decoded = TryDecodeLzmaWithPrefixedProperties(data, originalSize);
                if (decoded != null)
                    return decoded;

                // Backward-compat: older archives wrote payload without properties.
                // Try common dictionary presets used by this app.
                foreach (var dict in new[] { 1 << 16, 1 << 20, 1 << 23, 1 << 25 })
                {
                    decoded = TryDecodeLzmaPayload(data, BuildLzmaProperties(dict), originalSize);
                    if (decoded != null)
                        return decoded;
                }

                // Extra compatibility: try valid lc/lp/pb property combinations
                // with known dictionary sizes used by this app.
                foreach (byte prop0 in BuildLegacyPropertyByteCandidates())
                {
                    foreach (var dict in new[] { 1 << 16, 1 << 20, 1 << 23, 1 << 25 })
                    {
                        decoded = TryDecodeLzmaPayload(data, BuildLzmaProperties(dict, prop0), originalSize);
                        if (decoded != null)
                            return decoded;
                    }
                }

                throw new InvalidDataException("Unable to decode LZMA payload with known properties.");
            }
            catch
            {
                // Last-resort compatibility fallback if a file was mislabeled.
                try
                {
                    return DecompressDeflate(data, originalSize);
                }
                catch
                {
                    throw new InvalidDataException("Unable to decompress data as LZMA (and Deflate fallback also failed).");
                }
            }
        }

        private static byte[]? TryDecodeLzmaWithPrefixedProperties(byte[] data, long originalSize)
        {
            byte[] propsBytes = new byte[5];
            Buffer.BlockCopy(data, 0, propsBytes, 0, 5);

            byte[] payload = new byte[data.Length - 5];
            Buffer.BlockCopy(data, 5, payload, 0, payload.Length);

            return TryDecodeLzmaPayload(payload, propsBytes, originalSize);
        }

        private static byte[]? TryDecodeLzmaPayload(byte[] payload, byte[] propsBytes, long originalSize)
        {
            try
            {
                using var input = new MemoryStream(payload);
                using var output = new MemoryStream();

                long inputSize = payload.LongLength;
                long outputSize = originalSize > 0 ? originalSize : -1;

                using var decoder = outputSize > 0
                    ? new SharpCompress.Compressors.LZMA.LzmaStream(propsBytes, input, inputSize, outputSize)
                    : new SharpCompress.Compressors.LZMA.LzmaStream(propsBytes, input, inputSize);

                decoder.CopyTo(output);
                return output.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static byte[] BuildLzmaProperties(int dictionarySize, byte propertyByte = 0x5D)
        {
            // propertyByte encodes (pb, lp, lc). 0x5D corresponds to lc=3, lp=0, pb=2.
            return new[]
            {
                propertyByte,
                (byte)(dictionarySize & 0xFF),
                (byte)((dictionarySize >> 8) & 0xFF),
                (byte)((dictionarySize >> 16) & 0xFF),
                (byte)((dictionarySize >> 24) & 0xFF)
            };
        }

        private static byte[] BuildLegacyPropertyByteCandidates()
        {
            // Valid LZMA property byte: (pb * 5 + lp) * 9 + lc.
            // Keep only combinations where lc + lp <= 4 (common constraint).
            var values = new byte[75];
            int index = 0;
            for (int pb = 0; pb <= 4; pb++)
            {
                for (int lp = 0; lp <= 4; lp++)
                {
                    for (int lc = 0; lc <= 8; lc++)
                    {
                        if (lc + lp > 4) continue;
                        values[index++] = (byte)(((pb * 5) + lp) * 9 + lc);
                    }
                }
            }

            if (index == values.Length) return values;

            var trimmed = new byte[index];
            Buffer.BlockCopy(values, 0, trimmed, 0, index);
            return trimmed;
        }

        private static int SpeedToLzmaLevel(CompressionSpeed speed) => speed switch
        {
            CompressionSpeed.Fastest => 1,
            CompressionSpeed.Fast    => 3,
            CompressionSpeed.Normal  => 5,
            CompressionSpeed.Maximum => 9,
            _                        => 5
        };

        private static int SpeedToLzmaDictionary(CompressionSpeed speed) => speed switch
        {
            CompressionSpeed.Fastest => 1 << 16,   // 64 KB
            CompressionSpeed.Fast    => 1 << 20,   // 1 MB
            CompressionSpeed.Normal  => 1 << 23,   // 8 MB
            CompressionSpeed.Maximum => 1 << 25,   // 32 MB
            _                        => 1 << 23
        };

        private static int SpeedToLzmaFastBytes(CompressionSpeed speed) => speed switch
        {
            CompressionSpeed.Fastest => 32,
            CompressionSpeed.Fast    => 64,
            CompressionSpeed.Normal  => 128,
            CompressionSpeed.Maximum => 273,
            _                        => 128
        };
    }
}
