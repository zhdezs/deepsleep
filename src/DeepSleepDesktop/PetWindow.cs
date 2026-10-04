using System.Text.Json;
using Photino.NET;

namespace DeepSleep.Desktop;

/// <summary>
/// 跨平台桌宠：Windows 版的桌宠是 Win32 分层窗口直接画 pet.raw，这里改成
/// 一个透明、无边框、置顶的小窗口，里面放一张 pet.png + 一小段 JS：
///   · 按住拖 → 把增量发给外壳，外壳挪窗口（位置记进 config，下次还在原地）；
///   · 单击 → 开 / 关桌宠聊天浮窗；双击 → 显示主窗口；右键 → 收起桌宠（设置里能再打开）。
/// 自己的窗口跑在自己的线程上（Photino 每个窗口一个消息循环）。
/// </summary>
internal sealed class PetWindow : IDisposable
{
    private const int Size = 180;

    private readonly Action _onChat, _onMenu, _onOpenMain;
    private readonly Action<int, int> _onMoved;
    private readonly int _startX, _startY;
    private readonly string _html;
    private PhotinoWindow? _win;
    private int _x, _y;
    private volatile bool _closed;

    public PetWindow(string imgPath, Action onChat, Action onMenu, Action onOpenMain,
                     Action<int, int> onMoved, int startX, int startY)
    {
        _html = BuildHtml(imgPath);
        _onChat = onChat; _onMenu = onMenu; _onOpenMain = onOpenMain;
        _onMoved = onMoved; _startX = startX; _startY = startY;
        var th = new Thread(Run) { IsBackground = true, Name = "deepsleep-pet" };
        th.Start();
    }

    private static string BuildHtml(string imgPath)
    {
        string url = new Uri(imgPath, UriKind.Absolute).AbsoluteUri;
        return """
<!DOCTYPE html><html><head><meta charset="utf-8"><style>
  html,body{margin:0;padding:0;background:transparent;overflow:hidden;
            -webkit-user-select:none;user-select:none;cursor:grab}
  img{width:100%;height:100%;display:block;-webkit-user-drag:none}
</style></head><body>
<img id="p" src="IMGPATH" alt="">
<script>
'use strict';
var post = function (o) { try { window.external.sendMessage(JSON.stringify(o)); } catch (e) {} };
var dragging = false, lx = 0, ly = 0, moved = 0;
document.addEventListener('mousedown', function (e) {
  if (e.button !== 0) return;
  dragging = true; moved = 0; lx = e.screenX; ly = e.screenY;
  document.body.style.cursor = 'grabbing'; e.preventDefault();
});
window.addEventListener('mousemove', function (e) {
  if (!dragging) return;
  var dx = e.screenX - lx, dy = e.screenY - ly;
  lx = e.screenX; ly = e.screenY;
  if (!dx && !dy) return;
  moved += Math.abs(dx) + Math.abs(dy);
  post({ cmd: 'petDrag', dx: dx, dy: dy });
});
window.addEventListener('mouseup', function (e) {
  if (!dragging) return;
  dragging = false; document.body.style.cursor = 'grab';
  if (moved < 5) post({ cmd: 'petClick' });
});
window.addEventListener('dblclick', function () { post({ cmd: 'showWindow' }); });
window.addEventListener('contextmenu', function (e) { e.preventDefault(); post({ cmd: 'petMenu' }); });
</script></body></html>
""".Replace("IMGPATH", url);
    }

    private void Run()
    {
        try
        {
            var win = new PhotinoWindow()
                .SetTitle("deepsleep 桌宠")
                .SetUseOsDefaultSize(false)
                .SetSize(Size, Size)
                .SetResizable(false)
                .SetChromeless(true)
                .SetTransparent(true)
                .SetTopMost(true)
                .SetContextMenuEnabled(false)
                .SetLogVerbosity(0);
            _win = win;

            (int w, int h) = ScreenSize(win);
            _x = _startX == int.MinValue ? Math.Max(0, w - Size - 40) : _startX;
            _y = _startY == int.MinValue ? Math.Max(0, h - Size - 80) : _startY;
            try { win.MoveTo(_x, _y, true); } catch { }

            win.RegisterWebMessageReceivedHandler((_, message) => OnMessage(message));
            win.LoadRawString(_html);
            win.WaitForClose();
        }
        catch (Exception ex)
        {
            try
            {
                string log = Path.Combine(Path.GetTempPath(), "deepsleep-pet.log");
                File.AppendAllText(log, DateTime.Now.ToString("s") + " 桌宠窗口起不来：" + ex.Message + "\n");
            }
            catch { }
            _win = null;
        }
        finally { _closed = true; }
    }

    private static (int W, int H) ScreenSize(PhotinoWindow win)
    {
        try
        {
            var m = win.MainMonitor;
            if (m.WorkArea.Width > 200 && m.WorkArea.Height > 200)
                return (m.WorkArea.Width, m.WorkArea.Height);
        }
        catch { }
        return (1280, 800);
    }

    private void OnMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            string cmd = doc.RootElement.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
            switch (cmd)
            {
                case "petDrag":
                {
                    int dx = doc.RootElement.TryGetProperty("dx", out var a) ? a.GetInt32() : 0;
                    int dy = doc.RootElement.TryGetProperty("dy", out var b) ? b.GetInt32() : 0;
                    _x += dx; _y += dy;
                    try { _win?.MoveTo(_x, _y, true); } catch { }
                    break;
                }
                case "petClick":
                    _onChat();
                    break;
                case "petMenu":
                    _onMoved(_x, _y);
                    _onMenu();
                    break;
                case "showWindow":
                    _onMoved(_x, _y);
                    _onOpenMain();
                    break;
            }
        }
        catch { }
    }

    public void Show()
    {
        try { _win?.SetMinimized(false); } catch { }
    }

    public void Dispose()
    {
        if (_closed) return;
        try { _win?.SetTopMost(false); } catch { }
        try { _win?.Close(); } catch { }
    }
}
