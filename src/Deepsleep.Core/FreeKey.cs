namespace TrollWrangler;

/// <summary>
/// 内置免费 Key（智谱开放平台的免费模型矩阵：GLM-4.7-Flash 聊天 / GLM-4.6V-Flash 看图 /
/// CogView-3-Flash 画图）。
///
/// 网页版（web/web.js 的 BUILTIN_KEY）一直用它，客户端现在也接上：用户没填自己的 Key 时开箱即用。
/// 这是**共用的一把**，用的人多会被限流 —— 所以只在「用户没填 Key 且地址是智谱」时启用，
/// 绝不会把它发到别家平台去；设置里也能关（UseFreeKey）。
///
/// ⚠ **不覆盖视频生成**：这把 Key 调 CogVideoX 一直回 1305（访问量过大 = 没额度），
///   视频必须用用户自己的 Key，别在这里给它兜底。
/// </summary>
public static class FreeKey
{
    public const string Value = "8020bf5b2eda4be9831cbb3f1d016ca0.clnzkMFtEyt5wKUw";

    public const string ChatUrl = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
    public const string ImageUrl = "https://open.bigmodel.cn/api/paas/v4/images/generations";
    public const string VideoUrl = "https://open.bigmodel.cn/api/paas/v4/videos/generations";

    public const string ChatModel = "glm-4.7-flash";
    public const string VisionModel = "glm-4.6v-flash";
    public const string ImageModel = "cogview-3-flash";
    public const string VideoModel = "cogvideox-flash";

    /// <summary>这个地址是不是智谱（只有智谱认这把 Key，别把它发到别家去）。</summary>
    public static bool IsZhipu(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && url!.Contains("bigmodel.cn", StringComparison.OrdinalIgnoreCase);

    public static bool IsBlank(string? url) => string.IsNullOrWhiteSpace(url);

    /// <summary>没填自己的 Key、且目标是智谱（或没填地址）时，是否该用内置免费 Key。</summary>
    public static bool Applies(bool enabled, string? key, string? url)
        => enabled && string.IsNullOrWhiteSpace(key) && (IsBlank(url) || IsZhipu(url));
}
