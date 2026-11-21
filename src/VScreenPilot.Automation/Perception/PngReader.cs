using System.Buffers.Binary;
using System.IO.Compression;

namespace VScreenPilot.Perception;

internal sealed class PngFormatException : Exception
{
    public PngFormatException(string message) : base(message)
    {
    }
}

internal sealed record PngImage(byte[] Rgba, int Width, int Height);

internal sealed record PngInfo(int Width, int Height, int BitDepth, int ColorType, int Interlace);

/// <summary>
/// 纯托管 PNG 解码器：支持 8bit 灰度/RGB/RGBA、非隔行、filter 0-4。
/// 用于模板匹配，避免引入 System.Drawing / WIC 依赖。
/// </summary>
internal static class PngReader
{
    private const int MaxPixels = 16_000_000;

    public static PngImage Decode(byte[] data)
    {
        (int width, int height, int bitDepth, int colorType, int interlace, byte[] compressed) =
            ParseChunks(data);

        if (width <= 0 || height <= 0)
        {
            throw new PngFormatException("缺少有效 IHDR");
        }

        if ((long)width * height > MaxPixels)
        {
            throw new PngFormatException($"图片过大: {width}x{height}");
        }

        if (bitDepth != 8)
        {
            throw new PngFormatException($"仅支持 8bit PNG，当前 bitDepth={bitDepth}");
        }

        if (interlace != 0)
        {
            throw new PngFormatException("不支持隔行 PNG");
        }

        int channels = colorType switch
        {
            0 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            3 => throw new PngFormatException("不支持调色板 PNG，请另存为 RGBA"),
            _ => throw new PngFormatException($"不支持的 colorType={colorType}"),
        };

        if (compressed.Length == 0)
        {
            throw new PngFormatException("缺少 IDAT");
        }

        int stride = width * channels;
        byte[] raw = new byte[(stride + 1) * height];

        try
        {
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            int read = 0;

            while (read < raw.Length)
            {
                int current = zlib.Read(raw, read, raw.Length - read);
                if (current <= 0)
                {
                    break;
                }

                read += current;
            }

            if (read < raw.Length)
            {
                throw new PngFormatException("PNG 像素数据不完整");
            }
        }
        catch (InvalidDataException exception)
        {
            throw new PngFormatException($"PNG zlib 解压失败: {exception.Message}");
        }

        byte[] pixels = Unfilter(raw, width, height, channels);
        byte[] rgba = ToRgba(pixels, width, height, channels);
        return new PngImage(rgba, width, height);
    }

    private static (int Width, int Height, int BitDepth, int ColorType, int Interlace, byte[] Compressed)
        ParseChunks(byte[] data)
    {
        if (data.Length < 8)
        {
            throw new PngFormatException("数据太短，不是 PNG");
        }

        ReadOnlySpan<byte> signature = data.AsSpan(0, 8);
        ReadOnlySpan<byte> expected = stackalloc byte[]
        {
            137, 80, 78, 71, 13, 10, 26, 10,
        };

        if (!signature.SequenceEqual(expected))
        {
            throw new PngFormatException("PNG 签名不匹配");
        }

        int width = 0;
        int height = 0;
        int bitDepth = 0;
        int colorType = -1;
        int interlace = 0;
        var idat = new MemoryStream();

        int offset = 8;

        while (offset + 8 <= data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
            string type = System.Text.Encoding.ASCII.GetString(data, offset + 4, 4);

            if (length < 0 || (long)offset + 12 + length > data.Length)
            {
                throw new PngFormatException("PNG 数据块长度非法");
            }

            ReadOnlySpan<byte> payload = data.AsSpan(offset + 8, length);
            VerifyCrc(data, offset, length, type);

            switch (type)
            {
                case "IHDR":
                    if (length < 13)
                    {
                        throw new PngFormatException("IHDR 太短");
                    }

                    width = BinaryPrimitives.ReadInt32BigEndian(payload);
                    height = BinaryPrimitives.ReadInt32BigEndian(payload[4..]);
                    bitDepth = payload[8];
                    colorType = payload[9];
                    interlace = payload[12];
                    break;

                case "IDAT":
                    idat.Write(payload);
                    break;

                case "IEND":
                    offset = data.Length;
                    break;
            }

            offset += 12 + length;
        }

        byte[] compressed = idat.ToArray();
        return (width, height, bitDepth, colorType, interlace, compressed);
    }

