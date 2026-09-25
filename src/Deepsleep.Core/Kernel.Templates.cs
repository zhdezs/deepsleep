namespace TrollWrangler.Core;

/// <summary>
/// 内核内置的静态内容：免费模型预设、Prompt 模板库、集群分工模板、汇总提示词。
/// 界面（HTML）只负责展示，内容全部来自内核，改内容不用动前端。
/// </summary>
public sealed partial class Kernel
{
    /// <summary>免费大模型预设（官方免费档、OpenAI 兼容；选一个自动填地址和模型名）。</summary>
    public static readonly (string Name, string Url, string Model)[] FreePresets =
    {
        ("智谱 GLM-4.7-Flash（永久免费 · 国内直连）",
            "https://open.bigmodel.cn/api/paas/v4/chat/completions", "glm-4.7-flash"),
        ("硅基流动 SiliconFlow（注册送 2000 万 token）",
            "https://api.siliconflow.cn/v1/chat/completions", "Qwen/Qwen3-8B"),
        ("Groq 免费额度（Llama 3.3 70B）",
            "https://api.groq.com/openai/v1/chat/completions", "llama-3.3-70b-versatile"),
        ("Gemini 2.5 Flash 免费档",
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "gemini-2.5-flash"),
        ("OpenRouter 免费模型（30+）",
            "https://openrouter.ai/api/v1/chat/completions", "qwen/qwen3-coder:free"),
    };

    /// <summary>免费 Key 获取提示（设置面板里显示）。</summary>
    public const string FreeKeyHint =
        "免费 Key 获取：智谱 open.bigmodel.cn（微信登录+实名，永久免费）｜硅基流动 siliconflow.cn（注册送 2000 万 token）｜" +
        "Groq console.groq.com｜Gemini aistudio.google.com｜OpenRouter openrouter.ai";

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
            ("资料调研", "开启联网搜索，收集相关资料并整理要点。"),
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
        "你是 Agent 集群总指挥。下面是各 Agent 的任务与输出，请汇总成结构化总结：关键结论、完成情况、产物路径、遗留问题、下一步建议。只输出中文总结，不要 JSON。";

    /// <summary>集群工作区说明（写进集群 Agent 的额外提示词）。</summary>
    public static string ClusterExtraPrompt(string workspace) =>
        "你处于 Agent 集群中，拥有 boom 全自动权限：执行命令、运行 Python 脚本、读写文件都无需任何确认。" +
        "请把代码/脚本/文档/图片等产物保存到工作区：" + workspace +
        "（如需子目录可自行创建），并在结果里给出产物的完整本地路径。";
}