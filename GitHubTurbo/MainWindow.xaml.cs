using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GitHubTurbo;

public partial class MainWindow : Window
{
    readonly AppController controller;
    string? activePlatformId;
    bool isApplyingSettings;

    public ObservableCollection<PlatformModuleViewModel> PlatformModules { get; } = new();
    public ObservableCollection<DomainDiagnosticViewModel> DomainTests { get; } = new();

    public MainWindow(AppController controller)
    {
        InitializeComponent();
        this.controller = controller;
        DataContext = this;
        controller.PlatformStatusChanged += Controller_PlatformStatusChanged;
        isApplyingSettings = true;
        RestoreOnExitCheckBox.IsChecked = controller.Settings.RestoreHostsOnExit;
        AutoRefreshOnLaunchCheckBox.IsChecked = controller.Settings.AutoRefreshOnLaunch;
        isApplyingSettings = false;

        foreach (var provider in PlatformProviders.All)
        {
            var entries = provider.Domains.Select(d => new DomainEntry(d.Label, d.Domain)).ToList();
            var vm = new PlatformModuleViewModel(
                provider.Descriptor.Title,
                provider.Descriptor.Subtitle,
                provider.Descriptor.Abbreviation,
                provider.Descriptor.BrandColor,
                entries,
                provider.Descriptor.Id);
            vm.PropertyChanged += PlatformModule_PropertyChanged;
            PlatformModules.Add(vm);
        }

        if (PlatformModules.Count > 0)
        {
            activePlatformId = PlatformModules[0].PlatformId;
            PlatformModules[0].IsActive = true;
            foreach (var domain in PlatformDiagnostics.GetDiagnosticDomains(PlatformProviders.All[0]))
                DomainTests.Add(new DomainDiagnosticViewModel(domain.Domain, domain.Label));
        }

        RenderStatus(controller.PlatformStatuses.GetValueOrDefault(activePlatformId ?? "") ?? TurboStatus.Stopped);
    }

    void PlatformModule_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlatformModuleViewModel.IsActive)) return;
        if (sender is not PlatformModuleViewModel vm) return;
        if (!vm.IsActive) return;

        if (activePlatformId == vm.PlatformId) return;

        activePlatformId = vm.PlatformId;

        DomainTests.Clear();
        var provider = PlatformProviders.GetById(vm.PlatformId);
        if (provider is not null)
        {
            foreach (var domain in PlatformDiagnostics.GetDiagnosticDomains(provider))
                DomainTests.Add(new DomainDiagnosticViewModel(domain.Domain, domain.Label));
        }

        RenderStatus(controller.PlatformStatuses.GetValueOrDefault(activePlatformId) ?? TurboStatus.Stopped);
    }

    async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (activePlatformId is null) return;
        await RunActionAsync(() => controller.StartPlatformAsync(activePlatformId));
    }

    async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (activePlatformId is null) return;
        await RunActionAsync(() => controller.StopPlatformAsync(activePlatformId));
        MainTabs.SelectedIndex = 0;
    }

    async void DiagnoseButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 1;
        await RunNetworkTestAsync();
    }

    void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = AppPaths.LogDirectory,
            UseShellExecute = true,
        });
    }

    async void BackupHostsButton_Click(object sender, RoutedEventArgs e)
    {
        var result = await controller.BackupHostsAsync();
        System.Windows.MessageBox.Show(result.Message, "hosts 备份", MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    async void RestoreHostsButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(
            "将使用最近一次备份覆盖当前 hosts 文件，是否继续？",
            "恢复 hosts",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        var result = await controller.RestoreLatestBackupAsync();
        System.Windows.MessageBox.Show(result.Message, "恢复 hosts", MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        if (activePlatformId is not null)
            RenderStatus(controller.PlatformStatuses.GetValueOrDefault(activePlatformId) ?? TurboStatus.Stopped);
    }

    async void RestoreOnExitCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (isApplyingSettings)
            return;

        await controller.SetRestoreHostsOnExitAsync(RestoreOnExitCheckBox.IsChecked == true);
    }

    async void AutoRefreshOnLaunchCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (isApplyingSettings)
            return;

        await controller.SetAutoRefreshOnLaunchAsync(AutoRefreshOnLaunchCheckBox.IsChecked == true);
    }

    void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            MaximizeButton_Click(sender, e);
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    async Task RunActionAsync(Func<Task> action)
    {
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        DiagnoseButton.IsEnabled = false;
        try
        {
            await action();
        }
        finally
        {
            if (activePlatformId is not null)
                RenderStatus(controller.PlatformStatuses.GetValueOrDefault(activePlatformId) ?? TurboStatus.Stopped);
            DiagnoseButton.IsEnabled = true;
        }
    }

    async Task RunNetworkTestAsync()
    {
        DiagnoseButton.IsEnabled = false;
        try
        {
            if (activePlatformId is null) return;

            var status = controller.PlatformStatuses.GetValueOrDefault(activePlatformId) ?? TurboStatus.Stopped;
            if (!status.IsRunning)
            {
                System.Windows.MessageBox.Show("请先启动加速，再执行网络测试。", "网络测试", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SetDomainTestsPlaceholder("测试中...", "正在检测连通性");
            var results = await controller.RunDomainDiagnosticsAsync(activePlatformId);
            ApplyDomainResults(results);
        }
        finally
        {
            DiagnoseButton.IsEnabled = true;
            if (activePlatformId is not null)
                RenderStatus(controller.PlatformStatuses.GetValueOrDefault(activePlatformId) ?? TurboStatus.Stopped);
        }
    }

    void SetDomainTestsPlaceholder(string status, string detail)
    {
        foreach (var item in DomainTests)
        {
            item.Status = status;
            item.Latency = "--";
            item.Detail = detail;
            item.IsSuccess = false;
        }
    }

    void ApplyDomainResults(IReadOnlyList<DomainDiagnosticResult> results)
    {
        var lookup = results.ToDictionary(x => x.Host, StringComparer.OrdinalIgnoreCase);
        foreach (var item in DomainTests)
        {
            if (!lookup.TryGetValue(item.Host, out var result))
                continue;

            item.IsSuccess = result.Success;
            item.Status = result.Success ? "可用" : "失败";
            item.Latency = result.Latency;
            item.Detail = result.Detail;
        }
    }

    void Controller_PlatformStatusChanged(object? sender, PlatformStatusChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (e.PlatformId == activePlatformId)
                RenderStatus(e.Status);
        });
    }

    void RenderStatus(TurboStatus? status)
    {
        status ??= TurboStatus.Stopped;
        StatusTitleText.Text = status.Title;
        StatusDetailText.Text = status.Detail;
        if (status.IsRunning)
        {
            StatusIndicator.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
            StatusIndicator.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = 12,
                Color = System.Windows.Media.Color.FromRgb(16, 185, 129),
                Opacity = 0.6,
            };
        }
        else
        {
            StatusIndicator.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139));
            StatusIndicator.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = 6,
                Color = System.Windows.Media.Color.FromRgb(100, 116, 139),
                Opacity = 0.3,
            };
        }
        StartButton.IsEnabled = !status.IsRunning;
        StopButton.IsEnabled = status.IsRunning;
    }
}
