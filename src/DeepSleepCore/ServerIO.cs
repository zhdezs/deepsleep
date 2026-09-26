using System.Net.Sockets;
using System.Text;

namespace TrollWrangler.CoreHost;

/// <summary>请求解析 + 静态文件。</summary>
internal static partial class Program
{
    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".md"] = "text/plain; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".pdf"] = "application/pdf",
        [".mp3"] = "audio/mpeg",
        [".mp4"] = "video/mp4",
        [".woff2"] = "font/woff2",
        [".zip"] = "application/zip",
    };

    private static async Task<Req?> ReadRequestAsync(NetworkStream s)
    {
        var ms = new MemoryStream();
        byte[] buf = new byte[16384];
        int headEnd = -1;
        while (ms.Length < 1024 * 1024)
        {
            int n = await s.ReadAsync(buf);
            if (n <= 0) return null;
            ms.Write(buf, 0, n);
            headEnd = FindHeadEnd(ms);
            if (headEnd >= 0) break;
        }
        if (headEnd < 0) return null;

        byte[] all = ms.GetBuffer();
        string head = Encoding.UTF8.GetString(all, 0, headEnd);
        string[] lines = head.Split("\r\n");
        string[] first = lines[0].Split(' ');
        if (first.Length < 2) return null;

        var req = new Req { Method = first[0].ToUpperInvariant() };
        string target = first[1];
        int q = target.IndexOf('?');
        string rawPath = q < 0 ? target : target[..q];
        req.Query = q < 0 ? "" : target[q..];
        try { req.Path = Uri.UnescapeDataString(rawPath); } catch { req.Path = rawPath; }

        for (int i = 1; i < lines.Length; i++)
        {
            int c = lines[i].IndexOf(':');
            if (c > 0) req.Headers[lines[i][..c].Trim()] = lines[i][(c + 1)..].Trim();
        }

        int want = int.TryParse(req.Header("Content-Length"), out int cl) && cl > 0 ? cl : 0;
        if (want > 96 * 1024 * 1024) return null;
        var body = new byte[want];
        int have = Math.Min((int)ms.Length - headEnd - 4, want);
        if (have > 0) Buffer.BlockCopy(all, headEnd + 4, body, 0, have);
        while (have < want)
        {
            int n = await s.ReadAsync(body.AsMemory(have, want - have));
            if (n <= 0) break;
            have += n;
        }
        req.Body = body;
        return req;
    }

    private static int FindHeadEnd(MemoryStream ms)
    {
        byte[] a = ms.GetBuffer();
        int len = (int)ms.Length;
        for (int i = 0; i + 3 < len; i++)
            if (a[i] == 13 && a[i + 1] == 10 && a[i + 2] == 13 && a[i + 3] == 10) return i;
        return -1;
    }

    private static async Task ServeFileAsync(Res res, string baseDir, string rel)
    {
        try
        {
            string rootFull = Path.GetFullPath(baseDir);
            string full = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
            bool inside = full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
                          || full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!inside)
            {
                await res.SendTextAsync("forbidden", "text/plain; charset=utf-8", 403);
                return;
            }
            if (Directory.Exists(full)) full = Path.Combine(full, "index.html");
            if (!File.Exists(full))
            {
                await res.SendTextAsync("404 " + rel, "text/plain; charset=utf-8", 404);
                return;
            }
            string type = Mime.TryGetValue(Path.GetExtension(full), out var t) ? t : "application/octet-stream";
            byte[] data = await File.ReadAllBytesAsync(full);
            res.Headers["Cache-Control"] = "no-cache";
            await res.SendAsync(data, type);
        }
        catch (Exception ex)
        {
            await res.SendTextAsync(ex.Message, "text/plain; charset=utf-8", 500);
        }
    }
}
