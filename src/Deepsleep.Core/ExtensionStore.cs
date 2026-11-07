using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TrollWrangler;

/// <summary>一个已安装的自定义工具（.dslt）。</summary>
public sealed class CustomTool
{
    public string Id = "";
    public string Name = "";
    public string Version = "";
    public string Author = "";
    public string Description = "";
    /// <summary>入口脚本文件名（相对于包目录）。</summary>
    public string Entry = "";
    /// <summary>python / cmd / powershell / node。</summary>
    public string Runtime = "python";
    public string Dir = "";
    public bool Enabled = true;

    /// <summary>给模型看的说明行。</summary>
    public string PromptLine => $"- {Name}：{Description}（参数：把要处理的内容直接写在“输入”里）";
}

/// <summary>一个已安装的皮肤（.dslp）。</summary>
public sealed class SkinPack
{
    public string Id = "";
    public string Name = "";
    public string Version = "";
    public string Author = "";
    public string Description = "";
    /// <summary>light / dark：套用后先切到哪个底。</summary>
    public string Base = "dark";
    /// <summary>
    /// light / dark：这是该主题模式的「默认皮肤」，切主题时自动换上。空 = 普通皮肤。
    /// 默认皮肤之间就是这样成对工作的：切到深色用 dark 那套，切到浅色用 light 那套。
    /// </summary>
    public string DefaultFor = "";
    /// <summary>是不是随程序分发的内置皮肤（不能删，版本由 builtin 计数驱动升级）。</summary>
    public bool Builtin = false;
    public Dictionary<string, string> Vars = new();
    public string Css = "";
    public string Dir = "";
    /// <summary>壁纸文件的本地绝对路径（可选）。</summary>
    public string Wallpaper = "";
}

/// <summary>
/// deepsleep 扩展（.dslt 工具包 / .dslp 皮肤包）的安装、列表与卸载。
///
/// 两种包都是 **zip**，根目录放一个清单文件：
///   .dslt → manifest.json（id / name / entry / runtime / description / version / author）
///   .dslp → theme.json  （id / name / base / vars / css / wallpaper / …）
/// 安装 = 解压到 data\tools\或 data\skins\ 下，并登记到 data\extensions.json。
/// 双击安装靠注册表关联到 deepsleep.exe --install-ext "文件路径"。
/// </summary>
public static class ExtensionStore
{
    public const string ToolExt = ".dslt";
    public const string SkinExt = ".dslp";

    public static string ToolsDir(string dataDir) => Path.Combine(dataDir, "tools");
    public static string SkinsDir(string dataDir) => Path.Combine(dataDir, "skins");
    private static string IndexFile(string dataDir) => Path.Combine(dataDir, "extensions.json");

    // ------------------------------------------------------------------ 读清单

