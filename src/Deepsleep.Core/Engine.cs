using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TrollWrangler;

/// <summary>
/// 回怼引擎（内核）：攻击分类（朴素贝叶斯，真训练）、加权强度评估、文本脱敏、
/// 上下文记忆、复读检测、策略自适应选择、实时组装回怼，
/// 以及「对话 → 训练数据 → 模型进化」的自训练闭环（字符级语言模型 + 增量训练）。
/// 零第三方依赖，纯 C#。
/// </summary>
public sealed class Engine
{
    private static readonly Random Rng = new();

    public sealed class Reply
    {
        public string TrollText = "";
        public string Response = "";
        public string Strategy = "";
        public string StrategyName = "";
        public string Category = "";
        public string CategoryName = "";
        public int Aggression = 0;
        public int Turns = 0;
        public string Source = "local";
    }

    // ---- 本地大模型（Ollama）+ 外部 API ----
    private readonly OllamaClient _llm = new();
    private readonly ApiClient _api = new();

    /// <summary>LLM 使用模式："local" 纯本地 / "auto" 智能判断 / "llm" 本地大模型 / "api" 外部 API。</summary>
    public string Mode = "auto";

    /// <summary>Ollama 快模型名（默认 1.5b，简单攻击用）。</summary>
    public string LlmModel
    {
        get => _llm.Model;
        set => _llm.Model = value;
    }

    /// <summary>Ollama 强模型名（默认 qwen3:4b，复杂攻击「适当时机」才用）。</summary>
    public string StrongModel = "qwen3:4b";

    /// <summary>最近一次 LLM 探测结果。</summary>
    public bool LlmAvailable => _llm.Available;

    /// <summary>外部 API 是否已配置 key。</summary>
    public bool ApiConfigured => _api.Configured;

    /// <summary>应用配置（可改模型名 / API 地址 / key）。</summary>
    private AppConfig? _config;

    /// <summary>注入配置并应用到客户端。</summary>
    public void ApplyConfig(AppConfig cfg)
    {
        _config = cfg;
        _llm.Model = cfg.OllamaModel;
        _llm.BaseUrl = cfg.OllamaUrl;
        StrongModel = cfg.OllamaModelStrong;
        _api.Url = cfg.ApiUrl;
        _api.Model = cfg.ApiModel;
        _api.ApiKey = cfg.ApiKey;
    }

    // ---- 自训练模型 ----
    private NgramModel _lm = new();
    private NaiveBayes _nb = new();
    private string _lmPath = "model_lm.json";
    private string _nbPath = "model_nb.json";
    private bool _lmReady = false;

    /// <summary>语言模型已加载且可用（有语料）时返回 true。</summary>
    public bool ModelReady => _lmReady;

    public int ModelVocabSize => _lm.VocabSize;
    public int NbClassCount => _nb.ClassCount;

    private sealed class Turn
    {
        public string Text = "";          // 对方发言（脱敏后）
        public string Category = "";
        public int Aggression = 0;
        public List<string> Quotes = new();   // 该轮抽取的核心片段
    }

    private sealed class State
    {
        public int Turns = 0;
        public string LastStrategy = "";
        public int LastAggression = 0;
        public List<Turn> History = new();   // 对方历史轮次（含上下文）
        public PendingTurn? Pending;          // 待用户确认后学习的本轮样本
    }

    /// <summary>待用户确认的一轮对话（分类器只学习用户确认过的样本）。</summary>
    private sealed class PendingTurn
    {
        public string TrollText = "";
        public string Category = "";
    }

    private readonly Dictionary<int, State> _state = new();
    private readonly Dictionary<int, LinkedList<string>> _recent = new();

    public void NewSession(int sid)
    {
        _state[sid] = new State();
        _recent[sid] = new LinkedList<string>();
    }

    // ======================================================================
    // 〇、自训练模型：加载 / 保存 / 喂数据
    // ======================================================================

    /// <summary>设置模型持久化目录（每个会话独立模型文件，互不干扰）。</summary>
    public void SetModelDir(string dir)
    {
        _lmPath = System.IO.Path.Combine(dir, "model_lm.json");
        _nbPath = System.IO.Path.Combine(dir, "model_nb.json");
        LoadModels();
    }

    private void LoadModels()
    {
        _lm = NgramModel.Load(_lmPath);
        _nb = NaiveBayes.Load(_nbPath);
        _lmReady = _lm.VocabSize > 0;

        // 健康检查：如果加载的 NB 模型没有 "general" 类（说明从未见过非攻击样本，
        // 任何调侃词都会被错分到威胁类），强制重置重新种子训练
        if (_nb.ClassCount > 0 && !_nb.HasClass("general"))
        {
            _nb = new NaiveBayes();
            try { System.IO.File.Delete(_nbPath); } catch { /* ignore */ }
        }

        // 首次启动：用内置策略语料做「种子训练」，让模型立刻具备基础回怼能力
        if (_lm.VocabSize == 0)
            SeedTrain();
    }

