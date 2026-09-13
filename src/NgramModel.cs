using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TrollWrangler;

/// <summary>
/// 可增量训练的「词级」n-gram 语言模型（BPE 词表 + trigram + 插值回退 + 温度采样）。
///
/// 关键：字符级 n-gram 在几千句小语料上计数稀疏，采样会生成乱码。
/// 因此先用 BPE（字节对编码）从语料自动学习出「词表」（无监督分词，
/// 把高频字组合并成词），再在词序列上统计 n-gram。生成按「词」采样再拼接，
/// 能产出通顺的中文回复——这是真正从数据训练、且可增量学习的模型。
///
/// 支持：
///   - Collect(text)：收集语料（种子训练时批量喂）；
///   - FinalizeTrain()：学 BPE 词表 + 全量训练词级 n-gram；
///   - Learn(text)：增量训练（用已学词表分词后累加计数，对话即训练数据）；
///   - Sample(seed, temperature)：按词温度采样生成；
///   - Save / Load：持久化（含 BPE 合并表与 n-gram 计数）。
/// </summary>
public sealed class NgramModel
{
    private const string Bos = "\u0001";
    private const string Eos = "\u0002";
    private const string Sep = "\u0003";   // 词对连接符

    private readonly List<string> _corpus = new();       // 训练语料（供学 BPE）
    private readonly List<(string, string)> _merges = new();  // BPE 合并表

    // 词级 n-gram 计数
    private readonly Dictionary<string, int> _uni = new();
    private readonly Dictionary<string, int> _bi = new();      // "a|b"
    private readonly Dictionary<string, int> _tri = new();     // "a|b|c"
    private readonly Dictionary<string, int> _biCtx = new();   // "a"
    private readonly Dictionary<string, int> _triCtx = new();  // "a|b"
    private readonly HashSet<string> _vocab = new();           // 词表

    private static readonly Random Rng = new();
    private static readonly HashSet<char> Punct = new("，。！？；：、,.!?;:…“”‘’\"'（）()【】[]《》〈〉<> \t\n".ToCharArray());

    public int VocabSize => _vocab.Count;
    public int MergeCount => _merges.Count;
    public long TotalTokens { get; private set; }

    // ==================================================================
    // 分词：先按标点切「短语」，短语内部再用 BPE 合并
    // ==================================================================

    private static List<string> PreTokens(string text)
    {
        var toks = new List<string>();
        var buf = new StringBuilder();
        foreach (char c in text)
        {
            if (Punct.Contains(c) || char.IsWhiteSpace(c))
            {
                if (buf.Length > 0) { toks.Add(buf.ToString()); buf.Clear(); }
                toks.Add(c.ToString());
            }
            else
            {
                buf.Append(c);
            }
        }
        if (buf.Length > 0) toks.Add(buf.ToString());
        return toks.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
    }

    private List<string> ApplyMerges(List<string> toks)
    {
        foreach (var (a, b) in _merges)
        {
            string merged = a + b;
            var next = new List<string>();
            int i = 0;
            while (i < toks.Count)
            {
                if (i + 1 < toks.Count && toks[i] == a && toks[i + 1] == b)
                {
                    next.Add(merged);
                    i += 2;
                }
                else { next.Add(toks[i]); i++; }
            }
            toks = next;
        }
        return toks;
    }

    // ==================================================================
    // BPE 词表学习（无监督分词）
    // ==================================================================

    private void LearnMerges(int numMerges = 220)
    {
        var tokenLists = _corpus.Select(PreTokens).ToList();
        for (int iter = 0; iter < numMerges; iter++)
        {
            var stats = new Dictionary<(string, string), int>();
            foreach (var toks in tokenLists)
            {
                for (int i = 0; i < toks.Count - 1; i++)
                {
                    var key = (toks[i], toks[i + 1]);
                    stats[key] = stats.TryGetValue(key, out int n) ? n + 1 : 1;
                }
            }
            if (stats.Count == 0) break;
            var best = stats.OrderByDescending(kv => kv.Value).First();
            if (best.Value < 2) break;   // 只合并出现 ≥2 次的词对
            _merges.Add(best.Key);
            tokenLists = tokenLists.Select(ApplyMerges).ToList();
        }
    }

    // ==================================================================
    // 训练
    // ==================================================================

    /// <summary>收集语料（供 FinalizeTrain 学 BPE 用）。</summary>
    public void Collect(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _corpus.Add(text);
    }

    /// <summary>学 BPE 词表 + 全量训练词级 n-gram（种子训练时调用一次）。</summary>
    public void FinalizeTrain()
    {
        if (_merges.Count == 0) LearnMerges();

        // 用学到的词表把语料分词，重建词表
        _vocab.Clear();
        _uni.Clear(); _bi.Clear(); _tri.Clear(); _biCtx.Clear(); _triCtx.Clear();
        TotalTokens = 0;

        foreach (var line in _corpus)
            TrainSeq(ApplyMerges(PreTokens(line)));
    }

