using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TrollWrangler;

/// <summary>
/// 朴素贝叶斯分类器（字符 bigram 特征）：真正从带标签对话训练学习攻击意图，
/// 而非纯关键词匹配。支持增量训练与持久化。
///
/// 训练数据来自：
///   1. 内置的带标签语料（关键词 + 例句）；
///   2. 运行时用户确认/归档的对话（把分类结果作为标签回喂）。
/// </summary>
public sealed class NaiveBayes
{
    private readonly Dictionary<string, int> _classCount = new();
    private readonly Dictionary<string, Dictionary<string, int>> _featCount = new(); // class -> feature -> count
    private long _totalDocs = 0;

    private const double Alpha = 1.0;   // 拉普拉斯平滑

    public int ClassCount => _classCount.Count;

    /// <summary>判断模型是否见过某个类别（用于健康检查）。</summary>
    public bool HasClass(string label) => _classCount.ContainsKey(label);

    // ------------------------------------------------------------------
    // 特征：字符 bigram（能捕捉「懂个屁」「键盘侠」这类短语模式）
    // ------------------------------------------------------------------
    public static List<string> Featurize(string text)
    {
        var feats = new HashSet<string>();
        // unigram（单字）
        foreach (char c in text)
            if (!char.IsWhiteSpace(c)) feats.Add("1_" + c);
        // bigram（相邻双字）
        for (int i = 0; i < text.Length - 1; i++)
        {
            char a = text[i], b = text[i + 1];
            if (!char.IsWhiteSpace(a) && !char.IsWhiteSpace(b))
                feats.Add("2_" + a + b);
        }
        return feats.ToList();
    }

    // ------------------------------------------------------------------
    // 增量训练
    // ------------------------------------------------------------------
    public void Train(string text, string label)
    {
        var feats = Featurize(text);
        _classCount[label] = _classCount.TryGetValue(label, out int c) ? c + 1 : 1;
        _totalDocs++;

        if (!_featCount.TryGetValue(label, out var fmap))
        {
            fmap = new Dictionary<string, int>();
            _featCount[label] = fmap;
        }
        foreach (var f in feats)
            fmap[f] = fmap.TryGetValue(f, out int n) ? n + 1 : 1;
    }

    // ------------------------------------------------------------------
    // 预测
    // ------------------------------------------------------------------
    public string Predict(string text)
    {
        var (label, top1, top2) = PredictWithConfidence(text);
        return label;
    }

    /// <summary>返回 (label, top1 log 概率, top2 log 概率)。置信度不足时返回 general。</summary>
    public (string label, double top1, double top2) PredictWithConfidence(string text)
    {
        var feats = Featurize(text);
        int vocab = FeatVocabularySize();

        string best = "general";
        string second = "general";
        double bestScore = double.NegativeInfinity;
        double secondScore = double.NegativeInfinity;

        foreach (var cls in _classCount.Keys)
        {
            double score = Math.Log((_classCount[cls] + Alpha) / (_totalDocs + Alpha * _classCount.Count));
            if (!_featCount.TryGetValue(cls, out var fmap)) continue;
            int clsTotal = fmap.Values.Sum();
            foreach (var f in feats)
            {
                int c = fmap.TryGetValue(f, out int n) ? n : 0;
                score += Math.Log((c + Alpha) / (clsTotal + Alpha * vocab));
            }
            if (score > bestScore)
            {
                secondScore = bestScore;
                second = best;
                bestScore = score;
                best = cls;
            }
            else if (score > secondScore)
            {
                secondScore = score;
                second = cls;
            }
        }
        // 置信度不足（top1 优势小于阈值，或 top2 跟得很紧）→ 回退 general
        // 阈值设为 1.0 log units ≈ 2.7× 优势比；小语料下能挡住"破防"误判"威胁"这类情况
        if (bestScore - secondScore < 1.0 && best != "general")
            return ("general", bestScore, secondScore);
        return (best, bestScore, secondScore);
    }

    private int FeatVocabularySize()
    {
        var s = new HashSet<string>();
        foreach (var fmap in _featCount.Values)
            foreach (var k in fmap.Keys) s.Add(k);
        return s.Count;
    }

    // ------------------------------------------------------------------
    // 持久化
    // ------------------------------------------------------------------
    public void Save(string path)
    {
        var data = new Dictionary<string, object>
        {
            ["classCount"] = _classCount,
            ["featCount"] = _featCount,
            ["totalDocs"] = _totalDocs,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(data), Encoding.UTF8);
    }

    public static NaiveBayes Load(string path)
    {
        var m = new NaiveBayes();
        if (!File.Exists(path)) return m;
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(path, Encoding.UTF8));
            if (data == null) return m;

            if (data.TryGetValue("classCount", out var cc) && cc.ValueKind == JsonValueKind.Object)
                foreach (var p in cc.EnumerateObject()) m._classCount[p.Name] = p.Value.GetInt32();
            if (data.TryGetValue("totalDocs", out var td)) m._totalDocs = td.GetInt64();
            if (data.TryGetValue("featCount", out var fc) && fc.ValueKind == JsonValueKind.Object)
                foreach (var cls in fc.EnumerateObject())
                {
                    var fmap = new Dictionary<string, int>();
                    foreach (var f in cls.Value.EnumerateObject()) fmap[f.Name] = f.Value.GetInt32();
                    m._featCount[cls.Name] = fmap;
                }
        }
        catch { /* 损坏则返回空模型 */ }
        return m;
    }
}
