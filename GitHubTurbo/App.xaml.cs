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
        mainWindow.Closed += (_, _) =>
        {
            trayIcon!.Visible = false;
            trayIcon!.Dispose();
            trayIcon = null;
            Environment.Exit(0);
        };
        mainWindow.Show();
    }

    void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("启动全部加速", null, async (_, _) => await controller!.StartAllAsync());
        menu.Items.Add("停止全部加速", null, async (_, _) => await controller!.StopAllAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => mainWindow?.Close());

        trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "GitHub Turbo",
            Visible = true,
            ContextMenuStrip = menu,
        };
        trayIcon.DoubleClick += (_, _) => mainWindow?.Close();
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
