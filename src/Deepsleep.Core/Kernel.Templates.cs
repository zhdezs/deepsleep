namespace TrollWrangler.Core;

/// <summary>
/// 内核内置的静态内容：免费模型预设、Prompt 模板库、集群分工模板、汇总提示词。
/// 界面（HTML）只负责展示，内容全部来自内核，改内容不用动前端。
/// </summary>
public sealed partial class Kernel
{
    /// <summary>
    /// 免费大模型预设：选中自动填「地址 + 模型名 + 协议格式」三件套，用户只需要再粘一个自己的免费 Key。
    /// <para>
    /// Format 必须一起带出来 —— 以前只填地址和模型名，协议格式留在上一次的选择上，
    /// 选了 Gemini / Anthropic 预设却仍按 anthropic 发请求，直接报错，用户根本不知道为什么。
    /// </para>
    /// <para>
    /// ⚠ 模型 ID 是会变的，加新模型前务必去官方模型广场核一遍。已知的坑：
    /// 硅基流动上智谱的命名空间是 <c>zai-org/</c>（不是 zhipuai / THUDM）；
    /// DeepSeek 官方 Flash 已改名 <c>deepseek-flash</c>；智谱旗舰是 <c>glm-5.3</c>（不是 glm-5）。
    /// </para>
    /// </summary>
    public static readonly (string Name, string Url, string Model, string Format)[] FreePresets =
    {
        // ==== 硅基流动 SiliconFlow（注册送 14 元 ≈ 2000 万 token，国内直连不需要梯子）====
        // 【免费】标注的是官方永久免费档（输入输出均 0 元）
        ("硅基流动 · GLM-5.3-Flash【免费 · 最新 · 看图】",
            "https://api.siliconflow.cn/v1/chat/completions", "zai-org/GLM-5.3-Flash", "openai"),
        ("硅基流动 · Qwen3.5-4B【免费 · 轻量 · 看图】",
            "https://api.siliconflow.cn/v1/chat/completions", "Qwen/Qwen3.5-4B", "openai"),
        ("硅基流动 · Qwen3.5-9B【免费 · 看图】",
            "https://api.siliconflow.cn/v1/chat/completions", "Qwen/Qwen3.5-9B", "openai"),
        ("硅基流动 · Qwen3.5-27B【免费 · 看图】",
            "https://api.siliconflow.cn/v1/chat/completions", "Qwen/Qwen3.5-27B", "openai"),
        ("硅基流动 · Qwen3-8B【免费 · 纯文本】",
            "https://api.siliconflow.cn/v1/chat/completions", "Qwen/Qwen3-8B", "openai"),
        ("硅基流动 · R1-0528 蒸馏【免费 · 推理】",
            "https://api.siliconflow.cn/v1/chat/completions", "deepseek-ai/DeepSeek-R1-0528-Qwen3-8B", "openai"),
        ("硅基流动 · GLM-4.1V-9B-Thinking【免费 · 看图】",
            "https://api.siliconflow.cn/v1/chat/completions", "THUDM/GLM-4.1V-9B-Thinking", "openai"),
        ("硅基流动 · DeepSeek-OCR【免费 · 识别文字】",
            "https://api.siliconflow.cn/v1/chat/completions", "deepseek-ai/DeepSeek-OCR", "openai"),
        ("硅基流动 · PaddleOCR-VL-1.5【免费 · 文档解析 SOTA】",
            "https://api.siliconflow.cn/v1/chat/completions", "PaddlePaddle/PaddleOCR-VL-1.5", "openai"),
        ("硅基流动 · Xing4.0-29B【免费 · 国产昇腾】",
            "https://api.siliconflow.cn/v1/chat/completions", "XingChenAGI/Xing4.0-29B", "openai"),

        // 以下为付费模型，但可直接用「注册赠送的 14 元额度」跑
        ("硅基流动 · GLM-5.3（最新旗舰 · 赠送额度）",
            "https://api.siliconflow.cn/v1/chat/completions", "zai-org/GLM-5.3", "openai"),
        ("硅基流动 · GLM-5.2（旗舰 · 赠送额度）",
            "https://api.siliconflow.cn/v1/chat/completions", "zai-org/GLM-5.2", "openai"),
        ("硅基流动 · DeepSeek-V4-Flash（最新 · 赠送额度）",
            "https://api.siliconflow.cn/v1/chat/completions", "deepseek-ai/DeepSeek-V4-Flash", "openai"),
        ("硅基流动 · DeepSeek-V4-Pro（旗舰 · 赠送额度）",
            "https://api.siliconflow.cn/v1/chat/completions", "deepseek-ai/DeepSeek-V4-Pro", "openai"),
        ("硅基流动 · DeepSeek-V3.2（旗舰 · 赠送额度）",
            "https://api.siliconflow.cn/v1/chat/completions", "deepseek-ai/DeepSeek-V3.2", "openai"),
        ("硅基流动 · Kimi-K2.7-Code（代码 · 赠送额度）",
            "https://api.siliconflow.cn/v1/chat/completions", "moonshotai/Kimi-K2.7-Code", "openai"),
        ("硅基流动 · Kimi-K2.6（智能体 · 赠送额度）",
            "https://api.siliconflow.cn/v1/chat/completions", "moonshotai/Kimi-K2.6", "openai"),
        ("硅基流动 · Qwen3.5-122B-A10B（旗舰 · 赠送额度）",
            "https://api.siliconflow.cn/v1/chat/completions", "Qwen/Qwen3.5-122B-A10B", "openai"),
        ("硅基流动 · LongCat-2.0（智能体 · 赠送额度）",
            "https://api.siliconflow.cn/v1/chat/completions", "meituan-longcat/LongCat-2.0", "openai"),
        ("硅基流动 · Qwen3-235B-A22B（旗舰 · 赠送额度）",
            "https://api.siliconflow.cn/v1/chat/completions", "Qwen/Qwen3-235B-A22B-Instruct", "openai"),

        // ==== 智谱开放平台（微信登录 + 实名，Flash 系永久免费）====
        ("智谱 GLM-5.3（最新旗舰）",
            "https://open.bigmodel.cn/api/paas/v4/chat/completions", "glm-5.3", "openai"),
        ("智谱 GLM-4.7-Flash【永久免费 · 国内直连】",
            "https://open.bigmodel.cn/api/paas/v4/chat/completions", "glm-4.7-flash", "openai"),
        ("智谱 GLM-4.6V-Flash【永久免费 · 看图】",
            "https://open.bigmodel.cn/api/paas/v4/chat/completions", "glm-4.6v-flash", "openai"),

        // ==== DeepSeek 官方（⚠ Flash 已改名 deepseek-flash；无免费档，需充值）====
        ("DeepSeek V4.1-Flash（官方）",
            "https://api.deepseek.com/chat/completions", "deepseek-flash", "openai"),
        ("DeepSeek V4-Pro（官方旗舰）",
            "https://api.deepseek.com/chat/completions", "deepseek-v4-pro", "openai"),

        // ==== 其他家免费档 ====
        ("腾讯混元（免费额度）",
            "https://api.hunyuan.cloud.tencent.com/v1/chat/completions", "hunyuan-lite", "openai"),
        ("Groq 免费额度（Llama 3.3 70B · 极速）",
            "https://api.groq.com/openai/v1/chat/completions", "llama-3.3-70b-versatile", "openai"),
        ("Gemini 2.5 Flash（免费档 · 需梯子）",
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "gemini-2.5-flash", "openai"),
        ("OpenRouter 免费模型（30+ · 需梯子）",
            "https://openrouter.ai/api/v1/chat/completions", "qwen/qwen3-coder:free", "openai"),
    };

