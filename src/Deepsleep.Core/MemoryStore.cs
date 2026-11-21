using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TrollWrangler;

/// <summary>长期记忆：跨会话保存用户画像、稳定偏好与明确要求记住的事实， 持久化到 data\memory.json，AI 助手与 Agent 集群共用。</summary>
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
            : string.Join("\n", _entries.Select((e, i) => new { Entry = e, Index = i + 1 })
                .Where(e => !e.Entry.StartsWith("对话摘要（", StringComparison.Ordinal) &&
                            !e.Entry.StartsWith("更早纪要：", StringComparison.Ordinal))
                .Select(e => $"{e.Index}. {e.Entry}"));

    /// <summary>新增一条记忆。</summary>
    public void Add(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        string entry = content.Trim();
        if (entry.StartsWith("对话摘要（", StringComparison.Ordinal) ||
            entry.StartsWith("更早纪要：", StringComparison.Ordinal) ||
            _entries.Contains(entry, StringComparer.Ordinal)) return;
        _entries.Add(entry);
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
