using System.Text;

namespace TrollWrangler.CoreHost;

internal static partial class Program
{
    /// <summary>本机首页：显示端口 / 令牌 / 在线入口，点一下就能配好。</summary>
    private static string HomePage()
    {
        string tpl = """
<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>deepsleep 内核版</title>
<style>
 * { box-sizing: border-box; }
 body { margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center;
        background: #1c1c1e; color: #f2f2f7; font: 14px/1.7 -apple-system, "Segoe UI", "Microsoft YaHei", sans-serif; }
 .card { width: 660px; max-width: 92vw; background: #2c2c2e; border-radius: 16px; padding: 26px 28px;
         box-shadow: 0 20px 60px rgba(0,0,0,.45); }
 h1 { margin: 0 0 4px; font-size: 19px; }
 .sub { color: #98989d; font-size: 12.5px; margin-bottom: 18px; }
 .row { display: flex; align-items: center; gap: 10px; padding: 9px 12px; background: #3a3a3c;
        border-radius: 10px; margin-bottom: 8px; font-size: 13px; }
 .row b { color: #98989d; font-weight: 500; min-width: 76px; }
 code { font-family: ui-monospace, Consolas, monospace; word-break: break-all; }
 .tok { background: #48484a; padding: 2px 8px; border-radius: 6px; }
 a.btn, button.btn { display: inline-block; padding: 9px 15px; border-radius: 10px; border: 0; cursor: pointer;
        background: #0a84ff; color: #fff; text-decoration: none; font-size: 13px; font-weight: 600; }
 a.ghost { background: #48484a; color: #f2f2f7; }
 .btns { display: flex; gap: 8px; flex-wrap: wrap; margin: 16px 0 6px; }
 h2 { font-size: 13px; color: #98989d; margin: 18px 0 6px; font-weight: 600; }
 ul { margin: 0; padding-left: 20px; color: #d1d1d6; font-size: 12.5px; }
 .warn { margin-top: 14px; font-size: 12px; color: #ffd60a; }
</style></head><body><div class="card">
  <h1>deepsleep 内核版（Core）</h1>
  <div class="sub">已在本机运行，网页版连上来就能用完整能力（工具 / 文件 / 命令 / 记忆 / 技能）。</div>
  <div class="row"><b>端口</b><code>%PORT%</code></div>
  <div class="row"><b>配对令牌</b><code class="tok" id="tk">%TOKEN%</code></div>
  <div class="row"><b>数据目录</b><code>%DATA%</code></div>

  <div class="btns">
    <a class="btn" href="/web/core/?p=%PORT%&t=%TOKEN%">本机 · 内核版网页（操控这台电脑）</a>
    <a class="btn ghost" href="/web/app/?p=%PORT%&t=%TOKEN%">本机 · 极简网页版</a>
    <button class="btn ghost" id="cp">复制令牌</button>
  </div>

  <h2>在外网（GitHub Pages）上用这台电脑</h2>
  <ul>
    <li>打开 <code>https://zhdezs.github.io/deepsleep/web/core/</code>，把上面的端口和令牌填进去（或直接点下面这条链接）。</li>
    <li><a href="https://zhdezs.github.io/deepsleep/web/core/#p=%PORT%&t=%TOKEN%">https://zhdezs.github.io/deepsleep/web/core/#p=%PORT%&t=%TOKEN%</a></li>
  </ul>

  <h2>能做什么</h2>
  <ul>
    <li>聊天、写代码、长期记忆、技能；网络搜索 + 深度研究（带来源的研究报告落到 data\research\）。</li>
    <li>读写本机文件、跑命令（work 模式会先弹确认，boom 模式全自动）、Agent 集群分工干活。</li>
    <li>网页里选的文件会上传到 data\uploads\ 再交给 AI，和客户端一样。</li>
  </ul>

  <h2>安全</h2>
  <ul>
    <li>只监听 <code>127.0.0.1</code>，外网机器连不进来；所有接口都要配对令牌。</li>
    <li>跨域只放行 <code>zhdezs.github.io</code>、本机页面；换令牌：<code>deepsleep-core --new-token</code>。</li>
  </ul>
  <div class="warn">令牌等于这台电脑的钥匙：别截图给别人、别贴到公开场合。用完直接关掉这个窗口即可停掉内核。</div>
</div>
<script>
document.getElementById('cp').onclick = function () {
  navigator.clipboard.writeText(document.getElementById('tk').textContent);
  this.textContent = '已复制';
};
</script></body></html>
""";
        return tpl.Replace("%PORT%", _port.ToString())
                  .Replace("%DATA%", _dataDir)
                  .Replace("%TOKEN%", _token);
    }
}