    /// <summary>
    /// 可绑定平台（都是 OpenAI 兼容 + 有免费额度），用于设置面板「① 选平台 → ② 领 Key → ③ 拉模型」三步绑定。
    /// <para>
    /// KeyUrl 是「直达 API Key 创建页」的地址，点一下就跳到正确的地方，不用在官网里翻半天。
    /// Quota 写清免费额度的真实口径（很多平台是「每天 N 次」而不是「无限免费」）。
    /// 平台改版后 URL 可能失效，落地页兜底到控制台首页即可，不影响功能。
    /// </para>
    /// </summary>
    public static readonly (string Name, string Url, string Format, string KeyUrl, string Quota)[] Platforms =
    {
        ("硅基流动 SiliconFlow（注册送 14 元 · 国内直连 · 首推）",
            "https://api.siliconflow.cn/v1/chat/completions", "openai",
            "https://cloud.siliconflow.cn/account/ak",
            "注册送 14 元（约 2000 万 token），外加 GLM-5.3-Flash / Qwen3.5 等十余款永久免费模型。国内直连不用梯子，延迟低。"),
        ("魔搭 ModelScope 阿里（每日 2000 次免费 · 国内直连）",
            "https://api-inference.modelscope.cn/v1/chat/completions", "openai",
            "https://modelscope.cn/my/myaccesstoken",
            "每天 2000 次免费调用（单模型上限 200~500 次/天，动态限流），模型库 2 万+。需阿里云账号或中国手机号，并绑定阿里云账号。"),
        ("OpenRouter（25+ 款 :free 永久 0 元 · 支持一键绑定 · 需梯子）",
            "https://openrouter.ai/api/v1/chat/completions", "openai",
            "https://openrouter.ai/settings/keys",
            "⭐ 支持一键绑定：点一下在应用内登录授权，自动拿到专属 Key，不用手动创建复制。" +
            "25+ 款模型 ID 带 :free 后缀永久 $0。默认 50 次/天、20 次/分；历史上充过 $10 后升到 1000 次/天。"),
        ("NanoGPT（聚合多渠道 · 支持一键绑定 · 无地区限制）",
            "https://nano-gpt.com/api/v1/chat/completions", "openai",
            "https://nano-gpt.com/account",
            "⭐ 支持一键绑定：授权登录后自动拿到专属 sk-nano-… Key。聚合多家渠道、按量付费（无免费档），" +
            "适合当作「不用梯子也能到主流模型」的通道。"),
        ("NVIDIA NIM（82+ 免费模型 · 无每日 token 上限 · 需梯子）",
            "https://integrate.api.nvidia.com/v1/chat/completions", "openai",
            "https://build.nvidia.com/settings/api-keys",
            "加入 NVIDIA 开发者计划即可，约 40 次/分，无每日 token 上限。含 DeepSeek-V4、Nemotron、Llama 405B 等 82 款以上。"),
        ("Groq（约 1000 次/天 · 极速 LPU · 需梯子）",
            "https://api.groq.com/openai/v1/chat/completions", "openai",
            "https://console.groq.com/keys",
            "免费约 1000 次/天，LPU 芯片吞吐每秒数百 token。主力是 Llama / Qwen / GPT-OSS 系列。"),
        ("Cerebras（每日约 100 万 token · 需梯子）",
            "https://api.cerebras.ai/v1/chat/completions", "openai",
            "https://cloud.cerebras.ai/",
            "免费每日约 100 万 token、30 次/分，晶圆级芯片吞吐可达每秒数千 token，适合长输出。"),
        ("智谱 BigModel（Flash 系永久免费 · 需实名 · 国内直连）",
            "https://open.bigmodel.cn/api/paas/v4/chat/completions", "openai",
            "https://open.bigmodel.cn/usercenter/apikeys",
            "GLM-4.7-Flash（纯文本）与 GLM-4.6V-Flash（看图）永久免费，仅受 QPS 限速。旗舰 GLM-5.3 需付费。" +
            "⚠ 实测智谱的 /models 只返回「账号已开通」的模型，永久免费的 Flash 系不在其中 —— 这两款请用下面的「模型快速预设」直接选。"),
        ("Moonshot Kimi（Token 不设日上限 · 国内直连）",
            "https://api.moonshot.cn/v1/chat/completions", "openai",
            "https://platform.moonshot.cn/console/api-keys",
            "每分钟 3 次请求，token 消耗不设每日上限，256K 超长上下文。个人认证再送 15 元。"),
        ("Gemini · Google AI Studio（约 1500 次/天 · 需梯子）",
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "openai",
            "https://aistudio.google.com/apikey",
            "免费约 1500 次/天，100 万 token 上下文，能直接读图片/音频/视频。走的是 OpenAI 兼容层，格式选 OpenAI 即可。"),
        ("阿里云百炼 · 通义千问（每模型 100 万 token 免费）",
            "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", "openai",
            "https://bailian.console.aliyun.com/",
            "每个模型 100 万 token 免费额度（有效期 3 个月），覆盖通义千问全系 + DeepSeek + Kimi + GLM，适合多模型对比。"),
        ("火山方舟 · 豆包（每天 200 万 token 免费）",
            "https://ark.cn-beijing.volces.com/api/v3/chat/completions", "openai",
            "https://console.volcengine.com/ark",
            "每天 200 万 token 免费（按天重置）+ 一次性 50 万 token 体验额度。注意模型名要填「接入点 ID」或官方模型 ID。"),
        ("百度千帆 · 文心（ERNIE-Speed 永久免费）",
            "https://qianfan.baidubce.com/v2/chat/completions", "openai",
            "https://console.bce.baidu.com/iam/",
            "ERNIE-Speed / ERNIE-Lite 永久免费（QPS 限速），ERNIE-4.5 系列每模型 100 万 token。"),
        ("Mistral（每月约 10 亿 token 免费 · 需梯子）",
            "https://api.mistral.ai/v1/chat/completions", "openai",
            "https://console.mistral.ai/api-keys",
            "免费档每月约 10 亿 token、1 次/秒，全模型可用（Mistral Large / Small / Codestral）。"),
        ("PPIO 派欧云（送额度 · 国内直连）",
            "https://api.ppinfra.com/v3/openai/chat/completions", "openai",
            "https://ppio.com/settings/key-management",
            "注册送代金券，国内直连，聚合多家开源模型（DeepSeek / Qwen / Llama 等）。"),
        ("无问芯穹 Infini-AI（多款免费 · 国内直连）",
            "https://cloud.infini-ai.com/maas/v1/chat/completions", "openai",
            "https://cloud.infini-ai.com/iam/secretkey",
            "多款模型提供免费调用额度，国内直连，兼容 OpenAI 协议。"),
        ("GitHub Models（用 GitHub 令牌当 Key · 需梯子）",
            "https://models.inference.ai.azure.com/chat/completions", "openai",
            "https://github.com/settings/tokens",
            "用 GitHub PAT（勾 models:read）当 API Key，GPT-4.1 / o4-mini / Llama 等有免费额度，按 GitHub 账号限额。"),
        ("腾讯混元（免费额度 · 国内直连）",
            "https://api.hunyuan.cloud.tencent.com/v1/chat/completions", "openai",
            "https://console.cloud.tencent.com/hunyuan/api-key",
            "hunyuan-lite 等模型提供免费额度，国内直连。"),
        ("阶跃星辰 StepFun（Step 系免费档 · 国内直连）",
            "https://api.stepfun.com/v1/chat/completions", "openai",
            "https://platform.stepfun.com/interface-key",
            "Step 系列提供免费档，国内直连，多模态能力较强。"),
        // ==== 不带免费额度的主流平台（要花钱，但很多人已经有 Key 了）====
        ("OpenAI 官方（付费 · GPT 系列）",
            "https://api.openai.com/v1/chat/completions", "openai",
            "https://platform.openai.com/api-keys",
            "没有免费额度，按量付费。模型名如 gpt-5.2 / gpt-5.2-mini；新出的推理模型可能要走 Responses 协议" +
            "（把下面协议格式改成「OpenAI Responses API」）。"),
        ("Anthropic Claude 官方（付费）",
            "https://api.anthropic.com/v1/messages", "anthropic",
            "https://console.anthropic.com/settings/keys",
            "没有免费额度。模型名如 claude-sonnet-4-5 / claude-opus-4-1。" +
            "协议格式必须是 Anthropic（选本平台时会自动设好）。"),
        ("xAI Grok 官方（付费）",
            "https://api.x.ai/v1/chat/completions", "openai",
            "https://console.x.ai/",
            "没有免费额度。模型名如 grok-4 / grok-4-mini。"),
        ("MiniMax 海螺（送额度）",
            "https://api.minimax.chat/v1/chat/completions", "openai",
            "https://platform.minimaxi.com/user-center/basic-information/interface-key",
            "注册送额度，模型名如 MiniMax-M2。国内直连。"),
        ("零一万物 Yi（送额度）",
            "https://api.lingyiwanwu.com/v1/chat/completions", "openai",
            "https://platform.lingyiwanwu.com/apikeys",
            "注册送额度，模型名如 yi-lightning。国内直连。"),

        // ==== 本地模型（完全免费、不走网络，隐私最好）====
        ("本地模型 · Ollama（完全免费 · 不用联网）",
            "http://127.0.0.1:11434/v1/chat/completions", "openai",
            "https://ollama.com/download",
            "装好 Ollama 后 `ollama pull qwen3:4b`，这里不用填 API Key（随便填个 x 即可）。" +
            "模型名填 ollama list 里的名字，例如 qwen3:4b。注意小模型对工具调用格式很敏感。"),
        ("本地模型 · LM Studio / vLLM / one-api（完全免费）",
            "http://127.0.0.1:1234/v1/chat/completions", "openai",
            "https://lmstudio.ai/",
            "任何暴露 OpenAI 兼容接口的本地服务都行（LM Studio 默认 1234，vLLM 默认 8000，one-api 默认 3000）。" +
            "把「API 地址」改成对应端口即可；Key 随便填。"),

        ("DeepSeek 官方（无免费档 · 需充值）",
            "https://api.deepseek.com/chat/completions", "openai",
            "https://platform.deepseek.com/api_keys",
            "没有免费额度，需要充值。质量稳定、价格低，适合作为主力。注意 Flash 已改名 deepseek-flash。"),
    };

