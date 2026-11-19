# AGENTS.md — GitHub Turbo

## Project Overview

GitHub Turbo is a Windows-only WPF desktop application that accelerates access to GitHub, GitLab, and Google Antigravity. It writes loopback entries into the Windows hosts file and runs a local MITM proxy on `127.0.0.1:443`, which terminates TLS with a locally-trusted CA, reconnects to a scored egress IP using a fake SNI, and pipes bytes unmodified.

## Tech Stack

- **Framework**: .NET 8.0 (`net8.0-windows`), C# 12, nullable + implicit usings
- **UI**: WPF (MVVM) plus Windows Forms `NotifyIcon` for the tray
- **Tests**: xUnit (`GitHubTurbo.Tests`), currently 59 tests
- **Dependencies**: application has **no NuGet package references**; BCL only
- **Deployment**: self-contained, single-file, ReadyToRun, `win-x64`
- **Requires Administrator**: hosts writes, binding :443/:80, installing the root CA

## Repository Layout

```
.
├── GitHubTurbo/                     # WPF application (main project)
│   ├── App.xaml(.cs)                # Composition root: singleton mutex, tray, window lifecycle
│   ├── AppController.cs             # All start/stop/diagnostics orchestration
│   ├── MainWindow.xaml(.cs)         # Single-window UI + code-behind
│   ├── ViewModels.cs                # INotifyPropertyChanged view models
│   ├── PlatformProvider.cs          # IPlatformProvider + static registry
│   ├── GitHub|GitLab|AntigravityPlatformProvider.cs
│   ├── HostsFileService.cs          # Marker-based hosts read/write
│   ├── IpScoringService.cs          # ping + TCP + HTTP composite score
│   ├── IpSelectionHelpers.cs        # Source interleaving + median
│   ├── NetworkDiagnostics.cs        # TCP/TLS/HEAD probe
│   ├── PlatformDiagnostics.cs       # Per-platform critical domain subsets
│   ├── IpRefreshService.cs          # Periodic endpoint-cache refresh
│   ├── CommunityIpService.cs        # Community hosts list + embedded seed
│   ├── community-ip-seed.txt        # EmbeddedResource fallback IP list
│   ├── AppPaths.cs / AppSettings.cs / AppLogger.cs
│   ├── app.manifest                 # requireAdministrator
│   └── Proxy/
│       ├── MitmProxyServer.cs       # Raw TcpListener + SslStream byte pipe
│       ├── CertificateManager.cs    # Local root CA + per-SNI leaf certs
│       ├── EndpointCache.cs         # In-memory domain → ranked IP cache
│       └── TlsSniMode.cs            # Outbound SNI strategy enum/helper
├── GitHubTurbo.Tests/               # xUnit tests (unit + loopback MITM integration)
├── GitHubTurboTest/                 # Console smoke harness; mutates the real hosts file
├── Start-GitHubTurbo.ps1            # Self-elevating launcher: build + run with portable SDK
├── start-project.bat                # Wrapper for Start-GitHubTurbo.ps1
├── .github/workflows/release.yml    # CI: build on v* tags / release branch, publish + release
├── .tools/                          # Portable .NET SDKs and package cache (gitignored)
├── docs/superpowers/plans/          # Historical design docs (see note below)
└── upstream/SteamTools/             # Reference-only checkout (gitignored, not referenced by build)
```

There is **no solution file**. Always pass a project path (`GitHubTurbo\GitHubTurbo.csproj`).

> `docs/superpowers/plans/2026-07-22-github-turbo-proxy-rewrite.md` describes an earlier Kestrel-based design. The shipped implementation is the raw `TcpListener` + `SslStream` byte-stream proxy in `GitHubTurbo/Proxy/`. Do not reintroduce Kestrel/YARP based on that document.

## Build & Run

The repo bundles a portable .NET 10 SDK, but the project targets .NET 8. These environment variables are required for restore/build (otherwise restore fails with `Value cannot be null. (Parameter 'path1')`):

