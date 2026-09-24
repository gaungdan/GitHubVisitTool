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

# Required for NuGet restore with the bundled .NET 10 SDK (without these,
# restore fails with "Value cannot be null. (Parameter 'path1')"):
$env:DOTNET_ROOT = "D:\develop\python\GitHubVisitTool\.tools\dotnet10"
$env:DOTNET_HOST_PATH = "D:\develop\python\GitHubVisitTool\.tools\dotnet10\dotnet.exe"
$env:DOTNET_CLI_HOME = "C:\Users\lixug"
$env:ProgramW6432 = "C:\Program Files"
$env:MSBuildExtensionsPath = "D:\develop\python\GitHubVisitTool\.tools\dotnet10\sdk\10.0.302"
$env:MSBuildSDKsPath = "D:\develop\python\GitHubVisitTool\.tools\dotnet10\sdk\10.0.302\Sdks"
$env:MSBUILD_EXE_PATH = "D:\develop\python\GitHubVisitTool\.tools\dotnet10\sdk\10.0.302\MSBuild.dll"

# Build (from GitHubTurbo directory)
dotnet build -c Release

# Run
dotnet run -c Release

# Publish single-file executable
dotnet publish -c Release --no-build

# Tests (needs roll-forward because only .NET 10 runtime is bundled)
$env:DOTNET_ROLL_FORWARD = "LatestMajor"
dotnet test GitHubTurbo.Tests\GitHubTurbo.Tests.csproj -c Release

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
├── CommunityIpService.cs            # Community hosts IP source + seed fallback
├── GitHubPlatformProvider.cs        # GitHub domain definitions
├── GitLabPlatformProvider.cs        # GitLab domain definitions
├── GitHubTurbo.csproj               # Project file (SDK-style)
├── HostsFileService.cs              # Read/write/flush Windows hosts file
├── IpAddressHelpers.cs              # Public/private IP classification
├── IpRefreshService.cs              # Periodic endpoint-cache refresh
├── IpScoringService.cs              # Multi-dimensional IP scoring
├── IpSelectionHelpers.cs            # Candidate interleaving / median
├── MainWindow.xaml                  # UI layout (WPF)
├── MainWindow.xaml.cs               # UI code-behind
├── NetworkDiagnostics.cs            # TCP/TLS/HTTP connectivity checker
├── PlatformDiagnostics.cs           # Per-platform critical domain list
├── PlatformProvider.cs              # Platform registry & interface
├── ViewModels.cs                    # ViewModel classes (INotifyPropertyChanged)
└── Proxy/
    ├── CertificateManager.cs        # Root CA + per-domain certs
    ├── EndpointCache.cs             # Domain → egress IP cache
    ├── MitmProxyServer.cs           # Byte-stream MITM proxy
    └── TlsSniMode.cs                # Outbound fake-SNI strategies
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
   - Orchestrates proxy start → loopback hosts write → endpoint warmup → diagnostics

3. **Proxy Layer** (`Proxy/`)
   - `MitmProxyServer` — byte-stream MITM on 127.0.0.1:443; terminates TLS with a local CA, re-encrypts outbound with fake SNI, pipes bytes (HTTP/2 & WebSocket safe)
   - `CertificateManager` — root CA + per-domain certs; installs CA into LocalMachine Root
   - `EndpointCache` — domain → egress IP table used at connect time (warmup uses full IP scoring)
   - `TlsSniHelper` — outbound SNI strategies: IpAddress (default) → None → RealDomain

4. **Service Layer**
   - `HostsFileService` — atomic hosts file read/write with marker-based section management
   - `NetworkDiagnostics` — TCP/TLS/HTTP probe through the local MITM path
   - `IpScoringService` — multi-dimensional IP scoring (ping + TCP + HTTP)
   - `AppLogger` — async file logging with semaphore-guarded writes
   - `AppSettings` — JSON serialization with atomic write (tmp + move)

5. **Provider Layer** (`PlatformProvider.cs`, `GitHubPlatformProvider.cs`, `GitLabPlatformProvider.cs`)
   - `IPlatformProvider` interface defines `Descriptor` and `Domains`
   - `PlatformProviders` static registry with lazy initialization
   - Each platform has a unique `Id`, `Title`, `Abbreviation`, and `BrandColor`

### Key Patterns

- **Marker-based hosts file management**: Entries are wrapped in `# {platformId} Start` / `# {platformId} End` comments for idempotent add/remove
- **MITM acceleration**: hosts points every managed domain at `127.0.0.1`; the proxy picks a scored egress IP and connects with a fake SNI so SNI-based filtering cannot match the real domain. HTTP Host/:authority still routes correctly because bytes are piped unmodified
- **DNS fallback chain**: Community IP list + system DNS + DNSPod/Ali DoH (system DNS loopback results are filtered out to avoid a hosts → proxy → hosts loop)
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
    → HostsFileService.FindConflictingEntries() check
    → MitmProxyServer.Start() (trust CA if needed, listen 127.0.0.1:443/80)
    → HostsFileService.TryApplyHosts(domains → 127.0.0.1)
    → EndpointCache.WarmupAsync() per domain (IP scoring)
    → RunDiagnosticsCoreAsync() (TLS+HTTP through the local MITM)
    → UpdatePlatform() → PlatformStatusChanged event

Browser request
  → hosts resolves github.com to 127.0.0.1
  → MITM terminates TLS (local CA cert for SNI)
  → EndpointCache lookup → outbound TLS to egress IP with fake SNI
  → bidirectional byte pipe (HTTP/2 frames preserved)
```

## Adding a New Platform

1. Create a new `XXXPlatformProvider.cs` implementing `IPlatformProvider`
2. Register in `PlatformProviders.EnsureInitialized()`
3. Add domain entries as `PlatformDomain("Label", "domain.com")` array

## Important Notes

- The `app.manifest` file controls UAC behavior; `requireAdministrator` is required for hosts file access, binding :443/:80, and installing the root CA
- Logs are written to `logs\turbo-YYYY-MM-DD.log` under the app base directory
- Settings are stored at `%LOCALAPPDATA%\GitHubTurbo\settings.json`
- Root CA material lives in `%LOCALAPPDATA%\GitHubTurbo\certs\` (`githubturbo-ca.pfx` / `.cer`)
- The `HostsFileLock` / `_proxyLock` in `AppController` serialize hosts writes and proxy lifecycle
- Never resolve upstream IPs via `Dns.GetHostAddressesAsync` without filtering loopback: with hosts pointing at 127.0.0.1 that creates a proxy → hosts → proxy loop
