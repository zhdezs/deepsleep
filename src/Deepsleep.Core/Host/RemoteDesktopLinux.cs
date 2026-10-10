using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TrollWrangler.CoreHost;

/// <summary>
/// Linux 被控端的远程桌面：截屏 + 鼠标键盘注入。
///
/// Windows 那份直接用 GDI + SendInput，Linux 不能照搬：
///   · 截屏：X11 没有"随便读屏"的通用接口，Wayland 更必须过 compositor。
///     所以按顺序试 —— GNOME Shell 的 DBus 截图（GNOME 3.26+，Wayland 下最稳）→
///     xdg-desktop-portal 的 Screenshot（其它桌面 / 新版 GNOME）→ grim / spectacle /
///     gnome-screenshot / xfce4-screenshooter / ImageMagick import / scrot 这些命令行工具。
///     哪个先把 PNG 写出来就用哪个，之后一直用它。
///   · 输入：走 X11 的 XTest（libXtst）。X11 会话下全局有效；Wayland 会话下只能操作
///     XWayland 应用（GNOME/KDE 没有给普通程序留全局注入的口子），所以这种情况会回一句提示。
/// </summary>
internal static class RemoteDesktopLinux
{
    private static readonly object Gate = new();
    /// <summary>
    /// 临时截图放哪：优先 ~/Pictures（GNOME 41+ 的 Shell 截图 DBus 只肯往「图片」目录里写，
    /// 给它 /tmp 里的路径会被直接拒掉 —— Debian 13 的 GNOME 就是这么拦的）；
    /// 没有「图片」目录（或不是 GNOME）再退回 /tmp，别的截图工具不挑地方。
    /// </summary>
    private static readonly string Dir = PickDir();

