using System.Runtime.InteropServices;

namespace TrollWrangler.CoreHost;

/// <summary>
/// 内置远程桌面（被控端）：截屏（GDI）→ PNG，鼠标键盘注入（SendInput）。
/// 不引第三方库、不拍屏服务；Windows 全平台可用，其它系统暂时返回不可用（不影响别的功能）。
/// 坐标一律用「归一化 0~1」在网上传，服务端换算成屏幕像素，避免缩放/DPI 两边对不齐。
/// </summary>
public static class RemoteDesktop
{
#if WINDOWS
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] inputs, int cbSize);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr hDesk);
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private static bool _dpiSet;
    private static readonly object Gate = new();

    [ThreadStatic] private static bool _deskTried;

    /// <summary>
    /// 个别环境（沙箱 / 被放到独立桌面的进程）当前线程不在交互桌面上，这时 BitBlt 会失败（错误码 6）。
    /// 切到输入桌面再试即可 —— 只影响当前线程，正常桌面下压根不会走到这里。
    /// </summary>
    private static void TrySwitchToInputDesktop()
    {
        if (_deskTried) return;
        _deskTried = true;
        try
        {
            IntPtr desk = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
            if (desk != IntPtr.Zero) SetThreadDesktop(desk);
        }
        catch { }
    }

    /// <summary>上一次截屏失败的原生错误码（0 表示没失败过）。</summary>
    public static int LastError { get; private set; }

    private static void EnsureDpi()
    {
        if (_dpiSet) return;
        _dpiSet = true;
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }   // PER_MONITOR_AWARE_V2
    }

    public static bool Available { get { EnsureDpi(); return true; } }

    public static int ScreenWidth { get { EnsureDpi(); return GetSystemMetrics(SM_CXSCREEN); } }
    public static int ScreenHeight { get { EnsureDpi(); return GetSystemMetrics(SM_CYSCREEN); } }

    /// <summary>截一帧。maxWidth &gt; 0 时等比缩到该宽度（省带宽）。</summary>
    public static byte[] CapturePng(int maxWidth, out int outW, out int outH)
    {
        outW = outH = 0;
        LastError = 0;
        try
        {
            EnsureDpi();
            lock (Gate)
            {
                int sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);
                if (sw <= 0 || sh <= 0) return Array.Empty<byte>();

                IntPtr screenDc = GetDC(IntPtr.Zero), memDc = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    memDc = CreateCompatibleDC(screenDc);
                    bmp = CreateCompatibleBitmap(screenDc, sw, sh);
                    old = SelectObject(memDc, bmp);
                    // CAPTUREBLT 能抓到分层窗口，但个别受限环境会被拒，退一步用纯 SRCCOPY。
                    bool blt = BitBlt(memDc, 0, 0, sw, sh, screenDc, 0, 0, SRCCOPY | CAPTUREBLT) ||
                               BitBlt(memDc, 0, 0, sw, sh, screenDc, 0, 0, SRCCOPY);
                    if (!blt)
                    {
                        // 当前线程不在交互桌面上（沙箱 / 服务 / 独立桌面）—— 切过去再来一次
                        TrySwitchToInputDesktop();
                        blt = BitBlt(memDc, 0, 0, sw, sh, screenDc, 0, 0, SRCCOPY | CAPTUREBLT) ||
                              BitBlt(memDc, 0, 0, sw, sh, screenDc, 0, 0, SRCCOPY);
                    }
                    if (!blt)
                    {
                        LastError = Marshal.GetLastWin32Error();
                        return Array.Empty<byte>();
                    }

                    var bi = new BITMAPINFOHEADER
                    {
                        biSize = 40, biWidth = sw, biHeight = -sh, biPlanes = 1, biBitCount = 32,
                        biCompression = 0,
                    };
                    byte[] buf = new byte[sw * sh * 4];
                    if (GetDIBits(memDc, bmp, 0, (uint)sh, buf, ref bi, 0) == 0)
                    {
                        LastError = Marshal.GetLastWin32Error();
                        return Array.Empty<byte>();
                    }

                    int dw = sw, dh = sh;
                    if (maxWidth > 0 && sw > maxWidth)
                    {
                        dw = maxWidth;
                        dh = Math.Max(1, (int)Math.Round(sh * (double)maxWidth / sw));
                    }
                    byte[] rgb = new byte[dw * dh * 3];
                    for (int y = 0; y < dh; y++)
                    {
                        int sy = dw == sw ? y : (int)((long)y * sh / dh);
                        int srow = sy * sw * 4;
                        int drow = y * dw * 3;
                        for (int x = 0; x < dw; x++)
                        {
                            int sx = dw == sw ? x : (int)((long)x * sw / dw);
                            int s = srow + (sx << 2);
                            int d = drow + x * 3;
                            rgb[d] = buf[s + 2];
                            rgb[d + 1] = buf[s + 1];
                            rgb[d + 2] = buf[s];
                        }
                    }
                    outW = dw;
                    outH = dh;
                    return Png.EncodeRgb(rgb, dw, dh, dw * 3);
                }
                finally
                {
                    try { if (old != IntPtr.Zero) SelectObject(memDc, old); } catch { }
                    try { if (bmp != IntPtr.Zero) DeleteObject(bmp); } catch { }
                    try { if (memDc != IntPtr.Zero) DeleteDC(memDc); } catch { }
                    try { if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc); } catch { }
                }
            }
        }
        catch { return Array.Empty<byte>(); }
    }

    private static void Send(params INPUT[] list)
    {
        try { if (list.Length > 0) SendInput((uint)list.Length, list, Marshal.SizeOf<INPUT>()); } catch { }
    }

    public static void MouseMoveNorm(double nx, double ny)
    {
        int vx = (int)Math.Round(Math.Clamp(nx, 0, 1) * 65535);
        int vy = (int)Math.Round(Math.Clamp(ny, 0, 1) * 65535);
        Send(new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT { dx = vx, dy = vy, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE } },
        });
    }

    public static void MouseButton(string button, bool down)
    {
        uint f = button switch
        {
            "right" => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            "middle" => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
        };
        Send(new INPUT { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = f } } });
    }

    public static void Wheel(int delta)
    {
        Send(new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = MOUSEEVENTF_WHEEL } },
        });
    }

    /// <summary>按虚拟键码按键（网页传 event.keyCode，正好是 VK）。</summary>
    public static void Key(int vk, bool down)
    {
        if (vk <= 0 || vk > 0xFF) return;
        Send(new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = down ? 0u : KEYEVENTF_KEYUP } },
        });
    }
#else
    public static int LastError => 0;
    public static bool Available => false;
    public static int ScreenWidth => 0;
    public static int ScreenHeight => 0;
    public static byte[] CapturePng(int maxWidth, out int outW, out int outH) { outW = outH = 0; return Array.Empty<byte>(); }
    public static void MouseMoveNorm(double nx, double ny) { }
    public static void MouseButton(string button, bool down) { }
    public static void Wheel(int delta) { }
    public static void Key(int vk, bool down) { }
#endif
}