    /// <summary>兜底平台：用户自己填地址时用（KeyUrl 为空表示不显示「去官网拿 Key」按钮）。</summary>
    public static readonly (string Name, string Url, string Format, string KeyUrl, string Quota) CustomPlatform =
        ("自定义 / 其它平台（手动填地址和模型名）", "", "openai", "",
            "直接在下边填 API 地址、模型名、协议格式即可。地址写对话接口，例如 https://xxx/v1/chat/completions。");

    /// <summary>各平台「能不能一键绑定」的说明（界面在按钮下方显示，先讲清楚再让用户点）。</summary>
    public const string OAuthExplain =
        "能一键绑定的平台只有两家：OpenRouter 和 NanoGPT —— 它们开放了第三方应用 OAuth，" +
        "点一下就在应用内打开授权页，登录并同意后自动拿到专属 Key，全程不用手动创建 / 复制。" +
        "　其它平台（硅基流动、魔搭、智谱、Groq、NVIDIA…）不开放这种授权，只能在控制台手工建 Key，" +
        "所以走「按引导绑定」：一键跳到申请页 → 复制 → 粘回来 → 点「测试连接」验证。" +
        "　魔搭虽然也支持 OAuth 登录，但换回的是身份令牌（调不了推理接口），因此同样归在引导式。";

