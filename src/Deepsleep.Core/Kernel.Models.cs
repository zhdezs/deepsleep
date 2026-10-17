using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace TrollWrangler.Core;

/// <summary>
/// 设置面板「绑定平台」相关的内核命令：
/// <list type="bullet">
/// <item><c>apiModels</c> —— 联网拉取该平台的模型列表（模型列表联网获取）。</item>
/// <item><c>apiTest</c> —— 用当前填的 Key / 模型发一个最小请求，验证能不能通。</item>
/// </list>
/// 两个命令都从参数里取 url/key/model/format，**不依赖已保存的配置** ——
/// 用户可以先填好、点测试、通了再保存，不用「先存一次错的配置再改」。
/// </summary>
public sealed partial class Kernel
{
    private async Task FetchApiModelsAsync(JsonElement a)
    {
        string url = GetStr(a, "url") ?? _config.ApiUrl;
        // Key 没传就退回已保存的：第二次点「获取模型列表」不用重新粘贴
        string key = GetStr(a, "key") ?? "";
        if (string.IsNullOrWhiteSpace(key)) key = _config.ApiKey;
        string format = GetStr(a, "format") ?? _config.ApiFormat;

        var (ok, error, models) = await ModelCatalog
            .FetchAsync(url, key, format)
            .ConfigureAwait(false);

        // 没选过模型就默认用列表第一个。
        // 列表本身已经是「免费优先」排过序的（见 ModelCatalog），所以第一个基本就是
        // 这家最划算的那个 —— 拉到列表还要用户再点一下才敢用，属于多余的一步。
        string autoModel = "";
        bool persisted = false;
        if (models.Count > 0 && string.IsNullOrWhiteSpace(GetStr(a, "model") ?? _config.ApiModel))
        {
            autoModel = models[0].Id;
            // 只有「正在拉的地址就是当前配置的地址」时才落盘：
            // 用户可能只是临时试另一个平台的地址，那种情况不该改到他已保存的配置。
            persisted = string.Equals(url, _config.ApiUrl, StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrWhiteSpace(_config.ApiModel);
            if (persisted)
            {
                _config.ApiModel = autoModel;
                StoreProfile(_config.ApiUrl, _config.ApiKey, _config.ApiModel, _config.ApiFormat,
                             PlatformLabelFor(_config.ApiUrl));
                _config.Save();
                _engine.ApplyConfig(_config);
                _agent.ApplyConfig(_config);
                _clusterAgent.ApplyConfig(_config);
            }
        }

        Emit(new
        {
            ev = "apiModels",
            ok,
            error,
            count = models.Count,
            freeCount = models.Count(m => m.Free),
            autoModel,
            autoSelected = persisted,
            models = models.Select(m => new { id = m.Id, label = m.Label, free = m.Free }).ToList(),
        });
    }

    private async Task TestApiAsync(JsonElement a)
    {        string url = GetStr(a, "url") ?? _config.ApiUrl;
        string key = GetStr(a, "key") ?? "";
        if (string.IsNullOrWhiteSpace(key)) key = _config.ApiKey;
        string format = GetStr(a, "format") ?? _config.ApiFormat;
        string model = GetStr(a, "model") ?? _config.ApiModel;

        if (string.IsNullOrWhiteSpace(key))
        {
            Emit(new { ev = "apiTest", ok = false, error = "还没填 API Key" });
            Toast("⚠ 还没填 API Key —— 先点上面的「免费领取 Key」按钮。", "error");
            return;
        }
        if (string.IsNullOrWhiteSpace(model))
        {
            Emit(new { ev = "apiTest", ok = false, error = "还没选模型" });
            Toast("⚠ 还没填模型名 —— 点「联网获取模型列表」挑一个。", "error");
            return;
        }

        using var client = new ApiClient { Url = url, ApiKey = key, Model = model, Format = format };
        var sw = Stopwatch.StartNew();
        string? reply = await client
            .ChatAsync("你是连通性测试助手。", "只回复两个字：正常", 30)
            .ConfigureAwait(false);
        sw.Stop();

        if (reply == null)
        {
            string err = client.LastError ?? "未知错误";
            Emit(new { ev = "apiTest", ok = false, error = err, ms = sw.ElapsedMilliseconds, model });
            Toast("❌ 连接失败：" + Clip(err, 120), "error");
        }
        else
        {
            string text = reply.Trim();
            Emit(new
            {
                ev = "apiTest",
                ok = true,
                ms = sw.ElapsedMilliseconds,
                model,
                reply = Clip(text, 60),
            });
            Toast($"✅ 连接成功 · {model} · {sw.ElapsedMilliseconds} ms", "info");
        }
    }

    /// <summary>
    /// 一键绑定：走平台的 OAuth 授权页，拿回专属 API Key，**直接写进配置并落盘**，
    /// 再顺手把这个平台能用的模型列表也拉回来。用户全程只需要在授权页点一次「同意」。
    /// </summary>
    private async Task OAuthBindAsync(JsonElement a)
    {
        string id = GetStr(a, "provider") ?? "";
        var provider = OAuthFlow.Find(id);
        if (provider == null)
        {
            Toast("⚠ 这个平台不支持一键绑定，请用「按引导绑定」。", "error");
            Emit(new { ev = "oauthBind", ok = false, error = "不支持的平台：" + id });
            return;
        }

        Toast("🔐 已打开授权页 —— 登录并点「同意」，回来会自动填好 Key。", "info");
        Emit(new { ev = "oauthBind", phase = "waiting", provider = provider.Name });

        try
        {
            // 用户在授权页操作，给足 5 分钟
            string key = await OAuthFlow.RunAsync(provider, 300).ConfigureAwait(false);

            // 直接落盘：绑定的价值就在于「点完就能用」，不该再让用户点一次保存
            _config.ApiKey = key;
            _config.ApiUrl = provider.ApiUrl;
            _config.ApiFormat = provider.Format;
            // 换了一家平台，之前那个模型名大概率在这家不存在（各家模型 ID 不通用）。
            // 清空掉，让下面那次自动拉列表把「第一个」填进来。
            _config.ApiModel = "";
            _config.Save();
            _engine.ApplyConfig(_config);
            _agent.ApplyConfig(_config);
            _clusterAgent.ApplyConfig(_config);

            Emit(new
            {
                ev = "oauthBind",
                ok = true,
                provider = provider.Name,
                keyTail = key.Length > 4 ? key[^4..] : key,
                url = provider.ApiUrl,
                format = provider.Format,
                model = _config.ApiModel,
            });
            EmitSettings();
            Toast($"✅ 已绑定 {provider.Name}，Key 已保存（尾号 {key[^Math.Min(4, key.Length)..]}）。", "info");

            // 绑定完立刻把模型列表也带回来，用户不用再点一次
            await FetchApiModelsAsync(default).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Emit(new { ev = "oauthBind", ok = false, error = ex.Message });
            Toast("❌ 绑定失败：" + Clip(ex.Message, 120), "error");
        }
    }
}
