using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace VScreenPilot.Perception;

/// <summary>
/// 纯托管 PNG 编码器（RGBA8），避免 System.Drawing/WIC 的 AOT 兼容问题。
/// 输入为 GDI 的 BGRA 顶朝下位图。
///
/// 与旧实现的差别：
/// - 缩放与行转换一遍完成，不再生成整图中间缓冲；
/// - 每行自适应 None/Sub/Up/Average 过滤，体积通常降 2–4 倍；
/// - 扫描线与行缓冲走 ArrayPool，避免每次截图产生 ~12MB LOH 垃圾。
/// </summary>
internal static class PngWriter
{
    private const int Bpp = 4;
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>
    /// none | up | adaptive。默认 up：UI 截图通常比 None 小 2–4 倍，
    /// 又不像 adaptive 那样为每一行做 4 次代价估算（会把区域截图从 ~35ms 推到 ~66ms）。
    /// </summary>
    private static readonly string FilterMode =
        (Environment.GetEnvironmentVariable("VSP_PNG_FILTER") ?? "up").Trim().ToLowerInvariant();

    public static byte[] Encode(
        byte[] bgra, int width, int height, double scale, out int outputWidth, out int outputHeight)
    {
        double clamped = Math.Clamp(scale <= 0 ? 1.0 : scale, 0.1, 1.0);
        outputWidth = Math.Max(1, (int)Math.Round(width * clamped));
        outputHeight = Math.Max(1, (int)Math.Round(height * clamped));

        int stride = outputWidth * Bpp;
        int rawSize = (stride + 1) * outputHeight;

        byte[] raw = ArrayPool<byte>.Shared.Rent(rawSize);
        byte[] current = ArrayPool<byte>.Shared.Rent(stride);
        byte[] prior = ArrayPool<byte>.Shared.Rent(stride);

        try
        {
            bool hasPrior = false;

            for (int row = 0; row < outputHeight; row++)
            {
                if (outputWidth == width && outputHeight == height)
                {
                    ConvertRow(bgra, row * width * Bpp, current, outputWidth);
                }
                else
                {
                    ResizeRow(new PngCanvas(bgra, width, height), row, outputHeight, current, outputWidth);
                }

                int destination = row * (stride + 1);
                WriteFilteredRow(current, prior, hasPrior, stride, raw, destination);

                (current, prior) = (prior, current);
                hasPrior = true;
            }

            using var compressed = new MemoryStream(Math.Max(1024, rawSize / 4));

            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw, 0, rawSize);
            }

            using var png = new MemoryStream((int)(compressed.Length + 128));
            png.Write(Signature);

            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)outputWidth);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)outputHeight);
            ihdr[8] = 8;
            ihdr[9] = 6;
            ihdr[10] = 0;
            ihdr[11] = 0;
            ihdr[12] = 0;

            WriteChunk(png, "IHDR", ihdr);
            WriteChunk(png, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
            WriteChunk(png, "IEND", ReadOnlySpan<byte>.Empty);

            return png.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
            ArrayPool<byte>.Shared.Return(current);
            ArrayPool<byte>.Shared.Return(prior);
        }
    }

    private static void ConvertRow(byte[] bgra, int source, byte[] destination, int width)
    {
        int destinationIndex = 0;

        for (int column = 0; column < width; column++)
        {
            destination[destinationIndex++] = bgra[source + 2];
            destination[destinationIndex++] = bgra[source + 1];
            destination[destinationIndex++] = bgra[source];
            destination[destinationIndex++] = 255;
            source += 4;
        }
    }

    private static void ResizeRow(
        PngCanvas source, int outputRow, int outputHeight, byte[] destination, int outputWidth)
    {
        byte[] bgra = source.Bgra;
        int width = source.Width;
        int height = source.Height;
        int sy0 = outputRow * height / outputHeight;
        int sy1 = Math.Max(sy0 + 1, (outputRow + 1) * height / outputHeight);
        int destinationIndex = 0;

        for (int ox = 0; ox < outputWidth; ox++)
        {
            int sx0 = ox * width / outputWidth;
            int sx1 = Math.Max(sx0 + 1, (ox + 1) * width / outputWidth);

            long blue = 0, green = 0, red = 0;
            int count = 0;

            for (int sy = sy0; sy < sy1 && sy < height; sy++)
            {
                int rowOffset = sy * width * Bpp;

                for (int sx = sx0; sx < sx1 && sx < width; sx++)
                {
                    int index = rowOffset + sx * Bpp;
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

            destination[destinationIndex++] = (byte)(red / count);
            destination[destinationIndex++] = (byte)(green / count);
            destination[destinationIndex++] = (byte)(blue / count);
            destination[destinationIndex++] = 255;
        }
    }

    /// <summary>
    /// 逐行选择压缩代价最小的过滤（None/Sub/Up/Average）。
    /// 只用每 4 个字节抽样估算 SAD，避免为了选过滤把 CPU 翻五倍。
    /// </summary>
    private static void WriteFilteredRow(
        byte[] current, byte[] prior, bool hasPrior, int stride, byte[] raw, int destination)
    {
        byte filter = ChooseFilter(current, prior, hasPrior, stride);
        raw[destination] = filter;

        int offset = destination + 1;

        for (int index = 0; index < stride; index++)
        {
            raw[offset + index] = ApplyFilter(filter, current, prior, hasPrior, index);
        }
    }

    private static byte ChooseFilter(byte[] current, byte[] prior, bool hasPrior, int stride)
    {
        if (FilterMode == "none")
        {
            return 0;
        }

        if (FilterMode == "up")
        {
            return hasPrior ? (byte)2 : (byte)0;
        }

        long none = 0, sub = 0, up = 0, average = 0;

        for (int index = 0; index < stride; index += Bpp)
        {
            byte value = current[index];
            int left = index >= Bpp ? current[index - Bpp] : 0;
            int above = hasPrior ? prior[index] : 0;

            // 先转 int 再取绝对值：Math.Abs((sbyte)-128) 会抛 OverflowException。
            none += Math.Abs((int)(sbyte)value);
            sub += Math.Abs((int)(sbyte)(value - left));
            up += Math.Abs((int)(sbyte)(value - above));
            average += Math.Abs((int)(sbyte)(value - ((left + above) >> 1)));
        }

        return PickSmallest(none, sub, hasPrior ? up : long.MaxValue, average);
    }

    private static byte PickSmallest(long none, long sub, long up, long average)
    {
        long best = none;
        byte filter = 0;

        if (sub < best)
        {
            best = sub;
            filter = 1;
        }

        if (up < best)
        {
            best = up;
            filter = 2;
        }

        if (average < best)
        {
            filter = 3;
        }

        return filter;
    }

    private static byte ApplyFilter(
        byte filter, byte[] current, byte[] prior, bool hasPrior, int index)
    {
        byte value = current[index];
        int left = index >= Bpp ? current[index - Bpp] : 0;
        int above = hasPrior ? prior[index] : 0;

        return filter switch
        {
            1 => (byte)(value - left),
            2 => (byte)(value - above),
            3 => (byte)(value - ((left + above) >> 1)),
            _ => value,
        };
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

        for (uint index = 0; index < 256; index++)
        {
            uint value = index;

            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
