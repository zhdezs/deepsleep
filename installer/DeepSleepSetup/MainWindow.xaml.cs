using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace DeepSleepSetup;

public partial class MainWindow : Window
{
    private const string AppName = "deepsleep";
    private const string AppDisplay = "deepsleep AI 助手";
    private const string AppVersion = "1.0.14";
    private const string Publisher = "deepsleep";
    private const string ExeName = "deepsleep.exe";

    private static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "Programs", AppName);

    private static string UninstallKey =>
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName;

    public MainWindow()
    {
        InitializeComponent();
        PathBox.Text = DefaultDir;
    }

    // ------------------------------------------------------------ 界面事件

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择安装位置",
            InitialDirectory = Directory.Exists(PathBox.Text) ? PathBox.Text : DefaultDir,
        };
        if (dlg.ShowDialog(this) == true)
            PathBox.Text = dlg.FolderName;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        string target = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show(this, "请先选择安装位置。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try { target = Path.GetFullPath(target); }
        catch
        {
            MessageBox.Show(this, "安装位置不是合法路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            await InstallAsync(target, DesktopChk.IsChecked == true, true, true);
            MessageBox.Show(this,
                $"安装完成！\n\n程序位置：{target}\n用户数据：{Path.Combine(target, "data")}\n\n" +
                "首次使用请在 ⚙ 设置里填写自己的 API Key，或启用本地 Ollama 模型。",
                "安装完成", MessageBoxButton.OK, MessageBoxImage.Information);
            if (LaunchChk.IsChecked == true)
                Process.Start(new ProcessStartInfo(Path.Combine(target, ExeName)) { WorkingDirectory = target, UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "安装失败：\n" + ex.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        InstallBtn.IsEnabled = !busy;
        CancelBtn.IsEnabled = !busy;
        BrowseBtn.IsEnabled = !busy;
        PathBox.IsEnabled = !busy;
        DesktopChk.IsEnabled = !busy;
        LaunchChk.IsEnabled = !busy;
    }

    // ------------------------------------------------------------ 静默安装

    public void RunSilent(string? dir, bool desktop, bool launch, bool registry)
    {
        string target = string.IsNullOrWhiteSpace(dir) ? DefaultDir : Path.GetFullPath(dir);
        InstallCore(target, desktop, registry, null);   // 静默模式：同步执行，不依赖 UI 消息循环
        if (launch)
            Process.Start(new ProcessStartInfo(Path.Combine(target, ExeName)) { WorkingDirectory = target, UseShellExecute = true });
    }

    // ------------------------------------------------------------ 安装实现

    /// <summary>
    /// 自解压安装：把安装程序内嵌的 payload.zip 解压到目标目录，
    /// 再创建快捷方式、登记卸载信息。已存在的用户数据（data 目录）不会被覆盖。
    /// </summary>
    private async Task InstallAsync(string target, bool desktopShortcut, bool registerUninstall, bool reportProgress)
    {
        IProgress<(double Percent, string Text)> progress = new Progress<(double Percent, string Text)>(p =>
        {
            if (!reportProgress) return;
            Bar.Value = Math.Min(100, p.Percent);
            StatusText.Text = p.Text;
        });

        await Task.Run(() => InstallCore(target, desktopShortcut, registerUninstall, progress));
    }

    /// <summary>安装主体（同步）：自解压 → 快捷方式 → 卸载登记。</summary>
    private static void InstallCore(string target, bool desktopShortcut, bool registerUninstall,
                                    IProgress<(double Percent, string Text)>? progress)
    {
        ExtractPayload(target, progress);
        progress?.Report((92, "正在创建快捷方式…"));
        CreateShortcuts(target, desktopShortcut);

        progress?.Report((97, "正在登记卸载信息…"));
        if (registerUninstall) RegisterUninstallEntry(target);
        progress?.Report((100, "安装完成"));
    }

    private static void ExtractPayload(string target, IProgress<(double Percent, string Text)>? progress)
    {
        var asm = Assembly.GetExecutingAssembly();
        using Stream? zipStream = asm.GetManifestResourceStream("payload.zip")
            ?? throw new InvalidOperationException("安装程序损坏：找不到内嵌的程序包。");
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var entries = zip.Entries.Where(en => !string.IsNullOrEmpty(en.Name)).ToList();
        if (entries.Count == 0) throw new InvalidOperationException("程序包为空。");

        Directory.CreateDirectory(target);
        BackupUserConfig(target);          // 覆盖安装前先备份 data\config.json → config.json.bak
        int done = 0;
        foreach (var entry in entries)
        {
            string rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string dest = Path.Combine(target, rel);

            // 用户数据保护：data 目录下已存在的文件（配置、对话、记忆、模型）不覆盖
            bool isUserData = rel.StartsWith("data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (isUserData && File.Exists(dest))
            {
                done++;
                continue;
            }

            string? dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            entry.ExtractToFile(dest, overwrite: true);

            done++;
            if (progress != null && (done % 5 == 0 || done == entries.Count))
                progress.Report((done * 88.0 / entries.Count, $"正在自解压：{rel}"));
        }
    }

    /// <summary>
    /// 覆盖安装前把 data\config.json 备份成 data\config.json.bak。里面是 API Key 和各项设置，
    /// 一旦被程序包里的模板覆盖、或者被误删，靠它还能捞回来（程序启动时发现配置不在会自动恢复）。
    /// </summary>
    private static void BackupUserConfig(string target)
    {
        try
        {
            string src = Path.Combine(target, "data", "config.json");
            if (File.Exists(src)) File.Copy(src, src + ".bak", true);
        }
        catch { /* 备份失败不影响安装 */ }
    }

    private static void CreateShortcuts(string target, bool desktopShortcut)
    {
        string exe = Path.Combine(target, ExeName);
        string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppDisplay + ".lnk");
        CreateShortcut(startMenu, exe, target);
        if (desktopShortcut)
        {
            string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppDisplay + ".lnk");
            CreateShortcut(desktop, exe, target);
        }
    }

    /// <summary>用 WScript.Shell（COM 晚绑定）创建 .lnk，避免额外依赖。</summary>
    private static void CreateShortcut(string linkPath, string target, string workDir)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell == null) return;
            object? sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
            if (sc == null) return;
            var scType = sc.GetType();
            scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
            scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target });
            scType.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { AppDisplay });
            scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }
        catch
        {
            // 快捷方式失败不影响安装主体
        }
        finally
        {
            if (shell != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    private static void RegisterUninstallEntry(string target)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, writable: true);
            if (key == null) return;
            string exe = Path.Combine(target, ExeName);
            string uninstScript = Path.Combine(target, "uninstall.ps1");
            string uninstallString = $"powershell.exe -ExecutionPolicy Bypass -NoProfile -File \"{uninstScript}\"";

            key.SetValue("DisplayName", AppDisplay);
            key.SetValue("DisplayVersion", AppVersion);
            key.SetValue("Publisher", Publisher);
            key.SetValue("InstallLocation", target);
            key.SetValue("DisplayIcon", exe);
            key.SetValue("UninstallString", uninstallString);
            key.SetValue("QuietUninstallString", uninstallString + " -Silent");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            try
            {
                long size = new DirectoryInfo(target).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                key.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
            }
            catch { }
        }
        catch
        {
            // 注册表写入失败不影响安装
        }
    }
}
