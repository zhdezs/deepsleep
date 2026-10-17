using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler;

/// <summary>一条模型记录（联网拉取的结果）。</summary>
public sealed class ModelEntry
{
    /// <summary>模型 ID —— 发给 API 的就是这个（如 <c>zai-org/GLM-5.3-Flash</c>）。</summary>
    public string Id { get; init; } = "";
    /// <summary>人类可读名字（很多平台不提供，回退成 Id）。</summary>
    public string Label { get; init; } = "";
    /// <summary>是否确认免费（只有能拿到定价信息的平台才判得准，如 OpenRouter）。</summary>
    public bool Free { get; init; }
}

/// <summary>
/// 联网获取模型列表。
/// <para>
/// 各家的「列模型」端点并不统一，这里按已知规律推导：
/// OpenAI 兼容 → 把结尾的 <c>/chat/completions</c> 换成 <c>/models</c>；
/// Gemini 原生 → <c>:generateContent</c> 换成 <c>/models</c> 并把 Key 放到 <c>?key=</c>；
/// Anthropic → <c>/v1/messages</c> 换成 <c>/v1/models</c>，Key 走 <c>x-api-key</c> 头。
/// </para>
/// <para>零第三方依赖，只用 System.Net.Http + System.Text.Json，和 ApiClient 保持一致。</para>
/// </summary>
public static class ModelCatalog
{
    /// <summary>把「对话地址」推导成「模型列表地址」。推不出来时返回 null（界面会提示手动填）。</summary>
    public static string? ModelsUrl(string url, string format)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        string u = url.Trim();
        string lower = u.ToLowerInvariant();

        // OpenAI 兼容：/chat/completions → /models
        int cc = lower.IndexOf("/chat/completions", StringComparison.Ordinal);
        if (cc >= 0) return u[..cc] + "/models";

        // Gemini 原生：.../models/{model}:generateContent → .../models
        if (format == "gemini" || lower.Contains(":generatecontent"))
        {
            int gi = lower.IndexOf("/models/", StringComparison.Ordinal);
            if (gi >= 0) return u[..(gi + "/models".Length)];
            if (lower.Contains("/models")) return u;
            return null;
        }

        // Anthropic：/v1/messages → /v1/models
        int mm = lower.IndexOf("/messages", StringComparison.Ordinal);
        if (mm >= 0) return u[..mm] + "/models";