    /// <summary>用内置语料做种子训练（首次启动时），保证模型立刻可用。</summary>
    private void SeedTrain()
    {
        // 语言模型：先收集语料 → 学 BPE 词表 → 全量训练
        foreach (var s in Data.Strategies().Values)
            foreach (var line in s.FirstLines.Concat(s.FollowUps).Concat(s.EscalateLines))
                _lm.Collect(line);
        foreach (var s in Data.Sharp().Values)
            foreach (var line in s.FirstLines.Concat(s.FollowUps).Concat(s.EscalateLines))
                _lm.Collect(line);
        foreach (var line in Data.QuickCuts()) _lm.Collect(line);
        foreach (var line in Data.Finishers()) _lm.Collect(line);
        foreach (var line in Data.QuoteOpens()) _lm.Collect(line.Replace("{q}", "你这句话"));
        foreach (var line in Data.ElegantLines()) _lm.Collect(line);   // 优雅反击金句
        _lm.FinalizeTrain();

        // 分类器种子训练：内置关键词映射为带标签样例
        foreach (var kv in Data.CategoryKeywords())
            foreach (var w in kv.Value)
                _nb.Train(w, kv.Key);

        // 加一批「非攻击」样本作为 general 类，避免任何调侃词被错分到攻击类
        foreach (var t in Data.NonAttackSamples())
            _nb.Train(t, "general");

        _lmReady = true;
    }

    /// <summary>保存模型到磁盘（供关闭/归档时调用）。</summary>
    public void SaveModels()
    {
        if (_lm.VocabSize > 0) _lm.Save(_lmPath);
        if (_nb.ClassCount > 0) _nb.Save(_nbPath);
    }

    /// <summary>语言模型自动学习我方「新生成」的回复（去重，避免过拟合）。</summary>
    /// <remarks>
    /// 只学我方回怼（回怼风格），不学对方攻击语（否则会复述对方的话）；
    /// 骨架拼装的结果种子训练已含，不重复学；学习前剥除「你说「xxx」——」这类引用锚点前缀，
    /// 否则语料会固化「当前话 → 历史回复」配对，导致后续采样把早期对话语句复读出来。
    /// </remarks>
    private void LearnResponse(string response)
    {
        if (_learnedResponses.Contains(response)) return;
        _lm.Learn(StripQuoteAnchor(response));
        _learnedResponses.Add(response);
        _lmReady = true;
    }

    /// <summary>用户点击「接受并学习」后，把该轮样本喂给分类器（避免自我标注偏差）。</summary>
    public void ConfirmTurn(int sid)
    {
        if (!_state.TryGetValue(sid, out var st) || st.Pending == null) return;
        _nb.Train(st.Pending.TrollText, st.Pending.Category);
        _lmReady = true;
        st.Pending = null;
    }

    private readonly HashSet<string> _learnedResponses = new();

    // ======================================================================
    // 一、加权攻击强度评估
    // ======================================================================

    // 高强度词（辱骂/威胁/极端），命中权重更大
    private static readonly HashSet<string> HeavyWords = new()
    {
        "傻逼", "煞笔", "蠢货", "智障", "脑残", "白痴", "废物", "垃圾", "人渣",
        "贱人", "去死", "弄死", "砍你", "nmsl", "cnm", "草泥马", "操你", "妈逼",
        "狗东西", "杂种", "狗娘养", "闭嘴", "滚", "活该", "举报你", "人肉",
        "曝光你", "让你好看", "全网挂",
        // 补充常用高攻击词
        "脑子有坑", "有病", "你配", "你也配", "你算老几", "你懂什么", "没脑子",
        "没素质", "丢人现眼", "什么玩意", "狗屁不通", "社会的悲哀", "悲哀",
        "废物一个", "滚吧", "有多远滚", "死", "去死吧", "臭", "脏东西",
        "小丑", "巨婴", "犬吠", "吠", "泼妇", "疯子",
    };

    // 非攻击性噪音词（调侃/嘲讽但不是真正的攻击，命中要扣分）
    private static readonly HashSet<string> NoiseWords = new()
    {
        "哈哈", "哈哈哈", "呵呵", "嘻嘻", "嘿嘿",
        "破防", "破防了", "在意", "在意我", "好几句",
        "看懂", "加油", "别急", "算了", "随便",
        "你说的", "挺", "真", "确实", "哈哈哈哈",
    };

    private static int AggressionScore(string text)
    {
        int score = 0;
        var counted = new HashSet<string>();   // 已计过分的词，避免重复累计

        // 关键词加权：重词 +3，普通词 +1
        foreach (var kv in Data.CategoryKeywords())
        {
            foreach (var w in kv.Value)
            {
                if (text.Contains(w))
                {
                    score += HeavyWords.Contains(w) ? 3 : 1;
                    counted.Add(w);
                }
            }
        }

        // HeavyWords 独立扫描：只覆盖关键词表「未收录」的高攻击词，
        // 跳过已计分的词，避免同一词被计两次导致强度虚高（虚高会让 auto 档误升级到 API）
        foreach (var w in HeavyWords)
        {
            if (!counted.Contains(w) && text.Contains(w))
                score += 3;
        }

        // 感叹号：连续感叹号叠加惩罚
        int exclaim = 0;
        foreach (char c in text)
        {
            if (c == '!' || c == '！')
            {
                exclaim++;
                if (exclaim >= 3) score += 2;   // 连续 3 个以上感叹号，情绪爆表
            }
            else if (!char.IsWhiteSpace(c))
            {
                exclaim = 0;
            }
        }
        score += Math.Min(3, exclaim / 2);

        // 全大写/长文本泄愤
        score += Math.Min(3, text.Length / 60);

        // 噪音词扣除（调侃词不算攻击强度）——放在最后，确保前面攻击分占主导
        int noise = 0;
        foreach (var w in NoiseWords)
        {
            if (text.Contains(w))
                noise++;
        }
        score = Math.Max(0, score - noise);

        // 保底：文本明显带攻击意图但关键词不足时，靠感叹号/长度撑起最低档
        if (score <= 0 && exclaim >= 2)
            score = 2;

        return Math.Max(0, Math.Min(10, score));
    }

