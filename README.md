# pc-remote
面向 Windows 11 的局域网远控项目。当前 **v0.0.1 / Foundation**：可启动的 WPF 界面、模块契约、协议帧编解码与工程规范。
**当前没有设备发现、屏幕传输、输入注入或文件传输，也不会打开监听端口。**

## 开发
Windows 11 x64，安装 .NET 10 SDK（含桌面构建工具）。
```powershell
dotnet restore PcRemote.slnx
dotnet build PcRemote.slnx -c Release --no-restore
dotnet run --project tests/PcRemote.Tests -c Release --no-build
dotnet format PcRemote.slnx --verify-no-changes --no-restore
dotnet run --project src/PcRemote.App
```
测试使用零第三方依赖的可执行测试程序，失败返回非零退出码。
[产品范围](docs/SCOPE.md) · [路线](docs/ROADMAP.md) · [架构](docs/ARCHITECTURE.md) · [UI](docs/UI_GUIDE.md) · [协议](docs/PROTOCOL.md) · [发布](docs/RELEASE.md)

接入策略：Host 一次启用并配置局域网访问密钥和权限后，无需逐次确认即可被控；见 [ADR 0005](docs/decisions/0005.md)。该能力在 v0.0.3 后实现。