    /// <summary>免费 Key 获取提示（设置面板里显示）。</summary>
    public const string FreeKeyHint =
        "🏆 首选「硅基流动 SiliconFlow」：cloud.siliconflow.cn/account/ak 注册即送 14 元额度（约 2000 万 token），" +
        "国内直连不需要梯子，完全兼容 OpenAI 格式 —— 拿到的 Key 粘到下面「API Key」即可。" +
        "既有永久免费模型（GLM-5.3-Flash / Qwen3.5 系列 / R1 蒸馏 / DeepSeek-OCR / PaddleOCR），" +
        "付费旗舰（GLM-5.3 / DeepSeek-V4-Pro / Kimi-K2.7 / Qwen3.5-122B）也能直接用这笔赠送额度跑。" +
        "　　其他：智谱 open.bigmodel.cn（微信登录+实名，Flash 系永久免费）｜Groq console.groq.com｜" +
        "Gemini aistudio.google.com｜OpenRouter openrouter.ai｜DeepSeek api-docs.deepseek.com（无免费档，需充值）" +
        "　　💡 上面「平台」下拉里已经内置 18 家有免费额度的平台，选中会自动填好地址，点「免费领取 Key」直达申请页，" +
        "拿回来粘上再点「联网获取模型列表」，模型名就不用背了。";