    private static string PickDir()
    {
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 0)
            {
                string pics = Path.Combine(home, "Pictures");
                if (Directory.Exists(pics))
                {
                    string d = Path.Combine(pics, "deepsleep-rd");
                    Directory.CreateDirectory(d);
                    return d;
                }
            }
        }
        catch { }
        try
        {
            string t = Path.Combine(Path.GetTempPath(), "deepsleep-rd");
            Directory.CreateDirectory(t);
            return t;
        }
        catch { return Path.Combine(Path.GetTempPath(), "deepsleep-rd"); }
    }
    private static bool _probed;
    private static DateTime _probedAt = DateTime.MinValue;
    private static List<string> _order = new();
    private static string _tool = "";
    private static string _note = "";
    private static int _lastErr, _sw, _sh;

    public static int LastError => _lastErr;
    public static string Note { get { Probe(); return _note; } }

    public static bool Available
    {
        get { Probe(); return _tool.Length > 0; }
    }

    public static int ScreenWidth { get { Probe(); return _sw; } }
    public static int ScreenHeight { get { Probe(); return _sh; } }

    // ------------------------------------------------------------------ 探测

    private static bool IsWayland()
    {
        try
        {
            string t = (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "").ToLowerInvariant();
            if (t == "wayland") return true;
            if (t == "x11") return false;
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        }
        catch { return false; }
    }

    private static readonly string[] SearchDirs = { "/usr/local/bin", "/usr/bin", "/bin", "/usr/local/sbin", "/usr/sbin", "/sbin", "/snap/bin", "/usr/libexec" };

    private static bool Has(string exe)
    {
        try
        {
            foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
                if (File.Exists(Path.Combine(d, exe))) return true;
            foreach (string d in SearchDirs)
                if (File.Exists(Path.Combine(d, exe))) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 重新探测一次：装好截屏工具、或者换了桌面会话（X11 ↔ Wayland）之后，
    /// 刷新一下远控页就能用上，不用重启整个程序。15 秒内不重复探测（rd.html 一打开就会问
    /// /api/rd/info，别把 gdbus / 门户对话框反复拉起来）。
    /// </summary>
    public static void Reprope()
    {
        lock (Gate)
        {
            if ((DateTime.UtcNow - _probedAt).TotalSeconds < 15) return;
            _probed = false;
            _tool = "";
            _note = "";
            _order.Clear();
        }
        Probe();
    }

    private static void Probe()
    {
        lock (Gate)
        {
            if (_probed) return;
            _probed = true;
            _probedAt = DateTime.UtcNow;
            try
            {
                if (!TrollWrangler.Platform.IsLinux)
                {
                    _note = "远程桌面被控端目前只有 Windows / Linux 版可用";
                    return;
                }
                Directory.CreateDirectory(Dir);
                bool wayland = IsWayland();
                if (Has("gdbus")) _order.Add("gnome-dbus");
                if (Has("grim")) _order.Add("grim");
                if (Has("spectacle")) _order.Add("spectacle");
                if (Has("gnome-screenshot")) _order.Add("gnome-shot");
                if (Has("xfce4-screenshooter")) _order.Add("xfce");
                if (Has("import") || Has("magick")) _order.Add("import");
                if (Has("scrot")) _order.Add("scrot");
                if (Has("gdbus")) _order.Add("portal");

                // 真截一张来定：谁先成功就用谁（省得界面上报"可用"然后一片黑）。
                // 超时给 4 秒、最多试 6 个：探测是同步做的，不能让 /api/rd/info 卡太久。
                int tried = 0;
                foreach (string t in _order)
                {
                    if (tried++ >= 6) break;
                    if (!TryCapture(t, out _, out _, out _, 4000)) continue;
                    _tool = t;
                    break;
                }

                bool xtest = X11Ready();
                if (_tool.Length == 0)
                    _note = "这台机器没找到能用的截屏方式：装一个就行 —— sudo apt install gnome-screenshot" +
                            "（或 imagemagick、scrot）；装完刷新本页会自动重新检测";
                else if (wayland && !xtest)
                    _note = "Wayland 会话：画面可用；鼠标键盘只能操作 XWayland 应用";
                else if (wayland)
                    _note = "Wayland 会话：鼠标键盘只能操作 XWayland 应用；要完整远控，登录时选 “GNOME on Xorg”";
                else if (!xtest)
                    _note = "画面可用；这台机器上没有可用的 XTest（libXtst），鼠标键盘发不过去";
            }
            catch (Exception ex)
            {
                _note = "远程桌面初始化失败：" + ex.Message;
                _tool = "";
            }
        }
    }

    // ------------------------------------------------------------------ 截屏

    public static byte[] CapturePng(int maxWidth, out int outW, out int outH)
    {
        outW = outH = 0;
        Probe();
        if (_tool.Length == 0) return Array.Empty<byte>();

        byte[]? png = null;
        foreach (string t in Candidates())
        {
            if (TryCapture(t, out png, out int cw, out int ch, 12000))
            {
                _tool = t;
                break;
            }
        }
        if (png == null || png.Length == 0) return Array.Empty<byte>();

        byte[]? rgb = PngDecode.DecodeRgb(png, out int w, out int h);
        if (rgb == null) { _lastErr = -2; return Array.Empty<byte>(); }
        _sw = w; _sh = h;

        int dw = w, dh = h;
        if (maxWidth > 0 && w > maxWidth)
        {
            dw = maxWidth;
            dh = Math.Max(1, (int)Math.Round((double)h * maxWidth / w));
        }
        var rgbOut = new byte[dw * dh * 3];
        if (dw == w && dh == h)
        {
            Array.Copy(rgb, rgbOut, Math.Min(rgb.Length, rgbOut.Length));
        }
        else
        {
            // 抽点采样 + 5 位色量化（跟 Windows 那份一致：色数少 → PNG 压得狠 → 隧道省带宽）
            for (int y = 0; y < dh; y++)
            {
                int sy = (int)((long)y * h / dh);
                int srow = sy * w * 3, drow = y * dw * 3;
                for (int x = 0; x < dw; x++)
                {
                    int sp = srow + (int)((long)x * w / dw) * 3, d = drow + x * 3;
                    rgbOut[d] = (byte)(rgb[sp] & 0xF8);
                    rgbOut[d + 1] = (byte)(rgb[sp + 1] & 0xF8);
                    rgbOut[d + 2] = (byte)(rgb[sp + 2] & 0xF8);
                }
            }
        }
        outW = dw; outH = dh;
        return Png.EncodeRgb(rgbOut, dw, dh, dw * 3);
    }

    private static List<string> Candidates()
    {
        var list = new List<string>();
        if (_tool.Length > 0) list.Add(_tool);
        foreach (string t in _order) if (t != _tool) list.Add(t);
        return list;
    }

    private static bool TryCapture(string tool, out byte[]? png, out int w, out int h, int timeoutMs)
    {
        png = null; w = h = 0; _lastErr = 0;
        string file = Path.Combine(Dir, "shot.png");
        try { if (File.Exists(file)) File.Delete(file); } catch { }
        try
        {
            switch (tool)
            {
                case "gnome-dbus":
                    Run("gdbus", timeoutMs,
                        "call", "--session",
                        "--dest", "org.gnome.Shell.Screenshot",
                        "--object-path", "/org/gnome/Shell/Screenshot",
                        "--method", "org.gnome.Shell.Screenshot.Screenshot",
                        "false", "false", file);
                    break;
                case "portal":
                    PortalShot(file, timeoutMs);
                    break;
                case "grim":
                    Run("grim", timeoutMs, "-t", "png", file);
                    break;
                case "spectacle":
                    Run("spectacle", timeoutMs, "-b", "-n", "-o", file);
                    break;
                case "gnome-shot":
                    Run("gnome-screenshot", timeoutMs, "-f", file);
                    break;
                case "xfce":
                    Run("xfce4-screenshooter", timeoutMs, "-f", "-s", file);
                    break;
                case "import":
                    if (Has("import")) Run("import", timeoutMs, "-window", "root", file);
                    else Run("magick", timeoutMs, "import", "-window", "root", file);
                    break;
                case "scrot":
                    Run("scrot", timeoutMs, "-o", file);
                    break;
            }
        }
        catch (Exception ex) { _note = "截屏出错：" + ex.Message; }

        if (!File.Exists(file)) return false;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(file); } catch { return false; }
        try { File.Delete(file); } catch { }
        if (bytes.Length < 128) return false;
        if (PngDecode.DecodeRgb(bytes, out w, out h) == null) return false;
        png = bytes;
        return true;
    }

    /// <summary>
    /// xdg-desktop-portal 的截图：结果是异步的，方法只回一个 handle，
    /// 真正的文件地址在 org.freedesktop.portal.Request.Response 信号里 —— 所以先跑一个
    /// gdbus monitor，再发请求，然后在监控输出里等那个信号。
    /// </summary>
    private static bool PortalShot(string file, int timeoutMs)
    {
        Process? mon = null;
        try
        {
            var psi = new ProcessStartInfo("gdbus")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("monitor"); psi.ArgumentList.Add("--session");
            psi.ArgumentList.Add("--dest"); psi.ArgumentList.Add("org.freedesktop.portal.Desktop");
            mon = Process.Start(psi);
            if (mon == null) return false;

            var lines = new List<string>();
            var _ = Task.Run(() =>
            {
                try { string? l; while ((l = mon.StandardOutput.ReadLine()) != null) { lock (lines) lines.Add(l); } }
                catch { }
            });

            Run("gdbus", 8000,
                "call", "--session",
                "--dest", "org.freedesktop.portal.Desktop",
                "--object-path", "/org/freedesktop/portal/desktop",
                "--method", "org.freedesktop.portal.Screenshot.Screenshot",
                "", "{'interactive': <false>, 'modal': <false>}");

            string? uri = null;
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs && uri == null)
            {
                lock (lines)
                {
                    foreach (string l in lines)
                    {
                        if (!l.Contains("Response")) continue;
                        var st = Regex.Match(l, @"uint32\s+(\d+)");
                        var u = Regex.Match(l, @"'uri':\s*<'([^']+)'>");
                        if (u.Success && (!st.Success || st.Groups[1].Value == "0")) { uri = u.Groups[1].Value; break; }
                        if (st.Success && st.Groups[1].Value != "0") return false;
                    }
                }
                if (uri == null) Thread.Sleep(150);
            }
            if (uri == null) return false;
            string path = Uri.UnescapeDataString(new Uri(uri).LocalPath);
            if (!File.Exists(path)) return false;
            File.Copy(path, file, true);
            try { File.Delete(path); } catch { }     // 清掉 portal 落在“图片”目录里的那份
            return true;
        }
        catch { return false; }
        finally { try { mon?.Kill(true); } catch { } }
    }

    private static string Run(string exe, int timeoutMs, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return "";
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return "";
            }
            return (so.Result + " " + se.Result).Trim();
        }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ 输入（X11 / XTest）

    private const string LibX11 = "libX11.so.6";
    private const string LibXtst = "libXtst.so.6";

    [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(string? name);
    [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr dpy);
    [DllImport(LibX11)] private static extern int XFlush(IntPtr dpy);
    [DllImport(LibX11)] private static extern int XDefaultScreen(IntPtr dpy);
    [DllImport(LibX11)] private static extern int XDisplayWidth(IntPtr dpy, int screen);
    [DllImport(LibX11)] private static extern int XDisplayHeight(IntPtr dpy, int screen);
    [DllImport(LibX11)] private static extern ulong XStringToKeysym(string name);
    [DllImport(LibX11)] private static extern byte XKeysymToKeycode(IntPtr dpy, ulong keysym);
    [DllImport(LibXtst)] private static extern int XTestFakeMotionEvent(IntPtr dpy, int screen, int x, int y, ulong delay);
    [DllImport(LibXtst)] private static extern int XTestFakeButtonEvent(IntPtr dpy, uint button, int press, ulong delay);
    [DllImport(LibXtst)] private static extern int XTestFakeKeyEvent(IntPtr dpy, uint keycode, int press, ulong delay);

    private static IntPtr _dpy;
    private static bool _dpyTried;

    private static bool X11Ready()
    {
        try
        {
            if (!NativeLibrary.TryLoad(LibX11, out _)) return false;
            if (!NativeLibrary.TryLoad(LibXtst, out _)) return false;
            return Dpy() != IntPtr.Zero;
        }
        catch { return false; }
    }

    private static IntPtr Dpy()
    {
        if (!_dpyTried)
        {
            _dpyTried = true;
            try { _dpy = XOpenDisplay(null); } catch { _dpy = IntPtr.Zero; }
            if (_dpy != IntPtr.Zero)
            {
                try
                {
                    if (_sw <= 0) _sw = XDisplayWidth(_dpy, XDefaultScreen(_dpy));
                    if (_sh <= 0) _sh = XDisplayHeight(_dpy, XDefaultScreen(_dpy));
                }
                catch { }
            }
        }
        return _dpy;
    }

    public static void MouseMoveNorm(double nx, double ny)
    {
        var d = Dpy();
        if (d == IntPtr.Zero) return;
        try
        {
            int screen = XDefaultScreen(d);
            int w = Math.Max(1, XDisplayWidth(d, screen)), h = Math.Max(1, XDisplayHeight(d, screen));
            int x = (int)Math.Round(Math.Clamp(nx, 0, 1) * (w - 1));
            int y = (int)Math.Round(Math.Clamp(ny, 0, 1) * (h - 1));
            XTestFakeMotionEvent(d, screen, x, y, 0);
            XFlush(d);
        }
        catch { }
    }

    public static void MouseButton(string button, bool down)
    {
        var d = Dpy();
        if (d == IntPtr.Zero) return;
        try
        {
            uint b = button switch { "right" => 3u, "middle" => 2u, _ => 1u };
            XTestFakeButtonEvent(d, b, down ? 1 : 0, 0);
            XFlush(d);
        }
        catch { }
    }

    public static void Wheel(int delta)
    {
        var d = Dpy();
        if (d == IntPtr.Zero) return;
        try
        {
            uint b = delta > 0 ? 4u : 5u;      // X11 里滚轮就是 4/5 号键
            XTestFakeButtonEvent(d, b, 1, 0);
            XTestFakeButtonEvent(d, b, 0, 0);
            XFlush(d);
        }
        catch { }
    }

    public static void Key(int vk, bool down)
    {
        var d = Dpy();
        if (d == IntPtr.Zero) return;
        try
        {
            byte code = Keycode(d, vk);
            if (code == 0) return;
            XTestFakeKeyEvent(d, code, down ? 1 : 0, 0);
            XFlush(d);
        }
        catch { }
    }

    private static readonly Dictionary<int, byte> KeyCache = new();

    private static byte Keycode(IntPtr d, int vk)
    {
        lock (KeyCache)
        {
            if (KeyCache.TryGetValue(vk, out byte cached)) return cached;
            byte code = 0;
            string? name = VkToKeysymName(vk);
            if (name != null)
            {
                try
                {
                    ulong sym = XStringToKeysym(name);
                    if (sym != 0) code = XKeysymToKeycode(d, sym);
                }
                catch { }
            }
            KeyCache[vk] = code;
            return code;
        }
    }

    /// <summary>
    /// 网页发过来的是 Windows 虚拟键码（event.keyCode），这里换成 X11 的 keysym 名字，
    /// 再让 X 服务器查键位表 —— 比自己维护一张 keycode 表稳（不同键盘布局都对得上）。
    /// </summary>
    private static string? VkToKeysymName(int vk)
    {
        if (VkNames.TryGetValue(vk, out string? n)) return n;
        if (vk >= 0x41 && vk <= 0x5A) return ((char)('a' + (vk - 0x41))).ToString();   // A-Z
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();                     // 0-9
        if (vk >= 0x60 && vk <= 0x69) return "KP_" + (char)('0' + (vk - 0x60));         // 小键盘
        if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x6F);                         // F1-F12
        return null;
    }

    private static readonly Dictionary<int, string> VkNames = new()
    {
        [0x08] = "BackSpace", [0x09] = "Tab", [0x0D] = "Return", [0x13] = "Pause",
        [0x14] = "Caps_Lock", [0x1B] = "Escape", [0x20] = "space",
        [0x21] = "Prior", [0x22] = "Next", [0x23] = "End", [0x24] = "Home",
        [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
        [0x2C] = "Print", [0x2D] = "Insert", [0x2E] = "Delete",
        [0x5B] = "Super_L", [0x5C] = "Super_R", [0x5D] = "Menu",
        [0x90] = "Num_Lock", [0x91] = "Scroll_Lock",
        [0x10] = "Shift_L", [0x11] = "Control_L", [0x12] = "Alt_L",
        [0xA0] = "Shift_L", [0xA1] = "Shift_R", [0xA2] = "Control_L", [0xA3] = "Control_R",
        [0xA4] = "Alt_L", [0xA5] = "Alt_R",
        [0xBA] = "semicolon", [0xBB] = "equal", [0xBC] = "comma", [0xBD] = "minus",
        [0xBE] = "period", [0xBF] = "slash", [0xC0] = "grave",
        [0xDB] = "bracketleft", [0xDC] = "backslash", [0xDD] = "bracketright", [0xDE] = "apostrophe",
        [0x6A] = "KP_Multiply", [0x6B] = "KP_Add", [0x6C] = "KP_Separator",
        [0x6D] = "KP_Subtract", [0x6E] = "KP_Decimal", [0x6F] = "KP_Divide",
    };
}
