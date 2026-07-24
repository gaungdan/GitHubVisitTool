# GitHub Turbo

GitHub Turbo 是一个 Windows 桌面工具，用于通过智能 DNS 解析和 hosts 文件写入改善 GitHub / GitLab 访问。

当前版本采用 hosts 加速模式：启动时解析平台域名的可用公网 IPv4 地址，然后把结果写入系统 hosts 文件；停止时移除本程序写入的 hosts 段。

## 当前能力

- 支持 GitHub 与 GitLab 平台域名加速。
- 使用系统 DNS、Cloudflare DoH、Google DoH、Tencent DoH 组成解析回退链。
- 只修改带有本程序标记的 hosts 段，重复启动会先刷新旧条目再写入新 IP。
- 启动时会识别旧版 `127.0.0.1` 本机代理条目，并提示刷新为真实 IP。
- 提供域名连通性检测，测试目标域名的 TCP 443 访问状态。
- 日志写入 `%LOCALAPPDATA%\GitHubTurbo\logs\`。

## 重要说明

- 本程序需要管理员权限，因为 Windows hosts 文件位于 `C:\Windows\System32\drivers\etc\hosts`。
- 本程序不安装证书、不监听本地代理端口、不修改系统代理/PAC、不解密 TLS。
- hosts 加速只能改善 DNS 解析异常或解析结果不佳的问题；如果当前网络直接阻断了 GitHub 的 TCP 443 连接，仍需要可用的网络出口或代理工具。

## 构建

本仓库包含便携式 dotnet SDK。进入仓库根目录后执行：

```powershell
$env:PATH = "D:\develop\python\GitHubVisitTool\.tools\dotnet10;" + $env:PATH
dotnet build GitHubTurbo\GitHubTurbo.csproj -c Release
dotnet publish GitHubTurbo\GitHubTurbo.csproj -c Release --no-build
```

发布产物默认位于：

```text
GitHubTurbo\bin\Release\net8.0-windows\win-x64\GitHubTurbo.exe
```

## 使用

1. 以管理员身份运行程序。
2. 选择 GitHub 或 GitLab。
3. 点击“启动加速”，程序会刷新 hosts 条目。
4. 点击“检测”查看各域名的连通性。
5. 点击“停止”移除对应平台的 hosts 条目。
