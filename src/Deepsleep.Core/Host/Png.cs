using System.IO.Compression;
using System.Text;

namespace TrollWrangler.CoreHost;

/// <summary>
/// 最小 PNG 编码器（24 位 RGB，无依赖）。
/// 远程桌面用它把屏幕帧压成 PNG：屏幕画面大块纯色 + 文字，PNG 往往比 JPEG 还小，而且不糊。
/// </summary>
public static class Png
{
    public static byte[] EncodeRgb(byte[] rgb, int w, int h, int stride)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        WriteBE(ihdr, 0, w);
        WriteBE(ihdr, 4, h);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 2;    // color type: truecolor RGB
        Chunk(ms, "IHDR", ihdr);

        using (var raw = new MemoryStream())
        {
            for (int y = 0; y < h; y++)
            {
                raw.WriteByte(0);                       // filter: none
                raw.Write(rgb, y * stride, w * 3);
            }
            raw.Position = 0;
            using var comp = new MemoryStream();
            using (var z = new ZLibStream(comp, CompressionLevel.Fastest, true)) raw.CopyTo(z);
            Chunk(ms, "IDAT", comp.ToArray());
        }

        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteBE(byte[] a, int off, int v)
    {
        a[off] = (byte)(v >> 24); a[off + 1] = (byte)(v >> 16);
        a[off + 2] = (byte)(v >> 8); a[off + 3] = (byte)v;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, data.Length);
        s.Write(len);
        byte[] t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var crc = new byte[4];
        WriteBE(crc, 0, (int)Crc32(t, data));
        s.Write(crc);
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte x in a) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (byte x in b) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