    private static JsonObject? ReadJsonInZip(string zipPath, string entry)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var e = zip.GetEntry(entry) ?? zip.Entries.FirstOrDefault(x =>
                string.Equals(x.FullName.Replace('\\', '/'), entry, StringComparison.OrdinalIgnoreCase));
            if (e == null) return null;
            using var s = e.Open();
            using var sr = new StreamReader(s, Encoding.UTF8);
            return JsonNode.Parse(sr.ReadToEnd()) as JsonObject;
        }
        catch { return null; }
    }

    private static string S(JsonObject o, string k, string dflt = "")
        => o.TryGetPropertyValue(k, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? (s ?? dflt) : dflt;

    private static int I(JsonObject o, string k)
        => o.TryGetPropertyValue(k, out var v) && v is JsonValue jv && jv.TryGetValue<int>(out var i) ? i : 0;

    /// <summary>看一眼这个文件是哪种包（按扩展名 + 清单存在性判断），不认识返回 null。</summary>
    public static string? KindOf(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ToolExt) return "tool";
        if (ext == SkinExt) return "skin";
        return null;
    }

    /// <summary>安装一个包，返回 (成功, 说明, 包名)。</summary>
    public static (bool ok, string msg, string name) Install(string dataDir, string packagePath)
    {
        try
        {
            if (!File.Exists(packagePath)) return (false, "文件不存在：" + packagePath, "");
            string? kind = KindOf(packagePath);
            if (kind == null) return (false, "不是 .dslt / .dslp 文件", "");

            bool isTool = kind == "tool";
            string manifestName = isTool ? "manifest.json" : "theme.json";
            var man = ReadJsonInZip(packagePath, manifestName);
            if (man == null)
                return (false, $"包里没有 {manifestName}（或不是合法 JSON）", "");

            string id = S(man, "id").Trim();
            if (id.Length == 0) id = Path.GetFileNameWithoutExtension(packagePath);
            id = SafeId(id);
            if (id.Length == 0) return (false, "清单里的 id 不合法（只允许字母数字下划线和短横线）", "");

            string name = S(man, "name");
            if (name.Length == 0) name = id;

            if (isTool)
            {
                string entry = S(man, "entry");
                if (entry.Length == 0) return (false, "工具包缺少 entry（入口脚本名）", name);
                string runtime = S(man, "runtime", "python").ToLowerInvariant();
                if (runtime is not ("python" or "cmd" or "powershell" or "node"))
                    return (false, "runtime 只能是 python / cmd / powershell / node", name);
            }

            string root = isTool ? ToolsDir(dataDir) : SkinsDir(dataDir);
            Directory.CreateDirectory(root);
            string dest = Path.Combine(root, id);
            if (Directory.Exists(dest))
            {
                // 同 id 视为升级：先清空
                try { Directory.Delete(dest, true); } catch { }
            }
            Directory.CreateDirectory(dest);

            int n = ExtractSafe(packagePath, dest);
            if (n == 0) return (false, "包是空的", name);

            // 入口脚本必须真的存在
            if (isTool)
            {
                string entry = S(man, "entry");
                if (!File.Exists(Path.Combine(dest, entry)))
                    return (false, $"包里找不到入口脚本 {entry}", name);
            }

            Mark(dataDir, id, isTool, true);
            return (true, isTool ? $"已安装工具「{name}」（{n} 个文件）" : $"已安装皮肤「{name}」（{n} 个文件）", name);
        }
        catch (Exception ex)
        {
            return (false, "安装失败：" + ex.Message, "");
        }
    }

    /// <summary>解压并防目录穿越（zip 里写 ../ 跑到外面去）。</summary>
    private static int ExtractSafe(string zipPath, string destDir)
    {
        string rootFull = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;
        int count = 0;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue;   // 目录项
            string target = Path.GetFullPath(Path.Combine(destDir, e.FullName));
            if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;  // 跳过越界项
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, true);
            count++;
        }
        return count;
    }

    private static string SafeId(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.ToLowerInvariant())
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c >= 0x4e00 && c <= 0x9fff)
                sb.Append(c);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ 索引

    private sealed class Index
    {
        public HashSet<string> Tools = new();
        public HashSet<string> Skins = new();
        public string ActiveSkin = "";
        /// <summary>用户主动「关闭皮肤，恢复默认」。置上以后切主题不会再自动套默认皮肤。</summary>
        public bool SkinOff = false;
    }

    private static Index LoadIndex(string dataDir)
    {
        var idx = new Index();
        try
        {
            if (!File.Exists(IndexFile(dataDir))) return idx;
            var o = JsonNode.Parse(File.ReadAllText(IndexFile(dataDir))) as JsonObject;
            if (o == null) return idx;
            if (o["tools"] is JsonArray ta)
                foreach (var x in ta) if (x is JsonValue v && v.TryGetValue<string>(out var s)) idx.Tools.Add(s);
            if (o["skins"] is JsonArray sa)
                foreach (var x in sa) if (x is JsonValue v && v.TryGetValue<string>(out var s)) idx.Skins.Add(s);
            idx.ActiveSkin = S(o, "activeSkin");
            idx.SkinOff = o.TryGetPropertyValue("skinOff", out var so) && so is JsonValue sv
                          && sv.TryGetValue<bool>(out var sb) && sb;
        }
        catch { }
        return idx;
    }

    private static void SaveIndex(string dataDir, Index idx)
    {
        try
        {
            var o = new JsonObject
            {
                ["tools"] = new JsonArray(idx.Tools.OrderBy(x => x).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                ["skins"] = new JsonArray(idx.Skins.OrderBy(x => x).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                ["activeSkin"] = idx.ActiveSkin,
                ["skinOff"] = idx.SkinOff,
            };
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(IndexFile(dataDir), o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static void Mark(string dataDir, string id, bool isTool, bool on)
    {
        var idx = LoadIndex(dataDir);
        var set = isTool ? idx.Tools : idx.Skins;
        if (on) set.Add(id); else set.Remove(id);
        if (!isTool && !on && idx.ActiveSkin == id) idx.ActiveSkin = "";
        SaveIndex(dataDir, idx);
    }

    // ------------------------------------------------------------------ 列表

    public static List<CustomTool> Tools(string dataDir)
    {
        var list = new List<CustomTool>();
        string root = ToolsDir(dataDir);
        if (!Directory.Exists(root)) return list;
        var idx = LoadIndex(dataDir);
        foreach (var dir in Directory.GetDirectories(root))
        {
            string id = Path.GetFileName(dir);
            var man = ReadManifestFile(Path.Combine(dir, "manifest.json"));
            if (man == null) continue;
            list.Add(new CustomTool
            {
                Id = id,
                Name = S(man, "name", id),
                Version = S(man, "version"),
                Author = S(man, "author"),
                Description = S(man, "description"),
                Entry = S(man, "entry"),
                Runtime = S(man, "runtime", "python").ToLowerInvariant(),
                Dir = dir,
                Enabled = idx.Tools.Contains(id),
            });
        }
        return list.OrderBy(t => t.Name).ToList();
    }

    public static List<SkinPack> Skins(string dataDir)
    {
        var list = new List<SkinPack>();
        string root = SkinsDir(dataDir);
        if (!Directory.Exists(root)) return list;
        var idx = LoadIndex(dataDir);
        foreach (var dir in Directory.GetDirectories(root))
        {
            string id = Path.GetFileName(dir);
            var man = ReadManifestFile(Path.Combine(dir, "theme.json"));
            if (man == null) continue;
            var skin = new SkinPack
            {
                Id = id,
                Name = S(man, "name", id),
                Version = S(man, "version"),
                Author = S(man, "author"),
                Description = S(man, "description"),
                Base = S(man, "base", "dark").ToLowerInvariant() == "light" ? "light" : "dark",
                Builtin = I(man, "builtin") > 0,
                Dir = dir,
            };
            string dfltFor = S(man, "defaultFor").ToLowerInvariant();
            if (dfltFor == "light" || dfltFor == "dark") skin.DefaultFor = dfltFor;
            if (man["vars"] is JsonObject vars)
                foreach (var kv in vars)
                    if (kv.Value is JsonValue v && v.TryGetValue<string>(out var val) && val != null)
                        skin.Vars[kv.Key] = val;
            string cssFile = S(man, "css");
            if (cssFile.Length > 0)
            {
                string p = Path.Combine(dir, cssFile);
                if (File.Exists(p)) skin.Css = File.ReadAllText(p);
            }
            string wp = S(man, "wallpaper");
            if (wp.Length > 0)
            {
                string p = Path.Combine(dir, wp);
                if (File.Exists(p)) skin.Wallpaper = p;
            }
            list.Add(skin);
        }
        return list.OrderBy(s => s.Name).ToList();
    }

    private static JsonObject? ReadManifestFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ 启用 / 删除 / 皮肤

    public static bool SetToolEnabled(string dataDir, string id, bool on)
    {
        string dir = Path.Combine(ToolsDir(dataDir), id);
        if (!Directory.Exists(dir)) return false;
        Mark(dataDir, id, true, on);
        return true;
    }

    /// <summary>这套皮肤是不是内置的（内置的删了下次启动还会回来，所以干脆不让删）。</summary>
    public static bool IsBuiltinSkin(string dataDir, string id)
    {
        var man = ReadManifestFile(Path.Combine(SkinsDir(dataDir), id, "theme.json"));
        return man != null && I(man, "builtin") > 0;
    }

    public static bool Remove(string dataDir, string id)
    {
        if (IsBuiltinSkin(dataDir, id)) return false;
        bool did = false;
        foreach (var (root, isTool) in new[] { (ToolsDir(dataDir), true), (SkinsDir(dataDir), false) })
        {
            string dir = Path.Combine(root, id);
            if (!Directory.Exists(dir)) continue;
            try { Directory.Delete(dir, true); did = true; } catch { }
            Mark(dataDir, id, isTool, false);
        }
        return did;
    }

    public static string ActiveSkinId(string dataDir) => LoadIndex(dataDir).ActiveSkin;

    public static bool SetActiveSkin(string dataDir, string id)
    {
        var idx = LoadIndex(dataDir);
        if (id.Length > 0 && !idx.Skins.Contains(id)
            && !Directory.Exists(Path.Combine(SkinsDir(dataDir), id))) return false;
        idx.ActiveSkin = id;
        // 手动关掉皮肤 = 明确表示"我不要皮肤"，之后切主题也不要自动套回来
        idx.SkinOff = id.Length == 0;
        SaveIndex(dataDir, idx);
        return true;
    }

    /// <summary>取当前生效的皮肤（没启用返回 null）。</summary>
    public static SkinPack? ActiveSkin(string dataDir)
    {
        string id = ActiveSkinId(dataDir);
        if (id.Length == 0) return null;
        return Skins(dataDir).FirstOrDefault(s => s.Id == id);
    }

    // ------------------------------------------------------------------ 内置默认皮肤

    /// <summary>
    /// 把随程序分发的内置皮肤（assets\skins\&lt;id&gt;\）装进 data\skins。
    /// 用 theme.json 里的 <c>builtin</c> 计数判断要不要覆盖：计数变大 = 有新版本，
    /// 这样不必每次发版都去动 version 字段。返回这次更新了几套。
    /// </summary>
    public static int SeedBuiltins(string dataDir, string assetsDir)
    {
        if (string.IsNullOrWhiteSpace(assetsDir) || !Directory.Exists(assetsDir)) return 0;
        int upgraded = 0;
        foreach (var src in Directory.GetDirectories(assetsDir))
        {
            try
            {
                var man = ReadManifestFile(Path.Combine(src, "theme.json"));
                if (man == null) continue;
                string id = SafeId(S(man, "id"));
                if (id.Length == 0) continue;
                int wantRev = I(man, "builtin");
                if (wantRev <= 0) continue;      // 没标 builtin 的不算内置

                string dest = Path.Combine(SkinsDir(dataDir), id);
                var cur = ReadManifestFile(Path.Combine(dest, "theme.json"));
                if (cur != null && I(cur, "builtin") >= wantRev) continue;   // 已经是最新

                if (Directory.Exists(dest)) { try { Directory.Delete(dest, true); } catch { } }
                CopyDir(src, dest);
                Mark(dataDir, id, false, true);
                upgraded++;
            }
            catch { }
        }
        return upgraded;
    }

    /// <summary>
    /// 旧版本曾把「深/浅默认皮肤」当 .dslp 灌进 data\skins（dsh-style / dsh-light）。
    /// 现在两套 CSS 已经是亮/暗模式固有的（ui/theme-*.css），这些残留目录要清掉，
    /// 否则它们还会出现在扩展列表里，让人误以为需要手动点「使用」。
    /// 只清这两个已知 id：第三方皮肤就算自己写了 builtin 字段也不会被误删。
    /// 返回清理掉的套数。
    /// </summary>
    public static int PurgeBuiltins(string dataDir)
    {
        string root = SkinsDir(dataDir);
        if (!Directory.Exists(root)) return 0;
        int removed = 0;
        var ids = new List<string>();
        foreach (var old in new[] { "dsh-style", "dsh-light" })
        {
            string dir = Path.Combine(root, old);
            if (!Directory.Exists(dir)) continue;
            var man = ReadManifestFile(Path.Combine(dir, "theme.json"));
            if (man == null || I(man, "builtin") <= 0) continue;
            try { Directory.Delete(dir, true); ids.Add(old); removed++; } catch { }
        }
        if (ids.Count == 0) return 0;
        var idx = LoadIndex(dataDir);
        foreach (var id in ids) idx.Skins.Remove(id);
        if (ids.Contains(idx.ActiveSkin)) { idx.ActiveSkin = ""; idx.SkinOff = false; }
        SaveIndex(dataDir, idx);
        return removed;
    }

    private static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    /// <summary>某个主题模式（light / dark）的内置默认皮肤 id；没有就返回空串。</summary>
    public static string DefaultSkinFor(string dataDir, string theme)
    {
        foreach (var s in Skins(dataDir))
            if (s.DefaultFor.Length > 0 && s.DefaultFor == theme) return s.Id;
        return "";
    }

    /// <summary>
    /// 切主题 / 启动时决定该挂哪套皮肤：
    /// <list type="bullet">
    ///   <item>当前没选皮肤 → 用这个模式的默认皮肤（新装的用户开箱就是这个样子）</item>
    ///   <item>当前选的就是某套默认皮肤 → 换成新模式对应那套（深/浅色成对切换）</item>
    ///   <item>当前选的是第三方皮肤 / 用户自己关掉了皮肤 → 不动，用户的选择优先</item>
    /// </list>
    /// </summary>
    public static string ResolveSkinForTheme(string dataDir, string theme)
    {
        if (LoadIndex(dataDir).SkinOff) return "";    // 用户明确关掉了皮肤
        string dflt = DefaultSkinFor(dataDir, theme);
        var cur = ActiveSkin(dataDir);
        if (cur == null) return dflt;
        if (cur.DefaultFor.Length > 0) return cur.DefaultFor == theme ? cur.Id : dflt;
        return cur.Id;
    }

    // ------------------------------------------------------------------ 制作包

    /// <summary>
    /// 把一个目录打成 .dslt / .dslp（zip）。给 AI 造扩展用：
    /// 先写文件，再调这里打包，用户就能双击安装 / 直接安装。
    /// </summary>
    public static (bool ok, string msg) Pack(string srcDir, string outPath)
    {
        try
        {
            if (!Directory.Exists(srcDir)) return (false, "目录不存在：" + srcDir);
            string ext = Path.GetExtension(outPath).ToLowerInvariant();
            bool isTool = ext == ToolExt;
            string manName = isTool ? "manifest.json" : "theme.json";
            if (!File.Exists(Path.Combine(srcDir, manName)))
                return (false, $"目录里缺少 {manName}");

            if (File.Exists(outPath)) File.Delete(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            ZipFile.CreateFromDirectory(srcDir, outPath, CompressionLevel.Optimal, false);
            return (true, $"已生成 {Path.GetFileName(outPath)}（{new FileInfo(outPath).Length / 1024} KB）");
        }
        catch (Exception ex) { return (false, "打包失败：" + ex.Message); }
    }

    /// <summary>注册双击关联（HKCU，当前用户，不需要管理员）。只认 .dslt / .dslp。</summary>
    public static void RegisterAssociations(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;
        foreach (var (ext, cls, desc) in new[]
                 {
                     (ToolExt, "deepsleep.tool", "deepsleep 自定义工具"),
                     (SkinExt, "deepsleep.skin", "deepsleep 皮肤"),
                 })
        {
            try
            {
                string cmdLine = $"\"{exePath}\" --install-ext \"%1\"";
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}");
                k.SetValue("", cls);
                using var c = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{cls}");
                c.SetValue("", desc);
                using var cmd = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{cls}\shell\open\command");
                cmd.SetValue("", cmdLine);
                using var icon = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{cls}\DefaultIcon");
                icon.SetValue("", $"\"{exePath}\",0");

                // 「打开方式 → deepsleep」会写这条：Explorer 的 UserChoice 指向它，
                // 优先级高于上面的 .dslt / .dslp 映射。不带参数的话双击就只是把程序打开，包被忽略。
                string appName = System.IO.Path.GetFileName(exePath);
                using var appCmd = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\Applications\{appName}\shell\open\command");
                appCmd.SetValue("", cmdLine);
                using var appIco = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\Applications\{appName}\DefaultIcon");
                appIco.SetValue("", $"\"{exePath}\",0");
                using var appNameKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\Applications\{appName}");
                appNameKey.SetValue("FriendlyAppName", "deepsleep");
                // 让这两个后缀出现在「打开方式」列表里
                using var supported = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\Applications\{appName}\SupportedTypes");
                supported?.SetValue(ext, "");
            }
            catch { }
        }
    }
}
