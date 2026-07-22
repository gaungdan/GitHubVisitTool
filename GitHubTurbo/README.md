# GitHub Turbo

GitHub Turbo 是一个 Windows 便携式桌面工具原型，专注于 GitHub 网页访问优化。

当前版本仅覆盖浏览器中的 GitHub 网页访问；Git HTTPS、SSH、Release 下载、自动更新和流量拦截均不在当前实现范围内。

## 当前能力

- 在 `127.0.0.1` 启动 HTTP CONNECT 本地代理。
- 为当前 Windows 用户设置 PAC 地址，将 GitHub 网页域名定向到本地代理。
- 优先通过 Cloudflare 或 Google DoH 解析目标域名，失败时回退系统 DNS。
- PAC 规则之外的所有域名均保持 `DIRECT`，不会通过本机代理。
- 检测到已有手动系统代理或 PAC 脚本时拒绝接管，避免覆盖用户现有网络配置。
- 仅备份和恢复本程序实际修改的代理项；正常停止或下次启动时均会尝试恢复。
- 不安装驱动、不修改 Hosts、不修改系统 DNS、不解密 TLS，也不安装根证书。

本地代理仅接受 PAC 脚本中的 GitHub 域名规则，不是通用代理。它可以改善受 DNS 解析影响的 GitHub 网页连通性；如果当前网络本身不存在可用且合规的 GitHub 访问路径，仍需用户配置可信上游代理，或使用后续规划的合规中转服务。

## 上游 HTTP 代理

当 GitHub 的 TCP `443` 端口被当前网络阻断时，可在主界面“上游 HTTP 代理”中填写已授权代理的地址，例如 `http://127.0.0.1:7890`，点击“保存”，再停止并重新启动加速。

- 留空并保存：使用 DoH 优选后的直连模式。
- 仅 GitHub 网页域名会经上游代理转发；其他网站不会使用该代理。
- 当前仅支持无需认证的 HTTP 上游代理，不支持 HTTPS 上游、SOCKS5 和账号密码。

## 构建

本工作区将开发 SDK 放在 `.tools/dotnet`。进入 `GitHubTurbo` 目录后执行：

```powershell
& '..\.tools\dotnet\dotnet.exe' build '.\GitHubTurbo.csproj' --configuration Release
& '..\.tools\dotnet\dotnet.exe' publish '.\GitHubTurbo.csproj' --configuration Release --output '.\single-file'
```

发布产物为 `single-file\GitHubTurbo.exe`。它是自包含的 Windows x64 单文件程序，终端用户无需预先安装 .NET Runtime。

## 许可证说明

本项目未包含、链接或复制 SteamTools、WinDivert 的任何代码。SteamTools 仅用于架构调研；其 GPL-3.0 代码未被纳入本项目。
