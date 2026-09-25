using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace TrollWrangler.Core;

/// <summary>
/// 内核：OTA 自动更新（更新线路默认 Gitee、可在 ⚙ 设置里切成 GitHub；Gitee 线路上只有 Gitee 下不动才回退 GitHub，校验始终用官方 SHA256）与本地大模型探测。
/// </summary>
public sealed partial class Kernel
{
    private static readonly int[] OllamaPorts = { 11434, 11435, 11436, 1234, 8080, 8081, 8000, 5000 };

    private static Dictionary<string, object?> UpdateJson(UpdateInfo i) => new()
    {
        ["version"] = i.Version,
        ["current"] = Updater.CurrentVersion,
        ["notes"] = i.Notes,
        ["size"] = i.Size,
        ["sha256"] = i.Sha256,
        ["source"] = i.Source,
        ["mirror"] = i.MirrorUrl,
        ["parts"] = i.PartUrls.Count,
        ["route"] = RouteText(i),
    };

    private static string RouteText(UpdateInfo i)
    {
        bool giteeRoute = !string.IsNullOrWhiteSpace(i.MirrorUrl) || i.PartUrls.Count > 0;
        if (giteeRoute)
            return i.PartUrls.Count > 0
                ? "更新线路：Gitee 优先（可在 ⚙ 设置里切换）。装不下的安装包在 Gitee 那边切成多片，下齐后拼回整包；不再探速比快慢，只有 Gitee 连不上、卡住或下不动时才回退 GitHub。"
                : "更新线路：Gitee 优先（国内实测快一个数量级，可在 ⚙ 设置里切换）：不再探速比快慢，Gitee 连不上或下不动时才回退 GitHub。";
        if (i.Source == "gitee") return "该版本来自 Gitee 国内源（更新线路：Gitee 优先，可在 ⚙ 设置里切换）。";
        return "更新线路：GitHub（可在 ⚙ 设置里切回 Gitee）。直连失败或中断会自动切国内加速镜像接着下（断点续传、卡住会换源），下完仍按官方 SHA256 校验。";
    }

    /// <summary>启动时静默检查更新，有新版就推给界面（左下角出现「有新版」按钮）。</summary>
    private async Task AutoCheckUpdateAsync()
    {
        if (!_config.AutoCheckUpdate || string.IsNullOrWhiteSpace(_config.UpdateUrl)) return;
        try
        {
            var info = await Updater.CheckAsync(_config.UpdateUrl, _config.GiteeFirst).ConfigureAwait(false);
            if (info == null) return;
            _pendingUpdate = info;
            Emit(new { ev = "update", update = UpdateJson(info) });
        }
        catch { }
    }

    private async Task CheckUpdateAsync(bool notifyIfLatest)
    {
        if (string.IsNullOrWhiteSpace(_config.UpdateUrl))
        {
            if (notifyIfLatest) Toast("未配置更新源：请到设置里填 GitHub/Gitee 仓库（owner/repo）。", "error");
            return;
        }
        try
        {
            var info = await Updater.CheckAsync(_config.UpdateUrl, _config.GiteeFirst).ConfigureAwait(false);
            if (info == null)
            {
                _pendingUpdate = null;
                Emit(new { ev = "updateLatest", text = $"已是最新版本（v{Updater.CurrentVersion}）。" });
                return;
            }
            _pendingUpdate = info;
            Emit(new { ev = "update", update = UpdateJson(info) });
        }
        catch (Exception ex)
        {
            Toast("检查更新失败：" + ex.Message, "error");
        }
    }

    /// <summary>下载 + 静默覆盖安装 + 重启（界面收到 restarting 后自己关掉）。</summary>
    private async Task RunUpdateAsync()
    {
        if (_updateRunning) return;
        UpdateInfo? info = _pendingUpdate;
        try
        {
            if (info == null)
            {
                if (string.IsNullOrWhiteSpace(_config.UpdateUrl))
                {
                    Toast("未配置更新源：请到设置里填 GitHub/Gitee 仓库（owner/repo）。", "error");
                    return;
                }
                info = await Updater.CheckAsync(_config.UpdateUrl, _config.GiteeFirst).ConfigureAwait(false);
                if (info == null)
                {
                    Emit(new { ev = "updateLatest", text = $"已是最新版本（v{Updater.CurrentVersion}）。" });
                    return;
                }
                _pendingUpdate = info;
                Emit(new { ev = "update", update = UpdateJson(info) });
            }
        }
        catch (Exception ex)
        {
            Toast("检查更新失败：" + ex.Message, "error");
            return;
        }

        _updateRunning = true;
        try
        {
            string status = "正在下载更新包…";
            var progress = new Progress<double>(p => Emit(new { ev = "updateProgress", percent = p, status }));
            string installer = await Updater.DownloadAsync(info, progress, default, s => status = s).ConfigureAwait(false);
            Emit(new { ev = "updateStatus", text = "下载完成，正在启动升级程序…", done = false });
            string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "deepsleep.exe");
            string installDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
            Updater.ApplyAndRestart(installer, installDir, exePath);
            Emit(new { ev = "restarting" });
        }
        catch (OperationCanceledException)
        {
            Emit(new { ev = "updateStatus", text = "已取消升级。", done = true });
        }
        catch (Exception ex)
        {
            Emit(new { ev = "updateStatus", text = "升级失败：" + ex.Message, done = true });
        }
        finally
        {
            _updateRunning = false;
        }
    }

    // ------------------------------------------------------------------
    // 本地大模型（Ollama）探测
    // ------------------------------------------------------------------

    private async Task ProbeLlmAsync()
    {
        try
        {
            _llmOnline = await _engine.PingLlmAsync().ConfigureAwait(false);
            if (!_llmOnline)
            {
                string? found = await AutoDetectOllamaAsync().ConfigureAwait(false);
                if (found != null)
                {
                    _config.OllamaUrl = found;
                    _config.Save();
                    _engine.ApplyConfig(_config);
                    _agent.ApplyConfig(_config);
                    _clusterAgent.ApplyConfig(_config);
                    _llmOnline = await _engine.PingLlmAsync().ConfigureAwait(false);
                }
            }
            EmitStatus(0);
            EmitStatus(2);
        }
        catch { }
    }

    private async Task<string?> AutoDetectOllamaAsync()
    {
        foreach (string host in new[] { "http://127.0.0.1", "http://localhost" })
            foreach (int port in OllamaPorts)
            {
                string baseUrl = $"{host}:{port}";
                if (string.Equals(baseUrl, _config.OllamaUrl, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(1.2) };
                    using var resp = await hc.GetAsync(baseUrl + "/api/tags").ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode) return baseUrl;
                }
                catch { /* 该端口无服务，继续扫描 */ }
            }
        return null;
    }
}