    // ======================================================================
    // 二、多标签分类（主类 + 是否存在复读）
    // ======================================================================

    // 类别优先级：当多种攻击词同时出现时，优先按「意图明确度」判定，
    // 避免「键盘侠」这类标签词盖过「你懂什么/你算老几」这类强质疑句式。
    private static readonly List<string> CategoryPriority = new()
    {
        "threat",       // 威胁最严重，最优先
        "ad_hominem",   // 直接辱骂
        "doubt",        // 强质疑/贬低
        "provocation",  // 挑衅拉踩
        "label",        // 贴标签（弱，最后）
    };

    private string Classify(string text)
    {
        // 模型已训练且可用 → 用朴素贝叶斯预测（带置信度阈值，置信度不足回退关键词）
        if (_nb.ClassCount > 0)
        {
            var (pred, _, _) = _nb.PredictWithConfidence(text);
            if (pred != "general") return pred;
        }
        // 关键词兜底：按意图明确度优先级
        foreach (var cat in CategoryPriority)
        {
            if (!Data.CategoryKeywords().TryGetValue(cat, out var words)) continue;
            foreach (var w in words)
                if (text.Contains(w))
                    return cat;
        }
        return "general";
    }

    /// <summary>检测对方是否在复读自己之前说过的话（用于触发 echo 策略）。</summary>
    /// <summary>上下文信号：基于上一轮 vs 当前轮，识别对话中可被点破的破绽。</summary>
    private enum CtxSignal
    {
        None,          // 无特殊信号
        Repeat,        // 复读自己之前的话
        Contradiction, // 前后矛盾（上一句 vs 这一句）
        TopicShift,    // 被质询后转移话题
        Backtrack,     // 改口（先 A 后 B 且相互否定）
        Escalation,    // 情绪升级（强度持续上升）
    }

    /// <summary>分析当前轮相对于上一轮的上下文信号。</summary>
    private static CtxSignal AnalyzeContext(Turn cur, Turn? prev, bool rising, int turns)
    {
        if (prev == null) return CtxSignal.None;

        // 1) 复读：核心片段高度重叠（阈值从 3 提到 5，避免「那咋了」这种短词误触）
        foreach (var c in cur.Quotes)
            foreach (var p in prev.Quotes)
                if (c.Length >= 5 && p.Length >= 5 && (c.Contains(p) || p.Contains(c)))
                    return CtxSignal.Repeat;

        // 2) 情绪升级：连续强度上升
        if (rising && turns >= 1 && cur.Aggression >= 6)
            return CtxSignal.Escalation;

        // 3) 转移话题：类别完全不同，且上一轮是高冲突类别、这一轮是 general
        if (prev.Category != cur.Category &&
            cur.Category == "general" && prev.Aggression >= 4)
            return CtxSignal.TopicShift;

        // 4) 前后矛盾/改口：无法可靠判断语义，退化为「类别冲突 + 都有内容」的启发式
        if (cur.Quotes.Count > 0 && prev.Quotes.Count > 0 &&
            cur.Category != prev.Category && cur.Aggression >= 3)
            return CtxSignal.Contradiction;

        return CtxSignal.None;
    }

    // ======================================================================
    // 三、工具
    // ======================================================================

    private static string MaskProfanity(string text)
    {
        foreach (var w in Data.SensitiveWords())
            text = text.Replace(w, "**");
        return text;
    }

    private static readonly HashSet<char> PunctChars = new("，。！？；：、,.!?;:…“”‘’\"'（）()【】[]《》〈〉<> \t\n".ToCharArray());

    private static List<string> ExtractQuotes(string text, int maxLen = 14, int maxN = 3)
    {
        string t = MaskProfanity(text).Replace("**", "");
        foreach (var w in EmptyWords()) t = t.Replace(w, "");

        var sb = new StringBuilder();
        foreach (char c in t) sb.Append(PunctChars.Contains(c) ? ' ' : c);
        t = sb.ToString();

        var cands = t.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(s => s.Trim(PunctChars.ToArray()))
                     .Where(s => s.Length >= 2)
                     .ToList();

        var quotes = new List<string>();
        foreach (var c in cands)
        {
            string x = c.Length > maxLen ? c.Substring(0, maxLen) : c;
            if (!quotes.Contains(x)) quotes.Add(x);
            if (quotes.Count >= maxN) break;
        }
        return quotes;
    }

