using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler.Core;

using TrollWrangler;

/// <summary>连接器：接入外部账号或本机程序。令牌只存 data\connectors.json 的密文；</summary>
public sealed partial class Kernel
{
    private readonly GitHubConnector _github = new();
    private readonly BlenderConnector _blender = new();

    /// <summary>正在进行的 GitHub 设备码登录（同一时间只允许一个，避免用户点两次起两条轮询）。</summary>
    private CancellationTokenSource? _loginCts;

    /// <summary>把磁盘上的连接器状态同步给两个 Agent（GitHub / Blender 的执行器）。</summary>
    private void RefreshConnectors()
    {
        // GitHub：令牌在 → 工具可用
        _github.Token = ConnectorStore.Token(_dataDir, "github");
        Func<string, string, CancellationToken, Task<string>>? gh = _github.Connected ? RunGitHubAsync : null;
        _agent.GitHubRunner = gh;
        _clusterAgent.GitHubRunner = gh;

        // Blender：连过（路径存在）→ 工具可用；版本号来自连接时那一次 --version
        string exe = ConnectorStore.Token(_dataDir, "blender");
        _blender.Path = exe;
        _blender.Version = ConnectorStore.Account(_dataDir, "blender");
        Func<string, string, CancellationToken, Task<string>>? bl = _blender.Connected ? RunBlenderAsync : null;
        _agent.BlenderRunner = bl;
        _clusterAgent.BlenderRunner = bl;
    }

    private Task<string> RunGitHubAsync(string action, string argsJson, CancellationToken ct)
        => _github.RunAsync(action, argsJson, ct);

    private Task<string> RunBlenderAsync(string action, string argsJson, CancellationToken ct)
        => _blender.RunAsync(action, argsJson, ct);

    private List<object> ConnectorsJson()
        => ConnectorCatalog.All.Select(c =>
        {
            string account = ConnectorStore.Account(_dataDir, c.Id);
            return (object)new
            {
                id = c.Id,
                name = c.Name,
                hint = c.Hint,
                kind = c.Kind,
                connected = account.Length > 0 && ConnectorStore.Token(_dataDir, c.Id).Length > 0,
                account,
                at = ConnectorStore.At(_dataDir, c.Id),
            };
        }).ToList();

    private void EmitConnectors() => Emit(new { ev = "connectors", list = ConnectorsJson() });

/// <summary>连接一个连接器，带 token 就校验，不带就提示走登录。</summary>
    private async Task ConnectorConnectAsync(JsonElement a)
    {
        //  不能用 "id"：那是 RPC 信封的请求编号（前端 call() 用 Object.assign 合并参数，
        //   传 id 会把信封 id 覆盖成字符串，前端就再也等不到回复了）。统一用 "connector"。
        string id = (GetStr(a, "connector") ?? GetStr(a, "id") ?? "github").Trim().ToLowerInvariant();

        if (id == "blender")
        {
            string hint = (GetStr(a, "path") ?? GetStr(a, "路径") ?? _config.BlenderPath ?? "").Trim();
            string? found = BlenderConnector.Detect(string.IsNullOrWhiteSpace(hint) ? null : hint);
            if (found == null)
            {
                Toast("没找到 Blender：先装 Blender（blender.org），或在设置里填 blender.exe 路径再连。", "error");
                return;
            }
            var probe = new BlenderConnector { Path = found };
            var (ok, msg, ver) = await probe.ValidateAsync().ConfigureAwait(false);
            probe.Dispose();
            if (!ok)
            {
                Toast(" Blender 连接失败：" + msg, "error");
                return;
            }
            ConnectorStore.Save(_dataDir, "blender", found, ver);
            // 同步写进配置：以后换机器/重装，探测有个首选提示
            _config.BlenderPath = found;
            try { _config.Save(); } catch { }
            RefreshConnectors();
            Toast(" " + msg + "，Blender 工具已可用。", "info");
            EmitSettings();
            EmitConnectors();
            return;
        }

        string token = (GetStr(a, "token") ?? "").Trim();
        if (token.Length == 0)
        {
            Toast(" GitHub 请点「用 GitHub 登录」授权；也可以手动填一个 PAT。", "error");
            return;
        }
        var gh = new GitHubConnector { Token = token };
        var (ghOk, ghMsg, login) = await gh.ValidateAsync().ConfigureAwait(false);
        gh.Dispose();
        if (!ghOk)
        {
            Toast("连接失败：" + ghMsg, "error");
            return;
        }
        ConnectorStore.Save(_dataDir, id, token, login);
        RefreshConnectors();
        Toast(" " + ghMsg + "，GitHub 工具已可用。", "info");
        EmitSettings();
        EmitConnectors();
    }

