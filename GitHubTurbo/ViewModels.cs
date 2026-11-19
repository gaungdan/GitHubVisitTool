using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Color = System.Windows.Media.Color;

namespace GitHubTurbo;

public sealed class DomainEntry : INotifyPropertyChanged
{
    string? _currentIp;

    public DomainEntry(string prefix, string domain)
    {
        Prefix = prefix;
        Domain = domain;
    }

    public string Prefix { get; }
    public string Domain { get; }

    public string? CurrentIp
    {
        get => _currentIp;
        set
        {
            if (_currentIp == value) return;
            _currentIp = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class PlatformModuleViewModel : INotifyPropertyChanged
{
    bool _isActive;
    bool _isExpanded;

    public PlatformModuleViewModel(string title, string subtitle, string abbreviation, Color brandColor, IEnumerable<DomainEntry> domains, string platformId)
    {
        Title = title;
        Subtitle = subtitle;
        Abbreviation = abbreviation;
        BrandColor = brandColor;
        Domains = new ObservableCollection<DomainEntry>(domains);
        PlatformId = platformId;
        _isExpanded = false;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string Abbreviation { get; }
    public Color BrandColor { get; }
    public ObservableCollection<DomainEntry> Domains { get; }
    public string PlatformId { get; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class DomainDiagnosticViewModel : INotifyPropertyChanged
{
    string status = "未测试";
    string latency = "--";
    string detail = "等待测试";
    bool isSuccess;

    public DomainDiagnosticViewModel(string host, string description, string platformId, string platformTitle)
    {
        Host = host;
        Description = description;
        PlatformId = platformId;
        PlatformTitle = platformTitle;
    }

    public string Host { get; }
    public string Description { get; }
    public string PlatformId { get; }
    public string PlatformTitle { get; }

    public string Status
    {
        get => status;
        set
        {
            if (status == value)
                return;
            status = value;
            OnPropertyChanged();
        }
    }

    public string Latency
    {
        get => latency;
        set
        {
            if (latency == value)
                return;
            latency = value;
            OnPropertyChanged();
        }
    }

    public string Detail
    {
        get => detail;
        set
        {
            if (detail == value)
                return;
            detail = value;
            OnPropertyChanged();
        }
    }

    public bool IsSuccess
    {
        get => isSuccess;
        set
        {
            if (isSuccess == value)
                return;
            isSuccess = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