    private static string AttackWord(string text)
    {
        foreach (var kv in Data.CategoryKeywords())
            foreach (var w in kv.Value)
                if (text.Contains(w)) return w;
        return "";
    }

    private static List<string> EmptyWords() => new()
    {
        "傻逼", "煞笔", "傻b", "傻B", "sb", "SB", "nmsl", "cnm", "你妈", "你妈的",
        "操你", "去死", "滚", "垃圾", "废物", "白痴", "智障", "脑残", "蠢货", "蠢猪",
        "人渣", "贱人", "饭桶", "狗东西", "有病", "嘴贱", "吃错药", "活该", "欠教育",
        "没家教", "丢人现眼", "狗屁不通", "什么玩意", "没素质", "呵呵", "哈哈", "笑死",
        "菜", "怂", "你算老几", "你也配", "你配", "没资格", "不配", "有本事",
        "等着瞧", "谁怕谁",
    };

    private static string Fill(string tpl, string quote, string word, int turn)
    {
        tpl = tpl.Replace("{q}", string.IsNullOrEmpty(quote) ? "你上面这段话" : quote);
        tpl = tpl.Replace("{w}", string.IsNullOrEmpty(word) ? "你用的那些词" : word);
        tpl = tpl.Replace("{t}", turn.ToString());
        return tpl;
    }

    private static T Choice<T>(List<T> v) => v[Rng.Next(v.Count)];

    private static List<T> Sample<T>(List<T> v, int n)
    {
        var c = v.OrderBy(_ => Rng.Next()).ToList();
        return c.Take(Math.Min(n, c.Count)).ToList();
    }

    // ======================================================================
    // 四、策略自适应选择
    // ======================================================================

    private string ChooseStrategy(string category, int agg, int sid, string manual, bool repeating)
    {
        var st = _state[sid];
        List<string> pool;
        var strategies = Data.Strategies();

        if (!string.IsNullOrEmpty(manual) && manual != "auto" && strategies.ContainsKey(manual))
        {
            pool = new List<string> { manual };
        }
        else
        {
            // 复读检测优先：对方在重复自己 → 用 echo 精准点破
            if (repeating && st.Turns > 0)
            {
                pool = new() { "echo" };
            }
            else if (agg >= 7 && st.Turns == 0)
            {
                pool = new() { "zen", "care" };
            }
            else if (category == "ad_hominem") pool = new() { "praise", "zen" };
            else if (category == "threat") pool = new() { "care", "zen" };
            else if (category == "doubt") pool = new() { "logic", "question" };
            else if (category == "label") pool = new() { "praise", "zen" };
            else if (category == "provocation") pool = new() { "question", "zen", "echo" };
            else if (category == "general") pool = new() { "question", "logic", "zen" };
            else pool = Data.StrategyOrder();

            // 情绪持续上升：换招 + 优先降火策略
            bool rising = agg > st.LastAggression && agg >= 5;
            if (rising && !string.IsNullOrEmpty(st.LastStrategy) && pool.Count > 1)
            {
                pool.RemoveAll(s => s == st.LastStrategy);
                if (pool.Count == 0) pool = new() { "zen" };
            }
        }
        return Choice(pool);
    }

    // 全局去重：整段回复在会话内不重复（避免车轱辘）
    private string PickUnique(List<string> pool, int sid)
    {
        var used = _recent[sid];
        var candidates = pool.Where(s => !used.TakeLast(8).Contains(s)).ToList();
        if (candidates.Count == 0) candidates = pool;
        string line = Choice(candidates);
        used.AddLast(line);
        while (used.Count > 16) used.RemoveFirst();
        return line;
    }

    // ======================================================================
    // 五、实时组装
    // ======================================================================

