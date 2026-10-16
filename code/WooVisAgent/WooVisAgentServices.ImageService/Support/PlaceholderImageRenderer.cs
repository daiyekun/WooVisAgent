using System.IO.Compression;
using System.Text;

namespace WooVisAgentServices.ImageService.Support;

/// <summary>
/// 本地占位图渲染器：不依赖任何外部服务与第三方图片库，直接拼一个合法的 PNG 落盘。
///
/// 用途：当模型配置没有填接口地址时（例如先把链路跑通、或用户只想看流程），
/// 生成一张按提示词哈希着色的占位图，让「提交 → 队列 → Worker → 状态回写 → 页面显示图片」整条链路可验证。
/// 一旦模型配置了接口地址，就会走真实模型接口，不再使用这里。
/// </summary>
internal static class PlaceholderImageRenderer
{
    /// <summary>占位图不必按请求尺寸渲染，限制上限避免内存与耗时浪费</summary>
    private const int MaxRenderSize = 768;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] RenderPng(string prompt, int width, int height, int index)
    {
        var renderWidth = Math.Clamp(width, 64, MaxRenderSize);
        var renderHeight = Math.Clamp(height, 64, MaxRenderSize);

        var seed = StableHash(prompt ?? string.Empty) ^ (uint)((index + 1) * 2654435761u);

        var baseR = (byte)(seed % 180 + 60);
        var baseG = (byte)((seed >> 8) % 180 + 60);
        var baseB = (byte)((seed >> 16) % 180 + 60);

        // 每行开头 1 字节是 filter type，这里统一用 0（None）
        var stride = renderWidth * 3 + 1;
        var raw = new byte[stride * renderHeight];

        for (var y = 0; y < renderHeight; y++)
        {
            var rowStart = y * stride;
            raw[rowStart] = 0;

            var v = (double)y / renderHeight;
            for (var x = 0; x < renderWidth; x++)
            {
                var u = (double)x / renderWidth;

                // 对角波浪 + 每 64 像素一条网格线，让占位图一眼可辨
                var wave = Math.Sin((u + v) * Math.PI * 3) * 0.5 + 0.5;
                var grid = x % 64 == 0 || y % 64 == 0 ? 0.35 : 0;
                var factor = 0.6 + 0.4 * wave - grid;

                var offset = rowStart + 1 + x * 3;
                raw[offset] = Scale(baseR, factor);
                raw[offset + 1] = Scale(baseG, factor);
                raw[offset + 2] = Scale(baseB, factor);
            }
        }

        return EncodePng(raw, renderWidth, renderHeight);
    }

    private static byte Scale(byte channel, double factor)
        => (byte)Math.Clamp((int)Math.Round(channel * factor), 0, 255);

    private static byte[] EncodePng(byte[] rawScanlines, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write(PngSignature);

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8;  // 位深
        header[9] = 2;  // 颜色类型：真彩色 RGB
        header[10] = 0; // 压缩方式
        header[11] = 0; // 过滤方式
        header[12] = 0; // 逐行扫描
        WriteChunk(output, "IHDR", header);

        var compressed = CompressZlib(rawScanlines);
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    private static byte[] CompressZlib(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }

        return buffer.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, (uint)data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes, 0, typeBytes.Length);
        stream.Write(data, 0, data.Length);

        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);

        Span<byte> crc = stackalloc byte[4];
        WriteBigEndian(crc, Crc32(crcInput));
        stream.Write(crc);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteBigEndian(Span<byte> buffer, uint value)
    {
        buffer[0] = (byte)(value >> 24);
        buffer[1] = (byte)(value >> 16);
        buffer[2] = (byte)(value >> 8);
        buffer[3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>FNV-1a：同一段提示词始终得到同一张占位图，便于复现</summary>
    private static uint StableHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 16777619u;
            }

            return hash;
        }
    }
}
