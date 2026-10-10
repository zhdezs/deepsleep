using System.IO.Compression;

namespace TrollWrangler.CoreHost;

/// <summary>
/// 最小 PNG 解码器：只认截图工具产出的常规 PNG（非隔行；颜色类型 0/2/3/4/6；位深 8，
/// 16 位取高字节）。
/// Linux 被控端只能让外部工具（GNOME 的 DBus 截图、xdg-desktop-portal、grim、gnome-screenshot…）
/// 把画面存成 PNG 文件；隧道带宽很小，必须先把整屏缩到 960px 左右再发，所以这里得自己解一遍。
/// </summary>
internal static class PngDecode
{
    /// <summary>解码成 RGB24（每行 width*3 字节）。失败返回 null。</summary>
    public static byte[]? DecodeRgb(byte[] png, out int width, out int height)
    {
        width = height = 0;
        if (png.Length < 33 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return null;

        int bitDepth = 8, colorType = 6;
        byte[]? palette = null;
        using var idat = new MemoryStream();
        int pos = 8;
        while (pos + 12 <= png.Length)
        {
            int len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            if (len < 0) return null;
            string type = new string(new[] { (char)png[pos + 4], (char)png[pos + 5], (char)png[pos + 6], (char)png[pos + 7] });
            int at = pos + 8;
            if (at + len > png.Length) return null;
            if (type == "IHDR")
            {
                width = Be(png, at);
                height = Be(png, at + 4);
                bitDepth = png[at + 8];
                colorType = png[at + 9];
                if (png[at + 12] != 0) return null;                 // 隔行扫描：截图工具不会这么存
            }
            else if (type == "PLTE")
            {
                palette = new byte[len];
                Array.Copy(png, at, palette, 0, len);
            }
            else if (type == "IDAT")
            {
                idat.Write(png, at, len);
            }
            pos = at + len + 4;
            if (type == "IEND") break;
        }

        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0 || width <= 0 || height <= 0) return null;
        if (bitDepth != 8 && bitDepth != 16) return null;
        if (colorType == 3 && palette == null) return null;
        int sample = bitDepth / 8;
        int bpp = channels * sample;
        long stride = (long)width * bpp;
        if (stride * height > 1L << 30) return null;

        byte[] raw;
        try
        {
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            raw = ms.ToArray();
        }
        catch { return null; }
        if (raw.LongLength < (stride + 1) * height) return null;

        // 逐行反滤波（PNG 的 5 种 filter）
        var px = new byte[stride * height];
        int p = 0;
        for (int y = 0; y < height; y++)
        {
            int ft = raw[p++];
            long cur = y * stride, prev = (y - 1) * stride;
            for (long x = 0; x < stride; x++)
            {
                int a = x >= bpp ? px[cur + x - bpp] : 0;
                int b = y > 0 ? px[prev + x] : 0;
                int c = y > 0 && x >= bpp ? px[prev + x - bpp] : 0;
                int v = raw[p + x];
                switch (ft)
                {
                    case 0: break;
                    case 1: v += a; break;
                    case 2: v += b; break;
                    case 3: v += (a + b) >> 1; break;
                    case 4:
                        int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - 2 * c);
                        v += pa <= pb && pa <= pc ? a : (pb <= pc ? b : c);
                        break;
                    default: return null;
                }
                px[cur + x] = (byte)v;
            }
            p += (int)stride;
        }

        var rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            long srow = y * stride;
            int drow = y * width * 3;
            for (int x = 0; x < width; x++)
            {
                long s = srow + (long)x * bpp;
                byte r, g, b;
                if (colorType == 2 || colorType == 6)
                {
                    r = px[s]; g = px[s + sample]; b = px[s + 2 * sample];
                }
                else if (colorType == 3)
                {
                    int idx = px[s] * 3;
                    if (idx + 2 >= palette!.Length) return null;
                    r = palette[idx]; g = palette[idx + 1]; b = palette[idx + 2];
                }
                else
                {
                    r = g = b = px[s];        // 灰度（带不带 alpha 都按灰度用）
                }
                int d = drow + x * 3;
                rgb[d] = r; rgb[d + 1] = g; rgb[d + 2] = b;
            }
        }
        return rgb;
    }

    private static int Be(byte[] b, int at)
        => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
}
