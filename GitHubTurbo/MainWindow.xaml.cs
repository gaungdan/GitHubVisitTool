using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GitHubTurbo;

public partial class MainWindow : Window
{
    readonly AppController controller;
    string? activePlatformId;
    bool isApplyingSettings;
    readonly Progress<PlatformProgressEventArgs> progress;
    readonly SolidColorBrush _runningBrush;
    readonly SolidColorBrush _stoppedBrush;
    readonly System.Windows.Media.Effects.DropShadowEffect _runningEffect;
    readonly System.Windows.Media.Effects.DropShadowEffect _stoppedEffect;
    Storyboard? _pulseStoryboard;

    public ObservableCollection<PlatformModuleViewModel> PlatformModules { get; } = new();
    public ObservableCollection<DomainDiagnosticViewModel> DomainTests { get; } = new();

    public MainWindow(AppController controller)
    {
        InitializeComponent();
        this.controller = controller;
        DataContext = this;
        controller.PlatformStatusChanged += Controller_PlatformStatusChanged;
        progress = new(OnProgressChanged);
        _runningBrush = new(System.Windows.Media.Color.FromRgb(16, 185, 129));
        _stoppedBrush = new(System.Windows.Media.Color.FromRgb(100, 116, 139));
        _runningEffect = new()
        {
            ShadowDepth = 0,
            BlurRadius = 12,
            Color = System.Windows.Media.Color.FromRgb(16, 185, 129),
            Opacity = 0.6,
        };
        _stoppedEffect = new()
        {
            ShadowDepth = 0,
            BlurRadius = 6,
            Color = System.Windows.Media.Color.FromRgb(100, 116, 139),
            Opacity = 0.3,
        };
        isApplyingSettings = true;
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

        RefreshAllHostsIps();

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

    void RefreshAllHostsIps()
    {
        foreach (var provider in PlatformProviders.All)
            RefreshHostsIpsForPlatform(provider.Descriptor.Id);
    }

    void RefreshHostsIpsForPlatform(string platformId)
    {
        var entries = HostsFileService.ReadEntries(platformId);
        var vm = PlatformModules.FirstOrDefault(m => m.PlatformId == platformId);
        if (vm is null) return;

        foreach (var domain in vm.Domains)
            domain.CurrentIp = entries.TryGetValue(domain.Domain, out var ip) ? ip : null;
    }

    async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (activePlatformId is null) return;
        await RunActionAsync(() => controller.StartPlatformAsync(activePlatformId, progress));
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

    async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (controller.AnyRunning)
        {
            var confirm = System.Windows.MessageBox.Show(
                "检测到加速正在运行，是否停止加速？",
                "关闭",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (confirm == MessageBoxResult.Cancel)
                return;

            if (confirm == MessageBoxResult.Yes)
            {
                await controller.StopAllAsync();
                RefreshAllHostsIps();
                if (activePlatformId is not null)
                    RenderStatus(controller.PlatformStatuses.GetValueOrDefault(activePlatformId) ?? TurboStatus.Stopped);
            }
        }

        WindowState = WindowState.Minimized;
        Hide();
    }

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
        ShowProgress();
        try
        {
            await action();
        }
        finally
        {
            HideProgress();
            if (activePlatformId is not null)
            {
                RefreshHostsIpsForPlatform(activePlatformId);
                RenderStatus(controller.PlatformStatuses.GetValueOrDefault(activePlatformId) ?? TurboStatus.Stopped);
            }
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = true;
            DiagnoseButton.IsEnabled = true;
        }
    }

    async Task RunNetworkTestAsync()
    {
        DiagnoseButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        ShowProgress();
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
            var results = await controller.RunDomainDiagnosticsAsync(activePlatformId, progress);
            ApplyDomainResults(results);
        }
        finally
        {
            HideProgress();
            DiagnoseButton.IsEnabled = true;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = true;
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

    void OnProgressChanged(PlatformProgressEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressBar.Value = e.ProgressPercent;
            ProgressText.Text = e.StepDescription;
            if (e.ProgressPercent > 0 && e.ProgressPercent < 100)
            {
                ProgressBar.Visibility = Visibility.Visible;
                ProgressText.Visibility = Visibility.Visible;
            }
            else
            {
                ProgressBar.Visibility = Visibility.Collapsed;
                ProgressText.Visibility = Visibility.Collapsed;
            }
        });
    }

    void ShowProgress()
    {
        ProgressBar.Value = 0;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressText.Visibility = Visibility.Visible;
    }

    void HideProgress()
    {
        ProgressBar.Visibility = Visibility.Collapsed;
        ProgressText.Visibility = Visibility.Collapsed;
    }

    void RenderStatus(TurboStatus? status)
    {
        status ??= TurboStatus.Stopped;
        StatusTitleText.Text = status.Title;
        StatusDetailText.Text = status.Detail;
        if (status.IsRunning)
        {
            StatusIndicator.Fill = _runningBrush;
            StatusIndicator.Effect = _runningEffect;
            StartPulseAnimation();
        }
        else
        {
            StatusIndicator.Fill = _stoppedBrush;
            StatusIndicator.Effect = _stoppedEffect;
            StopPulseAnimation();
        }
        StartButton.IsEnabled = !status.IsRunning;
        StopButton.IsEnabled = status.IsRunning;
    }

    void StartPulseAnimation()
    {
        if (_pulseStoryboard is not null) return;
        _pulseStoryboard = (Storyboard)FindResource("StatusPulseAnimation");
        Storyboard.SetTarget(_pulseStoryboard, StatusIndicator);
        _pulseStoryboard.Begin();
    }

    void StopPulseAnimation()
    {
        if (_pulseStoryboard is null) return;
        _pulseStoryboard.Stop();
        _pulseStoryboard = null;
    }
}
