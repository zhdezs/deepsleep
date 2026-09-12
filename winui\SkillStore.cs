using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace TrollWrangler;

public sealed class SkillInfo
{
    public string Name = "";
    public string Description = "";
    public string Content = "";
    public string Path = "";
    public List<string> Aliases = new();
    public List<string> Files = new();
}

/// <summary>技能管理：从 data\skills\*\SKILL.md 加载技能，支持本地文件夹 / URL 安装。</summary>
public static class SkillStore
{
    public static string DirFor(string dataDir) => Path.Combine(dataDir, "skills");

    public static List<SkillInfo> Load(string dataDir)
    {
        var list = new List<SkillInfo>();
        try
        {
            string root = DirFor(dataDir);
            if (!Directory.Exists(root)) return list;
            foreach (string dir in Directory.GetDirectories(root))
            {
                string md = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(md)) continue;
                string content = File.ReadAllText(md);
                string name = Path.GetFileName(dir);
                string desc = "";
                var aliases = new List<string>();
                var lines = content.Split('\n');
                string fmName = "", fmDesc = "", fmAliases = "";
                if (lines.Length > 0 && lines[0].Trim() == "---")
                {
                    for (int i = 1; i < lines.Length; i++)
                    {
                        string t = lines[i].Trim();
                        if (t == "---") break;
                        if (t.StartsWith("name:", StringComparison.OrdinalIgnoreCase)) fmName = Clean(t[5..]);
                        else if (t.StartsWith("description:", StringComparison.OrdinalIgnoreCase)) fmDesc = Clean(t[12..]);
                        else if (t.StartsWith("aliases:", StringComparison.OrdinalIgnoreCase)) fmAliases = Clean(t[8..]);
                    }
                }
                if (fmName.Length > 0) name = fmName;
                if (fmDesc.Length > 0) desc = fmDesc;
                if (desc.Length == 0)
                    foreach (string line in lines)
                    {
                        string t = line.Trim();
                        if (t.StartsWith("# ", StringComparison.Ordinal)) { name = t[2..].Trim(); continue; }
                        if (t.Length > 0 && desc.Length == 0 && !t.StartsWith("```"))
                        {
                            desc = t.Length > 140 ? t[..140] + "…" : t;
                            break;
                        }
                    }
                if (fmAliases.Length > 0)
                    aliases.AddRange(fmAliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                aliases.Add(Path.GetFileName(dir));
                if (!aliases.Contains(name)) aliases.Add(name);
                if ((name + " " + desc).Contains("pptx", StringComparison.OrdinalIgnoreCase) ||
                    (name + " " + desc).Contains("presentation", StringComparison.OrdinalIgnoreCase))
                    aliases.AddRange(new[] { "ppt", "pptx", "幻灯片", "演示", "演示文稿" });
                aliases = aliases.Where(a => a.Length >= 2).Distinct().ToList();
                var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                    .Select(f => Path.GetRelativePath(dir, f))
                    .Take(25)
                    .ToList();
                list.Add(new SkillInfo { Name = name, Description = desc, Content = content, Path = md, Aliases = aliases, Files = files });
            }
        }
        catch { }
        return list;
    }

    private static string Clean(string s)
        => s.Trim().Trim('"', '\'', ' ', '`');

    public static void InstallFolder(string dataDir, string srcDir)
    {
        string name = Path.GetFileName(srcDir.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(name)) return;
        CopyDir(srcDir, Path.Combine(DirFor(dataDir), name));
    }

    public static async Task<string?> InstallUrl(string dataDir, string url)
    {
        using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        string content = await hc.GetStringAsync(url);
        string name = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
        if (string.IsNullOrWhiteSpace(name)) name = "skill_" + DateTime.Now.Ticks % 100000;
        string dest = Path.Combine(DirFor(dataDir), name);
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "SKILL.md"), content);
        return name;
    }

    public static void Remove(string dataDir, string name)
    {
        try
        {
            string dest = Path.Combine(DirFor(dataDir), name);
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
        }
        catch { }
    }

    private static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        foreach (string d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
    }
}
