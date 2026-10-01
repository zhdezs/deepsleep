using System;
using System.Diagnostics;
using System.IO;
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

        // Linux：桌面环境用 xdg-open；没有桌面（纯 SSH）时退化为打印路径
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch { /* 无桌面环境，忽略 */ }
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