    /// <summary>Prompt 模板库（快捷指令）。</summary>
    public static readonly (string Name, string Prompt)[] PromptTemplates =
    {
        ("翻译", "请把下面的内容翻译成中文，保留语气：\n\n"),
        ("总结要点", "请用要点总结下面的内容：\n\n"),
        ("代码审查", "请审查下面这段代码，找出 bug/隐患/可读性问题并给出改进建议：\n\n"),
        ("生成代码", "请根据下面的需求生成完整可运行的代码：\n\n"),
        ("写周报", "请把下面的工作内容整理成周报格式：\n\n"),
    };

    /// <summary>集群分工模板：一键创建多个不同角色的成员。</summary>
    public static readonly (string Name, (string Role, string Task)[] Members)[] ClusterTemplates =
    {
        ("开发团队", new[]
        {
            ("产品经理", "梳理需求，输出功能清单与验收标准。"),
            ("前端开发", "实现前端页面/组件，代码写到工作目录并说明如何运行。"),
            ("后端开发", "设计并实现接口与数据逻辑，提供代码与调用说明。"),
            ("测试工程师", "编写并运行测试，报告缺陷与修复建议。"),
            ("UI 设计", "用「生成图片」工具产出界面配图/图标，并给出布局建议。"),
        }),
        ("调研团队", new[]
        {
            ("资料调研", "开启联网搜索，用深度研究收集相关资料并整理成报告。"),
            ("数据分析", "写 Python 脚本处理/统计可获得的数据并给出结论。"),
            ("报告撰写", "把调研与数据结果写成结构化报告。"),
            ("质量校对", "检查报告的事实与错漏，给出修订意见。"),
        }),
        ("内容团队", new[]
        {
            ("文案", "撰写主体文案，风格贴合主题。"),
            ("配图", "用「生成图片」工具制作配图并保存。"),
            ("排版", "把文案与配图整理成可发布格式。"),
            ("审核", "检查内容合规性与质量，给出修改建议。"),
        }),
    };

    /// <summary>集群汇总提示词。</summary>
    public const string ClusterSummaryPrompt =
        "你是 Agent 集群总指挥。下面是每个 Agent 的分工、产出和执行过程（工具调用日志）。" +
        "请按成员逐个汇报：谁负责什么、实际做了什么（关键步骤 / 跑过的命令 / 写出的文件）、产出与结论；" +
        "然后再给总体结论、产物路径、遗留问题、下一步建议。" +
        "用小标题 + 短句分点写，别照抄原始日志和命令输出；只输出中文，不要 JSON。";

    /// <summary>集群工作区说明（写进集群 Agent 的额外提示词）。</summary>
    public static string ClusterExtraPrompt(string workspace) =>
        "你处于 Agent 集群中，拥有 boom 全自动权限：执行命令、运行 Python 脚本、读写文件都无需任何确认。" +
        "请把代码/脚本/文档/图片等产物保存到工作区：" + workspace +
        "（如需子目录可自行创建），并在结果里给出产物的完整本地路径。";
}