    private string Compose(string trollText, string category, string strategy, int agg, int turns, int sid,
                           bool rising, CtxSignal signal = CtxSignal.None, string prevQuote = "")
    {
        var quotes = ExtractQuotes(trollText);
        string quote = quotes.Count > 0 ? quotes[0] : "";
        string word = AttackWord(trollText);

        int nSkel, nCat, nQuick, nDissect;
        if (agg >= 7) { nSkel = Choice(new List<int> { 3, 3, 4 }); nCat = Choice(new List<int> { 2, 2, 3 }); nQuick = Choice(new List<int> { 2, 3 }); nDissect = 2; }
        else if (agg >= 4 || (rising && agg >= 5)) { nSkel = Choice(new List<int> { 2, 2, 3 }); nCat = Choice(new List<int> { 1, 2 }); nQuick = Choice(new List<int> { 1, 2 }); nDissect = quotes.Count >= 2 ? 1 : 0; }
        else { nSkel = Choice(new List<int> { 1, 1, 2 }); nCat = Choice(new List<int> { 0, 1 }); nQuick = 1; nDissect = 0; }

        var skelMap = Data.Skeletons();
        var catMap = Data.CategoryLines();
        var skelPool = skelMap.ContainsKey(strategy) ? skelMap[strategy] : skelMap["question"];
        var catPool = catMap.ContainsKey(category) ? catMap[category] : catMap["general"];

        var skels = Sample(skelPool, nSkel);
        var cats = Sample(catPool, nCat);
        var mid = skels.Concat(cats).OrderBy(_ => Rng.Next()).ToList();

        var dissects = new List<string>();
        if (nDissect > 0 && quotes.Count >= 2)
        {
            string qseq = "";
            for (int i = 0; i < quotes.Count && i < 3; i++)
            {
                if (i > 0) qseq += "…";
                qseq += "「" + quotes[i] + "」";
            }
            for (int i = 0; i < nDissect; i++)
                dissects.Add(Fill(Choice(Data.DissectTemplates()), qseq, word, turns));
        }

        var quicks = new List<string>();
        // 短打池 = 常规短打 + 优雅反击金句（借力打力/降维/关怀反将），混合随机挑
        var quickPool = Data.QuickCuts().Concat(Data.ElegantLines()).ToList();
        for (int i = 0; i < nQuick; i++)
            quicks.Add(Fill(PickUnique(quickPool, sid), quote, word, turns));

        var seq = mid.Concat(dissects).Concat(quicks).OrderBy(_ => Rng.Next()).ToList();

        // 上下文语料：根据检测到的信号，插入一条针对性点破句（放在开头附近，增强连贯感）
        string? ctxLine = null;
        string p = string.IsNullOrEmpty(prevQuote) ? "上一轮" : prevQuote;
        switch (signal)
        {
            case CtxSignal.Repeat:
                ctxLine = Fill(Choice(Data.ExhaustionLines()), quote, word, turns);
                break;
            case CtxSignal.Contradiction:
                ctxLine = Fill(Choice(Data.ContradictionLines()), quote, word, turns).Replace("{p}", p);
                break;
            case CtxSignal.TopicShift:
                ctxLine = Fill(Choice(Data.TopicShiftLines()), quote, word, turns).Replace("{p}", p);
                break;
            case CtxSignal.Backtrack:
                ctxLine = Fill(Choice(Data.BacktrackLines()), quote, word, turns).Replace("{p}", p);
                break;
            case CtxSignal.Escalation:
                ctxLine = Fill(Choice(Data.EscalationLines()), quote, word, turns);
                break;
        }

        var parts = new List<string>();
        if (ctxLine != null)
            parts.Add(ctxLine);   // 上下文点破句置顶，第一时间戳中
        parts.Add(Fill(PickUnique(Data.QuoteOpens(), sid), quote, word, turns));
        parts.AddRange(seq.Select(x => Fill(x, quote, word, turns)));
        parts.Add(Fill(PickUnique(Data.Finishers(), sid), quote, word, turns));

        return MaskProfanity(string.Join("\n", parts));
    }

    public Reply Generate(string text, int sid, string manual = "auto", string tone = "live")
    {
        var r = new Reply
        {
            TrollText = MaskProfanity(text),
            Category = Classify(text),
            Aggression = AggressionScore(text),
        };
        r.CategoryName = Data.CategoryNames().ContainsKey(r.Category) ? Data.CategoryNames()[r.Category] : r.Category;

        if (!_state.ContainsKey(sid)) NewSession(sid);
        var st = _state[sid];

        // 构造当前轮
        var cur = new Turn
        {
            Text = r.TrollText,
            Category = r.Category,
            Aggression = r.Aggression,
            Quotes = ExtractQuotes(text, maxLen: 20, maxN: 2),
        };
        var prev = st.History.Count > 0 ? st.History[^1] : null;

        bool rising = r.Aggression > st.LastAggression;
        CtxSignal signal = AnalyzeContext(cur, prev, rising, st.Turns);
        string prevQuote = prev != null && prev.Quotes.Count > 0 ? prev.Quotes[0] : "";

        // 复读信号强制 echo 策略
        bool repeating = signal == CtxSignal.Repeat;
        r.Strategy = ChooseStrategy(r.Category, r.Aggression, sid, manual, repeating);
        r.StrategyName = (Data.Strategies().ContainsKey(r.Strategy) ? Data.Strategies()[r.Strategy].Name : r.Strategy) + " · 实时组装";

        // 温和/尖锐档：不实时组装，直接从对应语料库按策略采样（零脏话、会话级去重）
        if (tone != "live")
        {
            var corpus = tone == "sharp" ? Data.Sharp() : Data.Strategies();
            if (!corpus.TryGetValue(r.Strategy, out var strat))
                strat = Data.Strategies()[r.Strategy];
            r.Response = PickUnique(
                strat.FirstLines.Concat(strat.FollowUps).Concat(strat.EscalateLines).ToList(), sid);
            r.Source = "local";
            r.StrategyName = (tone == "sharp" ? "尖锐" : "温和") + " · " + strat.Name;
            st.Turns++;
            st.LastStrategy = r.Strategy;
            st.LastAggression = r.Aggression;
            st.History.Add(cur);
            while (st.History.Count > 8) st.History.RemoveAt(0);
            r.Turns = st.Turns;
            st.Pending = new PendingTurn { TrollText = r.TrollText, Category = r.Category };
            return r;
        }

        // 语言模型生成：模型已训练时，优先用采样生成一段更自然的回怼，
        // 并锚定对方原话片段保证相关；失败/过短则回退实时组装。
        string? lmResponse = TryGenerateWithLm(text, r.Category, r.Strategy);
        bool responseIsGenerated = lmResponse != null;
        if (lmResponse != null)
        {
            r.Response = lmResponse;
            r.Source = "local-lm";
            r.StrategyName = r.StrategyName.Replace("实时组装", "自训练模型");
        }
        else
        {
            r.Response = Compose(text, r.Category, r.Strategy, r.Aggression, st.Turns, sid, rising,
                                 signal, prevQuote);
        }

        // 更新状态
        st.Turns++;
        st.LastStrategy = r.Strategy;
        st.LastAggression = r.Aggression;
        st.History.Add(cur);
        while (st.History.Count > 8) st.History.RemoveAt(0);
        r.Turns = st.Turns;

        // 自训练闭环：语言模型自动学习我方新生成的回复；
        // 分类器不自动回喂自我判定结果（避免确认偏差），记录本轮等用户确认后再学。
        if (responseIsGenerated) LearnResponse(r.Response);
        st.Pending = new PendingTurn { TrollText = r.TrollText, Category = r.Category };
        return r;
    }

