using System;
using System.IO;
using System.Text.RegularExpressions;

namespace TrollWrangler;

/// <summary>工具沙箱：限制 AI 的危险操作，防止病毒、误删和破坏系统。</summary>
public static class Sandbox
{
    private static readonly string[] AlwaysBlock =
    {
        "format ", "diskpart", "bcdedit", "bootrec", "vssadmin", "shutdown",
        "restart-computer", "stop-computer", "clear-disk", "set-disk",
        "set-mppreference", "add-mppreference", "remove-mppreference", "cipher /w",
        "reg delete",
    };

    private static readonly string[] SystemDirs =
    {
        @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData",
        @"C:\System Volume Information", @"C:\$Recycle.Bin",
    };

    public static string? CheckCommand(string cmd)
    {
        string c = cmd.ToLowerInvariant();
        foreach (var bad in AlwaysBlock)
            if (c.Contains(bad, StringComparison.Ordinal))
                return $"沙箱拦截：命令包含危险操作「{bad.Trim()}」，已禁止执行。";
        if (ContainsSystemDir(c) &&
            (c.Contains("takeown", StringComparison.Ordinal) ||
             c.Contains("icacls", StringComparison.Ordinal) ||
             c.Contains("fsutil", StringComparison.Ordinal) ||
             c.Contains("defrag", StringComparison.Ordinal)))
            return "沙箱拦截：检测到针对系统目录的权限/磁盘操作，已禁止执行。";
        if (Regex.IsMatch(c, @"remove-item.*-recurse", RegexOptions.IgnoreCase) &&
            (Regex.IsMatch(c, @"[a-z]:\\\s*[""']?\s*$", RegexOptions.IgnoreCase) || ContainsSystemDir(c)))
            return "沙箱拦截：禁止递归删除系统目录或盘符根目录。";
        if (Regex.IsMatch(c, @"rm\s+-rf\s+/|del\s+/f\s+/s\s+/q\s+[a-z]:\\windows", RegexOptions.IgnoreCase))
            return "沙箱拦截：检测到高危删除命令，已禁止执行。";
        return null;
    }

    public static string? CheckPython(string code)
    {
        string c = code.ToLowerInvariant();
        if (c.Contains("ctypes", StringComparison.Ordinal))
            return "沙箱拦截：Python 脚本禁止使用 ctypes（可注入任意原生代码）。";
        if (c.Contains("winreg", StringComparison.Ordinal) &&
            Regex.IsMatch(c, @"setvalue|createkey|deletekey|deletevalue|setvalueex", RegexOptions.IgnoreCase))
            return "沙箱拦截：Python 脚本禁止写入注册表（winreg 写入操作）。";
        if ((c.Contains("rmtree", StringComparison.Ordinal) || c.Contains("os.remove", StringComparison.Ordinal) ||
             c.Contains("unlink", StringComparison.Ordinal)) && ContainsSystemDir(c))
            return "沙箱拦截：禁止删除系统目录中的文件。";
        if (Regex.IsMatch(c, @"os\.system\s*\(\s*[""'](format|diskpart|shutdown|del|rmdir)", RegexOptions.IgnoreCase))
            return "沙箱拦截：Python 禁止调用高危系统命令。";
        return null;
    }

    public static string? CheckWritePath(string path)
    {
        string full = Path.GetFullPath(path);
        string lower = full.ToLowerInvariant();
        foreach (var d in SystemDirs)
            if (lower.StartsWith(d.ToLowerInvariant(), StringComparison.Ordinal))
                return $"沙箱拦截：禁止向系统目录写入文件（{d}）。";
        string ext = Path.GetExtension(full).ToLowerInvariant();
        if ((ext is ".exe" or ".dll" or ".bat" or ".cmd" or ".ps1" or ".vbs" or ".scr" or ".msi") &&
            lower.Contains(@"\windows\", StringComparison.Ordinal))
            return "沙箱拦截：禁止在系统目录写入可执行文件。";
        return null;
    }

    public static string? CheckOpenPath(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".exe" or ".dll" or ".bat" or ".cmd" or ".ps1" or ".vbs" or ".scr" or ".msi" or ".com")
            return "沙箱拦截：不允许直接打开可执行/脚本文件（避免运行未知代码）。";
        return null;
    }

    private static bool ContainsSystemDir(string lower)
    {
        foreach (var d in SystemDirs)
            if (lower.Contains(d.ToLowerInvariant(), StringComparison.Ordinal)) return true;
        return false;
    }
}
