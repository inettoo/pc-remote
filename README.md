# pc-remote
Windows 11 x64 局域网远控项目，当前 v0.0.2。

使用目标：
1. 被控电脑运行 **PcRemote.Host.exe**，自动安装并启动服务，之后随 Windows 启动。
2. 控制电脑打开 **PcRemote.App.exe**，自动发现设备，选中后控制。
无需选网卡、账号、配对口令或被控端逐次确认。首次安装系统服务仍需 Windows 管理员权限；这与远控授权是两回事。
服务启用即信任同一直接局域网控制端，不提供同网段用户间的身份隔离；不支持公网或外部服务器。

**当前已实现安装/启动/卸载和自动发现，屏幕、键鼠、文件仍在开发，按钮明确禁用。**
两个 EXE 均自包含，无需预装 .NET。Host 安装至 Program Files/PcRemote；卸载使用 PcRemote.Host.exe --uninstall。
不想安装时不要直接运行 Host；--diagnose 不修改系统。CI 在临时 Windows runner 验证安装生命周期。

## 开发
Windows 与 .NET 10 SDK：
```powershell
dotnet restore PcRemote.slnx
dotnet build PcRemote.slnx -c Release --no-restore
dotnet run --project tests/PcRemote.Tests -c Release --no-build
dotnet format PcRemote.slnx --verify-no-changes --no-restore
dotnet run --project src/PcRemote.App
dotnet publish src/PcRemote.Host -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```
运行 Host 开发构建不会安装；安装器只接受 single-file 发布构建。不要把 SDK 的 dotnet run 当作安装验证。
[范围](docs/SCOPE.md) · [路线](docs/ROADMAP.md) · [架构](docs/ARCHITECTURE.md) · [UI](docs/UI_GUIDE.md) · [协议](docs/PROTOCOL.md) · [发布](docs/RELEASE.md) · [最新交互决策](docs/decisions/0007.md)