    // ======================================================================
    // 六、本地大模型（Ollama）接入
    // ======================================================================

    private const string LlmSystemPrompt =
        "你在帮用户回怼一个正在骂人/抬杠的网络键盘侠。你的立场是「站在用户这边反击对方」，\n" +
        "绝不是当和事佬、更不能附和或赞同对方的攻击。铁律：\n" +
        "1. 全文绝不允许出现任何脏话、侮辱性词汇（如：垃圾、废物、傻X、滚、有病、智障等），违者整段作废；\n" +
        "2. 绝对禁止附和对方、禁止认错、禁止说「你说得对」「确实如此」「我承认」这类话——你是反击方；\n" +
        "3. 必须引用对方原话里的关键片段（用引号包起来）逐条拆解、逐一反击，让对方被精准点名，\n" +
        "   要具体指出「他说了什么、错在哪」，不要泛泛而谈；\n" +
        "4. 写成 2~4 行短句（可用换行分隔），信息量足，别只回一句敷衍；\n" +
        "5. 【关键】用真人网友抬杠的口吻：说人话、口语化、短句为主，可以带点损、带点阴阳怪气，\n" +
        "   绝对不要 AI 腔——禁止「首先/其次/综上/总而言之/不难发现/请问/希望我们共同」这类书面套话，\n" +
        "   禁止工整排比、禁止口号、禁止一本正经讲大道理；\n" +
        "6. 保持冷静、居高临下、游刃有余，不跟对方对骂，让对方自讨没趣；\n" +
        "\n【硬规则，违反任意一条整段作废】\n" +
        "· 输出里如果出现「你说得对/确实如此/我承认/我理解/加油/别难过/我们共同」" +
        "「希望/相信/每个人」这类附和、认错、鼓励、空洞升华的话，整段作废，请重新生成；\n" +
        "· 必须用「『』」引用对方原话里至少 1 个关键词片段；\n" +
        "· 必须给出至少 2 个具体拆解：对方说了什么、错在哪、为什么站不住脚；\n" +
        "· 回复总长度 2~4 行，多了就是凑字数；\n" +
        "· 输出末尾必须是句号/问号/感叹号，不能光秃秃一句话；\n" +
        "\n【正反对照（务必学「好的」风格，避开「坏的」风格）】\n" +
        "坏的（❌ 像心理咨询师/键盘侠劝导员）：\n" +
        "  「你说得对，确实是我没有理解你的话，继续加油哦！」\n" +
        "  「『破防』四个字就是你把没经历的事当成必然的体验。」\n" +
        "  「我们每个人都有不同的观点，希望我们能相互理解。」\n" +
        "好的（✅ 像评论区真人损人）：\n" +
        "  「『你懂个屁』这四个字，就是你把没词当成有道理的证据。」\n" +
        "  「『你也配』？配不配不是你定的，你这句话除了嗓门大，没一点分量。」\n" +
        "  「『破防了』——说明你这辈子在网上没赢过，这次终于找到一个赢点，当然高兴。」\n" +
        "  「翻来覆去就这一个词，看来你的词汇量跟你的人一样贫瘠。」\n" +
        "  「你一句『哈哈破防了』就想证明什么？只能证明你见识太少。」\n" +
        "\n【高级技巧：优雅反击（借力打力/降维打击/关怀反将），可选用】\n" +
        "· 借力打力：把对方的攻击反着用。「你说我『狗屁不通』？那你家的狗还挺有文化，能开直播让我见识下吗？」\n" +
        "· 降维打击：「夏虫不可语冰。你的认知天花板就在这，我不怪你。」\n" +
        "· 关怀反将（长辈口吻）：「孩子，隔着屏幕都能感受到你的痛苦，需要推荐个心理医生吗？」\n" +
        "· 反弹大法：「你每多骂一句，都在帮我确认：我刺痛你的地方，比你愿意承认的还要多。」\n" +
        "· 核心心法：不跟对方对骂，而是居高临下、体面碾压，让对方自破防。\n" +
        "\n直接输出回怼内容，不要任何解释与开场白。";

