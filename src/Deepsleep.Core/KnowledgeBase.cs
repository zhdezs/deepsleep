using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TrollWrangler;

/// <summary>
/// deepsleep 知识库：内置文档（安装目录 assets/knowledge/）+ 用户自建文档（data/knowledge/）。
/// <para>
/// 设计取舍：知识可以很多，但不能每次请求都把全文塞进上下文。所以只把一份**目录索引**
/// 注入系统提示词，模型确实需要细节时再调「查知识库」工具按主题取回对应章节全文。
/// 极速模式完全不接这套（不注入索引、工具调用也会被拦），否则多一次往返 + 长文本就不极速了。
/// </para>
/// </summary>
public static class KnowledgeBase
{
    public sealed class Entry
    {
        public string Id = "";
        public string Title = "";
        public string Summary = "";
        public string Keywords = "";
        public string Content = "";
        /// <summary>true = 用户放在 data/knowledge 里的，检索时优先于内置同名条目。</summary>
        public bool User;
    }

    public static string UserDir(string dataDir) => Path.Combine(dataDir, "knowledge");

    private static string BuiltinDir => Path.Combine(AppContext.BaseDirectory, "assets", "knowledge");

    /// <summary>每次调用都重新读盘：用户中途往 data/knowledge 丢文件，不用重启也能查到。</summary>
    public static List<Entry> Load(string dataDir)
    {
        var list = new List<Entry>();
        AddDir(list, BuiltinDir, false);
        AddDir(list, UserDir(dataDir), true);
        // 同名 id 以用户文档为准
        return list.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                   .Select(g => g.Last())
                   .OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    private static void AddDir(List<Entry> list, string dir, bool user)
    {
        if (!Directory.Exists(dir)) return;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        }
        catch { return; }

        foreach (var f in files)
        {
            try
            {
                string raw = File.ReadAllText(f);
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var e = Parse(Path.GetFileNameWithoutExtension(f), raw);
                e.User = user;
                list.Add(e);
            }
            catch { /* 单个坏文档不影响整体 */ }
        }
    }

