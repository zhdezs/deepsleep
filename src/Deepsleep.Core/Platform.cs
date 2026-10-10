using System;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TrollWrangler;

/// <summary>
/// 跨平台差异集中在这里：命令壳、默认数据目录、打开文件管理器、机器绑定、文件权限。
/// Windows 分支与改造前逐字一致（行为不变）；Linux / macOS 走各自的原生方式。
/// </summary>
public static class Platform
{
    public static bool IsWindows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsMacOS { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public static bool IsLinux { get; } = !IsWindows && !IsMacOS;

    /// <summary>平台短名：windows / macos / linux（界面文案用）。</summary>
    public static string DisplayName => IsWindows ? "Windows" : IsMacOS ? "macOS" : "Linux";

    /// <summary>命令壳的名字（提示词 / 报错文案用）。</summary>
    public static string ShellName => IsWindows ? "PowerShell" : "shell";

    /// <summary>
    /// 本机平台 + 架构的 RIP 后缀（OTA 挑包用），例如 win-x64 / linux-x64 / linux-arm64 / osx-arm64。
    /// Unix 上按 <see cref="RuntimeInformation.OSArchitecture"/> 算，免得 arm64 机器下到 x64 的包。
    /// </summary>
    public static string Rid
    {
        get
        {
            if (IsWindows) return "win-x64";     // Windows 端只发 x64
            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm => "arm",
                _ => "x64",
            };
            return (IsMacOS ? "osx" : "linux") + "-" + arch;
        }
    }

    /// <summary>这个平台可执行文件的扩展名（Windows 是 .exe，其余为空）。</summary>
    public static string ExeSuffix => IsWindows ? ".exe" : "";

    /// <summary>平台专属的发布包文件名（Linux/macOS 用 tar.gz，Windows 用 exe/zip）。</summary>
    public static string PackageSuffix => IsWindows ? ".exe" : ".tar.gz";

    /// <summary>宿主可执行文件名（Windows 是 deepsleep.exe，Unix 是 deepsleep-core）。</summary>
    public static string HostExeName => IsWindows ? "deepsleep.exe" : "deepsleep-core";