```powershell
$env:PATH = "D:\develop\python\GitHubVisitTool\.tools\dotnet10;" + $env:PATH
$env:DOTNET_ROOT = "D:\develop\python\GitHubVisitTool\.tools\dotnet10"
$env:DOTNET_HOST_PATH = "D:\develop\python\GitHubVisitTool\.tools\dotnet10\dotnet.exe"
$env:DOTNET_CLI_HOME = "C:\Users\lixug"
$env:ProgramW6432 = "C:\Program Files"
$env:MSBuildExtensionsPath = "D:\develop\python\GitHubVisitTool\.tools\dotnet10\sdk\10.0.302"
$env:MSBuildSDKsPath = "D:\develop\python\GitHubVisitTool\.tools\dotnet10\sdk\10.0.302\Sdks"
$env:MSBUILD_EXE_PATH = "D:\develop\python\GitHubVisitTool\.tools\dotnet10\sdk\10.0.302\MSBuild.dll"
```

```powershell
# Build (verified)
dotnet build GitHubTurbo\GitHubTurbo.csproj -c Release
# → GitHubTurbo\bin\Release\net8.0-windows\win-x64\GitHubTurbo.exe

# Run (terminal must already be elevated)
dotnet run --project GitHubTurbo\GitHubTurbo.csproj -c Release

# Publish self-contained single file
dotnet publish GitHubTurbo\GitHubTurbo.csproj -c Release --no-build
# → GitHubTurbo\bin\Release\net8.0-windows\win-x64\publish\GitHubTurbo.exe

# Tests (verified; roll-forward is required because only the .NET 10 runtime is bundled)
$env:DOTNET_ROLL_FORWARD = "LatestMajor"
dotnet test GitHubTurbo.Tests\GitHubTurbo.Tests.csproj -c Release
```

Launcher alternative: `Start-GitHubTurbo.ps1 [-Configuration Release] [-NoBuild]` self-elevates through `Start-Process -Verb RunAs`, locates `.tools\dotnet10` (falling back to `.tools\dotnet`), builds with `-r win-x64`, then runs with `--no-build`. `start-project.bat` simply forwards to it. Running the app from an unelevated terminal fails at hosts/proxy/CA steps.

`GitHubTurbo\NuGet.Config` clears all package sources except nuget.org. Tests restore xunit/coverlet packages; the app itself has no packages.

## Architecture

### Startup (`App.xaml.cs`)

- Named mutex `Local\GitHubTurbo.Singleton` enforces one instance.
- `AppController` is created directly (no DI container); `InitializeAsync()` runs before the window opens.
- WinForms `NotifyIcon` provides show/start-all/stop-all/exit; the tray menu is the only way to truly exit.
- Closing `MainWindow` cancels close and hides it; `App.Exit()` sets `_isShuttingDown` and shuts down.
- `OnExit` removes all managed hosts blocks before the proxy dies, so a normal exit never leaves domains pointed at a closed loopback port. This cleanup is skipped for a second instance (which never owns the controller).
- `InitializeAsync` scans each platform: external hosts conflicts → conflict status; legacy non-loopback entries → "需刷新"; existing loopback entries → start the proxy; then auto-starts platforms listed in `settings.EnabledPlatforms` when `AutoRefreshOnLaunch` is set.

### Controller (`AppController.cs`)

- Serializes hosts writes with `HostsFileLock` and proxy lifecycle with `_proxyLock`.
- Bounded concurrency: `IpProbeGate` (16) and `DnsResolutionGate` (16).
- Community IP list is fetched **at most once per process** via a shared cached `Task`; failures reset the cache.
- Emits `PlatformStatusChanged`; `TurboStatus` is an immutable record with Chinese UI strings.
- Stop removes the platform's hosts block, updates settings, and stops the proxy only when no platform is running.

### Proxy (`Proxy/`)

