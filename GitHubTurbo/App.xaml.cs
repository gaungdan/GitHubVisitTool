using System.Windows;
using Forms = System.Windows.Forms;

namespace GitHubTurbo;

public partial class App : System.Windows.Application
{
    AppController? controller;
    Forms.NotifyIcon? trayIcon;
    MainWindow? mainWindow;
    Mutex? instanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        instanceMutex = new Mutex(initiallyOwned: true, "Local\\GitHubTurbo.Singleton", out var isFirstInstance);
        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show("GitHub Turbo 已在运行。", "GitHub Turbo", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        controller = new AppController();
        await controller.InitializeAsync();
        CreateTrayIcon();

        mainWindow = new MainWindow(controller);
        mainWindow.Closing += MainWindow_Closing;
        mainWindow.Closed += (_, _) =>
        {
            trayIcon!.Visible = false;
            trayIcon!.Dispose();
            trayIcon = null;
        };
        mainWindow.Show();
    }

    void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isShuttingDown)
        {
            e.Cancel = true;
            mainWindow!.Hide();
        }
    }

    bool _isShuttingDown;

    void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => ShowMainWindow());
        menu.Items.Add("启动全部加速", null, async (_, _) => await controller!.StartAllAsync());
        menu.Items.Add("停止全部加速", null, async (_, _) => await controller!.StopAllAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Shutdown());

        trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "GitHub Turbo",
            Visible = true,
            ContextMenuStrip = menu,
        };
        trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    void ShowMainWindow()
    {
        if (mainWindow is null) return;
        mainWindow.Show();
        if (mainWindow.WindowState == WindowState.Minimized)
            mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    new void Shutdown()
    {
        _isShuttingDown = true;
        mainWindow?.Close();
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (controller?.RestoreHostsOnExit == true)
            {
                controller.StopAllAsync().GetAwaiter().GetResult();
                HostsFileService.RemoveAllHosts();
            }
        }
        catch { }
        if (trayIcon is not null)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
        }
        controller?.Dispose();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
