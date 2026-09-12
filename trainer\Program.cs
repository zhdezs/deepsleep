using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TrollWrangler.Trainer;

/// <summary>
/// 自训练模型进化器（RL 式）：
/// 阶段 A：用 GLM-4-Flash-250414 为键盘侠言论批量生成高质量损系回复，喂给本地模型（校准）。
/// 阶段 B：本地模型自己采样生成回复 → GLM 当评委打分 → 合格则 Learn（奖励），不合格则 Penalize（惩罚）。
/// 产出可直接被主应用加载的 model_lm.json / model_nb.json。
///
/// 用法：以理服人-trainer.exe [--count 60] [--rl 50] [--min-score 7] [--data "应用 data 目录"]
/// </summary>
public static class Program
{
    private const string CounterSystemPrompt =
        "你是「以理服人」回怼引擎，目标是让网络键盘侠破防、自取其辱。用户给你一句攻击言论，请生成一条回怼：\n" +
        "铁律：\n" +
        "1. 全文零脏话、零侮辱词（含谐音），违者作废；\n" +
        "2. 绝不附和、绝不认错、绝不当和事佬；\n" +
        "3. 必须引用对方原话里的关键片段（用「」括起来）逐条拆解、逐条打脸；\n" +
        "4. 语气要损、要阴阳怪气、居高临下、带刺但文明——像评论区老阴阳师，不像心理咨询师；\n" +
        "5. 2~4 行短句为主，结尾用句号/问号/感叹号；\n" +
        "6. 禁止书面套话（首先/其次/综上/不难发现/希望）和 AI 腔。\n" +
        "只输出回怼内容本身，不要解释、不要 JSON。";

    private const string JudgeSystemPrompt =
        "你是「以理服人」质量评委。用户会给你：一条键盘侠言论 + 一条 AI 生成的回怼。请判断回怼是否合格：\n" +
        "合格标准：① 零脏话零侮辱词（含谐音）；② 引用或针对了对方原话；③ 语气损、阴阳怪气但文明；④ 2~4 行；⑤ 不像 AI 腔/劝导员。\n" +
        "只输出一行：score=数字(0-10) 结论=合格|不合格。";

    private static readonly string[] CategoryPriority =
        { "threat", "ad_hominem", "doubt", "provocation", "label" };

    public static async Task<int> Main(string[] args)
    {
        string dataDir = GetArg(args, "--data")
            ?? @"C:\Users\lichenghan\CodeBuddy\20260812123427\winui\dist\以理服人-win-x64\data";
        int count = int.TryParse(GetArg(args, "--count"), out var c) ? c : 60;
        int rl = int.TryParse(GetArg(args, "--rl"), out var r) ? r : 50;
        int minScore = int.TryParse(GetArg(args, "--min-score"), out var ms) ? ms : 7;

        string cfgPath = Path.Combine(dataDir, "config.json");
        if (!File.Exists(cfgPath))
        {
            Console.WriteLine($"找不到配置文件：{cfgPath}");
            return 1;
        }
        using var cfgDoc = JsonDocument.Parse(File.ReadAllText(cfgPath));
        var cfg = cfgDoc.RootElement;
        string key = GetStr(cfg, "counterApiKey", "CounterApiKey") ?? "";
        string model = GetStr(cfg, "counterModel", "CounterModel") ?? "glm-4-flash-250414";
        string url = GetStr(cfg, "counterApiUrl", "CounterApiUrl")
            ?? "https://open.bigmodel.cn/api/paas/v4/chat/completions";
        if (string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("config.json 里没有 counterApiKey，请先在应用设置里填写以理服人 Key。");
            return 1;
        }

        string lmPath = Path.Combine(dataDir, "model_lm.json");
        string nbPath = Path.Combine(dataDir, "model_nb.json");

        Console.WriteLine("加载现有模型（没有则种子初始化）…");
        var lm = NgramModel.Load(lmPath);
        var nb = NaiveBayes.Load(nbPath);
        if (lm.VocabSize == 0) SeedLm(lm);
        if (nb.ClassCount == 0) SeedNb(nb);
        Console.WriteLine($"初始：词表 {lm.VocabSize}，分类器类别 {nb.ClassCount}");

        // ── 阶段 A：GLM 批量生成高质量回复，喂给本地模型（校准） ──
        var samples = BuildSamples(count);
        Console.WriteLine($"阶段 A：校准 {samples.Count} 条键盘侠言论…");
        int okA = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var (troll, cat) = samples[i];
            string? reply = await AskZhipuAsync(key, model, url, CounterSystemPrompt, troll);
            if (string.IsNullOrWhiteSpace(reply)) continue;
            lm.Learn(reply.Trim());
            nb.Train(troll, cat);
            okA++;
            if ((i + 1) % 10 == 0)
                Console.WriteLine($"  …{i + 1}/{samples.Count}（词表 {lm.VocabSize}）");
        }
        Console.WriteLine($"阶段 A 完成：{okA}/{samples.Count} 条入库。");

