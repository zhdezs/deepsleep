using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TrollWrangler.Core;

/// <summary>Watchdog：定时收掉状态停在 active、实际没在跑的会话。</summary>
public sealed partial class Kernel
{
    /// <summary>本进程此刻真正在跑的 Worker 会话（RunSplitAsync 进出时维护）。</summary>
    private readonly HashSet<int> _liveWorkers = new();
    private readonly object _liveGate = new();
    private Timer? _watchdogTimer;
    private int _watchdogBusy;   // 0/1，防止上一轮没跑完又进来一轮

    private void StartWatchdog()
    {
        StopWatchdog();
        if (_config.WatchdogMinutes <= 0) return;
        _watchdogTimer = new Timer(_ => WatchdogTick(), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void StopWatchdog()
    {
        try { _watchdogTimer?.Dispose(); } catch { }
        _watchdogTimer = null;
    }

    /// <summary>启动时清理：进程重启后，磁盘上不可能还有「正在跑」的 Worker，一律收成 stopped。</summary>
    private void ReapOrphanWorkers()
    {
        try
        {
            int n = 0;
            foreach (var e in Sessions.List("worker", "active"))
            {
                if (Sessions.SetState(e.Sid, "stopped")) n++;
            }
            if (n > 0)
            {
                Toast("清理了 " + n + "个上次没跑完的 Worker 会话（已标记为 stopped，历史仍保留）", "info");
                EmitWorkers();
            }
        }
        catch { /* 清理失败不影响启动 */ }
    }

    private void WatchdogTick()
    {
        if (Interlocked.CompareExchange(ref _watchdogBusy, 1, 0) != 0) return;
        try
        {
            int minutes = _config.WatchdogMinutes;
            if (minutes <= 0) return;
            var limit = DateTimeOffset.UtcNow.AddMinutes(-minutes);
            var reaped = new List<int>();
            List<int> live;
            lock (_liveGate) live = _liveWorkers.ToList();

            foreach (var e in Sessions.List("worker", "active"))
            {
                if (live.Contains(e.Sid)) continue;          // 真在跑，别动
                if (e.UpdatedUtc > limit) continue;           // 还没到判定时限
                if (Sessions.SetState(e.Sid, "stopped")) reaped.Add(e.Sid);
            }

            if (reaped.Count == 0) return;
            EmitWorkers();
            Emit(new { ev = "watchdog", reaped, minutes });
            Toast("看门狗收掉 " + reaped.Count + "个卡死的 Worker 会话（" + minutes + "分钟没动静）", "info");
        }
        catch { /* 看门狗自己绝不能把主流程带崩 */ }
        finally { Interlocked.Exchange(ref _watchdogBusy, 0); }
    }

    /// <summary>RunSplitAsync 用：某路 Worker 开始 / 结束（决定它算不算「活着」）。</summary>
    private void MarkWorkerLive(int sid, bool live)
    {
        lock (_liveGate)
        {
            if (live) _liveWorkers.Add(sid);
            else _liveWorkers.Remove(sid);
        }
    }

    /// <summary>架构面板用：真在跑的 Worker 数（内存状态，不走磁盘）。</summary>
    private int LiveWorkerCount()
    {
        lock (_liveGate) return _liveWorkers.Count;
    }
}
