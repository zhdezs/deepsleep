using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler;

/// <summary>
/// 多模态看图客户端：调用智谱 GLM-4.6V-Flash（OpenAI 兼容 chat/completions）。
/// 把本地图片 base64 后与问题一起发给模型，返回文字识别/描述结果。
/// </summary>
public sealed class VisionClient : IDisposable
{
    private readonly HttpClient _http = new();

    public string Url { get; set; } = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "glm-4.6v-flash";

    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>分析本地图片，返回模型回答；失败返回 null。</summary>
    public async Task<string?> AnalyzeAsync(string imagePath, string? question = null,
                                            CancellationToken ct = default)
    {
        if (!Configured || string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return null;
        try
        {
            string b64 = Convert.ToBase64String(File.ReadAllBytes(imagePath));
            string ext = Path.GetExtension(imagePath).ToLowerInvariant();
            string mime = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "image/png",
            };

            var payload = new
            {
                model = Model,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "image_url", image_url = new { url = $"data:{mime};base64,{b64}" } },
                            new { type = "text", text = question ?? "请描述这张图片的内容。" },
                        },
                    },
                },
                temperature = 0.5,
                max_tokens = 800,
                stream = false,
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(120));
            using var req = new HttpRequestMessage(HttpMethod.Post, Url);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey.Trim());

            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            string body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return null;
            return choices[0].GetProperty("message").GetProperty("content").GetString();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