    /// <summary>只读 IHDR，用于在完整解码前拒绝超大模板。</summary>
    public static PngInfo ReadInfo(byte[] data)
    {
        if (data.Length < 8)
        {
            throw new PngFormatException("数据太短，不是 PNG");
        }

        ReadOnlySpan<byte> expected = stackalloc byte[]
        {
            137, 80, 78, 71, 13, 10, 26, 10,
        };

        if (!data.AsSpan(0, 8).SequenceEqual(expected))
        {
            throw new PngFormatException("PNG 签名不匹配");
        }

        int offset = 8;

        while (offset + 8 <= data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));

            if (length < 0 || (long)offset + 12 + length > data.Length)
            {
                throw new PngFormatException("PNG 数据块长度非法");
            }

            string type = System.Text.Encoding.ASCII.GetString(data, offset + 4, 4);
            VerifyCrc(data, offset, length, type);

            if (type == "IHDR")
            {
                if (length < 13)
                {
                    throw new PngFormatException("IHDR 太短");
                }

                ReadOnlySpan<byte> payload = data.AsSpan(offset + 8, length);
                int width = BinaryPrimitives.ReadInt32BigEndian(payload);
                int height = BinaryPrimitives.ReadInt32BigEndian(payload[4..]);

                if (width <= 0 || height <= 0)
                {
                    throw new PngFormatException("IHDR 尺寸非法");
                }

                return new PngInfo(width, height, payload[8], payload[9], payload[12]);
            }

            offset += 12 + length;
        }

        throw new PngFormatException("缺少 IHDR");
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;

            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>CRC 覆盖 chunk type + data；坏模板在解码前就被拒绝（R17）。</summary>
    private static void VerifyCrc(byte[] data, int offset, int length, string type)
    {
        uint stored = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 8 + length, 4));
        uint actual = Crc32(data.AsSpan(offset + 4, 4 + length));

        if (stored != actual)
        {
            throw new PngFormatException($"PNG CRC 校验失败: {type}");
        }
    }

    private static byte[] Unfilter(byte[] raw, int width, int height, int channels)
    {
        int stride = width * channels;
        byte[] output = new byte[stride * height];
        int previousRow = -1;

        for (int row = 0; row < height; row++)
        {
            int source = row * (stride + 1);
            byte filter = raw[source++];
            int destination = row * stride;
            int up = previousRow;

            for (int column = 0; column < stride; column++)
            {
                int value = raw[source + column];
                int left = column >= channels ? output[destination + column - channels] : 0;
                int above = up >= 0 ? output[up + column] : 0;
                int upperLeft = up >= 0 && column >= channels ? output[up + column - channels] : 0;

                int restored = filter switch
                {
                    0 => value,
                    1 => value + left,
                    2 => value + above,
                    3 => value + ((left + above) >> 1),
                    4 => value + Paeth(left, above, upperLeft),
                    _ => throw new PngFormatException($"不支持的 PNG filter={filter}"),
                };

                output[destination + column] = (byte)restored;
            }

            previousRow = destination;
        }

        return output;
    }

    private static int Paeth(int left, int above, int upperLeft)
    {
        int estimate = left + above - upperLeft;
        int distanceLeft = Math.Abs(estimate - left);
        int distanceAbove = Math.Abs(estimate - above);
        int distanceUpperLeft = Math.Abs(estimate - upperLeft);

        if (distanceLeft <= distanceAbove && distanceLeft <= distanceUpperLeft)
        {
            return left;
        }

        return distanceAbove <= distanceUpperLeft ? above : upperLeft;
    }

    private static byte[] ToRgba(byte[] pixels, int width, int height, int channels)
    {
        int count = width * height;
        byte[] rgba = new byte[count * 4];

        for (int index = 0; index < count; index++)
        {
            int source = index * channels;
            int destination = index * 4;

            switch (channels)
            {
                case 1:
                    rgba[destination] = pixels[source];
                    rgba[destination + 1] = pixels[source];
                    rgba[destination + 2] = pixels[source];
                    rgba[destination + 3] = 255;
                    break;

                case 2:
                    rgba[destination] = pixels[source];
                    rgba[destination + 1] = pixels[source];
                    rgba[destination + 2] = pixels[source];
                    rgba[destination + 3] = pixels[source + 1];
                    break;

                case 3:
                    rgba[destination] = pixels[source];
                    rgba[destination + 1] = pixels[source + 1];
                    rgba[destination + 2] = pixels[source + 2];
                    rgba[destination + 3] = 255;
                    break;

                default:
                    rgba[destination] = pixels[source];
                    rgba[destination + 1] = pixels[source + 1];
                    rgba[destination + 2] = pixels[source + 2];
                    rgba[destination + 3] = pixels[source + 3];
                    break;
            }
        }

        return rgba;
    }
}
