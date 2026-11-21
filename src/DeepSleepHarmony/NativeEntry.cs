using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using TrollWrangler;
using TrollWrangler.Core;
using TrollWrangler.CoreHost;

namespace DeepSleepHarmony;

/// <summary>deepsleep 内核的鸿蒙原生入口。</summary>
public static unsafe class NativeEntry
{
    private static Kernel? _kernel;
    private static string _dataDir = "";
    private static string _rootDir = "";
    private static string _token = "";
    private static int _port;
    private static string _lastError = "";
    private static volatile bool _started;

    /// <summary>日志回调：NAPI 层注册进来，内核的输出转成 hilog。</summary>
    private static Action<string>? _logSink;

    [UnmanagedCallersOnly(EntryPoint = "deepsleep_set_logger")]
    public static void SetLogger(IntPtr fn)
    {
        if (fn == IntPtr.Zero) { _logSink = null; return; }
        var cb = (delegate* unmanaged[Cdecl]<IntPtr, void>)fn;
        _logSink = s => cb(ToUtf8(s));
    }

    [UnmanagedCallersOnly(EntryPoint = "deepsleep_version")]
    public static IntPtr Version() => ToUtf8(AppInfo.Version);

    [UnmanagedCallersOnly(EntryPoint = "deepsleep_start")]
    public static int Start(IntPtr dataDirUtf8, IntPtr rootDirUtf8, int port)
    {
        if (_started) return 0;
        try
        {
            _dataDir = FromUtf8(dataDirUtf8);
            _rootDir = FromUtf8(rootDirUtf8);
            if (_dataDir.Length == 0) { _lastError = "数据目录为空"; return -1; }
            if (_rootDir.Length == 0) _rootDir = _dataDir;

            // 鸿蒙跑在沙箱里，默认数据目录（XDG 那套）不存在，必须显式建。
            Directory.CreateDirectory(_dataDir);

            _port = port > 0 && port < 65536 ? port : 8756;
            _token = LoadOrCreateToken();

            _kernel = new Kernel();
            _kernel.Push += OnKernelPush;
            _kernel.Init(_dataDir);

            bool up = CoreServer.Start(new CoreServerOptions
            {
                Kernel = _kernel,
                Port = _port,
                Token = _token,
                RootDir = _rootDir,
                DataDir = _dataDir,
                // 只监听回环：鸿蒙上内核只服务本应用自己的 ArkWeb，
                // 不对外暴露（要跨机用超级连接的隧道那时再 Rebind）。
                Bind = System.Net.IPAddress.Loopback,
                // 内置隧道靠系统 ssh，鸿蒙沙箱里没有 ssh，开了只会白试。
                Tunnel = false,
            });

            if (!up)
            {
                _lastError = "端口 " + _port + "起不来";
                return -2;
            }

            _started = true;
            Say("[harmony] 内核已就绪 127.0.0.1:" + _port);
            return 0;
        }
        catch (Exception ex)
        {
            _lastError = ex.GetType().Name + ": " + ex.Message;
            return -3;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "deepsleep_port")]
    public static int Port() => _port;

    [UnmanagedCallersOnly(EntryPoint = "deepsleep_token")]
    public static IntPtr Token() => ToUtf8(_token);

    [UnmanagedCallersOnly(EntryPoint = "deepsleep_error")]
    public static IntPtr Error() => ToUtf8(_lastError);

    [UnmanagedCallersOnly(EntryPoint = "deepsleep_free")]
    public static void Free(IntPtr p) { if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }

    [UnmanagedCallersOnly(EntryPoint = "deepsleep_stop")]
    public static void Stop()
    {
        try { CoreServer.Stop(); } catch { }
        try { _kernel?.Shutdown(); } catch { }
        _kernel = null;
        _started = false;
    }

    private static void OnKernelPush(string json)
    {
        // 内核事件流：桌面版走 WebView 的 postMessage，网页版走 SSE。
        // 鸿蒙这边不需要额外转发（ArkWeb 直接连 HTTP/SSE），只把日志捞出来。
        if (json.Contains("\"event\":\"log\"", StringComparison.Ordinal)) Say(json);
    }

    private static void Say(string line)
    {
        try { _logSink?.Invoke(line); } catch { }
    }

    private static string LoadOrCreateToken()
    {
        try
        {
            string file = Path.Combine(_dataDir, "core-token.txt");
            if (File.Exists(file))
            {
                string t = File.ReadAllText(file).Trim();
                if (t.Length >= 16) return t;
            }
            string nt = Guid.NewGuid().ToString("N");
            File.WriteAllText(file, nt, new UTF8Encoding(false));
            return nt;
        }
        catch { return Guid.NewGuid().ToString("N"); }
    }

    // UTF-8 字符串在托管/非托管之间来回返回给 NAPI 的串用 CoTaskMem 分配，对方必须调 deepsleep_free。

    private static IntPtr ToUtf8(string s)
        => Marshal.StringToCoTaskMemUTF8(s ?? "");

    private static string FromUtf8(IntPtr p)
        => p == IntPtr.Zero ? "" : (Marshal.PtrToStringUTF8(p) ?? "");
}

/// <summary>版本号从程序集元数据读，不写死 —— 版本来回改时只动 Directory.Build.props 一处 （AGENTS.md 里记过一次教训：内核那份忘了同步，程序自报版本一直是旧的）。</summary>
internal static class AppInfo
{
    public static string Version { get; } = Read();

    private static string Read()
    {
        try
        {
            var asm = typeof(AppInfo).Assembly;
            var attr = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>();
            string v = attr?.InformationalVersion ?? "";
            int plus = v.IndexOf('+');          // SDK 会拼上 +<commit sha>，去掉
            if (plus > 0) v = v[..plus];
            if (!string.IsNullOrWhiteSpace(v)) return v;
            return asm.GetName().Version?.ToString(3) ?? "unknown";
        }
        catch { return "unknown"; }
    }
}
