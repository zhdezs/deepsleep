using System.Text;
using System.Text.Json;
using TrollWrangler.Core;

namespace TrollWrangler.CoreHost;

/// <summary>
/// CMD 模式（--cli 交互 / --say "一句话" 发完即退）：内核还是那个内核、会话还是那份会话，
/// 只是宿主从 GUI 换成命令行 —— 发消息、看流式回复、看工具结果卡、回答权限确认都在 CMD 里完成。
/// </summary>
internal static partial class Program
{
    private static bool _cliMode;
    private static string? _cliAskId;
    private static string _cliState = "idle";
    private static string _cliModeName = "work";
    private static string _cliLastText = "";
    private static bool _cliOpen;                    // 流式输出是不是停在半行
    private static int _cliSid;
    private static readonly Dictionary<string, string> _cliStream = new();
    private static readonly List<(int Sid, string Title)> _cliConvs = new();

    /// <summary>--cli / --say 都算 CMD 模式。</summary>
    private static bool CliWanted(string[] args) => args.Contains("--cli") || args.Contains("--say");

    /// <summary>CMD 模式一次性消息：--say "..." 优先，其次 --cli "..."。</summary>
    private static string? CliOneShot(string[] args)
    {
        string? say = ArgStr(args, "--say");
        if (!string.IsNullOrWhiteSpace(say)) return say;
        string? inline = ArgStr(args, "--cli");
        if (!string.IsNullOrWhiteSpace(inline) && !inline!.StartsWith("--", StringComparison.Ordinal)) return inline;
        return null;
    }

    private static void RunCli(string[] args)
    {
        try { RunCliAsync(CliOneShot(args)).GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("CMD 模式异常退出：" + ex.Message); }
    }

    private static async Task RunCliAsync(string? oneShot)
    {
        Console.WriteLine();
        Console.WriteLine("deepsleep 内核版 · CMD 模式");
        Console.WriteLine("──────────────────────────────────────────────────────");
        Console.WriteLine("  版本      " + CoreServer.VersionString());
        Console.WriteLine("  数据目录  " + _dataDir);
        Console.WriteLine("  会话数据  和桌面端 / 网页版完全共用（同一个内核）");
        if (oneShot == null)
            Console.WriteLine("  用法      直接输入消息回车发送 ｜ /help 看命令 ｜ /exit 退出");
        Console.WriteLine();

        await InvokeCliAsync("boot");           // 取对话列表 / 当前会话 / 模式 / 状态

        if (oneShot != null)
        {
            var run = SendCliAsync(oneShot);
            while (!run.IsCompleted || _cliAskId != null)
            {
                if (_cliAskId != null)
                {
                    Console.Write("允许？(y=允许 n=拒绝 a=始终允许) ");
                    string? l = null;
                    try { l = Console.ReadLine(); } catch { }
                    await CliAnswerAsync(l == null ? "n" : l.Trim());
                    if (l == null) Console.WriteLine("（没有可读的输入，按拒绝处理；要免确认先切 /mode boom）");
                }
                await Task.Delay(100);
            }
            await run;
            CliFlush();
            return;
        }

        while (true)
        {
            Console.Write(_cliAskId != null ? "允许？(y=允许 n=拒绝 a=始终允许) " : "你> ");
            string? line;
            try { line = Console.ReadLine(); } catch { line = null; }
            if (line == null) break;
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line[0] == '/')
            {
                if (!await CliSlashAsync(line)) break;
                continue;
            }
            string plain = line.Replace(" ", "").TrimEnd('。', '.', '！', '!');
            if (plain is "打开超级连接" or "超级连接" or "superlink")
            {
                CliSuperLink("");
                continue;
            }
            if (plain.StartsWith("超级连接", StringComparison.Ordinal) && plain.Length == 10)
            {
                CliSuperLink(plain[4..]);          // 「超级连接123456」= 直接连对方
                continue;
            }
            if (_cliAskId != null) { await CliAnswerAsync(line); continue; }
            _ = SendCliAsync(line);                       // 不阻塞：运行中还能继续输入（停止 / 新指令）
        }

