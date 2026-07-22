# AGENTS.md — GitHub Turbo

## Project Overview

GitHub Turbo is a Windows desktop application that accelerates GitHub and GitLab network access through intelligent DNS resolution and hosts file modification. It provides a WPF-based GUI for managing platform acceleration, running network diagnostics, and viewing logs.

## Tech Stack

- **Framework**: .NET 8.0
- **UI**: WPF (Windows Presentation Foundation)
- **Platform**: Windows-only (win-x64)
- **Architecture**: Single-file published, self-contained executable
- **Language**: C# 12 with nullable reference types and implicit usings

## Build & Run

```powershell
# dotnet SDK path (portable, bundled with this repo)
$env:PATH = "D:\develop\python\GitHubVisitTool\.tools\dotnet10;" + $env:PATH

# Build (from GitHubTurbo directory)
dotnet build -c Release

# Run
dotnet run -c Release

# Publish single-file executable
dotnet publish -c Release --no-build

# Output location
# bin\Release\net8.0-windows\win-x64\GitHubTurbo.exe
```

> **Note**: The application requires Administrator privileges to modify the system hosts file (`C:\Windows\System32\drivers\etc\hosts`). Always run with `-Verb RunAs` or from an elevated prompt.

## Project Structure

```
GitHubTurbo/
├── App.xaml / App.xaml.cs          # Application entry point, DI setup
├── AppController.cs                 # Core business logic: start/stop/diagnostics
├── AppLogger.cs                     # File-based logging (UTC date-stamped)
├── AppPaths.cs                      # File system paths (LocalAppData\GitHubTurbo)
├── AppSettings.cs                   # JSON-serialized user settings
├── App.manifest                     # UAC execution level requirement
├── AssemblyInfo.cs                  # Assembly metadata
├── ColorToBrushConverter.cs         # IValueConverter: Color → SolidColorBrush
├── GitHubHosts.cs                   # Domain list helper
├── GitHubPlatformProvider.cs        # GitHub domain definitions (24 domains)
├── GitLabPlatformProvider.cs        # GitLab domain definitions (5 domains)
├── GitHubTurbo.csproj               # Project file (SDK-style)
├── HostsFileService.cs              # Read/write/flush Windows hosts file
├── MainWindow.xaml                  # UI layout (WPF)
├── MainWindow.xaml.cs               # UI code-behind
├── NetworkDiagnostics.cs            # TCP 443 connectivity checker
├── PlatformProvider.cs             # Platform registry & interface
└── ViewModels.cs                    # ViewModel classes (INotifyPropertyChanged)
```

## Architecture

### Layers

1. **UI Layer** (`MainWindow.xaml`, `MainWindow.xaml.cs`, `ViewModels.cs`)
   - WPF MVVM pattern with data binding
   - `MainWindow` exposes `ObservableCollection<T>` properties bound to `ItemsControl`
   - ViewModels implement `INotifyPropertyChanged` for reactive updates

2. **Controller Layer** (`AppController.cs`)
   - Centralized async operation lock (`SemaphoreSlim`) prevents concurrent start/stop
   - Emits `PlatformStatusChanged` events for status bar updates
   - Orchestrates DNS resolution → hosts file write → diagnostics pipeline

3. **Service Layer**
   - `HostsFileService` — atomic hosts file read/write with marker-based section management
   - `NetworkDiagnostics` — raw TCP socket checks on port 443 with HTTP HEAD probe
   - `AppLogger` — async file logging with semaphore-guarded writes
   - `AppSettings` — JSON serialization with atomic write (tmp + move)

4. **Provider Layer** (`PlatformProvider.cs`, `GitHubPlatformProvider.cs`, `GitLabPlatformProvider.cs`)
   - `IPlatformProvider` interface defines `Descriptor` and `Domains`
   - `PlatformProviders` static registry with lazy initialization
   - Each platform has a unique `Id`, `Title`, `Abbreviation`, and `BrandColor`

### Key Patterns

- **Marker-based hosts file management**: Entries are wrapped in `# {platformId} Start` / `# {platformId} End` comments for idempotent add/remove
- **DNS fallback chain**: System DNS → Cloudflare DoH → Google DoH → Tencent DoH
- **Single-file deployment**: Published as self-contained single executable with ReadyToRun

## Coding Conventions

- File-scoped namespace declarations (`namespace GitHubTurbo;`)
- `sealed` classes by default
- `record` types for immutable data transfer objects
- `async/await` with `CancellationToken` propagation
- Exception swallowing in fire-and-forget logging paths (never throw from logger)
- XAML styles use `StaticResource` with centralized `Window.Resources` palette
- No code comments unless explaining non-obvious logic

## Color Palette (Dark Theme)

| Resource | Hex | Usage |
|----------|-----|-------|
| `Bg0` | `#0F172A` | Page background |
| `Bg1` | `#1E293B` | Card/panel background |
| `Bg2` | `#334155` | Secondary background, hover states |
| `Line` | `#334155` | Borders, separators |
| `Text0` | `#F1F5F9` | Primary text |
| `Text1` | `#CBD5E1` | Secondary text |
| `Text2` | `#64748B` | Muted/placeholder text |
| `Accent` | `#3B82F6` | Primary action, selection |
| `AccentDark` | `#2563EB` | Pressed state |
| `AccentLight` | `#1E3A5F` | Badge backgrounds |
| `Good` | `#10B981` | Success indicator |
| `Bad` | `#EF4444` | Error indicator |

## Data Flow

```
User clicks "启动加速"
  → AppController.StartPlatformAsync()
    → HostsFileService.HasEntries() check (idempotent)
    → ResolvePlatformIpsAsync() (parallel DNS resolution)
      → ResolveIpAsync() per domain
        → Dns.GetHostAddressesAsync() (system DNS)
        → ResolveViaDohAsync() (DoH fallback × 3 providers)
    → HostsFileService.ApplyHosts() (atomic write + ipconfig /flushdns)
    → RunDiagnosticsCoreAsync() (TCP 443 check on primary domain)
    → UpdatePlatform() → PlatformStatusChanged event
      → MainWindow.RenderStatus() (UI update)
```

## Adding a New Platform

1. Create a new `XXXPlatformProvider.cs` implementing `IPlatformProvider`
2. Register in `PlatformProviders.EnsureInitialized()`
3. Add domain entries as `PlatformDomain("Label", "domain.com")` array

## Important Notes

- The `app.manifest` file controls UAC behavior; set `requireAdministrator` for hosts file access
- Logs are written to `%LOCALAPPDATA%\GitHubTurbo\logs\turbo-YYYY-MM-DD.log`
- Settings are stored at `%LOCALAPPDATA%\GitHubTurbo\settings.json`
- The `operationLock` in `AppController` serializes all start/stop/diagnostic operations
