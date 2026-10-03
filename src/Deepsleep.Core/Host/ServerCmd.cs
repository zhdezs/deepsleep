using System.Diagnostics;
using System.Text.Json;
using TrollWrangler.Core;

namespace TrollWrangler.CoreHost;

/// <summary>命令分发：桌面外壳才有的命令在这里就地实现或明确拒绝。</summary>
public static partial class CoreServer
{
    private static async Task<string> ShellCommandAsync(string json)
    {
        string cmd = "";
        int id = 0;
        string? url = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            cmd = doc.RootElement.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
            id = doc.RootElement.TryGetProperty("id", out var i) && i.TryGetInt32(out int n) ? n : 0;
            url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
        }
        catch { }

        // 桌面外壳可以在这里接管（真的去显示 / 隐藏窗口）；内核版没有外壳，走下面的默认分支。
        if (ShellOverride != null)
        {
            try
            {
                string? handled = await ShellOverride(cmd, id, url);
                if (handled != null) return handled;
            }
            catch { }
        }

        switch (cmd)
        {
            case "openUrl":
                if (!string.IsNullOrWhiteSpace(url) && url!.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
                }
                return "{\"id\":" + id + ",\"ok\":true}";
            case "showWindow":
            case "hideWindow":
            case "quitApp":
                return "{\"id\":" + id + ",\"ok\":true}";
            default:
                return await _kernel.InvokeAsync(json);
        }
    }
}
