using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler;

/// <summary>
/// 图像生成客户端：调用智谱 CogView-3-Flash（OpenAI 兼容 images/generations 接口）。
/// 生成的图片下载保存到本地，返回本地路径供聊天展示。
/// </summary>
public sealed class ImageGenClient : IDisposable
{
    private readonly HttpClient _http = new();

    public string Url { get; set; } = "https://open.bigmodel.cn/api/paas/v4/images/generations";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "cogview-3-flash";
    public string SaveDir { get; set; } = "";

    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>生成图片并保存到本地，返回本地文件路径；失败返回 null。</summary>
    public async Task<string?> GenerateAsync(string prompt, string? size = null,
                                             CancellationToken ct = default)
    {
        if (!Configured || string.IsNullOrWhiteSpace(prompt)) return null;
        try
        {
            var payload = new
            {
                model = Model,
                prompt = prompt,
                size = string.IsNullOrWhiteSpace(size) ? "1024x1024" : size,
                response_format = "url",
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(180));

            using var req = new HttpRequestMessage(HttpMethod.Post, Url);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey.Trim());

            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            string body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0) return null;
            var first = data[0];

            byte[] bytes;
            if (first.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
            {
                bytes = Convert.FromBase64String(b64.GetString()!);
            }
            else if (first.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
            {
                using var imgResp = await _http.GetAsync(u.GetString(), cts.Token).ConfigureAwait(false);
                if (!imgResp.IsSuccessStatusCode) return null;
                bytes = await imgResp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            }
            else return null;

            string dir = string.IsNullOrWhiteSpace(SaveDir)
                ? Path.Combine(Path.GetTempPath(), "agent_images")
                : SaveDir;
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir,
                $"img_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}.png");
            File.WriteAllBytes(file, bytes);
            return file;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
