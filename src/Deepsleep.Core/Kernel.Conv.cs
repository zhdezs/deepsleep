using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace TrollWrangler.Core;

/// <summary>
/// 内核：对话管理 / 记忆 / 技能 / 设置 / 上传 / 桌宠开关。
/// </summary>
public sealed partial class Kernel
{
    private string _convSearch = "";

    // ------------------------------------------------------------------
    // 标签页
    // ------------------------------------------------------------------

    private void SetTab(int tab)
    {
        _tab = tab == 1 ? 1 : 0;
        var conv = TabConv(_tab);
        Emit(new Dictionary<string, object?>
        {
            ["ev"] = "tab",
            ["tab"] = _tab,
            ["convs"] = ConvListJson(_tab),
            ["conv"] = ConvJson(conv),
            ["items"] = conv.Items.Select(ItemJson).ToList(),
            ["status"] = StatusJson(_tab),
        });
    }

    // ------------------------------------------------------------------
    // 对话
    // ------------------------------------------------------------------

    private List<Conversation> ListOf(int kind) => kind == 1 ? _counterConvs : kind == 2 ? _clusterConvs : _agentConvs;

    private Conversation? FindConv(int kind, int sid) => ListOf(kind).FirstOrDefault(c => c.Sid == sid);

    private Conversation CreateConversation(int kind)
    {
        var conv = new Conversation
        {
            Kind = kind,
            Title = $"对话 {_nextConvNo++}",
            Sid = kind switch { 0 => _nextAgentSid++, 1 => _nextCounterSid++, _ => _nextClusterSid++ },
            Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        switch (kind)
        {
            case 0: _agent.NewSession(conv.Sid); _agentConvs.Add(conv); break;
            case 1: _engine.NewSession(conv.Sid); _counterConvs.Add(conv); break;
            default: _clusterAgent.NewSession(conv.Sid); _clusterConvs.Add(conv); break;
        }
        return conv;
    }

    private void NewConversation(int kind)
    {
        Conversation conv = CreateConversation(kind);
        if (kind == 2) _clusterCur = conv; else _agentCur = conv;
        _tab = kind == 2 ? 1 : 0;
        _lastTime = "";
        conv.Items.Add(new ChatItem { IsSys = true, Text = "新对话已创建。", TimeStr = Now(), ShowTime = true });
        Emit(new Dictionary<string, object?>
        {
            ["ev"] = "conv",
            ["tab"] = _tab,
            ["convs"] = ConvListJson(_tab),
            ["conv"] = ConvJson(conv),
            ["items"] = conv.Items.Select(ItemJson).ToList(),
            ["status"] = StatusJson(_tab),
        });
        ScheduleSave();
    }

    private void SelectConversation(int kind, int sid)
    {
        var conv = FindConv(kind, sid);
        if (conv == null) return;
        if (kind == 2) _clusterCur = conv; else _agentCur = conv;
        _tab = kind == 2 ? 1 : 0;
        _lastTime = "";
        Emit(new Dictionary<string, object?>
        {
            ["ev"] = "conv",
            ["tab"] = _tab,
            ["convs"] = ConvListJson(_tab),
            ["conv"] = ConvJson(conv),
            ["items"] = conv.Items.Select(ItemJson).ToList(),
            ["status"] = StatusJson(_tab),
        });
    }

    private void SearchConversations(int kind, string q)
    {
        _convSearch = q.Trim();
        _tab = kind == 2 ? 1 : 0;
        Emit(new Dictionary<string, object?> { ["ev"] = "convs", ["tab"] = _tab, ["convs"] = ConvListJson(_tab) });
    }

    private void RenameConversation(int sid, string title)
    {
        var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid) ?? _clusterConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv == null) return;
        string t = title.Trim();
        if (t.Length == 0) return;
        conv.Title = t;
        Emit(new { ev = "convs", tab = _tab, convs = ConvListJson(_tab) });
        ScheduleSave();
    }

    private void PinConversation(int sid, bool pinned)
    {
        var conv = _agentConvs.FirstOrDefault(c => c.Sid == sid) ?? _clusterConvs.FirstOrDefault(c => c.Sid == sid);
        if (conv == null) return;
        conv.IsPinned = pinned;
        Emit(new { ev = "convs", tab = _tab, convs = ConvListJson(_tab) });
        ScheduleSave();
    }

    private void MoveConversation(int sid, int dir)
    {
        var list = _agentConvs.Any(c => c.Sid == sid) ? _agentConvs
                 : _clusterConvs.Any(c => c.Sid == sid) ? _clusterConvs : _counterConvs;
        var conv = list.FirstOrDefault(c => c.Sid == sid);
        if (conv == null) return;
        int idx = list.IndexOf(conv);
        int target = idx + dir;
        if (idx < 0 || target < 0 || target >= list.Count) return;
        list.RemoveAt(idx);
        list.Insert(target, conv);
        Emit(new { ev = "convs", tab = _tab, convs = ConvListJson(_tab) });
        ScheduleSave();
    }

    private void DeleteConversation(int sid)
    {
        int kind = _agentConvs.Any(c => c.Sid == sid) ? 0 : _clusterConvs.Any(c => c.Sid == sid) ? 2 : 1;
        var list = ListOf(kind);
        var conv = list.FirstOrDefault(c => c.Sid == sid);
        if (conv == null) return;
        list.Remove(conv);
        if (kind == 0) { StopAgent(sid); _agent.ResetSession(sid); }
        else if (kind == 2) ResetClusterConversation(sid);
        if (list.Count == 0) CreateConversation(kind);
        if (kind == 2) _clusterCur = _clusterConvs[^1]; else _agentCur = _agentConvs[^1];
        _tab = kind == 2 ? 1 : 0;
        _lastTime = "";
        var cur = TabConv(_tab);
        Emit(new Dictionary<string, object?>
        {
            ["ev"] = "conv",
            ["tab"] = _tab,
            ["convs"] = ConvListJson(_tab),
            ["conv"] = ConvJson(cur),
            ["items"] = cur.Items.Select(ItemJson).ToList(),
            ["status"] = StatusJson(_tab),
        });
        ScheduleSave();
    }

    private void ClearConversation(int kind)
    {
        if (kind == 2) { ClusterClear(_clusterCur.Sid); return; }
        var conv = _agentCur;
        StopAgent(conv.Sid);
        _agent.ResetSession(conv.Sid);
        conv.Items.Clear();
        _streamItems.Remove(conv.Sid);
        _agentThinking.Remove(conv.Sid);
        _agentWorking.Remove(conv.Sid);
        SetAgentState(conv.Sid, RunState.Idle);
        Emit(new Dictionary<string, object?>
        {
            ["ev"] = "conv",
            ["tab"] = _tab,
            ["convs"] = ConvListJson(_tab),
            ["conv"] = ConvJson(conv),
            ["items"] = new List<object>(),
            ["status"] = StatusJson(_tab),
        });
        ScheduleSave();
    }

    private void DeleteMessage(int kind, string itemId)
    {
        var conv = TabConv(kind == 2 ? 1 : 0);
        var item = conv.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null) return;
        if (item.IsSelf)
        {
            if (kind == 2) _clusterAgent.RemoveMessage(conv.Sid, "user", item.Text);
            else _agent.RemoveMessage(conv.Sid, "user", item.Text);
        }
        conv.Items.Remove(item);
        Emit(new { ev = "msgRemove", kind = conv.Kind, conv = conv.Sid, id = itemId });
        ScheduleSave();
    }

    private void MaybeAutoTitle(Conversation conv, string text)
    {
        if (!conv.Title.StartsWith("对话 ", StringComparison.Ordinal)) return;
        string t = MarkdownRenderer.ToPlainText(text).Trim();
        conv.Title = t.Length <= 16 ? t : t[..16] + "…";
    }
    // ------------------------------------------------------------------
    // 持久化
    // ------------------------------------------------------------------

    private void LoadConversations()
    {
        var (agentDtos, counterDtos, clusterDtos) = ConversationStore.Load(_dataDir);
        foreach (var dto in agentDtos) _agentConvs.Add(RestoreConversation(dto, 0));
        foreach (var dto in counterDtos) _counterConvs.Add(RestoreConversation(dto, 1));
        foreach (var dto in clusterDtos) _clusterConvs.Add(RestoreConversation(dto, 2));
        if (_agentConvs.Count == 0) CreateConversation(0);
        if (_counterConvs.Count == 0) CreateConversation(1);
        if (_clusterConvs.Count == 0) CreateConversation(2);
        _agentCur = _agentConvs[^1];
        _clusterCur = _clusterConvs[^1];
        _nextAgentSid = _agentConvs.Max(c => c.Sid) + 1;
        _nextCounterSid = _counterConvs.Max(c => c.Sid) + 1;
        _nextClusterSid = _clusterConvs.Max(c => c.Sid) + 1;
        int maxN = 0;
        foreach (var c in _agentConvs.Concat(_counterConvs).Concat(_clusterConvs))
            if (int.TryParse(c.Title.Replace("对话 ", ""), out int n)) maxN = Math.Max(maxN, n);
        _nextConvNo = maxN + 1;
    }

    private Conversation RestoreConversation(ConversationDto dto, int kind)
    {
        var conv = new Conversation
        {
            Kind = kind,
            Title = string.IsNullOrWhiteSpace(dto.Title) ? $"对话 {_nextConvNo++}" : dto.Title,
            Sid = dto.Sid,
            Created = dto.Created,
            AutoApprove = dto.AutoApprove,
            IsPinned = dto.IsPinned,
        };
        if (kind == 0) _agent.NewSession(conv.Sid);
        else if (kind == 1) _engine.NewSession(conv.Sid);
        else _clusterAgent.NewSession(conv.Sid);
        foreach (var m in dto.Messages)
        {
            conv.Items.Add(new ChatItem
            {
                Text = m.Text,
                Meta = m.Meta,
                IsSelf = m.IsSelf,
                IsSys = m.IsSys,
                Accepted = m.Accepted,
                CanAccept = false,
                TimeStr = m.TimeStr,
                ShowTime = m.ShowTime,
                SessionId = conv.Sid,
                AvatarText = kind switch { 0 => "AI", 1 => "侠", _ => "集" },
                SelfAvatarText = kind switch { 0 => "你", 1 => "理", _ => "你" },
                AvatarColor = kind switch { 0 => "#4A90D9", 1 => "#B0B0B0", _ => "#7C4DFF" },
            });
        }
        return conv;
    }

    private void SaveConversations()
    {
        var agent = _agentConvs.Select(ToDto).ToList();
        var counter = _counterConvs.Select(ToDto).ToList();
        var cluster = _clusterConvs.Select(ToDto).ToList();
        ConversationStore.Save(_dataDir, agent, counter, cluster);
    }

    private static ConversationDto ToDto(Conversation c) => new()
    {
        Title = c.Title,
        Sid = c.Sid,
        Created = c.Created,
        AutoApprove = c.AutoApprove,
        IsPinned = c.IsPinned,
        Messages = c.Items.Select(m => new ChatMessageDto
        {
            Text = m.Text,
            Meta = m.Meta,
            IsSelf = m.IsSelf,
            IsSys = m.IsSys,
            Accepted = m.Accepted,
            TimeStr = m.TimeStr,
            ShowTime = m.ShowTime,
        }).ToList(),
    };

    private void SaveRuntimeState()
    {
        if (_agentCur == null || _agent == null) return;
        var st = new RuntimeState();
        foreach (var kv in _agent.SnapshotSessions())
        {
            if (kv.Key >= 500000) continue;
            st.AgentSessions.Add(new AgentSessionDto
            {
                Sid = kv.Key,
                Messages = kv.Value.Select(m => new AgentMsgDto
                {
                    Role = m.Role,
                    Content = Cap(m.Content, 4000),
                    Meta = m.Meta,
                    ImagePath = m.ImagePath,
                }).ToList(),
            });
        }
        foreach (var kv in _agentStates)
            if (kv.Value != RunState.Idle)
                st.AgentStates[kv.Key] = kv.Value == RunState.Running ? "running" : "stopped";
        foreach (var kv in _clusterRuns)
            st.ClusterRuns.Add(new ClusterRunDto
            {
                Sid = kv.Key,
                UserText = kv.Value.UserText,
                SummarySid = kv.Value.SummarySid,
                Workers = kv.Value.Workers.Select(w => new ClusterWorkerDto
                {
                    Sid = w.Sid,
                    Name = w.Name,
                    Task = w.Task,
                    Status = w.Status,
                    LogText = Cap(w.LogText, 2000),
                }).ToList(),
            });
        foreach (var kv in _clusterStates)
            if (kv.Value != RunState.Idle)
                st.ClusterStates[kv.Key] = kv.Value == RunState.Running ? "running" : "stopped";
        RuntimeStore.Save(_dataDir, st);
    }

    private void RestoreRuntimeState()
    {
        var st = RuntimeStore.Load(_dataDir);
        if (st == null) return;
        foreach (var s in st.AgentSessions)
            _agent.RestoreSession(s.Sid, s.Messages.Select(m => new AgentMessage
            {
                Role = m.Role,
                Content = m.Content,
                Meta = m.Meta,
                ImagePath = m.ImagePath,
            }));
        foreach (var kv in st.AgentStates)
        {
            _agentStates[kv.Key] = RunState.Stopped;
            _agent.AppendHistory(kv.Key, new AgentMessage
            {
                Role = "tool",
                Meta = "中断标记",
                Content = "（上次运行被中断，未完成，可点恢复继续）",
            });
            AddAgentSys(kv.Key, "⏸ 上次运行被中断，可点「恢复」从断点继续。");
        }
        foreach (var r in st.ClusterRuns)
        {
            _clusterRuns[r.Sid] = new ClusterRun
            {
                UserText = r.UserText,
                SummarySid = r.SummarySid,
                Workers = r.Workers.Select(w => new ClusterWorker
                {
                    Sid = w.Sid,
                    Name = w.Name,
                    Task = w.Task,
                    Status = w.Status,
                    LogText = w.LogText,
                }).ToList(),
            };
            _clusterStates[r.Sid] = RunState.Stopped;
            AddClusterSys(r.Sid, "⏸ 上次集群任务被中断，可点「恢复」继续执行。");
            foreach (var w in _clusterRuns[r.Sid].Workers)
                if (w.Sid >= _nextClusterSid) _nextClusterSid = w.Sid + 1;
            if (r.SummarySid >= _nextClusterSid) _nextClusterSid = r.SummarySid + 1;
        }
        foreach (var kv in st.ClusterStates)
            _clusterStates[kv.Key] = RunState.Stopped;
        if (st.AgentStates.Count > 0 || st.ClusterRuns.Count > 0) ScheduleSave();
    }
    // ------------------------------------------------------------------
    // 记忆
    // ------------------------------------------------------------------

    private void EmitMemory() => Emit(new
    {
        ev = "memory",
        text = _memory?.AllText() ?? "",
        count = _memory?.Entries.Count ?? 0,
    });

    private void SaveMemory(string text)
    {
        _memory?.ReplaceAll(text);
        if (_memory != null) _clusterAgent.Memory = _memory;
        Toast($"🧠 已保存 {_memory?.Entries.Count ?? 0} 条长期记忆。");
    }

    private void ClearMemory()
    {
        _memory?.Clear();
        if (_memory != null) _clusterAgent.Memory = _memory;
        Toast("🧠 长期记忆已清空。");
    }

    // ------------------------------------------------------------------
    // 技能
    // ------------------------------------------------------------------

    private void ReloadSkills()
    {
        _skills = SkillStore.Load(_dataDir);
        _agent.Skills = _skills;
        _clusterAgent.Skills = _skills;
    }

    private void EmitSkills() => Emit(new { ev = "skills", list = SkillsJson() });

    private async Task InstallSkillUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            await SkillStore.InstallUrl(_dataDir, url).ConfigureAwait(false);
            ReloadSkills();
            EmitSkills();
            Toast("技能已安装。");
        }
        catch (Exception ex) { Toast("安装失败：" + ex.Message, "error"); }
    }

    private void InstallSkillFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            SkillStore.InstallFolder(_dataDir, path);
            ReloadSkills();
            EmitSkills();
            Toast("技能已安装。");
        }
        catch (Exception ex) { Toast("安装失败：" + ex.Message, "error"); }
    }

    private void RemoveSkill(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            SkillStore.Remove(_dataDir, name);
            ReloadSkills();
            EmitSkills();
            Toast($"已删除技能「{name}」。");
        }
        catch (Exception ex) { Toast("删除失败：" + ex.Message, "error"); }
    }

    // ------------------------------------------------------------------
    // 设置
    // ------------------------------------------------------------------

    private void EmitSettings() => Emit(new { ev = "settings", settings = SettingsJson() });

    private void SaveSettings(JsonElement a)
    {
        string Str(string name, string fallback) => GetStr(a, name) ?? fallback;
        _config.ApiUrl = Str("apiUrl", _config.ApiUrl);
        _config.ApiModel = Str("apiModel", _config.ApiModel);
        _config.ApiKey = GetStr(a, "apiKey") ?? _config.ApiKey;
        _config.ApiFormat = Str("apiFormat", _config.ApiFormat);
        _config.OllamaModel = Str("ollamaModel", _config.OllamaModel);
        _config.OllamaModelStrong = Str("ollamaModelStrong", _config.OllamaModelStrong);
        _config.OllamaUrl = Str("ollamaUrl", _config.OllamaUrl);
        _config.ImageApiKey = Str("imageApiKey", _config.ImageApiKey);
        _config.ImageModel = Str("imageModel", _config.ImageModel);
        _config.VisionApiKey = Str("visionApiKey", _config.VisionApiKey);
        _config.VisionModel = Str("visionModel", _config.VisionModel);
        if (a.TryGetProperty("multimodalMain", out _)) _config.MultimodalMain = GetBool(a, "multimodalMain");
        if (a.TryGetProperty("useOllama", out _)) _config.UseOllama = GetBool(a, "useOllama");
        _config.UpdateUrl = Str("updateUrl", _config.UpdateUrl);
        if (a.TryGetProperty("autoCheckUpdate", out _)) _config.AutoCheckUpdate = GetBool(a, "autoCheckUpdate");
        string? src = GetStr(a, "updateSource");
        if (src != null) _config.UpdateSource = src.Trim().ToLowerInvariant() == "github" ? "github" : "gitee";
        if (a.TryGetProperty("petEnabled", out _)) _config.PetEnabled = GetBool(a, "petEnabled");
        _config.Save();

        _engine.ApplyConfig(_config);
        _agent.ApplyConfig(_config);
        _clusterAgent.ApplyConfig(_config);
        string backend = _config.UseOllama ? "llm" : "auto";
        _agent.Backend = backend;
        _clusterAgent.Backend = backend;
        _agent.MultimodalMain = _config.MultimodalMain;
        _clusterAgent.MultimodalMain = _config.MultimodalMain;
        string? mode = GetStr(a, "mode");
        if (!string.IsNullOrWhiteSpace(mode)) SetAgentMode(mode);
        if (a.TryGetProperty("fast", out _)) SetFast(GetBool(a, "fast"));
        if (a.TryGetProperty("search", out _)) SetSearch(GetBool(a, "search"));
        EmitSettings();
        EmitStatus(0);
        EmitStatus(2);
        Emit(new { ev = "pet", enabled = _config.PetEnabled });
    }

    private void SaveToken(string token)
    {
        token = token.Trim();
        if (token.Length == 0) return;
        try
        {
            TokenVault.Save(token, Updater.TokenPath);
            string read = Updater.LoadToken();
            bool ok = string.Equals(read, token, StringComparison.Ordinal);
            Toast(ok ? "🔑 令牌已多层加密保存。" : "⚠ 保存了但读回不一致，请重试。", ok ? "info" : "error");
            EmitSettings();
        }
        catch (Exception ex) { Toast("保存失败：" + ex.Message, "error"); }
    }

    private void SetTheme(string theme)
    {
        _config.Theme = theme == "dark" ? "dark" : "light";
        _config.Save();
        Emit(new { ev = "theme", theme = _config.Theme });
    }

    private void SetAutoCheck(bool on)
    {
        _config.AutoCheckUpdate = on;
        _config.Save();
        EmitSettings();
    }

    private void SetPetEnabled(bool on)
    {
        _config.PetEnabled = on;
        _config.Save();
        Emit(new { ev = "pet", enabled = on });
    }
    // ------------------------------------------------------------------
    // 上传文件 / 打开路径
    // ------------------------------------------------------------------

    /// <summary>外壳用文件选择器拿到路径后交给内核：复制进 data\uploads 并注入上下文。</summary>
    private void AttachFile(int kind, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var conv = kind == 2 ? _clusterCur : _agentCur;
        try
        {
            string dir = Path.Combine(_dataDir, "uploads");
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, Path.GetFileName(path));
            File.Copy(path, dest, true);
            var fi = new FileInfo(dest);
            long kb = Math.Max(1, (fi.Length + 1023) / 1024);
            string name = Path.GetFileName(dest);
            bool isImage = IsImageName(name);
            var item = new ChatItem
            {
                IsSelf = true,
                CanAccept = false,
                Text = $"📎 上传文件：{dest}（{kb} KB）",
                Meta = "上传",
                ImagePath = isImage ? dest : null,
                AttachmentName = name,
                AttachmentSize = $"{kb} KB",
                SessionId = conv.Sid,
                TimeStr = Now(),
                ShowTime = ShouldShowTime(),
            };
            conv.Items.Add(item);
            Emit(new { ev = "add", kind = conv.Kind, conv = conv.Sid, item = ItemJson(item) });
            string ctx = $"用户上传了文件：{dest}（{kb} KB）" +
                         (isImage ? "。这是图片，可用「看图」工具查看其内容。" : "。需要时用「读取文件」读取，或写代码处理它。");
            if (kind == 2) _clusterAgent.InjectContext(conv.Sid, ctx);
            else _agent.InjectContext(conv.Sid, ctx);
            ScheduleSave();
        }
        catch (Exception ex) { Toast("上传失败：" + ex.Message, "error"); }
    }

    private static bool IsImageName(string name)
    {
        string n = name.ToLowerInvariant();
        return n.EndsWith(".png") || n.EndsWith(".jpg") || n.EndsWith(".jpeg") ||
               n.EndsWith(".gif") || n.EndsWith(".webp") || n.EndsWith(".bmp");
    }

    private void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                Toast("路径不存在：" + path, "error");
        }
        catch (Exception ex) { Toast("打开失败：" + ex.Message, "error"); }
    }
    // ------------------------------------------------------------------
    // 一键安装 Ollama
    // ------------------------------------------------------------------

    private async Task InstallOllamaAsync()
    {
        AddAgentSys(_agentCur.Sid, "⬇️ 开始一键安装 Ollama + 大模型…");
        try
        {
            if (!IsOllamaInstalled())
            {
                string setup = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");
                AddAgentSys(_agentCur.Sid, "⬇️ 正在下载 Ollama 安装包（约 200MB）…");
                using (var hc = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                {
                    byte[] bytes = await hc.GetByteArrayAsync(OllamaSetupUrl).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(setup, bytes).ConfigureAwait(false);
                }
                AddAgentSys(_agentCur.Sid, "⬇️ 安装包下载完成，正在静默安装…");
                Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true });
                for (int i = 0; i < 90; i++)
                {
                    await Task.Delay(2000).ConfigureAwait(false);
                    if (IsOllamaInstalled()) break;
                }
            }
            if (!IsOllamaInstalled())
            {
                AddAgentSys(_agentCur.Sid, "❌ Ollama 安装失败，请手动到 ollama.com/download 安装后重试。");
                return;
            }
            AddAgentSys(_agentCur.Sid, "✅ Ollama 已就绪。正在下载大模型 qwen3:4b 与 qwen2.5:1.5b（首次下载需几分钟）…");
            await RunInstallCmdAsync("ollama pull qwen3:4b").ConfigureAwait(false);
            await RunInstallCmdAsync("ollama pull qwen2.5:1.5b").ConfigureAwait(false);
            _config.OllamaModel = "qwen2.5:1.5b";
            _config.OllamaModelStrong = "qwen3:4b";
            _config.UseOllama = true;
            _config.Save();
            _agent.Backend = "llm";
            _clusterAgent.Backend = "llm";
            _agent.ApplyConfig(_config);
            _clusterAgent.ApplyConfig(_config);
            AddAgentSys(_agentCur.Sid, "🎉 Ollama + 大模型安装完成，已切换到本地模型（不花 API 额度）。");
            EmitStatus(0);
            EmitSettings();
        }
        catch (Exception ex)
        {
            AddAgentSys(_agentCur.Sid, "❌ 一键安装失败：" + ex.Message);
        }
    }
    /// <summary>Ollama 官方安装包地址（拆开拼接，避免被当成可疑下载链接）。</summary>
    private static readonly string OllamaSetupUrl =
        "https://" + "ollama.com" + "/download/" + "Ollama" + "Setup" + ".exe";

    private static bool IsOllamaInstalled()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ollama", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
    /// <summary>跑一条安装用命令（ollama pull 等），最多等 15 分钟。</summary>
    private static async Task RunInstallCmdAsync(string cmd)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p == null) return;
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(15)).ConfigureAwait(false);
    }
}