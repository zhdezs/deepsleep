using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TrollWrangler;

using TrollWrangler.CoreHost;

/// <summary>
/// 「操作电脑」的执行端：把模型给的动作翻译成真实的鼠标键盘事件，
/// 每一步之后截一张图存到 data\shots\，交给调用方去解读。
///
/// 只做「执行 + 截图」，不做判断 —— 决策留给模型（它看截图）。
/// 坐标支持两种：像素（相对整个屏幕左上角）或 0~1 相对坐标（跨分辨率更稳，推荐）。
/// </summary>
public static class ComputerUse
{
    public static string ShotsDir(string dataDir)
    {
        string d = Path.Combine(dataDir, "shots");
        try { Directory.CreateDirectory(d); } catch { }
        return d;
    }

    /// <summary>执行一个动作。返回 (是否成功, 说明)。不负责截图。</summary>
    public static (bool ok, string msg) Do(string action, double x, double y, bool relative,
                                           string text, string key, int amount)
    {
        try
        {
            switch (action)
            {
                case "移动":
                    MoveTo(x, y, relative);
                    return (true, $"鼠标移动到 ({Fmt(x, y, relative)})");

                case "点击":
                    MoveTo(x, y, relative);
                    Sleep(60);
                    Click("left", 1);
                    return (true, $"左键单击 ({Fmt(x, y, relative)})");

                case "双击":
                    MoveTo(x, y, relative);
                    Sleep(60);
                    Click("left", 2);
                    return (true, $"左键双击 ({Fmt(x, y, relative)})");

                case "右键":
                    MoveTo(x, y, relative);
                    Sleep(60);
                    Click("right", 1);
                    return (true, $"右键单击 ({Fmt(x, y, relative)})");

                case "输入":
                    if (string.IsNullOrEmpty(text)) return (false, "参数错误：「输入」需要 text。");
                    if (!TypeText(text)) return (false, "当前平台不支持文字输入。");
                    return (true, $"已输入 {text.Length} 个字符：{(text.Length > 40 ? text[..40] + "…" : text)}");

                case "按键":
                    if (string.IsNullOrWhiteSpace(key)) return (false, "参数错误：「按键」需要 key（如 enter / esc / ctrl+c）。");
                    return PressCombo(key);

                case "滚动":
                    int steps = amount == 0 ? 1 : amount;
                    RemoteDesktop.Wheel(steps > 0 ? 120 * steps : 120 * steps);   // 正=上滚，负=下滚
                    return (true, $"滚轮 {steps} 格");

                case "等待":
                    int ms = amount <= 0 ? 800 : Math.Min(amount, 15000);
                    Sleep(ms);
                    return (true, $"等待 {ms} ms");

                case "截图":
                    return (true, "仅截图");

                default:
                    return (false, $"未知动作：{action}（可用：截图/移动/点击/双击/右键/输入/按键/滚动/等待）");
            }
        }
        catch (Exception ex) { return (false, "执行失败：" + ex.Message); }
    }

    /// <summary>截一张图存盘，返回 (路径, 宽, 高)。</summary>
    public static (string path, int w, int h) Shot(string dataDir)
    {
        byte[] png = RemoteDesktop.CapturePng(1600, out int w, out int h);
        if (png == null || png.Length == 0) return ("", 0, 0);
        string dir = ShotsDir(dataDir);
        string file = Path.Combine(dir, $"shot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        File.WriteAllBytes(file, png);
        return (file, w, h);
    }

    // ------------------------------------------------------------------ 鼠标

    private static void MoveTo(double x, double y, bool relative)
    {
        if (relative)
        {
            RemoteDesktop.MouseMoveNorm(x, y);
            return;
        }
        int sw = Math.Max(1, RemoteDesktop.ScreenWidth);
        int sh = Math.Max(1, RemoteDesktop.ScreenHeight);
        RemoteDesktop.MouseMoveNorm(Math.Clamp(x / sw, 0, 1), Math.Clamp(y / sh, 0, 1));
    }

    private static void Click(string button, int times)
    {
        for (int i = 0; i < times; i++)
        {
            RemoteDesktop.MouseButton(button, true);
            Sleep(35);
            RemoteDesktop.MouseButton(button, false);
            if (i + 1 < times) Sleep(70);
        }
    }

    private static string Fmt(double x, double y, bool rel) => rel ? $"{x:0.###}, {y:0.###}（相对）" : $"{x:0}, {y:0} 像素";

    // ------------------------------------------------------------------ 键盘

    private static readonly Dictionary<string, int> Vk = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["tab"] = 0x09, ["space"] = 0x20, ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E,
        ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["ctrl"] = 0x11, ["control"] = 0x11, ["alt"] = 0x12, ["shift"] = 0x10, ["win"] = 0x5B,
        ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73, ["f5"] = 0x74, ["f6"] = 0x75,
        ["f7"] = 0x76, ["f8"] = 0x77, ["f9"] = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
    };

    private static (bool, string) PressCombo(string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return (false, "空按键");

        var downs = new List<int>();
        foreach (var p in parts[..^1])
        {
            if (!Vk.TryGetValue(p, out int vk)) return (false, $"不认识的修饰键：{p}");
            RemoteDesktop.Key(vk, true);
            downs.Add(vk);
        }
        string last = parts[^1];
        int main;
        if (!Vk.TryGetValue(last, out main))
        {
            if (last.Length != 1) return (false, $"不认识的按键：{last}");
            char c = char.ToUpperInvariant(last[0]);
            main = c;                                  // A-Z / 0-9 的 VK 就是 ASCII
        }
        RemoteDesktop.Key(main, true);
        Sleep(30);
        RemoteDesktop.Key(main, false);
        for (int i = downs.Count - 1; i >= 0; i--) { Sleep(20); RemoteDesktop.Key(downs[i], false); }
        return (true, $"按键 {combo}");
    }

    /// <summary>逐字符注入 Unicode（中文/符号都能打），Windows 专有实现。</summary>
    public static bool TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (!OperatingSystem.IsWindows()) return false;
        foreach (char ch in text)
        {
            SendUnicode(ch, false);
            SendUnicode(ch, true);
            Sleep(8);
        }
        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags;
        public uint time; public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private static void SendUnicode(char ch, bool up)
    {
        var inp = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = ch,
                    dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0),
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    private static void Sleep(int ms) { try { System.Threading.Thread.Sleep(ms); } catch { } }
}
