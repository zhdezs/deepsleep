using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler;

/// <summary>本地渲染：用本机已装的 Chrome / Edge / Chromium 无头模式把网页（或一段 HTML）画成 PNG。</summary>
public static class PageRenderer
{
    private static string? _cached;

    /// <summary>找浏览器：设置里的覆盖 → 常见安装目录 → PATH。</summary>
    public static string? FindBrowser(string? hint = null)
    {
        if (!string.IsNullOrWhiteSpace(hint) && File.Exists(hint)) return hint;
        if (!string.IsNullOrEmpty(_cached) && File.Exists(_cached)) return _cached;
        var cands = new List<string>();
        try
        {
            if (Platform.IsWindows)
            {
                foreach (var root in new[]
                         {
                             Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files",
                             Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)",
                             Environment.GetEnvironmentVariable("LocalAppData") ?? "",
                         })
                {
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    cands.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
                    cands.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
                    cands.Add(Path.Combine(root, "Chromium", "Application", "chrome.exe"));
                }
            }
            else if (Platform.IsMacOS)
            {
                cands.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
                cands.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
                cands.Add("/Applications/Chromium.app/Contents/MacOS/Chromium");
            }
            else
            {
                cands.AddRange(new[]
                {
                    "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/usr/bin/chromium",
                    "/usr/bin/chromium-browser", "/snap/bin/chromium", "/usr/bin/microsoft-edge",
                });
            }
        }
        catch { }
        foreach (var c in cands.Distinct())
        {
            try { if (File.Exists(c)) { _cached = c; return c; } } catch { }
        }
        // 最后试 PATH
        try
        {
            foreach (var name in new[] { "chrome", "google-chrome", "chromium", "msedge" })
            {
                var psi = new ProcessStartInfo(Platform.IsWindows ? "where" : "which", name)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p == null) continue;
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                string first = outp.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
                if (first.Length > 0 && File.Exists(first)) { _cached = first; return first; }
            }
        }
        catch { }
        return null;
    }

    public sealed class Result
    {
        public bool Ok { get; init; }
        /// <summary>截图文件（Ok 为 true 时一定存在）。</summary>
        public string Path { get; init; } = "";
        public int Width { get; init; }
        public int Height { get; init; }
        public string Error { get; init; } = "";
    }

    /// <summary>把网址或 HTML 渲染成 PNG。非空时渲染这段 HTML（页面自渲染）， 否则渲染 。</summary>
    public static async Task<Result> ShotAsync(string url, string? html, string outPath,
                                               int width = 1280, int height = 1600,
                                               string? browserHint = null, CancellationToken ct = default)
    {
        string? browser = FindBrowser(browserHint);
        if (browser == null)
            return new Result { Error = "本机没找到 Chrome / Edge / Chromium，无法本地渲染。装一个浏览器，或在设置里指定它的可执行文件路径。" };

        string? tempHtml = null;
        try
        {
            width = Math.Clamp(width, 320, 2400);
            height = Math.Clamp(height, 240, 6000);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
            if (!string.IsNullOrWhiteSpace(html))
            {
                string tmpDir = Path.Combine(Path.GetTempPath(), "deepsleep-render");
                Directory.CreateDirectory(tmpDir);
                tempHtml = Path.Combine(tmpDir, "r" + Guid.NewGuid().ToString("N")[..8] + ".html");
                File.WriteAllText(tempHtml, html, new UTF8Encoding(false));
                url = new Uri(tempHtml).AbsoluteUri;
            }
            if (string.IsNullOrWhiteSpace(url))
                return new Result { Error = "没给要渲染的内容（网址或 HTML 二者之一）。" };

            // --headless=new 的截图是「视口大小」：把高度给足就是近似整页，页面矮也不会留大片空白
            // （Chrome 会按内容裁剪到实际高度）。
            string args =
                "--headless=new --disable-gpu --no-sandbox --hide-scrollbars --mute-audio " +
                "--virtual-time-budget=8000 --run-all-compositor-stages-before-draw " +
                $"--window-size={width},{height} --screenshot=\"{outPath}\" \"{url}\"";
            var psi = new ProcessStartInfo(browser, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return new Result { Error = "浏览器进程启动失败。" };
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            {
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            string err = await stderrTask.ConfigureAwait(false);
            if (!File.Exists(outPath))
                return new Result { Error = "浏览器没有产出截图：" + FirstLine(err) };
            var fi = new FileInfo(outPath);
            if (fi.Length < 1024)
                return new Result { Error = "截图几乎是空的（" + fi.Length + "字节），页面可能没加载出来。" };
            return new Result { Ok = true, Path = outPath, Width = width, Height = height };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new Result { Error = "渲染已停止。" };
        }
        catch (Exception ex)
        {
            return new Result { Error = ex.Message };
        }
        finally
        {
            if (tempHtml != null) { try { File.Delete(tempHtml); } catch { } }
        }
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "（没有错误输出）";
        var line = s.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
