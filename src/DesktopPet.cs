using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TrollWrangler;

/// <summary>
/// 透明桌宠：Win32 分层窗口（WS_EX_LAYERED + UpdateLayeredWindow），逐像素 alpha，
/// 完全不用框架窗口装饰 —— 桌面上只有鲸鱼本体，四周是真透明。
/// 图像由构建期转好的 pet.raw（8 字节头 + 预乘 BGRA）直接喂进去，不依赖任何图像库。
/// </summary>
public sealed class DesktopPet : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED = 0x00080000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TOPMOST = 0x00000008;
    private const int ULW_ALPHA = 0x02, AC_SRC_OVER = 0x00, AC_SRC_ALPHA = 0x01;
    private const int WM_LBUTTONDOWN = 0x0201, WM_MOUSEMOVE = 0x0200, WM_LBUTTONUP = 0x0202,
                      WM_RBUTTONUP = 0x0205, WM_TIMER = 0x0113, WM_COMMAND = 0x0111;
    private const int SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private const uint MF_STRING = 0x0, TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
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
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
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

    private readonly Action _onOpen;
    private readonly Action<bool>? _onVisible;
    private IntPtr _hwnd = IntPtr.Zero, _prevProc = IntPtr.Zero, _memDC = IntPtr.Zero, _bmp = IntPtr.Zero;
    private WndProcDelegate? _proc;
    private int _w, _h, _x, _y, _phase;
    private bool _dragging;
    private POINT _grab;

    private delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);

    /// <param name="onOpen">单击桌宠：打开主界面。</param>
    /// <param name="onVisible">显示 / 隐藏状态变化（隐藏时主程序记进配置，下次启动不再自己冒出来）。</param>
    public DesktopPet(string rawPath, Action onOpen, Action<bool>? onVisible = null)
    {
        _onOpen = onOpen;
        _onVisible = onVisible;
        byte[] blob = File.ReadAllBytes(rawPath);
        _w = BitConverter.ToInt32(blob, 0);
        _h = BitConverter.ToInt32(blob, 4);
        _x = GetSystemMetrics(0) - _w - 60;
        _y = GetSystemMetrics(1) - _h - 120;

        _hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST, "STATIC", "deepsleep 桌宠",
            WS_POPUP, _x, _y, _w, _h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
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
        Marshal.Copy(blob, 8, bits, blob.Length - 8);
        SelectObject(_memDC, _bmp);
        Paint();

        _proc = Hook;
        _prevProc = SetWindowLongPtrW(_hwnd, -4, Marshal.GetFunctionPointerForDelegate(_proc));
        SetTimer(_hwnd, 1, 50, IntPtr.Zero);
    }

    private IntPtr Hook(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        switch (m)
        {
            case WM_LBUTTONDOWN:
                _dragging = true; GetCursorPos(out _grab);
                _grab.X -= _x; _grab.Y -= _y;
                return IntPtr.Zero;
            case WM_MOUSEMOVE when _dragging:
                GetCursorPos(out POINT p);
                _x = p.X - _grab.X; _y = p.Y - _grab.Y;
                SetWindowPos(h, IntPtr.Zero, _x, _y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                return IntPtr.Zero;
            case WM_LBUTTONUP when _dragging:
                _dragging = false;
                _onOpen();                       // 拖完松手当作"点一下"：打开主界面
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
        AppendMenuW(menu, MF_STRING, 1, "打开主界面");
        AppendMenuW(menu, MF_STRING, 2, "隐藏桌宠（在 ⚙ 设置里可重新显示）");
        AppendMenuW(menu, MF_STRING, 9, "退出 deepsleep");
        GetCursorPos(out POINT p);
        int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, 0, h, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd == 1) _onOpen();
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

    public void Hide()
    {
        if (_hwnd != IntPtr.Zero) SetWindowPos(_hwnd, IntPtr.Zero, -4000, -4000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public void Show()
    {
        if (_hwnd != IntPtr.Zero) { SetWindowPos(_hwnd, IntPtr.Zero, _x, _y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE); Paint(); }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) { KillTimer(_hwnd, 1); DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        if (_bmp != IntPtr.Zero) { DeleteObject(_bmp); _bmp = IntPtr.Zero; }
        if (_memDC != IntPtr.Zero) { DeleteDC(_memDC); _memDC = IntPtr.Zero; }
    }
}