    /// <summary>GitHub 设备码登录：申请设备码 → 推给界面（显示 8 位 code + 网址）→ 后台轮询换令牌。</summary>
    private async Task ConnectorLoginAsync(JsonElement a)
    {
        string id = (GetStr(a, "connector") ?? "github").Trim().ToLowerInvariant();
        if (id != "github")
        {
            Toast("这个连接器不支持登录授权，点「连接」就行。", "error");
            return;
        }
        if (_loginCts != null && !_loginCts.IsCancellationRequested)
        {
            Toast("已经有一个 GitHub 登录在等授权，先完成或取消它。", "error");
            return;
        }

        string clientId = string.IsNullOrWhiteSpace(_config.GitHubClientId)
            ? GitHubDeviceFlow.DefaultClientId : _config.GitHubClientId.Trim();
        var (start, err) = await GitHubDeviceFlow.StartAsync(clientId, "repo read:user").ConfigureAwait(false);
        if (start == null)
        {
            Toast("发起 GitHub 登录失败：" + err, "error");
            Emit(new { ev = "connectorLogin", connector = "github", state = "fail", msg = err });
            return;
        }

        _loginCts = new CancellationTokenSource();
        var cts = _loginCts;
        Emit(new
        {
            ev = "connectorLogin",
            connector = "github",
            state = "code",
            userCode = start.UserCode,
            url = start.VerificationUri,
            expiresIn = start.ExpiresIn,
            interval = start.Interval,
        });
        Toast("去 " + start.VerificationUri + "输入 " + start.UserCode + "（" + (start.ExpiresIn / 60) + "分钟内有效）", "info");

        _ = Task.Run(async () =>
        {
            try
            {
                int lastLeft = -1;
                var (ok, token, why) = await GitHubDeviceFlow.PollAsync(clientId, start, cts.Token,
                    (interval, left) =>
                    {
                        // 每 30 秒推一次剩余时间，界面能显示倒计时但不用刷屏
                        if (lastLeft < 0 || left <= 30 || lastLeft - left >= 30)
                        {
                            lastLeft = left;
                            Emit(new { ev = "connectorLogin", connector = "github", state = "waiting", left });
                        }
                    }).ConfigureAwait(false);

                if (!ok)
                {
                    Emit(new { ev = "connectorLogin", connector = "github", state = "fail", msg = why });
                    Toast(" GitHub 登录未完成：" + why, "error");
                    return;
                }

                var probe = new GitHubConnector { Token = token };
                var (vok, vmsg, login) = await probe.ValidateAsync().ConfigureAwait(false);
                probe.Dispose();
                if (!vok)
                {
                    Emit(new { ev = "connectorLogin", connector = "github", state = "fail", msg = vmsg });
                    Toast("拿到令牌但验证失败：" + vmsg, "error");
                    return;
                }
                ConnectorStore.Save(_dataDir, "github", token, login);
                RefreshConnectors();
                Emit(new { ev = "connectorLogin", connector = "github", state = "ok", account = login, msg = vmsg });
                Toast(" " + vmsg + "，GitHub 工具已可用。", "info");
                EmitSettings();
                EmitConnectors();
            }
            catch (OperationCanceledException)
            {
                Emit(new { ev = "connectorLogin", connector = "github", state = "fail", msg = "已取消" });
            }
            catch (Exception ex)
            {
                Emit(new { ev = "connectorLogin", connector = "github", state = "fail", msg = ex.Message });
            }
            finally
            {
                try { cts.Dispose(); } catch { }
                if (ReferenceEquals(_loginCts, cts)) _loginCts = null;
            }
        });
    }

    private void ConnectorLoginCancel()
    {
        try { _loginCts?.Cancel(); } catch { }
        Toast("已取消 GitHub 登录");
    }

    private void ConnectorDisconnect(JsonElement a)
    {
        // 同 ConnectorConnectAsync：连接器 id 走 "connector"，别碰信封的 "id"
        string id = (GetStr(a, "connector") ?? GetStr(a, "id") ?? "").Trim().ToLowerInvariant();
        if (id.Length == 0) return;
        ConnectorStore.Remove(_dataDir, id);
        if (id == "github") { try { _loginCts?.Cancel(); } catch { } }
        RefreshConnectors();
        string name = id == "github" ? "GitHub" : id == "blender" ? "Blender" : id;
        Toast("已断开 " + name + "，本机保存的连接信息已删除。", "info");
        EmitSettings();
        EmitConnectors();
    }
}
