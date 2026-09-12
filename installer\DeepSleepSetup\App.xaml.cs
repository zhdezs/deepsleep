using System.Windows;

namespace DeepSleepSetup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var win = new MainWindow();

        bool silent = e.Args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        if (silent)
        {
            string? dir = null;
            for (int i = 0; i < e.Args.Length - 1; i++)
                if (e.Args[i].Equals("--dir", StringComparison.OrdinalIgnoreCase))
                    dir = e.Args[i + 1];
            bool desktop = !e.Args.Any(a => a.Equals("--no-desktop", StringComparison.OrdinalIgnoreCase));
            bool launch = !e.Args.Any(a => a.Equals("--no-launch", StringComparison.OrdinalIgnoreCase));
            bool registry = !e.Args.Any(a => a.Equals("--no-registry", StringComparison.OrdinalIgnoreCase));
            win.RunSilent(dir, desktop, launch, registry);
            Shutdown(0);
            return;
        }

        win.Show();
    }
}
