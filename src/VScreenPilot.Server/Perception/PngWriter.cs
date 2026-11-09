using System.Buffers.Binary;
using System.IO.Compression;

namespace VScreenPilot.Perception;

/// <summary>
/// 纯托管 PNG 编码器（RGBA8），避免 System.Drawing/WIC 的 AOT 兼容问题。
/// 输入为 GDI 的 BGRA 顶朝下位图。
/// </summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(
        byte[] bgra, int width, int height, double scale, out int outputWidth, out int outputHeight)
    {
        double clamped = Math.Clamp(scale <= 0 ? 1.0 : scale, 0.1, 1.0);
        outputWidth = Math.Max(1, (int)Math.Round(width * clamped));
        outputHeight = Math.Max(1, (int)Math.Round(height * clamped));

        int stride = outputWidth * 4;
        byte[] raw = new byte[(stride + 1) * outputHeight];

        if (outputWidth == width && outputHeight == height)
        {
            // 1:1 时直接转换到 PNG 扫描线，省掉一份整图中间缓冲。
            int source = 0;
            int destination = 0;

            for (int row = 0; row < outputHeight; row++)
            {
                raw[destination++] = 0; // filter: None

                for (int column = 0; column < outputWidth; column++)
                {
                    raw[destination++] = bgra[source + 2];
                    raw[destination++] = bgra[source + 1];
                    raw[destination++] = bgra[source];
                    raw[destination++] = 255; // GDI 的 alpha 未定义，强制不透明
                    source += 4;
                }
            }
        }
        else
        {
            byte[] rgba = ResizeToRgba(bgra, width, height, outputWidth, outputHeight);
            int source = 0;
            int destination = 0;

            for (int row = 0; row < outputHeight; row++)
            {
                raw[destination++] = 0; // filter: None
                Buffer.BlockCopy(rgba, source, raw, destination, stride);
                source += stride;
                destination += stride;
            }
        }

        using var compressed = new MemoryStream(raw.Length / 2);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        using var png = new MemoryStream((int)(compressed.Length + 128));
        png.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)outputWidth);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)outputHeight);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // color type: RGBA
        ihdr[10] = 0;  // compression
        ihdr[11] = 0;  // filter
        ihdr[12] = 0;  // interlace

        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(png, "IEND", ReadOnlySpan<byte>.Empty);

        return png.ToArray();
    }

    private static byte[] ResizeToRgba(byte[] bgra, int width, int height, int outputWidth, int outputHeight)
    {
        var result = new byte[outputWidth * outputHeight * 4];

        if (outputWidth == width && outputHeight == height)
        {
            for (int i = 0, j = 0; i < bgra.Length; i += 4, j += 4)
            {
                result[j] = bgra[i + 2];
                result[j + 1] = bgra[i + 1];
                result[j + 2] = bgra[i];
                result[j + 3] = 255;
            }

            return result;
        }

        for (int oy = 0; oy < outputHeight; oy++)
        {
            int sy0 = oy * height / outputHeight;
            int sy1 = Math.Max(sy0 + 1, (oy + 1) * height / outputHeight);

            for (int ox = 0; ox < outputWidth; ox++)
            {
                int sx0 = ox * width / outputWidth;
                int sx1 = Math.Max(sx0 + 1, (ox + 1) * width / outputWidth);

                long blue = 0, green = 0, red = 0;
                int count = 0;

                for (int sy = sy0; sy < sy1 && sy < height; sy++)
                {
                    int rowOffset = sy * width * 4;
                    for (int sx = sx0; sx < sx1 && sx < width; sx++)
                    {
                        int index = rowOffset + sx * 4;
                        blue += bgra[index];
                        green += bgra[index + 1];
                        red += bgra[index + 2];
                        count++;
                    }
                }

                if (count == 0)
                {
                    count = 1;
                }

                int outputIndex = (oy * outputWidth + ox) * 4;
                result[outputIndex] = (byte)(red / count);
                result[outputIndex + 1] = (byte)(green / count);
                result[outputIndex + 2] = (byte)(blue / count);
                result[outputIndex + 3] = 255;
            }
        }

        return result;
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
        {
            typeBytes[i] = (byte)type[i];
        }

        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = 0xFFFFFFFF;
        crc = UpdateCrc(crc, typeBytes);
        crc = UpdateCrc(crc, data);
        crc ^= 0xFFFFFFFF;

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
