using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler;

/// <summary>GitHub 设备码登录（OAuth Device Flow）。</summary>
public static class GitHubDeviceFlow
{
    /// <summary>GitHub 官方 CLI 的公开 OAuth App client_id（device flow 专用，无 client_secret）。</summary>
    public const string DefaultClientId = "Iv1.b507a08c87ecfe98";

    private const string CodeUrl = "https://github.com/login/device/code";
    private const string TokenUrl = "https://github.com/login/oauth/access_token";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public sealed class StartResult
    {
        public string DeviceCode { get; set; } = "";
        public string UserCode { get; set; } = "";
        public string VerificationUri { get; set; } = "";
        /// <summary>建议轮询间隔（秒）。</summary>
        public int Interval { get; set; } = 5;
        public int ExpiresIn { get; set; } = 900;
    }

    /// <summary>第一步：申请设备码。失败返回 null，err 里是原因。</summary>
    public static async Task<(StartResult? r, string err)> StartAsync(string clientId, string scope,
                                                                       CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) clientId = DefaultClientId;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, CodeUrl);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "deepsleep-connector");
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = string.IsNullOrWhiteSpace(scope) ? "repo read:user" : scope,
            });
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (null, $"GitHub 返回 HTTP {(int)resp.StatusCode}：{Short(body)}");
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var e))
                return (null, "申请设备码失败：" + (r.TryGetProperty("error_description", out var ed) ? ed.GetString() : e.GetString()));
            var res = new StartResult
            {
                DeviceCode = Str(r, "device_code"),
                UserCode = Str(r, "user_code"),
                VerificationUri = Str(r, "verification_uri") is { Length: > 0 } u ? u : "https://github.com/login/device",
                Interval = r.TryGetProperty("interval", out var iv) && iv.TryGetInt32(out int ivi) ? Math.Max(5, ivi) : 5,
                ExpiresIn = r.TryGetProperty("expires_in", out var ex) && ex.TryGetInt32(out int exi) ? exi : 900,
            };
            if (res.DeviceCode.Length == 0 || res.UserCode.Length == 0)
                return (null, "GitHub 返回的设备码不完整：" + Short(body));
            return (res, "");
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>第二步：轮询直到用户授权 / 过期 / 被拒 / 取消。 onTick 每次轮询回调一次，方便界面显示「还在等你授权…（剩余 N 秒）」。</summary>
    public static async Task<(bool ok, string token, string err)> PollAsync(
        string clientId, StartResult start, CancellationToken ct = default,
        Action<int, int>? onTick = null)
    {
        if (string.IsNullOrWhiteSpace(clientId)) clientId = DefaultClientId;
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, start.ExpiresIn));
        int interval = Math.Max(5, start.Interval);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            int left = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalSeconds);
            onTick?.Invoke(interval, left);
            try { await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return (false, "", "已取消"); }

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                req.Headers.TryAddWithoutValidation("User-Agent", "deepsleep-connector");
                req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["device_code"] = start.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                });
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                var r = doc.RootElement;

                if (r.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String)
                {
                    string token = at.GetString() ?? "";
                    if (token.Length > 0) return (true, token, "");
                }
                string err = Str(r, "error");
                switch (err)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval = Math.Min(60, interval + 5);
                        continue;
                    case "expired_token":
                        return (false, "", "设备码已过期，重新点一次登录");
                    case "access_denied":
                        return (false, "", "你取消了授权");
                    case "device_flow_disabled":
                        return (false, "", "这个 OAuth App 没启用 device flow，换一个 client_id 或改用令牌");
                    case "":
                        return (false, "", "GitHub 返回了无法识别的响应：" + Short(body));
                    default:
                        return (false, "", "授权失败：" + (Str(r, "error_description") is { Length: > 0 } d ? d : err));
                }
            }
            catch (OperationCanceledException) { return (false, "", "已取消"); }
            catch (Exception ex) { return (false, "", ex.Message); }
        }
        return (false, "", "等太久没授权，已超时（可重新点一次登录）");
    }

    private static string Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static string Short(string s) => (s ?? "").Length <= 200 ? (s ?? "") : s[..200] + "…";
}