    /// <summary>
    /// Linux 用户级自更新目录（不需要 root）：$XDG_DATA_HOME/deepsleep/app 或 ~/.local/share/deepsleep/app。
    /// deb / rpm 把程序装到 /opt/deepsleep（root 所有），普通用户写不进去 —— OTA 只能落到这里，
    /// 再由 deepsleep.sh / 用户级 .desktop 优先拉起这份新版本，全程静默、不用输密码。
    /// </summary>
    public static string LinuxAppsDir
    {
        get
        {
            string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? "";
            if (string.IsNullOrWhiteSpace(xdg))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                xdg = Path.Combine(home, ".local", "share");
            }
            return Path.Combine(xdg, "deepsleep", "app");
        }
    }

    /// <summary>目录能不能写（不存在就试着建）。OTA 靠它决定是就地覆盖还是退到用户级目录。</summary>
    public static bool CanWriteDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".ds-write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------
    // 命令执行
    // ------------------------------------------------------------------

    /// <summary>
    /// 把一条命令包成可直接启动的进程信息。
    /// Windows：powershell.exe（-NoProfile -NonInteractive -ExecutionPolicy Bypass），并先设 UTF-8 输出；
    /// Unix：/bin/bash -lc（登录 shell，PATH 与用户环境一致）。
    /// </summary>
    public static ProcessStartInfo ShellStartInfo(string cmd, string? workDir)
    {
        ProcessStartInfo psi;
        if (IsWindows)
        {
            // 让 PowerShell 以 UTF-8 输出，避免中文乱码
            string full = "$OutputEncoding=[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + cmd;
            psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{full.Replace("\"", "\\\"")}\"");
        }
        else
        {
            psi = new ProcessStartInfo("/bin/bash");
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(cmd);
        }
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(workDir)) psi.WorkingDirectory = workDir;
        return psi;
    }

    static Platform()
    {
        // .NET Core 默认不带非 Unicode 代码页，不注册就拿不到 936（GBK）。
        // 命令输出里的中文能不能正常显示（nvidia-smi 那种乱码）全靠它。
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { /* 拿不到就算了，下面有兜底 */ }
    }

    /// <summary>
    /// 读子进程输出（按原始字节解码）。
    /// 我们给 ProcessStartInfo 设了 StandardOutputEncoding=UTF8 —— 这对 PowerShell 自己的输出是对的，
    /// 但老式 Windows 程序（nvidia-smi、7z、部分国产软件）根本不理它，照样按系统 ANSI 代码页
    /// （简体中文是 GBK/936）把字节直接写进管道。按 UTF-8 硬解就成了「…\��������.exe」这种乱码。
    /// 所以这里拿原始字节：先按严格 UTF-8 解（解不通会抛），失败再按本机 ANSI / GBK 解。
    /// </summary>
    public static async Task<string> ReadProcessOutputAsync(StreamReader reader, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await reader.BaseStream.CopyToAsync(ms, 81920, ct).ConfigureAwait(false);
        return DecodeOutput(ms.ToArray());
    }

    /// <summary>字节 → 文字：严格 UTF-8 优先，失败则按本机 ANSI 代码页（中文 Windows = 936）解。</summary>
    public static string DecodeOutput(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { /* 不是合法 UTF-8 → 多半是本地代码页 */ }
        foreach (int cp in OutputCodePages())
        {
            try { return Encoding.GetEncoding(cp).GetString(bytes); } catch { /* 该代码页不可用 */ }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static IEnumerable<int> OutputCodePages()
    {
        var cps = new List<int>();
        if (IsWindows)
        {
            try { cps.Add(CultureInfo.CurrentCulture.TextInfo.ANSICodePage); } catch { }
        }
        cps.Add(936);    // 简体中文 GBK
        cps.Add(950);    // 繁体中文 Big5
        cps.Add(1252);   // 西欧
        return cps.Distinct();
    }

    /// <summary>把 shell 命令交给系统执行（安装、批处理等），调用方自己等退出。</summary>
    public static ProcessStartInfo ShellCommand(string cmd)
    {
        ProcessStartInfo psi;
        if (IsWindows)
        {
            psi = new ProcessStartInfo("cmd.exe", "/c " + cmd);
        }
        else
        {
            psi = new ProcessStartInfo("/bin/bash");
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(cmd);
        }
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        return psi;
    }

    /// <summary>用系统默认程序打开文件/文件夹（跨平台）。</summary>
    public static void OpenWithShell(string path, bool revealInFolder)
    {
        if (IsWindows)
        {
            if (revealInFolder)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }

        if (IsMacOS)
        {
            if (revealInFolder)
                Process.Start(new ProcessStartInfo("open")
                {
                    ArgumentList = { "-R", path },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            else
                Process.Start(new ProcessStartInfo("open")
                {
                    ArgumentList = { path },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            return;
        }

        // Linux：桌面环境用 xdg-open；GNOME 那边一定还有 gio（glib），xdg-open 没装也能顶上；
        // 两个都没有（纯 SSH / 没装 xdg-utils）就静默放弃 —— 以前只试 xdg-open，
        // 缺了它「远程桌面」按钮就等于没反应。
        string target = path;
        if (revealInFolder)
        {
            // Linux 没有"在文件管理器里选中文件"的统一接口，退一步打开所在目录
            try
            {
                string? dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) target = dir;
            }
            catch { }
        }
        foreach (string opener in new[] { "xdg-open", "gio", "sensible-browser" })
        {
            try
            {
                var psi = new ProcessStartInfo(opener) { UseShellExecute = false, CreateNoWindow = true };
                if (opener == "gio") psi.ArgumentList.Add("open");
                psi.ArgumentList.Add(target);
                Process.Start(psi);
                return;
            }
            catch { /* 这个没有 / 起不来，试下一个 */ }
        }
    }

    // ------------------------------------------------------------------
    // 数据目录
    // ------------------------------------------------------------------

    /// <summary>
    /// 默认数据目录。
    /// Windows 沿用安装目录（%LocalAppData%\Programs\deepsleep\data）；
    /// Linux 用 XDG（$XDG_DATA_HOME/deepsleep 或 ~/.local/share/deepsleep）；
    /// macOS 用 ~/Library/Application Support/deepsleep。
    /// </summary>
    public static string DefaultDataDir()
    {
        if (IsWindows)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string app = Path.Combine(local, "Programs", "deepsleep", "data");
            if (Directory.Exists(app)) return app;
            return Path.Combine(local, "deepsleep-core", "data");
        }

        if (IsMacOS)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "deepsleep");
        }

        string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? "";
        if (string.IsNullOrWhiteSpace(xdg))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            xdg = Path.Combine(home, ".local", "share");
        }
        return Path.Combine(xdg, "deepsleep");
    }

    // ------------------------------------------------------------------
    // 机器绑定 & 文件权限
    // ------------------------------------------------------------------

    /// <summary>
    /// 机器 + 用户绑定串（作为 PBKDF2 口令 / AES-GCM 的 AAD）。
    /// Windows：MachineGuid + 用户 SID + 机器名；
    /// Unix：/etc/machine-id（或 dbus 的） + 用户名 + 机器名。
    /// </summary>
    public static string MachineBinding()
    {
        string guid = "";
        if (IsWindows)
        {
#if WINDOWS
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Cryptography");
                guid = key?.GetValue("MachineGuid")?.ToString() ?? "";
            }
            catch { /* 读不到就用机器名代替 */ }
#endif
        }
        else
        {
            foreach (var f in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
            {
                try
                {
                    if (File.Exists(f))
                    {
                        guid = File.ReadAllText(f).Trim();
                        if (guid.Length > 0) break;
                    }
                }
                catch { }
            }
        }

        string sid = "";
#if WINDOWS
        try
        {
            sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
        }
        catch { }
#else
        try { sid = Environment.UserName; } catch { }
#endif
        return guid + "|" + sid + "|" + Environment.MachineName;
    }

    /// <summary>
    /// ④ 文件层：把文件权限收紧到"只有当前用户"。
    /// Windows 用 icacls 断开继承；Unix 直接 chmod 600。
    /// </summary>
    public static void LockDownFile(string path)
    {
        try
        {
            if (IsWindows)
            {
#if WINDOWS
                var me = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "icacls.exe"))
                {
                    Arguments = "\"" + path + "\" /inheritance:r /grant:r \"" + me + ":F\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                Process.Start(psi)?.WaitForExit(5000);
#endif
            }
            else
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch { /* 非 NTFS / 非本地文件系统等情况忽略 */ }
    }
}
