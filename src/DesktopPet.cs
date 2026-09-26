/// <summary>
/// deepsleep 桌面桌宠：Win32 分层窗口（WS_EX_LAYERED + UpdateLayeredWindow），逐像素 alpha，
/// 完全不用框架窗口装饰 —— 桌面上只有鲸鱼本体，四周是真透明。
/// 图像由构建期转好的 pet.raw（8 字节头 + 预乘 BGRA）直接喂进去，不依赖任何图像库。
///
/// 行为：
///   · 单击 → 打开桌宠自己的交互窗口（小聊天框）
///   · 按住拖动 → 移动位置（松手不算点击，位置记进配置）
///   · 右键 → 菜单（聊天 / 打开主界面 / 隐藏 / 退出）
///   · 显示尺寸 = 原图的 45%（PetScale），缩小用面积平均，预乘 alpha 求平均不会出黑边
/// </summary>
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TrollWrangler;

public sealed class DesktopPet : IDisposable
{
    /// <summary>桌宠显示尺寸 = 原图的 45%。</summary>
    public const double PetScale = 0.45;
    /// <summary>移动超过这么多像素才算拖拽，否则当单击。</summary>
    private const int DragThreshold = 4;

    private const int WS_POPUP = unchecked((int)0x80000000), WS_VISIBLE = 0x10000000;
    private const int WS_EX_LAYERED = 0x00080000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TOPMOST = 0x00000008;
    private const int ULW_ALPHA = 0x02, AC_SRC_OVER = 0x00, AC_SRC_ALPHA = 0x01;
    private const int WM_LBUTTONDOWN = 0x0201, WM_MOUSEMOVE = 0x0200, WM_LBUTTONUP = 0x0202,
                      WM_RBUTTONUP = 0x0205, WM_TIMER = 0x0113, WM_COMMAND = 0x0111,
                      WM_NCHITTEST = 0x0084, WM_MOUSEACTIVATE = 0x0021;
    private const int HTCLIENT = 1, HTTRANSPARENT = -1, MA_NOACTIVATE = 3;
    /// <summary>命中判定：alpha 低于这个值的像素算「空白处」，鼠标直接穿过去（别让透明方框挡住桌面）。</summary>
    private const byte HitAlphaMin = 24;
    private const int SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    private const uint MF_STRING = 0x0, TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int ex, string cls, string title, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CallWindowProcW(IntPtr p, IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint el, IntPtr p);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr h, IntPtr id);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr m, uint f, uint id, string t);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr m, uint f, int x, int y, int r, IntPtr h, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr m);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr d);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr d);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr d, ref BITMAPINFOHEADER bmi, uint u, out IntPtr bits, IntPtr s, uint o);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr d, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr d);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT pptDst,
        ref SIZE size, IntPtr src, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);

    private readonly Action _onChat;              // 单击：打开桌宠交互窗口
    private readonly Action _onOpenMain;          // 右键菜单：打开主界面
    private readonly Action<bool>? _onVisible;    // 显示 / 隐藏状态变化
    private readonly Action<int, int>? _onMoved;  // 拖拽结束：把位置记进配置
    private IntPtr _hwnd = IntPtr.Zero, _prevProc = IntPtr.Zero, _memDC = IntPtr.Zero, _bmp = IntPtr.Zero;
    private WndProcDelegate? _proc;
    private int _w, _h, _x, _y, _phase;
    /// <summary>缩小后每个像素的 alpha（命中判定用；0/低 = 该点鼠标穿透）。</summary>
    private byte[] _alpha = Array.Empty<byte>();
    private bool _pressed, _dragging;
    private int _downX, _downY;
    private POINT _grab;

    private delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);

    /// <param name="rawPath">pet.raw 路径（8 字节头 + 预乘 BGRA）。</param>
    /// <param name="onChat">单击桌宠：打开桌宠自己的交互窗口。</param>
    /// <param name="onOpenMain">右键菜单里的「打开主界面」。</param>
    /// <param name="onVisible">显示 / 隐藏状态变化（隐藏时主程序记进配置，下次启动不再自己冒出来）。</param>
    /// <param name="onMoved">拖拽结束后回调新坐标（主程序落盘）。</param>
    /// <param name="startX">上次拖到的坐标；int.MinValue 表示用默认右下角。</param>
    public DesktopPet(string rawPath, Action onChat, Action onOpenMain, Action<bool>? onVisible = null,
                      Action<int, int>? onMoved = null, int startX = int.MinValue, int startY = int.MinValue)
    {
        _onChat = onChat;
        _onOpenMain = onOpenMain;
        _onVisible = onVisible;
        _onMoved = onMoved;

        byte[] blob = File.ReadAllBytes(rawPath);
        int srcW = BitConverter.ToInt32(blob, 0);
        int srcH = BitConverter.ToInt32(blob, 4);
        // 缩小到原图的 45%：按面积平均，预乘 alpha 直接平均就是正确结果
        _w = Math.Max(1, (int)Math.Round(srcW * PetScale));
        _h = Math.Max(1, (int)Math.Round(srcH * PetScale));
        byte[] pixels = Downscale(blob, srcW, srcH, _w, _h);
        _alpha = new byte[_w * _h];
        for (int i = 0; i < _alpha.Length; i++) _alpha[i] = pixels[i * 4 + 3];

        if (startX == int.MinValue || startY == int.MinValue)
        {
            _x = GetSystemMetrics(0) - _w - 40;
            _y = GetSystemMetrics(1) - _h - 90;
        }
        else
        {
            _x = startX;
            _y = startY;
            ClampToScreen();
        }

        // 必须带 WS_VISIBLE：光靠 UpdateLayeredWindow 不会让窗口显示出来（之前漏了，桌宠一直没露面）
        _hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST, "STATIC", "deepsleep 桌宠",
            WS_POPUP | WS_VISIBLE, _x, _y, _w, _h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("创建桌宠窗口失败");

        IntPtr screen = GetDC(IntPtr.Zero);
        _memDC = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = _w, biHeight = -_h,
            biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        _bmp = CreateDIBSection(screen, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, screen);
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        SelectObject(_memDC, _bmp);
        Paint();

        _proc = Hook;
        _prevProc = SetWindowLongPtrW(_hwnd, -4, Marshal.GetFunctionPointerForDelegate(_proc));
        SetTimer(_hwnd, 1, 50, IntPtr.Zero);
    }

    /// <summary>把预乘 BGRA 的像素按面积平均缩放（box filter）；预乘数据求平均不会产生黑边。</summary>
    private static byte[] Downscale(byte[] blob, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        if (srcW <= 0 || srcH <= 0) return dst;
        for (int y = 0; y < dstH; y++)
        {
            int sy0 = (int)((long)y * srcH / dstH);
            int sy1 = (int)((long)(y + 1) * srcH / dstH);
            if (sy1 <= sy0) sy1 = sy0 + 1;
            for (int x = 0; x < dstW; x++)
            {
                int sx0 = (int)((long)x * srcW / dstW);
                int sx1 = (int)((long)(x + 1) * srcW / dstW);
                if (sx1 <= sx0) sx1 = sx0 + 1;
                int b = 0, g = 0, r = 0, a = 0, n = 0;
                for (int sy = sy0; sy < sy1; sy++)
                {
                    int row = sy * srcW;
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        int o = (row + sx) * 4 + 8;      // 跳过 8 字节头
                        if (o + 3 >= blob.Length) continue;
                        b += blob[o]; g += blob[o + 1]; r += blob[o + 2]; a += blob[o + 3];
                        n++;
                    }
                }
                if (n == 0) n = 1;
                int d = (y * dstW + x) * 4;
                dst[d] = (byte)(b / n); dst[d + 1] = (byte)(g / n);
                dst[d + 2] = (byte)(r / n); dst[d + 3] = (byte)(a / n);
            }
        }
        return dst;
    }

    /// <summary>屏幕坐标 (sx,sy) 这一点是不是落在鲸鱼身上（按缩小后的 alpha 判定）。</summary>
    private bool AlphaHit(IntPtr h, int sx, int sy)
    {
        if (_alpha.Length == 0 || !GetWindowRect(h, out RECT r)) return true;
        int x = sx - r.Left, y = sy - r.Top;
        if (x < 0 || y < 0 || x >= _w || y >= _h) return false;
        return _alpha[y * _w + x] >= HitAlphaMin;
    }

    private void ClampToScreen()
    {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        if (_x > sw - 40) _x = sw - 40;
        if (_y > sh - 40) _y = sh - 40;
        if (_x < 40 - _w) _x = 40 - _w;          // 至少留一点在屏幕里，别拖丢了
        if (_y < 0) _y = 0;
    }

    private IntPtr Hook(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        switch (m)
        {
            // 【桌宠无法交互的真根因】窗口是照系统的 "STATIC" 类建的，静态控件的窗口过程对
            // WM_NCHITTEST 一律回 HTTRANSPARENT（静态控件天生「鼠标穿透」）→ 鲸鱼看得见、动画也在动，
            // 但单击 / 拖拽 / 右键的鼠标消息全被透给了下面的桌面，桌宠等于一块摆设。
            // 现在自己应答：鲸鱼身上（alpha 够高）回 HTCLIENT，四周真透明的区域回 HTTRANSPARENT，
            // 既点得到，又不会拿那个透明方框去挡桌面的点击。
            case WM_NCHITTEST:
            {
                long lp = l.ToInt64();
                int sx = (short)(lp & 0xFFFF), sy = (short)((lp >> 16) & 0xFFFF);
                return (IntPtr)(AlphaHit(h, sx, sy) ? HTCLIENT : HTTRANSPARENT);
            }

            // 点桌宠不抢焦点：不然点一下鲸鱼，用户正在打字的窗口就丢了焦点
            case WM_MOUSEACTIVATE:
                return (IntPtr)MA_NOACTIVATE;

            case WM_LBUTTONDOWN:
                _pressed = true; _dragging = false;
                GetCursorPos(out _grab);
                _grab.X -= _x; _grab.Y -= _y;        // 抓取点相对窗口左上角的偏移
                _downX = _grab.X + _x; _downY = _grab.Y + _y;
                SetCapture(h);                        // 拖到窗口外面也要能收到移动消息
                return IntPtr.Zero;

            case WM_MOUSEMOVE when _pressed:
                GetCursorPos(out POINT p);
                if (!_dragging &&
                    (Math.Abs(p.X - _downX) > DragThreshold || Math.Abs(p.Y - _downY) > DragThreshold))
                    _dragging = true;                 // 超过阈值才算拖拽，手抖不算
                if (_dragging)
                {
                    _x = p.X - _grab.X; _y = p.Y - _grab.Y;
                    SetWindowPos(h, IntPtr.Zero, _x, _y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                }
                return IntPtr.Zero;

            case WM_LBUTTONUP when _pressed:
                _pressed = false; ReleaseCapture();
                if (_dragging)
                {
                    _dragging = false;
                    ClampToScreen();
                    SetWindowPos(h, IntPtr.Zero, _x, _y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                    _onMoved?.Invoke(_x, _y);         // 拖完记住位置，下次启动还在那儿
                }
                else
                {
                    _onChat();                        // 单击（没拖）→ 打开桌宠交互窗口
                }
                return IntPtr.Zero;

            case WM_RBUTTONUP:
                ShowMenu(h);
                return IntPtr.Zero;

            case WM_TIMER:
                _phase = (_phase + 1) % 60;
                int off = (int)Math.Round(Math.Sin(_phase / 60.0 * Math.PI * 2) * 3);
                SetWindowPos(h, IntPtr.Zero, _x, _y + off, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                return IntPtr.Zero;
        }
        return CallWindowProcW(_prevProc, h, m, w, l);
    }

    private void ShowMenu(IntPtr h)
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, 4, "💬 跟桌宠聊天");
        AppendMenuW(menu, MF_STRING, 1, "打开主界面");
        AppendMenuW(menu, MF_STRING, 2, "隐藏桌宠（在 ⚙ 设置里可重新显示）");
        AppendMenuW(menu, MF_STRING, 9, "退出 deepsleep");
        GetCursorPos(out POINT p);
        int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, 0, h, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd == 4) _onChat();
        else if (cmd == 1) _onOpenMain();
        else if (cmd == 2) { Hide(); _onVisible?.Invoke(false); }
        else if (cmd == 9) App.ExitApp();
    }

    private void Paint()
    {
        var dst = new POINT { X = _x, Y = _y };
        var size = new SIZE { cx = _w, cy = _h };
        var src = new POINT { X = 0, Y = 0 };
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, AlphaFormat = AC_SRC_ALPHA, SourceConstantAlpha = 255 };
        IntPtr screen = GetDC(IntPtr.Zero);
        UpdateLayeredWindow(_hwnd, screen, ref dst, ref size, _memDC, ref src, 0, ref blend, ULW_ALPHA);
        ReleaseDC(IntPtr.Zero, screen);
    }

    public void Move(int x, int y)
    {
        _x = x; _y = y;
        SetWindowPos(_hwnd, IntPtr.Zero, _x, _y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public (int X, int Y) Position => (_x, _y);

    /// <summary>桌宠在屏幕上的矩形（缩放后的尺寸）。</summary>
    public (int X, int Y, int W, int H) Bounds => (_x, _y, _w, _h);

    public void Hide()
    {
        if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE);
    }

    public void Show()
    {
        if (_hwnd != IntPtr.Zero) { ShowWindow(_hwnd, SW_SHOWNOACTIVATE); Paint(); }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) { KillTimer(_hwnd, 1); DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        if (_bmp != IntPtr.Zero) { DeleteObject(_bmp); _bmp = IntPtr.Zero; }
        if (_memDC != IntPtr.Zero) { DeleteDC(_memDC); _memDC = IntPtr.Zero; }
    }
}