        CliFlush();
        Console.WriteLine();
        Console.WriteLine("已退出 CMD 模式（会话已保存）。");
    }

    // ------------------------------------------------------------------
    // 发消息 / 回答确认
    // ------------------------------------------------------------------

    private static async Task SendCliAsync(string text)
    {
        try
        {
            string r = await _kernel.InvokeAsync(JsonSerializer.Serialize(new { id = 0, cmd = "send", kind = 0, text }));
            if (r.Contains("\"ok\":false", StringComparison.Ordinal)) { CliFlush(); Console.WriteLine("内核返回：" + r); }
        }
        catch (Exception ex) { CliFlush(); Console.WriteLine("发送失败：" + ex.Message); }
    }

    private static async Task CliAnswerAsync(string line)
    {
        string a = line.ToLowerInvariant();
        bool yes = a is "y" or "yes" or "1" or "是" or "允许" or "好" or "可以";
        bool remember = a is "a" or "always" or "总是" or "全部允许";
        if (remember) yes = true;
        string askId = _cliAskId!;
        _cliAskId = null;
        Console.WriteLine(yes ? (remember ? "→ 本对话内始终允许" : "→ 允许") : "→ 拒绝");
        await InvokeCliAsync("answer", ("askId", askId), ("yes", yes), ("remember", remember));
    }

    private static async Task InvokeCliAsync(string cmd, params (string Key, object Value)[] args)
    {
        var d = new Dictionary<string, object> { ["id"] = 0, ["cmd"] = cmd };
        foreach (var (k, v) in args) d[k] = v;
        try { await _kernel.InvokeAsync(JsonSerializer.Serialize(d)); }
        catch (Exception ex) { CliFlush(); Console.WriteLine("命令失败：" + ex.Message); }
    }

    // ------------------------------------------------------------------
    // 斜杠命令
    // ------------------------------------------------------------------

    private static async Task<bool> CliSlashAsync(string line)
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string c = p[0].ToLowerInvariant();
        string arg = p.Length > 1 ? p[1].ToLowerInvariant() : "";
        switch (c)
        {
            case "/exit" or "/quit" or "/q" or "/bye":
                return false;
            case "/help" or "/?" or "/h":
                CliHelp();
                return true;
            case "/stop":
                await InvokeCliAsync("stop");
                Console.WriteLine("→ 已请求停止");
                return true;
            case "/new":
                await InvokeCliAsync("newConv");
                Console.WriteLine("→ 新开了一个对话");
                return true;
            case "/clear":
                await InvokeCliAsync("clear");
                Console.WriteLine("→ 已清空当前对话");
                return true;
            case "/regenerate" or "/again":
                _ = InvokeCliAsync("regenerate");
                return true;
            case "/resume":
                _ = InvokeCliAsync("resume");
                return true;
            case "/mode":
                if (arg is not ("chat" or "work" or "boom")) { Console.WriteLine("用法：/mode chat|work|boom"); return true; }
                await InvokeCliAsync("setMode", ("mode", arg));
                _cliModeName = arg;
                Console.WriteLine("→ 模式：" + arg + (arg == "work" ? "（执行命令前会问你）" : arg == "boom" ? "（命令不再确认）" : "（只聊天 + 搜索/研究）"));
                return true;
            case "/search":
                if (arg is not ("on" or "off")) { Console.WriteLine("用法：/search on|off"); return true; }
                await InvokeCliAsync("setSearch", ("on", arg == "on"));
                Console.WriteLine("→ 联网搜索：" + arg);
                return true;
            case "/fast":
                if (arg is not ("on" or "off")) { Console.WriteLine("用法：/fast on|off"); return true; }
                await InvokeCliAsync("setFast", ("on", arg == "on"));
                Console.WriteLine("→ 极速模式：" + arg);
                return true;
            case "/list" or "/convs":
                CliList();
                return true;
            case "/status":
                Console.WriteLine("→ 状态：" + _cliState + "　模式：" + _cliModeName + "　版本：" + CoreServer.VersionString());
                return true;
            case "/version":
                Console.WriteLine("deepsleep 内核版 " + CoreServer.VersionString());
                return true;
            case "/superlink" or "/sl":
                if (arg is "stop" or "off" or "断开" or "关闭")
                {
                    SuperLink.Stop();
                    Console.WriteLine("→ 超级连接已断开");
                    return true;
                }
                if (arg is "status" or "状态")
                {
                    CliSlPrint(true);
                    return true;
                }
                CliSuperLink(arg);
                return true;
            case "/rd":
                CliSlPrint(true);
                if (SuperLink.State == "connected" && SuperLink.BaseUrl.Length > 0 && SuperLink.RemoteToken.Length > 0)
                    Console.WriteLine("  远程桌面：" + SuperLink.BaseUrl + "/web/core/rd.html#t=" + SuperLink.RemoteToken);
                else if (SuperLink.State == "waiting" || SuperLink.State == "connected")
                    Console.WriteLine("  本机被控中：把配对码给对方，对方连上后会自动弹出远程桌面地址。");
                return true;
            default:
                Console.WriteLine("未知命令：" + c + "（/help 看全部）");
                return true;
        }
    }

    /// <summary>「打开超级连接」：生成配对码等对方来连；带参数就是去连别人。</summary>
    private static void CliSuperLink(string code)
    {
        if (code.Length == 0)
        {
            SuperLink.StartHost();
            Console.WriteLine();
            Console.WriteLine("  ╭─ 超级连接 ────────────────────────────────────────");
            Console.WriteLine("  │  配对码：  " + SuperLink.Code);
            Console.WriteLine("  │  把 6 位数字给对方；对方在桌面版点 🔗「超级连接」输入，");
            Console.WriteLine("  │  也可以让他打开：");
            Console.WriteLine("  │  https://zhdezs.github.io/deepsleep/superlink/?type=" + SuperLink.Code);
            Console.WriteLine("  │  连上之后对方就能操作这台电脑（远程桌面），5 分钟内有效。");
            Console.WriteLine("  ╰──────────────────────────────────────────────────");
            Console.WriteLine();
        }
        else
        {
            SuperLink.Connect(code);
            Console.WriteLine("→ 正在连配对码 " + code + " 的设备…");
        }
        CliSlWatch();
    }

    private static bool _slWatching;
    private static string _slShown = "";

    /// <summary>盯一会儿状态，连着的时候把远程桌面地址打出来。</summary>
    private static void CliSlWatch()
    {
        if (_slWatching) return;
        _slWatching = true;
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 2400; i++)
                {
                    string key = SuperLink.State + "|" + SuperLink.Message;
                    if (key != _slShown)
                    {
                        _slShown = key;
                        if (SuperLink.State == "idle") { Console.WriteLine("→ 超级连接：已结束"); break; }
                        Console.WriteLine("→ 超级连接[" + SuperLink.State + "] " + SuperLink.Message);
                        if (SuperLink.State == "connected")
                        {
                            if (SuperLink.Role == "host")
                                Console.WriteLine("  （对方已连上，它那边可以打开远程桌面控制这台电脑）");
                            else if (SuperLink.BaseUrl.Length > 0)
                                Console.WriteLine("  远程桌面：" + SuperLink.BaseUrl + "/web/core/rd.html#t=" +
                                                  SuperLink.RemoteToken);
                        }
                    }
                    await Task.Delay(1500);
                }
            }
            catch { }
            _slWatching = false;
        });
    }

    private static void CliSlPrint(bool brief)
    {
        Console.WriteLine("→ 超级连接：角色=" + (SuperLink.Role.Length > 0 ? SuperLink.Role : "-") +
                          " 状态=" + SuperLink.State + (brief ? "" : " " + SuperLink.Message));
        if (SuperLink.Code.Length > 0) Console.WriteLine("  配对码：" + SuperLink.Code);
        if (SuperLink.PeerName.Length > 0) Console.WriteLine("  对方：" + SuperLink.PeerName);
        if (SuperLink.BaseUrl.Length > 0) Console.WriteLine("  对方地址：" + SuperLink.BaseUrl);
        if (SuperLink.State == "connected")
            Console.WriteLine("  连接方式：" + (SuperLink.P2P ? "点对点直连" : "内置隧道"));
    }

    private static void CliHelp()
    {
        Console.WriteLine();
        Console.WriteLine("  /help                看这份帮助");
        Console.WriteLine("  /mode chat|work|boom chat 只聊天+搜索；work 命令前确认；boom 命令不确认");
        Console.WriteLine("  /search on|off       联网搜索开关");
        Console.WriteLine("  /fast on|off         极速模式");
        Console.WriteLine("  /stop                停止当前这一轮");
        Console.WriteLine("  /new                 新开对话");
        Console.WriteLine("  /clear               清空当前对话");
        Console.WriteLine("  /resume              继续上一次被停止的任务");
        Console.WriteLine("  /regenerate          重新生成上一条回复");
        Console.WriteLine("  /list                列出所有对话");
        Console.WriteLine("  /status              看当前状态");
        Console.WriteLine("  /superlink [配对码]  超级连接：不带参数=生成配对码等人连；带 6 位码=连对方");
        Console.WriteLine("  /rd                  看超级连接状态 / 远程桌面地址");
        Console.WriteLine("  /exit                退出（等价于 Ctrl+C）");
        Console.WriteLine();
        Console.WriteLine("  直接输入文字就是发消息；命令需要确认时，输入 y / n / a 回答。");
        Console.WriteLine();
    }

    private static void CliList()
    {
        if (_cliConvs.Count == 0) { Console.WriteLine("→ 还没有对话"); return; }
        Console.WriteLine("→ 对话列表（* = 当前）：");
        foreach (var (sid, title) in _cliConvs)
            Console.WriteLine("    " + (sid == _cliSid ? "* " : "  ") + "#" + sid + "  " + title);
    }

    // ------------------------------------------------------------------
    // 内核事件 -> 终端
    // ------------------------------------------------------------------

    private static void OnCliPush(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            string ev = Str(r, "ev");
            int kind = Int(r, "kind", -1);
            if (kind >= 0 && kind != 0) return;           // CMD 只跟 AI 助手（kind 0）那条线
            switch (ev)
            {
                case "add": CliAdd(r); break;
                case "delta": CliDelta(r); break;
                case "msgUpdate": CliUpdate(r); break;
                case "msgRemove": CliRemove(r); break;
                case "status": CliStatus(r); break;
                case "convs": CliConvs(r); break;
                case "boot": CliBoot(r); break;
                case "ask": CliAsk(r); break;
                case "mode": _cliModeName = Str(r, "mode"); break;
                case "askClose": _cliAskId = null; break;
            }
        }
        catch { }
    }

    private static void CliAdd(JsonElement r)
    {
        if (!r.TryGetProperty("item", out var it)) return;
        string id = Str(it, "id");
        string text = Str(it, "text");
        if (Bool(it, "sys")) { CliLine("· " + text); return; }
        if (Bool(it, "thinking"))
        {
            if (text.Length > 0 && text != "思考中") CliLine("… " + text);
            return;
        }
        if (Bool(it, "self")) { CliLine(""); CliLine("你> " + text); return; }

        string tool = Str(it, "tool");
        if (tool.Length > 0) { CliTool(it, tool); return; }

        if (text.Length == 0)                             // 流式回复的开头（正文靠 delta 增量打）
        {
            CliLine("");
            Console.Write("AI> ");
            _cliOpen = true;
            _cliStream[id] = "";
            return;
        }
        if (text.Trim() == _cliLastText.Trim()) return;    // 流式已经打过，别重复
        CliLine("");
        CliLine("AI> " + text);
        _cliLastText = text;
    }

    private static void CliDelta(JsonElement r)
    {
        string id = Str(r, "id");
        string text = Str(r, "text");
        string prev = _cliStream.TryGetValue(id, out var p) ? p : "";
        if (!_cliOpen) { CliLine(""); Console.Write("AI> "); _cliOpen = true; }
        if (text.Length >= prev.Length && prev.Length > 0 && text.StartsWith(prev, StringComparison.Ordinal))
            Console.Write(text[prev.Length..]);
        else if (prev.Length == 0)
            Console.Write(text);
        else
        {
            Console.WriteLine();
            Console.Write("AI> " + text);
        }
        _cliStream[id] = text;
        _cliLastText = text;
    }

    private static void CliUpdate(JsonElement r)
    {
        string id = Str(r, "id");
        if (r.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
        {
            string text = t.GetString() ?? "";
            if (!_cliStream.ContainsKey(id) && text.Length == 0) return;
            _cliStream[id] = text;
        }
    }

    private static void CliRemove(JsonElement r)
    {
        string id = Str(r, "id");
        _cliStream.Remove(id);
    }

    private static void CliStatus(JsonElement r)
    {
        int sid = Int(r, "conv", _cliSid);
        _cliSid = sid;
        _cliState = Str(r, "state");
        if (Str(r, "busy").Length > 0) return;
        if (_cliState == "running") return;
        if (_cliOpen) { Console.WriteLine(); _cliOpen = false; }
        _cliStream.Clear();
    }

    /// <summary>开机状态：把对话列表 / 当前会话 / 模式 / 状态取过来（items 不看，CMD 里是逐条事件打印的）。</summary>
    private static void CliBoot(JsonElement r)
    {
        CliConvs(r);
        if (r.TryGetProperty("conv", out var c) && c.ValueKind == JsonValueKind.Object) _cliSid = Int(c, "sid", _cliSid);
        if (r.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object) _cliState = Str(st, "state");
        if (r.TryGetProperty("settings", out var se) && se.ValueKind == JsonValueKind.Object)
        {
            string mode = Str(se, "mode");
            if (mode.Length > 0) _cliModeName = mode;
        }
    }

    private static void CliConvs(JsonElement r)
    {
        if (!r.TryGetProperty("convs", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        _cliConvs.Clear();
        foreach (JsonElement c in arr.EnumerateArray())
        {
            int sid = Int(c, "sid", 0);
            string title = Str(c, "title");
            _cliConvs.Add((sid, title));
        }
    }

    private static void CliAsk(JsonElement r)
    {
        _cliAskId = Str(r, "askId");
        CliLine("");
        Console.WriteLine("⚠ " + Str(r, "title"));
        foreach (string ln in Str(r, "detail").Split('\n')) Console.WriteLine("    " + ln.TrimEnd());
        Console.WriteLine("（就在这行输入 y=允许 / n=拒绝 / a=本对话里始终允许，然后回车）");
    }

    private static void CliTool(JsonElement it, string tool)
    {
        string sum = Str(it, "toolSummary");
        string detail = Str(it, "toolDetail");
        if (detail.Length == 0) detail = Str(it, "text");
        CliLine("");
        Console.WriteLine("⚙ " + tool + (sum.Length > 0 ? " · " + sum : ""));
        foreach (string ln in detail.Split('\n')) Console.WriteLine("    " + ln.TrimEnd());
        _cliOpen = false;
    }

    // ------------------------------------------------------------------
    // 小工具
    // ------------------------------------------------------------------

    private static void CliLine(string s)
    {
        if (_cliOpen) { Console.WriteLine(); _cliOpen = false; }
        Console.WriteLine(s);
    }

    private static void CliFlush()
    {
        if (_cliOpen) { Console.WriteLine(); _cliOpen = false; }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static int Int(JsonElement e, string name, int def)
        => e.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : def;

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