    /// <summary>
    /// 三档强度调度（auto 档核心）：
    /// 返回 0=自训练模型（低强度）、1=本地大模型（中强度）、2=外部 API（高强度）。
    /// </summary>
    private int GetTier(string category, int agg, bool rising, int turns)
    {
        if (Mode == "local") return 0;                 // 纯本地档：永远自训练
        if (Mode == "llm") return 1;                   // 本地大模型档
        if (Mode == "api") return 2;                   // 外部 API 档
        // auto 智能分档
        if (category == "threat") return 2;            // 威胁恫吓：直接上 API
        if (agg >= 7) return 2;                        // 高强度：API
        if (rising && turns >= 2) return 2;            // 持续升级：API
        if (agg >= 4) return 1;                        // 中强度：本地大模型
        return 0;                                      // 低强度：自训练模型
    }

    /// <summary>
    /// 决定本地大模型这档里用「快模型」还是「强模型」。
    /// 强模型慢（4~6 秒），只在「中高强度」时用，简单攻击交给快模型，省时省算力。
    /// </summary>
    private bool ShouldUseStrongModel(string category, int agg, bool rising, int turns)
    {
        if (agg >= 6) return true;              // 中高攻击
        if (category == "threat") return true;  // 威胁恫吓
        if (rising && turns >= 2) return true;  // 持续升级
        return false;
    }

    // 思考链泄漏的特征词：思考型模型把「思考过程」当正文输出时，常以这些词开头/大量出现
    private static readonly string[] ThinkingMarkers =
    {
        "首先", "其次", "然后", "最后", "综上",
        "分析", "用户说", "用户发", "用户问", "对方说", "我需要", "我需要帮",
        "关键点", "关键片段", "回顾", "硬规则", "逐条", "拆解：", "意思是",
        "从例子", "好的风格", "坏的风格", "在中文网络用语", "可能用户", "可能对方",
    };

    /// <summary>判断大模型输出是否「思考链泄漏」而非真正的回怼。</summary>
    private static bool LooksLikeThinking(string text)
    {
        if (text.Length > 400) return true;   // 思考链通常很长
        int hits = 0;
        foreach (var m in ThinkingMarkers)
            if (text.Contains(m)) hits++;
        // 命中 3 个以上思考特征词，判定为思考链泄漏
        return hits >= 3;
    }

    /// <summary>
    /// 剥除回复开头的「引用对方原话」锚点前缀（你说「q」—— / 「q」—— / 就冲你这句「q」。等），
    /// 只保留反击正文。防止训练语料固化「当前话 → 历史回复」配对，
    /// 导致后续采样把早期对话的语句当上下文复读出来。
    /// </summary>
    private static string StripQuoteAnchor(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string t = text.Trim();
        foreach (var pre in new[] { "你上一句说「", "你刚说「", "你说「", "就冲你这句「", "「" })
        {
            if (!t.StartsWith(pre, StringComparison.Ordinal)) continue;
            int close = t.IndexOf('」', pre.Length);
            if (close < 0) continue;
            int cut = close + 1;
            // 跳过「」之后的连接标点（——，。；：！？）
            while (cut < t.Length && "——，。；：！？!?,.; \t".Contains(t[cut])) cut++;
            string rest = t.Substring(cut).Trim();
            if (rest.Length >= 6) return rest;   // 正文足够长才保留，纯引用句不学
            break;
        }
        return text;
    }

