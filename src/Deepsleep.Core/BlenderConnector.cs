using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler;

/// <summary>Blender 连接器：连上本机 Blender 后，模型才拿到 Blender 工具。</summary>
public sealed class BlenderConnector : IDisposable
{
    /// <summary>blender 可执行文件路径（连接时定下来）。</summary>
    public string Path { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Connected => !string.IsNullOrWhiteSpace(Path) && File.Exists(Path);
    public string LastError { get; private set; } = "";

    /// <summary>探测本机 Blender：显式路径 → 常见安装目录 → PATH。 用户没连过时，内核首启也用它试一次，探到就自动连上（省得人人去填路径）。</summary>
    public static string? Detect(string? hint = null)
    {
        if (!string.IsNullOrWhiteSpace(hint) && File.Exists(hint)) return hint;
        try
        {
            if (Platform.IsWindows)
            {
                foreach (var root in new[]
                {
                    @"C:\Program Files\Blender Foundation",
                    @"C:\Program Files (x86)\Blender Foundation",
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                           "Programs", "Blender Foundation"),
                })
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var dir in Directory.GetDirectories(root).OrderByDescending(d => d))
                    {
                        string exe = System.IO.Path.Combine(dir, "blender.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
            }
            else if (Platform.IsMacOS)
            {
                foreach (var p in new[] { "/Applications/Blender.app/Contents/MacOS/Blender" })
                    if (File.Exists(p)) return p;
            }
            else
            {
                foreach (var p in new[] { "/usr/bin/blender", "/usr/local/bin/blender", "/snap/bin/blender", "/opt/blender/blender" })
                    if (File.Exists(p)) return p;
            }
        }
        catch { }
        // PATH：真能跑就说明装过并且进了环境变量
        try
        {
            var psi = new ProcessStartInfo(Platform.IsWindows ? "where" : "which", "blender")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                string first = outp.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
                if (first.Length > 0 && File.Exists(first)) return first;
            }
        }
        catch { }
        return null;
    }

    /// <summary>连接前的验证：跑 `blender --version`，拿到真版本号才算连上（配错路径在这一步就暴露）。</summary>
    public async Task<(bool ok, string msg, string version)> ValidateAsync(CancellationToken ct = default)
    {
        if (!Connected) return (false, "没有找到 blender 可执行文件", "");
        try
        {
            var psi = new ProcessStartInfo(Path, "--version")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 Blender");
            var outTask = Platform.ReadProcessOutputAsync(p.StandardOutput);
            var errTask = Platform.ReadProcessOutputAsync(p.StandardError);
            var done = await Task.WhenAny(p.WaitForExitAsync(ct), Task.Delay(TimeSpan.FromSeconds(20), ct)).ConfigureAwait(false);
            if (done is not Task<bool> && p.HasExited == false)
            {
                try { p.Kill(true); } catch { }
                return (false, "Blender 启动超时（20 秒没响应）", "");
            }
            string o = (await outTask).Trim();
            string e = (await errTask).Trim();
            string all = (o.Length > 0 ? o : e);
            // 第一行形如 "Blender 4.3.0"或 "Blender 4.2.1 LTS"
            string ver = all.Split('\n').Select(x => x.Trim())
                .FirstOrDefault(x => x.StartsWith("Blender", StringComparison.OrdinalIgnoreCase)) ?? "";
            if (ver.Length == 0)
                return (false, "blender 能启动，但没报出版本号：" + Trunc(all, 160), "");
            Version = ver;
            return (true, $"已连接 {ver} · {Path}", ver);
        }
        catch (OperationCanceledException) { return (false, "连接被取消", ""); }
        catch (Exception ex) { return (false, ex.Message, ""); }
    }

