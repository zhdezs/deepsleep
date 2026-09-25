using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TrollWrangler;

/// <summary>
/// 长期记忆：跨会话保存关于用户/任务的关键事实与偏好，
/// 持久化到 data\memory.json，AI 助手与 Agent 集群共用。
/// </summary>
public sealed class MemoryStore
{
    private readonly string _path;
    private List<string> _entries = new();

    public MemoryStore(string dataDir)
        => _path = Path.Combine(dataDir, "memory.json");

    public IReadOnlyList<string> Entries => _entries;

    public void Load()
    {
        try
        {
            _entries = new List<string>();
            if (!File.Exists(_path)) return;
            string json = File.ReadAllText(_path);
            var list = JsonSerializer.Deserialize<List<string>>(json);
            if (list != null)
                _entries = list.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        }
        catch
        {
            _entries = new List<string>();
        }
    }

    /// <summary>带序号的多行文本（注入系统提示词用）。</summary>
    public string AllText()
        => _entries.Count == 0
            ? ""
            : string.Join("\n", _entries.Select((e, i) => $"{i + 1}. {e}"));

    /// <summary>新增一条记忆。</summary>
    public void Add(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        _entries.Add(content.Trim());
        Save();
    }

    /// <summary>按序号删除一条记忆（序号从 1 开始）。</summary>
    public bool RemoveAt(int index)
    {
        if (index < 1 || index > _entries.Count) return false;
        _entries.RemoveAt(index - 1);
        Save();
        return true;
    }

    /// <summary>整段替换（用户手动编辑记忆时用），每行一条，自动去掉行首序号。</summary>
    public void ReplaceAll(string text)
    {
        _entries = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Trim())
            .Select(l => l.Length > 0 && char.IsDigit(l[0]) && l.IndexOf(". ", StringComparison.Ordinal) > 0
                ? l[(l.IndexOf(". ", StringComparison.Ordinal) + 2)..].Trim()
                : l)
            .Where(l => l.Length > 0)
            .ToList();
        Save();
    }

    public void Clear()
    {
        _entries.Clear();
        Save();
    }

    /// <summary>按条件删除记忆条目。</summary>
    public void RemoveWhere(Func<string, bool> match)
    {
        int n = _entries.RemoveAll(s => match(s));
        if (n > 0) Save();
    }

    /// <summary>限制记忆总条数，超出时丢弃最旧的条目。</summary>
    public void Cap(int max)
    {
        if (_entries.Count <= max) return;
        _entries.RemoveRange(0, _entries.Count - max);
        Save();
    }

    /// <summary>
    /// 超出字数上限时自动压缩：把最旧的「对话摘要」逐条压短并并入「更早纪要」，
    /// 尽量保留全部信息而不删除；压缩完仍超限则截断纪要。
    /// </summary>
    public void CompressIfOver(int maxChars)
    {
        if (maxChars <= 0) { Clear(); return; }
        if (_entries.Sum(e => e.Length) <= maxChars) return;
        while (_entries.Sum(e => e.Length) > maxChars)
        {
            int idx = -1;
            for (int i = 0; i < _entries.Count; i++)
                if (_entries[i].StartsWith("对话摘要", StringComparison.Ordinal)) { idx = i; break; }
            if (idx < 0) break;   // 只剩纪要/事实，无法再压
            string s = _entries[idx];
            _entries.RemoveAt(idx);
            string shortS = ShortenSummary(s);
            int gi = _entries.FindIndex(e => e.StartsWith("更早纪要", StringComparison.Ordinal));
            if (gi < 0) _entries.Insert(0, "更早纪要：" + shortS + "；");
            else _entries[gi] += shortS + "；";
        }
        if (_entries.Sum(e => e.Length) > maxChars)
        {
            int ti = _entries.FindIndex(e => e.StartsWith("更早纪要", StringComparison.Ordinal));
            if (ti >= 0) _entries[ti] = _entries[ti][..maxChars];
        }
        Save();
    }

    /// <summary>把一条对话摘要压短：问题保留前 30 字，回答保留前 24 字。</summary>
    private static string ShortenSummary(string s)
    {
        int q = s.IndexOf("问「", StringComparison.Ordinal);
        int a = s.IndexOf("答「", StringComparison.Ordinal);
        if (q < 0 || a < 0) return s.Length <= 60 ? s : s[..60] + "…";
        string head = s[..q];
        string qText = s[(q + 2)..a];
        string aText = s[(a + 2)..];
        string qShort = qText.Length > 30 ? qText[..30] + "…" : qText;
        string aShort = aText.Length > 24 ? aText[..24] + "…" : aText;
        return head + "问「" + qShort + "」答「" + aShort;
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 写入失败不影响主流程
        }
    }
}
