using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrollWrangler;

/// <summary>
/// 轻量 Markdown 清洗器（无第三方依赖、不创建 UI 元素，杜绝 XAML 绑定崩溃）：
/// 把 Markdown 转成干净可读的纯文本 —— 去掉 **、`、#、``` 等标记符号，
/// 让 AI 输出不再出现 {JSON}、**加粗** 之类的原始符号。
/// </summary>
public static class MarkdownRenderer
{
    public static string ToPlainText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = StripToolJson(text);   // 全局 JSON 过滤器：工具调用 JSON 一律不显示

        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string s = raw.TrimEnd().TrimStart();

            // 围栏代码块：丢弃 ``` 标记行本身，内容原样保留
            if (s.StartsWith("```"))
            {
                sb.AppendLine();
                continue;
            }

            // 分隔线
            if (Regex.IsMatch(s, @"^(-{3,}|\*{3,}|_{3,})$"))
            {
                sb.AppendLine("────────");
                continue;
            }

            // 标题：去掉 # 前缀
            s = Regex.Replace(s, @"^#{1,6}\s+", "");

            // 引用：去掉 > 前缀
            if (s.StartsWith(">")) s = s.TrimStart('>').TrimStart();

            // 无序列表 → 项目符号；有序列表保留编号
            var lm = Regex.Match(s, @"^([-*+])\s+(.*)$");
            if (lm.Success) s = "• " + lm.Groups[2].Value;

            // 链接 [label](url) → label（url）
            s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)]+)\)", "$1（$2）");

            // 行内代码：去掉反引号
            s = Regex.Replace(s, @"`([^`]+)`", "$1");

            // 粗体 / 斜体：去掉 * 标记
            s = s.Replace("**", "");
            s = Regex.Replace(s, @"\*([^*]+)\*", "$1");

            sb.AppendLine(s.TrimEnd());
        }

        // 合并连续空行（最多保留一个空行）
        string result = Regex.Replace(sb.ToString().Trim('\n'), @"\n{3,}", "\n\n");
        return result;
    }

    /// <summary>
    /// 全局 JSON 过滤器：把形如 {"tool":"...","args":{...}} 的工具调用 JSON（含 ```json 围栏、
    /// 包裹在段落里、或格式有误的情况）从显示文本中统一抠掉，杜绝原始 JSON 漏进对话。
    /// </summary>
    public static string StripToolJson(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // 先去掉 ```json ... ``` 围栏里的工具调用
        text = Regex.Replace(text,
            @"```(?:json)?\s*\{[^{}]*""tool""[^{}]*\}\s*```",
            " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOf('{', i);
            if (start < 0)
            {
                sb.Append(text[i..]);
                break;
            }
            int end = FindJsonEnd(text, start);
            if (end > start && LooksLikeToolCall(text[start..end]))
            {
                sb.Append(' ');
                i = end;
                continue;
            }
            sb.Append(text[start]);
            i = start + 1;
        }

        string result = Regex.Replace(sb.ToString(), @"[ \t]{2,}", " ");
        result = Regex.Replace(result, @"\n{3,}", "\n\n");
        return result.Trim();
    }

    private static bool LooksLikeToolCall(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("tool", out var t) && t.ValueKind == JsonValueKind.String;
        }
        catch
        {
            // 解析失败也过滤：只要同时含 "tool" 与 "args" 键特征
            return json.Contains("\"tool\"", StringComparison.Ordinal) &&
                   json.Contains("\"args\"", StringComparison.Ordinal);
        }
    }

    private static int FindJsonEnd(string s, int start)
    {
        int depth = 0;
        bool inStr = false, esc = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) return i + 1; }
        }
        return -1;
    }
}
