using System.Windows;

namespace CodexMultiLauncher;

public partial class App : Application, IDisposable
{
    private LauncherInstanceLease? _instanceLease;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (PackagedAppResume.IsResumeInvocation(e.Args))
        {
            Shutdown(PackagedAppResume.Resume(e.Args) ? 0 : -1);
            return;
        }

        base.OnStartup(e);

        try
        {
            var root = RootLocator.FindProjectRoot();
            _instanceLease = new LauncherInstanceLease(root);
            if (!_instanceLease.Acquired)
            {
                MessageBox.Show("此数据目录已有启动器运行，请从任务栏切换到已有窗口。", "Hyperion");
                Shutdown();
                return;
            }
            MainWindow = new MainWindow(root);
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "Hyperion 无法启动",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        _instanceLease?.Dispose();
        _instanceLease = null;
        GC.SuppressFinalize(this);
    }
}