        // 已经写了 /models 就直接用；只写了主机名 / /v1 就补标准路径
        if (lower.Contains("/models")) return u;
        if (lower.Contains("/responses"))
        {
            int ri = lower.IndexOf("/responses", StringComparison.Ordinal);
            return u[..ri] + "/models";
        }
        string t = u.TrimEnd('/');
        if (t.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ||
            t.EndsWith("/v4", StringComparison.OrdinalIgnoreCase) ||
            t.EndsWith("/v3", StringComparison.OrdinalIgnoreCase))
            return t + "/models";
        return t + "/v1/models";
    }

    /// <summary>
    /// 拉取模型列表。<paramref name="url"/> 传的是对话地址即可（内部自动推导 models 地址）。
    /// 失败返回 <c>ok=false</c> 与一句人话原因，界面直接显示。
    /// </summary>
    public static async Task<(bool Ok, string? Error, List<ModelEntry> Models)> FetchAsync(
        string url, string apiKey, string format, int timeoutSeconds = 25, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (false, "还没填 API Key —— 先点上面「免费领取 Key」拿到再粘进来。", new());

        string? modelsUrl = ModelsUrl(url, format);
        if (modelsUrl == null)
            return (false, "无法从这个地址推导出模型列表端点，请确认「API 地址」填的是对话接口（含 /chat/completions 或 /v1 前缀）。", new());

        bool anthropic = format == "anthropic" ||
                         modelsUrl.Contains("/v1/models", StringComparison.OrdinalIgnoreCase) && url.Contains("/messages", StringComparison.OrdinalIgnoreCase);

        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "deepsleep/3.1 (+https://github.com/zhdezs/deepsleep)");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");

            string requestUrl = modelsUrl;
            if (anthropic)
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey.Trim());
                http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }
            else if (format == "gemini" && !requestUrl.Contains("/openai/", StringComparison.OrdinalIgnoreCase))
            {
                // Gemini 原生接口用 ?key= 传凭据，不放 Authorization
                requestUrl = requestUrl + (requestUrl.Contains('?') ? "&" : "?") + "key=" + Uri.EscapeDataString(apiKey.Trim());
            }
            else
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Trim());
            }

            using var resp = await http.GetAsync(requestUrl, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string hint = (int)resp.StatusCode switch
                {
                    401 or 403 => "（Key 不对或没开通该平台）",
                    404 => "（这个平台可能不提供 /models 端点，手动填模型名即可）",
                    429 => "（触发限流，稍后再试）",
                    >= 500 => "（平台服务端错误）",
                    _ => "",
                };
                string snippet = body.Length > 200 ? body[..200] : body;
                return (false, $"HTTP {(int)resp.StatusCode}{hint} {snippet}", new());
            }

            var list = Parse(body, out string? parseError);
            if (list.Count == 0)
                return (false, parseError ?? "接口有响应，但没解析出任何模型。", new());

            // 免费的排前面，其余按名字排；数量做个上限防止界面卡
            var ordered = list
                .OrderByDescending(m => m.Free)
                .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .Take(600)
                .ToList();
            return (true, null, ordered);
        }
        catch (TaskCanceledException)
        {
            return (false, $"请求超时（{timeoutSeconds}s）—— 可能是网络不通，或该平台需要梯子。", new());
        }
        catch (Exception ex)
        {
            return (false, "拉取失败：" + ex.Message, new());
        }
    }

    /// <summary>解析各家五花八门的模型列表响应。</summary>
    private static List<ModelEntry> Parse(string body, out string? error)
    {
        error = null;
        var result = new List<ModelEntry>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            JsonElement arr = default;
            bool found = false;
            if (root.ValueKind == JsonValueKind.Array) { arr = root; found = true; }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // OpenAI / Anthropic / OpenRouter / Groq / NVIDIA：{ "data": [ ... ] }
                if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array) { arr = d; found = true; }
                // Gemini 原生：{ "models": [ ... ] }
                else if (root.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array) { arr = m; found = true; }
                // 少数平台包一层：{ "data": { "models": [ ... ] } }
                else if (root.TryGetProperty("data", out var d2) && d2.ValueKind == JsonValueKind.Object &&
                         d2.TryGetProperty("models", out var m2) && m2.ValueKind == JsonValueKind.Array) { arr = m2; found = true; }
                // 智谱等：{ "result": [...] } / { "items": [...] }
                else if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Array) { arr = r; found = true; }
                else if (root.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array) { arr = it; found = true; }
            }

            if (!found) { error = "响应里没有找到模型数组（data / models / result）。"; return result; }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                string id = Str(item, "id") ?? Str(item, "model") ?? Str(item, "name") ?? "";
                id = id.Trim();
                if (id.Length == 0) continue;
                // Gemini 的 id 形如 models/gemini-2.5-flash，去掉前缀才是调用时该写的名字
                if (id.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) id = id["models/".Length..];
                if (!seen.Add(id)) continue;

                string label = Str(item, "displayName") ?? Str(item, "display_name") ?? Str(item, "name") ?? id;
                if (label == id || label.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) label = id;

                result.Add(new ModelEntry { Id = id, Label = label, Free = IsFree(item, id) });
            }
        }
        catch (JsonException ex)
        {
            error = "返回的不是合法 JSON（可能被网关/登录页拦截了）：" + ex.Message;
        }
        return result;
    }

    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>判断是否免费：OpenRouter 的 :free 后缀 / pricing 为 0；也认一些显式字段。</summary>
    private static bool IsFree(JsonElement o, string id)
    {
        if (id.EndsWith(":free", StringComparison.OrdinalIgnoreCase)) return true;

        if (o.TryGetProperty("pricing", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            if (Zero(Str(p, "prompt")) && Zero(Str(p, "completion"))) return true;
        }
        if (o.TryGetProperty("is_free", out var f) && f.ValueKind == JsonValueKind.True) return true;
        if (o.TryGetProperty("free", out var f2) && f2.ValueKind == JsonValueKind.True) return true;
        return false;

        static bool Zero(string? s)
            => s != null && decimal.TryParse(s, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out decimal d) && d == 0m;
    }
}