    /// <summary>支持可选 YAML 风格 front matter：title / summary / keywords；没有就用文件名和首行标题。</summary>
    private static Entry Parse(string id, string raw)
    {
        var e = new Entry { Id = id, Title = id, Content = raw };
        string body = raw.Replace("\r\n", "\n");
        if (body.StartsWith("---\n", StringComparison.Ordinal))
        {
            int end = body.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                string head = body.Substring(4, end - 4);
                foreach (var line in head.Split('\n'))
                {
                    int c = line.IndexOf(':');
                    if (c <= 0) continue;
                    string k = line.Substring(0, c).Trim().ToLowerInvariant();
                    string v = line.Substring(c + 1).Trim();
                    if (k == "title") e.Title = v;
                    else if (k == "summary") e.Summary = v;
                    else if (k == "keywords") e.Keywords = v;
                }
                body = body.Substring(end + 5);
                e.Content = body.Trim();
            }
        }
        if (e.Title == id)
        {
            var m = Regex.Match(body, @"^\s*#\s+(.+)$", RegexOptions.Multiline);
            if (m.Success) e.Title = m.Groups[1].Value.Trim();
        }
        if (string.IsNullOrWhiteSpace(e.Summary))
        {
            var plain = Regex.Replace(body, @"[#*`>\|\-\s]+", " ").Trim();
            e.Summary = plain.Length > 90 ? plain.Substring(0, 90) + "…" : plain;
        }
        return e;
    }

    /// <summary>注入系统提示词的目录索引（只有普通模式、决策阶段会用）。</summary>
    public static string Index(IReadOnlyList<Entry> entries)
    {
        if (entries == null || entries.Count == 0) return "";
        var sb = new StringBuilder("\n\n【deepsleep 知识库 · 可查阅】\n");
        sb.AppendLine("本软件自身的功能规格、工具参数、扩展格式、文本与输出规范、界面设置、发布与排错都写在知识库里。" +
                      "遇到「关于 deepsleep 本身」或需要准确字段/参数的问题，先调用「查知识库」取回对应章节再回答，不要凭印象编。");
        sb.AppendLine("调用：{\"tool_call\":{\"name\":\"查知识库\",\"arguments\":{\"主题\":\"章节名或关键词\"}}}；可查目录：");
        foreach (var e in entries)
            sb.AppendLine($"- {e.Title}（主题可写：{e.Keywords}）—— {e.Summary}");
        return sb.ToString();
    }

    /// <summary>按主题检索，返回最相关的若干章节全文（有总长上限，避免撑爆上下文）。</summary>
    public static string Query(IReadOnlyList<Entry> entries, string? topic, int maxChars = 14000)
    {
        if (entries == null || entries.Count == 0)
            return "知识库为空：安装目录 assets/knowledge/ 下没有内置文档，data/knowledge/ 下也没有用户文档。";
        if (string.IsNullOrWhiteSpace(topic)) return Index(entries) + "\n\n（没给「主题」，先返回目录；请挑一个主题再查一次。）";

        var terms = Terms(topic);
        var scored = entries.Select(e => (e, score: Score(e, terms)))
                            .Where(x => x.score > 0)
                            .OrderByDescending(x => x.score)
                            .ThenBy(x => x.e.Id, StringComparer.OrdinalIgnoreCase)
                            .ToList();

        if (scored.Count == 0)
            return "没查到与「" + topic.Trim() + "」匹配的章节。可用目录：\n" + string.Join("\n", entries.Select(e => "- " + e.Title + "：" + e.Summary)) +
                   "\n\n换个更具体的主题（例如「扩展」「文本规范」「工具参数」「部署」）再查一次。";

        var sb = new StringBuilder();
        sb.AppendLine($"【deepsleep 知识库检索】主题：{topic.Trim()}　命中 {scored.Count} 章\n");
        foreach (var (e, _) in scored)
        {
            string block = $"===== {e.Title}（{e.Id}{(e.User ? " · 用户文档" : "")}）=====\n{e.Content}\n";
            if (sb.Length + block.Length > maxChars)
            {
                int left = maxChars - sb.Length;
                if (left > 300) sb.Append(block.Substring(0, left) + "\n…（本章已截断，可换更具体的主题单独再查）\n");
                break;
            }
            sb.AppendLine(block);
        }
        sb.AppendLine("\n按以上规范作答；引用字段/参数时以这里的原文为准。");
        return sb.ToString();
    }

    private static HashSet<string> Terms(string topic)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string t = topic.Trim();
        if (t.Length > 0) set.Add(t);
        foreach (var w in Regex.Split(t, @"[\s,，、;；:：/\\.。?？!！()（）\[\]""']+"))
            if (w.Length > 0) set.Add(w);
        // 中文没有空格，补 2~3 字滑窗，让「扩展格式」也能命中「扩展」「格式」
        string compact = Regex.Replace(t, @"[\s\p{P}]+", "");
        for (int n = 2; n <= 3; n++)
            for (int i = 0; i + n <= compact.Length; i++)
                set.Add(compact.Substring(i, n));
        return set;
    }

    private static int Score(Entry e, HashSet<string> terms)
    {
        int score = 0;
        foreach (var w in terms)
        {
            if (w.Length < 2) continue;
            if (e.Title.Contains(w, StringComparison.OrdinalIgnoreCase)) score += 12 * w.Length;
            if (e.Keywords.Contains(w, StringComparison.OrdinalIgnoreCase)) score += 8 * w.Length;
            if (e.Summary.Contains(w, StringComparison.OrdinalIgnoreCase)) score += 3 * w.Length;
            int hits = Count(e.Content, w);
            score += Math.Min(hits, 6) * w.Length;
        }
        return score;
    }

    private static int Count(string hay, string needle)
    {
        int n = 0, i = 0;
        while ((i = hay.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