    /// <summary>在一条已分词的词序列上做 n-gram 计数。</summary>
    private void TrainSeq(List<string> toks)
    {
        var seq = new List<string> { Bos, Bos };
        seq.AddRange(toks);
        seq.Add(Eos);

        for (int i = 2; i < seq.Count; i++)
        {
            string a = seq[i - 2], b = seq[i - 1], c = seq[i];
            Inc(_uni, c);
            Inc(_bi, b + Sep + c);
            Inc(_biCtx, b);
            Inc(_tri, a + Sep + b + Sep + c);
            Inc(_triCtx, a + Sep + b);
            if (c != Eos) { _vocab.Add(c); TotalTokens++; }
        }
    }

    /// <summary>增量训练：用已有词表分词后累加计数（对话即训练数据）。</summary>
    public void Learn(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        TrainSeq(ApplyMerges(PreTokens(text)));
        _corpus.Add(text);
    }

    /// <summary>惩罚：扣减该文本 n-gram 计数（用于 RL 训练里给不合格回复降权）。</summary>
    public void Penalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var seq = new List<string> { Bos, Bos };
        seq.AddRange(ApplyMerges(PreTokens(text)));
        seq.Add(Eos);
        for (int i = 2; i < seq.Count; i++)
        {
            string a = seq[i - 2], b = seq[i - 1], c = seq[i];
            Dec(_uni, c);
            Dec(_bi, b + Sep + c);
            Dec(_biCtx, b);
            Dec(_tri, a + Sep + b + Sep + c);
            Dec(_triCtx, a + Sep + b);
            if (c != Eos) TotalTokens = Math.Max(0, TotalTokens - 1);
        }
    }

    private static void Dec(Dictionary<string, int> d, string k)
    {
        if (d.TryGetValue(k, out int v))
        {
            if (v <= 1) d.Remove(k);
            else d[k] = v - 1;
        }
    }

    private static void Inc(Dictionary<string, int> d, string k, int n = 1)
    {
        d[k] = d.TryGetValue(k, out int v) ? v + n : n;
    }

    // ==================================================================
    // 概率与采样（词级）
    // ==================================================================

    private double Prob(string tok, List<string> ctx)
    {
        int v = _vocab.Count > 0 ? _vocab.Count : 1;
        double p3 = 1.0 / v, p2 = 1.0 / v, p1;

        if (ctx.Count >= 2)
        {
            string h2 = ctx[^2] + Sep + ctx[^1];
            int c3 = _tri.TryGetValue(h2 + Sep + tok, out int x) ? x : 0;
            int n3 = _triCtx.TryGetValue(h2, out int y) ? y : 0;
            p3 = n3 > 0 ? (c3 + 1.0) / (n3 + v) : 1.0 / v;
        }
        if (ctx.Count >= 1)
        {
            string h1 = ctx[^1];
            int c2 = _bi.TryGetValue(h1 + Sep + tok, out int x) ? x : 0;
            int n2 = _biCtx.TryGetValue(h1, out int y) ? y : 0;
            p2 = n2 > 0 ? (c2 + 1.0) / (n2 + v) : 1.0 / v;
        }
        int c1 = _uni.TryGetValue(tok, out int z) ? z : 0;
        p1 = (c1 + 1.0) / (TotalTokens + v);

        return 0.6 * p3 + 0.3 * p2 + 0.1 * p1;
    }

    private string WeightedChoice(List<string> ctx, double temperature, int topK = 60)
    {
        var scored = new List<(double p, string tok)>();
        foreach (var tok in _vocab)
        {
            double p = Prob(tok, ctx);
            if (p > 0) scored.Add((p, tok));
        }
        scored.Sort((x, y) => y.p.CompareTo(x.p));
        scored = scored.Take(topK).ToList();

        double maxLog = double.NegativeInfinity;
        var logs = new List<(double l, string tok)>();
        foreach (var (p, tok) in scored)
        {
            double l = Math.Log(p + 1e-12) / Math.Max(temperature, 0.05);
            logs.Add((l, tok));
            maxLog = Math.Max(maxLog, l);
        }
        var weights = logs.Select(x => (w: Math.Exp(x.l - maxLog), x.tok)).ToList();
        double total = weights.Sum(x => x.w);
        double r = Rng.NextDouble() * total;
        double acc = 0;
        foreach (var (w, tok) in weights)
        {
            acc += w;
            if (r <= acc) return tok;
        }
        return weights[^1].tok;
    }

    // ==================================================================
    // 采样生成
    // ==================================================================

    public string Sample(string seed = "", double temperature = 0.9, int maxLen = 150, int minLen = 14)
    {
        if (_vocab.Count == 0) return "";

        var seedToks = ApplyMerges(PreTokens(seed ?? ""));
        var ctx = seedToks.Count >= 2 ? seedToks.GetRange(seedToks.Count - 2, 2)
                  : seedToks.Count == 1 ? new List<string> { seedToks[0] }
                  : new List<string>();
        var outToks = new List<string>(seedToks);
        // 只统计「新生成部分」的长度：seed（锚点前缀如「你说「xxx」——」）不计入最短长度，
        // 否则 seed 本身就有 7~8 字，模型采样几个标点就满足 minLen 提前收尾，产出「——。」这类空回复。
        int seedLen = seedToks.Sum(t => t.Length);

        var stop = new HashSet<string> { "。", "！", "？", "!", "?", "…" };
        int sentences = 0, guard = 0;
        while (guard < maxLen * 3)
        {
            guard++;
            string tok = WeightedChoice(ctx, temperature);
            if (tok == Eos) break;
            outToks.Add(tok);
            ctx.Add(tok);
            if (ctx.Count > 2) ctx.RemoveAt(0);

            int totalLen = outToks.Sum(t => t.Length);
            int genLen = totalLen - seedLen;   // 仅新生成部分
            if (totalLen >= maxLen) break;
            if (stop.Contains(tok))
            {
                sentences++;
                // 生成部分达到最短长度后，写完一个完整句子就果断收尾（最多 2 句，避免越写越散）
                if (genLen >= minLen || sentences >= 2) break;
            }
            // 兜底：生成部分超过 minLen 的 2.5 倍后，即便没遇到标点也逐步收尾
            else if (genLen >= minLen * 2 && Rng.NextDouble() < 0.3)
            {
                break;
            }
        }
        return Postprocess(string.Join("", outToks));
    }

    private static string Postprocess(string t)
    {
        if (string.IsNullOrEmpty(t)) return t;
        t = t.Replace(Bos, "").Replace(Eos, "").Replace(Sep, "");
        // 合并连续同类标点
        var sb = new StringBuilder();
        foreach (char c in t)
        {
            if (sb.Length > 0)
            {
                char prev = sb[^1];
                bool cEnd = "。！？!?…".Contains(c);
                bool prevEnd = "。！？!?…".Contains(prev);
                bool cMid = "，、；：,;".Contains(c);
                bool prevMid = "，、；：,;".Contains(prev);
                if ((cEnd && prevEnd) || (cMid && prevMid)) continue;   // 跳过连续同类标点
            }
            sb.Append(c);
        }
        t = sb.ToString().Trim(' ', '\t', '\n');
        t = t.TrimStart('，', '、', '；', '：', ',', ';');
        if (t.Length > 0 && !"。！？!?…".Contains(t[^1])) t += "。";
        return t;
    }

    // ==================================================================
    // 持久化
    // ==================================================================

    public void Save(string path)
    {
        var data = new Dictionary<string, object>
        {
            ["merges"] = _merges.Select(m => m.Item1 + "\u0004" + m.Item2).ToList(),
            ["uni"] = _uni,
            ["bi"] = _bi,
            ["tri"] = _tri,
            ["biCtx"] = _biCtx,
            ["triCtx"] = _triCtx,
            ["vocab"] = _vocab.ToList(),
            ["totalTokens"] = TotalTokens,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(data), Encoding.UTF8);
    }

    public static NgramModel Load(string path)
    {
        var m = new NgramModel();
        if (!File.Exists(path)) return m;
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(path, Encoding.UTF8));
            if (data == null) return m;

            void Fill(Dictionary<string, int> dst, string key)
            {
                if (data.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Object)
                    foreach (var p in el.EnumerateObject()) dst[p.Name] = p.Value.GetInt32();
            }
            if (data.TryGetValue("merges", out var mg) && mg.ValueKind == JsonValueKind.Array)
                foreach (var e in mg.EnumerateArray())
                {
                    var parts = e.GetString()!.Split('\u0004');
                    if (parts.Length == 2) m._merges.Add((parts[0], parts[1]));
                }
            Fill(m._uni, "uni");
            Fill(m._bi, "bi");
            Fill(m._tri, "tri");
            Fill(m._biCtx, "biCtx");
            Fill(m._triCtx, "triCtx");
            if (data.TryGetValue("vocab", out var v) && v.ValueKind == JsonValueKind.Array)
                foreach (var e in v.EnumerateArray()) m._vocab.Add(e.GetString()!);
            if (data.TryGetValue("totalTokens", out var tc)) m.TotalTokens = tc.GetInt64();
        }
        catch { /* 损坏则返回空模型 */ }
        return m;
    }
}