- `MitmProxyServer` binds `127.0.0.1:443` (backlog 256) and, when available, `127.0.0.1:80` (backlog 64) with raw `TcpListener`. Port 80 is optional: if it is taken, the proxy logs a warning and continues HTTPS-only. Start/stop is restartable (fresh `CancellationTokenSource` per run) and rolls back state if binding fails.
- Inbound connections are capped at 512 concurrently; TCP keepalive is enabled on both inbound and outbound sockets.
- HTTPS: server TLS with ALPN `h2, http/1.1`; `ServerCertificateSelectionCallback` selects a leaf cert by SNI; 8 s handshake timeout. It never parses HTTP — bytes are piped both ways (64 KB buffer), so HTTP/2, WebSocket, and large downloads work.
- HTTP :80 only reads the request line far enough to find `Host` and replies `301` to `https://...`.
- Outbound: fetch ranked IP list from `EndpointCache`, try SNI modes in `TlsSniHelper.PreferredOrder` (`None` → `RealDomain`), starting with the previously successful mode; 5 candidate IPs are dialed concurrently per mode with a 3 s timeout. `IpAddress` is retained for compatibility but excluded from the retry order because .NET omits IP literals from SNI anyway.
- Outbound certificate validation is intentionally bypassed (fake SNI produces a name mismatch). Do not "fix" this without redesigning the strategy.
- On upstream failure, write the 502 **inside** the TLS stream; plaintext would desync the client.
- `CertificateManager` creates a 10-year root CA and 2-year leaf certs (RSA 2048, SAN = domain + loopback), stores them in `%LOCALAPPDATA%\GitHubTurbo\certs`, and installs the root into `LocalMachine\Root`. The leaf-cert cache is capped at 512 entries; overflow SNI names are served uncached.
- `EndpointCache` is in-memory only: top 5 ranked IPs, 30 min TTL, 2 s quick TCP probe on cold path, remembers preferred SNI mode per domain.

### IP resolution and scoring

- Candidate sources, in priority order: community hosts list (jsdelivr → fastly → raw.githubusercontent, 4 s timeout, merged with the embedded seed), previous IP, system DNS (2 s), DNSPod DoH A/AAAA, AliDNS DoH A/AAAA (1 s each).
- `IpSelectionHelpers.Interleave` mixes sources round-robin up to 8 candidates.
- Only public IPs are accepted: loopback, private, link-local, CGNAT, documentation, multicast, and reserved ranges are filtered. **Never** use system DNS results without this filter — with hosts pointing at `127.0.0.1` it creates a proxy → hosts → proxy loop.
- `IpScoringService` probes ping (1 s), TCP 443 (2 s), and HTTP HEAD over TLS (2.5 s) concurrently; the HTTP probe uses the no-SNI outbound path. Composite = ping 0.35 + TCP 0.35 + HTTP 0.30. A previous IP is kept when its score is at least 80% of the best.
- `StartPlatformAsync` synchronously warms the first 3 domains (primary/API/Gist) and finishes the remaining domains in a background task, so startup is not blocked by the full list.
- `IpRefreshService` first fires after 5 min, then every 30 min; refreshes only platforms that currently have hosts entries.

### Hosts file management

- `C:\Windows\System32\drivers\etc\hosts`, UTF-8 without BOM, max 50 MB.
- Blocks are wrapped in `# {platformId} Start` / `# {platformId} End`; legacy single block uses `# GitHubTurbo Start` / `# GitHubTurbo End`.
- Starting a platform removes its domains from the legacy block (preserving unrelated legacy entries inside the markers), so stale IPs cannot win resolution after migration.
- `FindConflictingEntries` ignores lines inside any managed block and comment/blank lines, so Steam++ (or similar) entries outside our blocks surface as conflicts.
- Writes are atomic: temp file in the same directory + `File.Replace`, followed by `ipconfig /flushdns` (5 s timeout). Read-only attribute is temporarily cleared and restored.
- All mutations go through `HostsFileService` while holding `HostsFileLock`.

### Diagnostics

- `NetworkDiagnostics` connects to the domain (through hosts → local MITM) and performs TLS plus a `HEAD /` request; 5 s budget. Per-IP and TLS-only variants exist for tests/scoring.
- `PlatformDiagnostics.GetDiagnosticDomains` narrows the UI test list to critical domains per platform; unknown platforms fall back to their full domain list.
- `RunDomainDiagnosticsAsync` re-warms failed domains (up to 8) and rechecks them, reporting replaced egress IPs.

### Providers

- `IPlatformProvider` exposes `Descriptor` (`Id`, `Title`, `Subtitle`, `Abbreviation`, `BrandColor`) and `Domains` (`PlatformDomain` records).
- `PlatformProviders.EnsureInitialized()` registers GitHub, GitLab, Antigravity; lookup is case-insensitive.
- `HasLegacyEntries` lets a provider opt into legacy-migration messaging.

## UI Conventions

- Dark theme palette defined as `Color` resources in `MainWindow.xaml`; reference them via `StaticResource`, never hardcode new colors:

