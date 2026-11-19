using System.Windows;
using System.Windows.Threading;
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

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        instanceMutex = new Mutex(initiallyOwned: true, "Local\\GitHubTurbo.Singleton", out var isFirstInstance);
        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show("GitHub Turbo 已在运行。", "GitHub Turbo", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        controller = new AppController();
        try
        {
            await controller.InitializeAsync();
        }
        catch (Exception ex)
        {
            await AppLogger.ErrorAsync($"Initialize failed: {ex}");
            System.Windows.MessageBox.Show($"初始化失败：{ex.Message}", "GitHub Turbo", MessageBoxButton.OK, MessageBoxImage.Error);
        }

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
        menu.Items.Add("退出", null, (_, _) => Exit());

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

    public new void Exit()
    {
        _isShuttingDown = true;
        mainWindow?.Close();
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // The proxy dies with this process; leaving loopback hosts entries behind
        // would point every accelerated domain at a closed port.
        if (controller is not null)
        {
            try
            {
                if (HostsFileService.HasAnyEntries)
                {
                    var removed = HostsFileService.TryRemoveAllHosts();
                    _ = AppLogger.InfoAsync($"Exit hosts cleanup: {removed.Message}");
                }
            }
            catch (Exception ex)
            {
                _ = AppLogger.ErrorAsync($"Exit hosts cleanup failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (trayIcon is not null)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
        }
        controller?.Dispose();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }

    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _ = AppLogger.ErrorAsync($"Unhandled UI exception: {e.Exception}");
        System.Windows.MessageBox.Show(
            $"发生未处理异常：{e.Exception.Message}\n详细信息已写入日志。",
            "GitHub Turbo",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            _ = AppLogger.ErrorAsync($"Unhandled exception: {ex}");
    }
}
