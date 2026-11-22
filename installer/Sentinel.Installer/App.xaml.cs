using System.Windows;

namespace Sentinel.Installer;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Any(arg => string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase)))
        {
            var exitCode = Task.Run(() => SilentInstall.RunAsync(e.Args)).GetAwaiter().GetResult();
            Environment.Exit(exitCode);
            return;
        }
        if (e.Args.Any(arg => string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            var installed = InstallerCore.FindInstalled() ?? InstallerCore.DefaultInstallDir;
            new UninstallWindow(installed).Show();
            return;
        }
        new MainWindow().Show();
    }
}