        // ── 阶段 B：本地模型自评进化（奖励/惩罚） ──
        Console.WriteLine($"阶段 B：RL 进化 {rl} 轮（合格分 ≥{minScore}）…");
        var trollPool = samples.Select(s => s.troll).ToList();
        int reward = 0, punish = 0;
        for (int i = 0; i < rl; i++)
        {
            string troll = trollPool[Random.Shared.Next(trollPool.Count)];
            string? candidate = SampleReply(lm, troll);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                Console.WriteLine($"  [{i + 1}/{rl}] ✗ 本地模型采样失败，跳过");
                continue;
            }
            int score = await JudgeAsync(key, model, url, troll, candidate);
            if (score >= minScore)
            {
                // 奖励增强：高分样本多学几遍，让优质回复在概率分布里占更大权重
                int times = score >= 10 ? 3 : score >= 9 ? 2 : 1;
                for (int k = 0; k < times; k++) lm.Learn(candidate);
                reward++;
                Console.WriteLine($"  [{i + 1}/{rl}] ✓ 奖励（{score} 分 ×{times}）：{candidate[..Math.Min(40, candidate.Length)]}…");
            }
            else
            {
                lm.Penalize(candidate);   // 惩罚：扣减这段 n-gram 概率
                punish++;
                Console.WriteLine($"  [{i + 1}/{rl}] ✗ 惩罚（{score} 分）：{candidate[..Math.Min(40, candidate.Length)]}…");
            }
        }

        lm.Save(lmPath);
        nb.Save(nbPath);
        Console.WriteLine();
        Console.WriteLine($"完成：阶段 A 校准 {okA} 条，阶段 B 奖励 {reward} / 惩罚 {punish}。");
        Console.WriteLine($"词表 {lm.VocabSize}，分类器类别 {nb.ClassCount}");
        Console.WriteLine($"模型已保存：{lmPath} / {nbPath}");
        Console.WriteLine("重启「以理服人」应用后，点亮 🧠 自训练 按钮即可使用进化后的模型。");
        return 0;
    }

    /// <summary>本地模型自己采样一条回复（带对方原话锚点种子）。</summary>
    private static string? SampleReply(NgramModel lm, string troll)
    {
        var quotes = troll.Split(new[] { '，', '。', '！', '？', '、', ' ', ',', '.', '!', '?' },
                                 StringSplitOptions.RemoveEmptyEntries)
                         .Where(q => q.Length >= 2 && q.Length <= 12)
                         .ToList();
        string seed = quotes.Count > 0
            ? $"你说「{quotes[Random.Shared.Next(quotes.Count)]}」——"
            : "我不急，";
        for (int t = 0; t < 6; t++)
        {
            string? text = lm.Sample(seed, temperature: 0.9, maxLen: 120, minLen: 10);
            if (string.IsNullOrWhiteSpace(text)) continue;
            // 要求「生成部分」至少有实质内容（去除 seed 锚点后仍 ≥ 8 字），
            // 避免「你说「xxx」——。」这类空回复进入评委环节浪费 API 调用。
            string body = text;
            int close = body.IndexOf('」');
            if (close >= 0) body = body.Substring(close + 1).Trim('—', '，', '。', ' ', '：', '；');
            if (body.Length < 8) continue;
            if (text.Length <= 400) return text;
        }
        // 兜底：连续采样都失败时，从内置策略语料取一条高质量回怼，保证每轮都有 candidate
        return FallbackLine(quotes);
    }

    /// <summary>采样失败兜底：从内置语料取一条，拼上对方原话锚点（若有）。</summary>
    private static string FallbackLine(List<string>? quotes)
    {
        var pool = new List<string>();
        foreach (var s in Data.Strategies().Values)
            pool.AddRange(s.FirstLines.Concat(s.FollowUps).Concat(s.EscalateLines));
        foreach (var s in Data.Sharp().Values)
            pool.AddRange(s.FirstLines.Concat(s.FollowUps).Concat(s.EscalateLines));
        foreach (var l in Data.QuickCuts()) pool.Add(l);
        foreach (var l in Data.ElegantLines()) pool.Add(l);
        if (pool.Count == 0) return "";
        string line = pool[Random.Shared.Next(pool.Count)];
        if (quotes is { Count: > 0 })
            return $"你说「{quotes[Random.Shared.Next(quotes.Count)]}」——{line}";
        return line;
    }

    /// <summary>GLM 评委：返回 0-10 分。</summary>
    private static async Task<int> JudgeAsync(string key, string model, string url,
                                              string troll, string candidate)
    {
        string user = $"键盘侠言论：{troll}\nAI 回怼：{candidate}";
        string? text = await AskZhipuAsync(key, model, url, JudgeSystemPrompt, user);
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"score\s*=\s*(\d{1,2})");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int score))
            return Math.Clamp(score, 0, 10);
        return 0;
    }

    private static async Task<string?> AskZhipuAsync(string key, string model, string url,
                                                     string system, string user, double temperature = 0.9)
    {
        using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var payload = new
                {
                    model = model,
                    messages = new object[]
                    {
                        new { role = "system", content = system },
                        new { role = "user", content = user },
                    },
                    temperature = temperature,
                    max_tokens = 512,
                    stream = false,
                };
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key.Trim());
                using var resp = await hc.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    // 内容被安全过滤（level=1）：降温重试——更克制的表达通常能通过审核
                    if (body.Contains("contentFilter", StringComparison.OrdinalIgnoreCase) && attempt < 3)
                    {
                        temperature = Math.Max(0.3, temperature - 0.3);
                        await Task.Delay(1500);
                        continue;
                    }
                    if ((int)resp.StatusCode == 429 && attempt < 3)
                    {
                        await Task.Delay(3000 * (attempt + 1));
                        continue;
                    }
                    Console.WriteLine($"  ⚠ HTTP {(int)resp.StatusCode} {body[..Math.Min(80, body.Length)]}");
                    return null;
                }
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.GetProperty("choices")[0]
                          .GetProperty("message").GetProperty("content").GetString();
            }
            catch (Exception ex)
            {
                if (attempt < 3) { await Task.Delay(2000); continue; }
                Console.WriteLine($"  ⚠ {ex.Message}");
                return null;
            }
        }
        return null;
    }

    private static List<(string troll, string category)> BuildSamples(int count)
    {
        var list = new List<(string troll, string category)>();
        foreach (var l in TrollLines) list.Add((l, Classify(l)));

        var templates = new (string cat, string[] tpl)[]
        {
            ("ad_hominem", new[] {
                "你真是个{0}", "你就是个{0}，没救了", "滚吧，{0}",
                "{0}，你就是个{0}", "你这种{0}，还好意思说话", "你这{0}样，真让人恶心",
                "我见过不要脸的，没见过你这么{0}的", "你全家都{0}",
            }),
            ("threat", new[] {
                "你再这样我就{0}", "信不信我{0}",
                "小心我{0}你", "我已经准备好{0}了，你等着",
                "别逼我{0}", "你最好别让我{0}",
            }),
            ("doubt", new[] {
                "{0}？你也配？", "就你也配说这个？{0}",
                "你{0}吗？", "你有什么资格{0}", "先{0}自己再说话",
                "你连{0}都不懂，还敢发言",
            }),
            ("provocation", new[] {
                "不服来辩，{0}", "{0}，有本事来",
                "来啊，{0}啊", "就你？{0}？", "有胆子{0}吗",
                "{0}都不敢，还吹什么",
            }),
            ("label", new[] {
                "你就是个{0}", "典型{0}一个",
                "一看就是{0}", "你这种{0}，最让人看不起",
                "专业{0}就是你", "装什么{0}",
            }),
        };
        foreach (var (cat, tpls) in templates)
            if (Data.CategoryKeywords().TryGetValue(cat, out var kws))
                foreach (var kw in kws)
                    foreach (var t in tpls)
                        list.Add((string.Format(t, kw), cat));

        var seen = new HashSet<string>();
        var result = new List<(string troll, string category)>();
        foreach (var s in list)
            if (seen.Add(s.troll))
                result.Add(s);
        return result.Take(Math.Max(1, count)).ToList();
    }

    private static string Classify(string text)
    {
        foreach (var cat in CategoryPriority)
            if (Data.CategoryKeywords().TryGetValue(cat, out var words))
                foreach (var w in words)
                    if (text.Contains(w, StringComparison.Ordinal))
                        return cat;
        return "general";
    }

    private static void SeedLm(NgramModel lm)
    {
        foreach (var s in Data.Strategies().Values)
            foreach (var line in s.FirstLines.Concat(s.FollowUps).Concat(s.EscalateLines)) lm.Collect(line);
        foreach (var s in Data.Sharp().Values)
            foreach (var line in s.FirstLines.Concat(s.FollowUps).Concat(s.EscalateLines)) lm.Collect(line);
        foreach (var line in Data.QuickCuts()) lm.Collect(line);
        foreach (var line in Data.Finishers()) lm.Collect(line);
        foreach (var line in Data.QuoteOpens()) lm.Collect(line.Replace("{q}", "你这句话"));
        foreach (var line in Data.ElegantLines()) lm.Collect(line);
        lm.FinalizeTrain();
    }

    private static void SeedNb(NaiveBayes nb)
    {
        foreach (var kv in Data.CategoryKeywords())
            foreach (var w in kv.Value) nb.Train(w, kv.Key);
        foreach (var t in Data.NonAttackSamples()) nb.Train(t, "general");
    }

    private static string? GetArg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string? GetStr(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    private static readonly string[] TrollLines =
    {
        "你懂个屁，就你还配发表意见？",
        "你这种键盘侠就是网络蛀虫，滚吧！",
        "呵呵，就你这水平还敢来对线？垃圾",
        "不服来辩啊，你倒是说说你懂什么",
        "你以为你谁啊，装什么大尾巴狼",
        "算了，懒得跟你这种人说，没意思",
        "你妈没教过你怎么做人吗？",
        "你这种人就该被全网骂死",
        "你个废物，连这都不懂",
        "滚出这个帖子，这里不欢迎你",
        "傻逼，别在这刷存在感",
        "你配吗？你算老几？",
        "笑死，你这种智商也敢发言？",
        "你就是个喷子，除了喷还会什么？",
        "键盘侠闭嘴吧，现实里你什么都不是",
        "你是不是闲得慌，在这找存在感？",
        "你这种网络乞丐也配谈三观？",
        "回家种田吧，别在这丢人现眼",
        "你脑子是不是进水了？",
        "这都看不懂，建议回炉重造",
        "你的言论完美展示了什么叫没文化",
        "就这？就这就这？",
        "破防了吧？气急败坏了吧？",
        "你是哪来的勇气说这种话？",
        "举报了，等着被封吧",
        "你这种人就该被拉黑",
        "别装了，你什么水平大家心里有数",
        "你说这些不觉得丢人吗？",
        "没见过你这么厚颜无耻的",
        "键盘侠就是键盘侠，一辈子见不得光",
        "你再多说一句试试？",
        "你这种人也配在网上说话？",
        "笑死我了，你连基本常识都没有",
        "你懂什么叫理吗？你只懂喷",
        "不服憋着，没人惯着你",
        "你的话连标点符号都在暴露你的素质",
        "省省吧，你这种回复我一天见八百个",
        "建议你先把话说利索了再来",
        "你这水平也能自信成这样，佩服",
        "你骂人的词汇量，也就小学生水平",
    };
}