    /// <summary>清理大模型输出的残留噪声：方括号引用片段、多余标记等。</summary>
    private static string CleanLlmText(string text)
    {
        // 去掉 ['xxx'] 这类模型自己拼出来的示例引用残留
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\[['\""].*?['\""]\]", "");
        // 去掉成对的方括号内容
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\[[^\]]{0,20}\]", "");
        // 压缩连续空白
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
        return text.Trim();
    }

    /// <summary>
    /// 异步生成：先跑本地引擎拿到分类/强度/策略与状态更新，
    /// 再按模式决定是否调用大模型覆盖回复（失败自动回退本地）。
    /// </summary>
    public async Task<Reply> GenerateAsync(string text, int sid, string manual = "auto", string tone = "live")
    {
        // 先用本地逻辑生成（含分类、强度、策略、状态更新、自训练闭环）
        var r = Generate(text, sid, manual, tone);
        if (tone != "live") return r;   // 温和/尖锐档纯本地，不调用大模型

        // 上一轮强度（用于 auto 判断的「情绪上升」）：从 History 倒数第二条取
        int prevAgg = 0;
        if (_state.ContainsKey(sid) && _state[sid].History.Count >= 2)
            prevAgg = _state[sid].History[^2].Aggression;
        bool isRising = r.Aggression > prevAgg;

        // 三档调度：0=自训练模型 / 1=本地大模型 / 2=外部 API
        int tier = GetTier(r.Category, r.Aggression, isRising, r.Turns);
        if (tier == 0)
            return r;   // 1档：自训练模型（Generate 已生成），不调任何大模型

        // 「适当时机」判断：本地大模型档里用快还是强
        bool useStrong = ShouldUseStrongModel(r.Category, r.Aggression, isRising, r.Turns);
        int numPredict = 300;

        // 2档：本地大模型（Ollama 快/强自适应）；3档：外部 API，失败降级本地大模型
        string? llmText = null;
        string source = "";
        if (tier == 2 && _api.Configured)
        {
            llmText = await _api.ChatAsync(LlmSystemPrompt, text).ConfigureAwait(false);
            source = "api";
        }
        if (string.IsNullOrEmpty(llmText))
        {
            // 2档（或 3档降级）：本地 Ollama
            string model = useStrong ? StrongModel : _llm.Model;
            llmText = await _llm.ChatAsync(LlmSystemPrompt, text, model, numPredict)
                               .ConfigureAwait(false);
            source = useStrong ? "ollama-strong" : "ollama";
        }

        if (string.IsNullOrEmpty(llmText)) return r;   // 失败回退本地
        llmText = llmText.Trim();
        if (llmText.Length < 4) return r;

        // 思考链残留检测：思考型模型（qwen3）偶尔会把「首先…分析…用户说…」
        // 这类思考过程漏进 content。检测到这种明显是思考而非回怼的内容，直接回退本地。
        if (LooksLikeThinking(llmText))
            return r;

        r.Response = MaskProfanity(CleanLlmText(llmText));
        r.Source = source;
        string tag = source switch
        {
            "api" => "外部 API",
            "ollama-strong" => "本地强模型",
            _ => "本地大模型",
        };
        r.StrategyName = r.StrategyName.Replace("实时组装", tag).Replace("自训练模型", tag);

        // 把大模型的高质量回复喂给自训练语言模型，让本地模型越用越强
        // （同样剥除引用锚点前缀，避免早期对话语句被固化进语料）
        LearnResponse(r.Response);
        return r;
    }

    /// <summary>探测 Ollama 是否在线且模型可用。</summary>
    public Task<bool> PingLlmAsync() => _llm.PingAsync();

    /// <summary>尝试用自训练语言模型生成回复；不可用/质量不足返回 null。</summary>
    private string? TryGenerateWithLm(string trollText, string category, string strategy)
    {
        if (_lm.VocabSize == 0) return null;

        // 种子：优先锚定对方原话片段（保证相关），否则用策略话头
        var quotes = ExtractQuotes(trollText, maxLen: 10, maxN: 1);
        string seed;
        if (quotes.Count > 0)
        {
            string q = quotes[0];
            var openers = new[] { $"你说「{q}」——", $"「{q}」——", $"就冲你这句「{q}」。" };
            seed = openers[Rng.Next(openers.Length)];
        }
        else
        {
            seed = strategy switch
            {
                "logic" => "你这段话",
                "question" => "我问你：",
                "praise" => "讲真，",
                "echo" => "你反复说",
                "care" => "你这么大的火气，",
                _ => "我不急，",
            };
        }

        // 多次采样，取第一个「够长、确实锚定了原话、且不是残片拼接」的结果
        for (int i = 0; i < 4; i++)
        {
            double temp = new[] { 0.7, 0.8, 0.9, 1.0 }[Rng.Next(4)];
            string text = _lm.Sample(seed, temperature: temp, maxLen: 150, minLen: 14);
            if (string.IsNullOrEmpty(text)) continue;
            if (text.Length < 10) continue;
            // 若种子来自原话，要求输出包含该片段（保证相关）
            if (quotes.Count > 0 && !text.Contains(quotes[0])) continue;
            if (LooksLikeScraps(text)) continue;   // 残片拼接回退
            return MaskProfanity(text);
        }
        return null;
    }

    /// <summary>
    /// 判断 n-gram 采样结果是否「残片拼接」：
    /// 大量孤立的短残片（如「敢吗」「。，」）、重复词、缺主谓的碎片堆叠。
    /// </summary>
    private static bool LooksLikeScraps(string text)
    {
        // 拆出子句（按标点）
        var clauses = System.Text.RegularExpressions.Regex.Split(text, "[，。！？、；：]")
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (clauses.Count <= 1) return false;

        // ① 存在 3 字以内的孤立残片 ≥ 2 个（「敢吗」「散会」这类词碎片）
        int tiny = clauses.Count(s => s.Length <= 3);
        if (tiny >= 2) return true;

        // ② 末尾是孤零零的语气词/助词收尾（「。，」「。嗯」）
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"[，。]{2,}$")) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(嗯|啊|吧|吗|呢|哈)[，。]{1,3}$")) return true;

        // ③ 同一短词重复 ≥ 3 次（「敢吗，散会，敢吗，」）
        var words = clauses.Where(s => s.Length <= 4).GroupBy(s => s)
                           .Where(g => g.Count() >= 3);
        if (words.Any()) return true;

        return false;
    }

    public List<string> Alternatives(string text, int n = 3, string manual = "auto")
    {
        string category = Classify(text);
        int agg = AggressionScore(text);
        string strategy = ChooseStrategy(category, agg, 0, manual, repeating: false);
        var outList = new List<string>();
        for (int i = 0; i < n; i++)
            outList.Add(Compose(text, category, strategy, agg, 0, 1000 + i, false));
        return outList;
    }
}