    /// <summary>执行一次工具调用。action：""/version 查版本；run 跑 bpy 脚本（默认）。</summary>
    public async Task<string> RunAsync(string action, string argsJson, CancellationToken ct = default)
    {
        if (!Connected)
            return "Blender 连接器还没连：让用户去设置 →  连接器 点「连接」Blender（会自动找安装路径），连上之后这个工具才可用。";

        JsonElement a = default;
        try
        {
            if (!string.IsNullOrWhiteSpace(argsJson))
                using (var d = JsonDocument.Parse(argsJson)) a = d.RootElement.Clone();
        }
        catch { /* 参数不是合法 JSON 就当空对象 */ }

        string S(params string[] names)
        {
            if (a.ValueKind != JsonValueKind.Object) return "";
            foreach (var n in names)
                if (a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
            return "";
        }

        string act = (action ?? "").Trim().ToLowerInvariant();
        if (act is "version" or "版本" or "info")
            return $"Blender：{Version}\n路径：{Path}";

        string code = S("代码", "code", "script");
        if (string.IsNullOrWhiteSpace(code))
            return "参数错误：缺少「代码」（Blender Python / bpy 脚本）。";

        string outPath = S("输出", "output", "path");
        string workDir = S("工作目录", "cwd", "dir");
        int timeoutSec = 600;
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("超时", out var tv) && tv.TryGetInt32(out int tvi) && tvi > 0)
            timeoutSec = Math.Clamp(tvi, 10, 3600);

        var sb = new StringBuilder();
        sb.AppendLine("# -*- coding: utf-8 -*-");
        sb.AppendLine("# 由 deepsleep 生成并通过 `blender --background --python` 执行");
        sb.AppendLine(code);
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            string full = System.IO.Path.GetFullPath(outPath);
            string dir = System.IO.Path.GetDirectoryName(full) ?? "";
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            string esc = full.Replace("\\", "\\\\");
            string ext = System.IO.Path.GetExtension(full).ToLowerInvariant();
            sb.AppendLine("\n# ---- deepsleep: 自动导出 ----");
            sb.AppendLine("import bpy as _bpy, os as _os");
            sb.AppendLine("_out = r\"" + esc + "\"");
            sb.AppendLine("_os.makedirs(_os.path.dirname(_out) or '.', exist_ok=True)");
            switch (ext)
            {
                case ".blend": sb.AppendLine("_bpy.ops.wm.save_as_mainfile(filepath=_out)"); break;
                case ".glb":
                case ".gltf":
                    sb.AppendLine("_bpy.ops.export_scene.gltf(filepath=_out, export_format='GLB' if _out.lower().endswith('.glb') else 'GLTF_SEPARATE')");
                    break;
                case ".obj": sb.AppendLine("_bpy.ops.wm.obj_export(filepath=_out)"); break;
                case ".stl": sb.AppendLine("_bpy.ops.wm.stl_export(filepath=_out)"); break;
                case ".fbx": sb.AppendLine("_bpy.ops.export_scene.fbx(filepath=_out)"); break;
                default: sb.AppendLine("_bpy.ops.wm.save_as_mainfile(filepath=_out)"); break;
            }
            sb.AppendLine("print('__DS_EXPORTED__', _out)");
        }

        string scriptDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agent_blender");
        Directory.CreateDirectory(scriptDir);
        string script = System.IO.Path.Combine(scriptDir, $"bpy_{Guid.NewGuid():N}.py");
        File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));

        var psi = new ProcessStartInfo(Path, $"--background --factory-startup --python \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir)) psi.WorkingDirectory = workDir;

        Process? proc = null;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 Blender");
            var stdoutTask = Platform.ReadProcessOutputAsync(proc.StandardOutput);
            var stderrTask = Platform.ReadProcessOutputAsync(proc.StandardError);
            var timeout = Task.Delay(TimeSpan.FromSeconds(timeoutSec), ct);
            if (await Task.WhenAny(proc.WaitForExitAsync(ct), timeout) == timeout)
            {
                try { proc.Kill(true); } catch { }
                if (ct.IsCancellationRequested) return "Blender 已停止。";
                return $"Blender 执行超时（{timeoutSec} 秒），已终止。脚本：{script}";
            }
            string stdout = (await stdoutTask).Trim();
            string stderr = (await stderrTask).Trim();
            string result = string.Join("\n", new[] { stdout, stderr }.Where(x => x.Length > 0));
            if (proc.ExitCode != 0) result = $"（退出码 {proc.ExitCode}）\n" + result;
            if (string.IsNullOrWhiteSpace(result)) result = "（Blender 没有输出）";

            var made = new List<string>();
            if (!string.IsNullOrWhiteSpace(outPath) && File.Exists(outPath)) made.Add(outPath);
            string head = $"Blender：{Version}（{Path}）\n脚本：{script}\n";
            if (made.Count > 0) head += "产物：" + string.Join("、", made) + "\n";
            return Trunc(head + result, 12000);
        }
        catch (OperationCanceledException)
        {
            try { proc?.Kill(true); } catch { }
            return "Blender 已停止。";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return "Blender 调用失败：" + ex.Message;
        }
    }

    private static string Trunc(string s, int n) => (s ?? "").Length <= n ? (s ?? "") : s[..n] + "…";

    public void Dispose() { }
}
