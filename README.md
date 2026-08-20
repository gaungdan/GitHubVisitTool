# GitHub Turbo

<div align="center">

![GitHub Release](https://img.shields.io/github/v/release/gaungdan/GitHubTool?style=flat-square)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-blue?style=flat-square)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square)

加速 GitHub / GitLab 网络访问的 Windows 桌面工具

</div>

---

## ✨ 功能

- 🚀 **智能 DNS 解析** — 系统 DNS + Cloudflare/Google/Tencent DoH 多源兜底
- 🎯 **最优 IP 筛选** — 两阶段探针（TLS 快速筛选 → TCP+HTTP 精确测速），取中位数延迟选出最稳 IP
- 🔒 **冲突检测** — 自动识别 Steam++ 等第三方加速器写入的冲突 hosts 条目
- 🔄 **断网保持** — 扫描失败时自动保留旧 hosts 记录，避免断网
- 📊 **实时进度** — 解析/写入/检测各阶段进度可视化
- 🟢 **系统托盘** — 关闭窗口最小到托盘，不占用任务栏
- 🧹 **安全还原** — 退出时可选自动还原 hosts 文件

## 📥 下载

前往 [Releases](../../releases) 页面下载最新版 `GitHubTurbo.exe`，右键 **以管理员身份运行** 即可。

## 🖥️ 系统要求

- Windows 10 / 11 (x64)
- 无需安装额外运行时（已内嵌 .NET 8 Runtime）

## 🚀 使用

1. 下载 `GitHubTurbo.exe`
2. 右键 → **以管理员身份运行**（修改 hosts 文件需要管理员权限）
3. 点击 **启动加速**，自动完成 DNS 解析 → hosts 写入 → 网络检测
4. 点击 **网络检测** 可单独验证各域名连通性
5. 勾选 **退出时还原 hosts** 可在关闭程序时自动清理

## 🏗️ 从源码构建

```powershell
# 构建
$env:PATH = "D:\develop\python\GitHubVisitTool\.tools\dotnet10;" + $env:PATH
dotnet build -c Release

# 运行
dotnet run -c Release

# 发布单文件
dotnet publish -c Release --no-build

# 输出位置
# bin\Release\net8.0-windows\win-x64\GitHubTurbo.exe
```

## 📁 项目结构

```
GitHubTurbo/
├── App.xaml / App.xaml.cs          # 应用入口、DI 设置、系统托盘
├── AppController.cs                 # 核心业务逻辑：启动/停止/诊断
├── AppLogger.cs                     # 文件日志（按 UTC 日期分片）
├── AppPaths.cs                      # 文件路径（LocalAppData\GitHubTurbo）
├── AppSettings.cs                   # JSON 序列化的用户设置
├── HostsFileService.cs              # 原子化 hosts 文件读写
├── MainWindow.xaml                  # WPF 界面
├── MainWindow.xaml.cs               # 界面代码后置
├── NetworkDiagnostics.cs            # TCP 443 连通性检测
├── PlatformProvider.cs              # 平台注册与接口定义
├── GitHubPlatformProvider.cs        # GitHub 平台域名定义（36 个域名）
├── GitLabPlatformProvider.cs        # GitLab 平台域名定义
├── IpAddressHelpers.cs              # IP 地址工具方法
├── IpSelectionHelpers.cs            # IP 优选算法辅助
└── ViewModels.cs                    # MVVM 数据绑定模型
```

## 🔧 添加新平台

1. 创建 `XXXPlatformProvider.cs` 实现 `IPlatformProvider`
2. 在 `PlatformProviders.EnsureInitialized()` 中注册
3. 添加域名条目 `new PlatformDomain("标签", "domain.com")`

## 📝 配置说明

- **日志位置**: `%LOCALAPPDATA%\GitHubTurbo\logs\turbo-YYYY-MM-DD.log`
- **设置文件**: `%LOCALAPPDATA%\GitHubTurbo\settings.json`
- **Hosts 备份**: `%LOCALAPPDATA%\GitHubTurbo\backups\`

## 🤝 CI/CD

推送 `v*` 版本标签即可触发 GitHub Actions 自动构建并发布 Release：

```powershell
git tag v1.0.0
git push origin v1.0.0
```

## 📄 许可证

[MIT](LICENSE)