| Resource | Hex | Usage |
|----------|-----|-------|
| `Bg0` | `#0F172A` | Page background |
| `Bg1` | `#1E293B` | Card/panel background |
| `Bg2` | `#334155` | Secondary background, hover |
| `Line` | `#334155` | Borders, separators |
| `Text0` | `#F1F5F9` | Primary text |
| `Text1` | `#CBD5E1` | Secondary text |
| `Text2` | `#64748B` | Muted/placeholder text |
| `Accent` | `#3B82F6` | Primary action, selection |
| `AccentDark` | `#2563EB` | Pressed state |
| `AccentLight` | `#1E3A5F` | Badge backgrounds |
| `Good` | `#10B981` | Success |
| `Bad` | `#EF4444` | Error |

- User-facing copy is concise Chinese; log messages are English.
- Keep layouts content-sized (Auto/Star where appropriate); avoid fixed widths that leave dead space or clip domain/label text.
- New UI must visually match the existing cards/rows; do not introduce an independent style system.

## Coding Conventions

- File-scoped namespaces (`namespace GitHubTurbo;`), `sealed` by default, records for DTOs.
- `async/await` with `CancellationToken` propagation; fire-and-forget logging must never throw.
- Logging goes through `AppLogger` (`INFO`/`ERROR`), never `Console` in the WPF app.
- Avoid comments unless explaining non-obvious logic; no license headers.
- Keep the proxy on raw `NetworkStream`/`SslStream`; do not add HTTP parsing or third-party proxy libraries.
- Tests must run without admin rights or internet. MITM integration tests use a loopback fake upstream, ephemeral ports, a temp cert directory, and `Start(requireTrustedRoot: false)`.

## Adding a Platform

1. Add `XXXPlatformProvider.cs` implementing `IPlatformProvider` (static `Instance`, descriptor, domain array).
2. Register it in `PlatformProviders.EnsureInitialized()`.
3. Optionally add its critical domains to `PlatformDiagnostics` for the network-test list.
4. If a legacy marker exists, override `HasLegacyEntries`.

No other registration is required: UI modules, hosts blocks, diagnostics, and settings all derive from the provider list.

## Runtime Data & Paths

| Item | Location |
|------|----------|
| Logs | `<app dir>\logs\turbo-yyyy-MM-dd.log` (UTC date, appended, UTF-8) |
| Settings | `%LOCALAPPDATA%\GitHubTurbo\settings.json` (`AutoRefreshOnLaunch`, `EnabledPlatforms`) |
| Root CA / leaf material | `%LOCALAPPDATA%\GitHubTurbo\certs\` (`githubturbo-ca.pfx`, `.cer`) |
| Endpoint cache | In-memory only (rebuilt on start; 30 min TTL) |
| Hosts file | `C:\Windows\System32\drivers\etc\hosts` |

Settings and certificates are written atomically where applicable; settings writes use `tmp` + `File.Move(overwrite: true)`.

## CI

`.github/workflows/release.yml` runs on `v*` tags and pushes to the `release` branch:

- `windows-latest`, .NET 8 SDK, restore from nuget.org.
- Publish self-contained single file with `IncludeNativeLibrariesForSelfExtract=true` and `EnableCompressionInSingleFile=true` (local csproj keeps compression disabled).
- Uploads `publish/GitHubTurbo.exe`, then creates a GitHub Release. Tag pushes produce a normal release; branch pushes produce a `v0.0.0-<sha>` pre-release.

## Pitfalls / Invariants

- The proxy must be running whenever hosts loopback entries exist; `InitializeAsync` restarts it on launch for that reason.
- Never call `Dns.GetHostAddressesAsync` for egress selection without filtering loopback/private/non-public results.
- Do not bypass `HostsFileLock`; concurrent hosts writes can corrupt the file.
- Port 443/80 binding and root CA installation fail without elevation.
- The outbound TLS cert callback deliberately accepts all certificates; changing it breaks fake-SNI acceleration.
- `GitHubTurboTest` is a manual console harness: it starts acceleration and rewrites the real hosts file. Do not run it in automated verification; use `GitHubTurbo.Tests` instead.
- `upstream/SteamTools` and `.tools/` are reference/tooling only — never add them to the build.
