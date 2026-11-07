using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TrollWrangler.Core;

using TrollWrangler;

/// <summary>
/// 扩展（.dslt 工具包 / .dslp 皮肤包）的界面命令。
/// 安装逻辑在 ExtensionStore，这里只负责收发与把结果推给前端。
/// </summary>
public sealed partial class Kernel
{
    /// <summary>扩展 API 版本：界面显示出来，一眼看出前端/内核是不是同一代。</summary>
    internal const string ExtApiVersion = "2026-11-07a";

    private static readonly JsonSerializerOptions ExtJson = new() { WriteIndented = false };

    /// <summary>
    /// 扩展相关命令的落盘日志。界面那头没有控制台，出问题只能靠这个查：
    /// 有日志说明命令到了内核，没日志说明前端没发出来 / 跑的是旧 DLL。
    /// </summary>
    private void ExtLog(string msg)
    {
        try
        {
            string p = System.IO.Path.Combine(_dataDir, "ext.log");
            System.IO.File.AppendAllText(p,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>前端上报的日志（异常、点击），写到 data\ui.log。</summary>
    private void UiLog(string text)
    {
        try
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(_dataDir, "ui.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text + Environment.NewLine);
        }
        catch { }
    }

    private void EmitExtensions()
    {
        Emit(new
        {
            ev = "extensions",
            tools = ExtToolsJson(),
            skins = ExtSkinsJson(),
            activeSkin = ExtensionStore.ActiveSkinId(_dataDir),
        });
    }

    internal List<object> ExtToolsJson() => ExtensionStore.Tools(_dataDir)
        .Select(t => (object)new
        {
            id = t.Id, name = t.Name, version = t.Version, author = t.Author,
            description = t.Description, runtime = t.Runtime, entry = t.Entry, enabled = t.Enabled,
        }).ToList();

    internal List<object> ExtSkinsJson() => ExtensionStore.Skins(_dataDir)
        .Select(s => (object)new
        {
            id = s.Id, name = s.Name, version = s.Version, author = s.Author,
            description = s.Description, baseTheme = s.Base,
            defaultFor = s.DefaultFor, builtin = s.Builtin,
        }).ToList();

    /// <summary>当前选中的第三方皮肤打包给前端（CSS / 变量 / 壁纸）。没选返回 null。</summary>
    internal object? SkinPayload()
    {
        var skin = ExtensionStore.ActiveSkin(_dataDir);
        if (skin == null) return null;
        return new
        {
            extApi = ExtApiVersion,
            id = skin.Id,
            name = skin.Name,
            body = skin.Base,                 // light / dark
            vars = skin.Vars,
            css = skin.Css,
            wallpaper = ImageUrl(skin.Wallpaper),
        };
    }

    /// <summary>
    /// 重新扫盘。用于「另一个进程刚装完扩展包」的场景：
    /// 双击 .dslt/.dslp 会新起一个进程装完就退，正开着的窗口靠这条命令把
    /// 新工具塞进 Agent 工具表和界面（切回窗口时自动触发，不弹提示）。
    /// </summary>
    internal void ExtRescan()
    {
        RefreshAgentTools();
        EmitExtensions();
        EmitSettings();
    }

    private void ExtInstall(string path)
    {
        ExtLog("extInstall 收到: " + path);
        var (ok, msg, name) = ExtensionStore.Install(_dataDir, path);
        ExtLog("extInstall 结果: " + ok + " " + msg);
        Emit(new { ev = "toast", ok, text = msg });
        if (ok)
        {
            RefreshAgentTools();
            EmitExtensions();
            EmitSettings();
        }
    }

    private void ExtRemove(string id)
    {
        if (ExtensionStore.IsBuiltinSkin(_dataDir, id))
        {
            Emit(new { ev = "toast", ok = false, text = "内置默认皮肤不能删除，换个皮肤或关掉即可" });
            return;
        }
        bool ok = ExtensionStore.Remove(_dataDir, id);
        Emit(new { ev = "toast", ok, text = ok ? $"已删除扩展 {id}（重启对话后生效）" : "没找到这个扩展" });
        if (ok) { RefreshAgentTools(); EmitExtensions(); EmitSettings(); }
    }

    private void ExtToolToggle(string id, bool on)
    {
        bool ok = ExtensionStore.SetToolEnabled(_dataDir, id, on);
        if (!ok) { Emit(new { ev = "toast", ok = false, text = "没找到这个工具" }); return; }
        RefreshAgentTools();
        EmitExtensions();
        EmitSettings();
    }

    private void ExtSkinApply(string id)
    {
        ExtLog("extSkinApply 收到 id=" + (id ?? "(null)"));
        bool ok = ExtensionStore.SetActiveSkin(_dataDir, id ?? "");
        ExtLog("extSkinApply 结果 ok=" + ok + " active=" + ExtensionStore.ActiveSkinId(_dataDir));
        Emit(new { ev = "toast", ok, text = ok ? (string.IsNullOrEmpty(id) ? "已关闭皮肤" : "皮肤已应用") : "没找到这个皮肤" });
        if (ok)
        {
            // 主题（亮/暗）由右上角 ☀️/🌙 独占控制：皮肤只是叠加层，
            // 绝不能因为装了个 base=dark 的皮肤就把当前浅色模式掰过去。
            EmitExtensions();
            EmitSettings();
            // 通知界面重载：皮肤是整页样式，增量改容易漏（优先级/缓存都坑过），
            // 重载一次等于"重新导入 CSS"，最稳。
            Emit(new { ev = "skinChanged", id = ExtensionStore.ActiveSkinId(_dataDir) });
        }
    }

    /// <summary>把目录打成 .dslt / .dslp（AI 造完扩展后调用；手搓的人也能用）。</summary>
    private void ExtPack(string dir, string outPath)
    {
        var (ok, msg) = ExtensionStore.Pack(dir, outPath);
        Emit(new { ev = "toast", ok, text = msg });
    }

    /// <summary>把已启用的自定义工具塞给 Agent（它才知道有这么些工具可调）。</summary>
    internal void RefreshAgentTools()
    {
        try
        {
            var list = ExtensionStore.Tools(_dataDir).Where(t => t.Enabled).ToList();
            _agent.SetCustomTools(list);
            _clusterAgent.SetCustomTools(list);
        }
        catch { }
    }
}
