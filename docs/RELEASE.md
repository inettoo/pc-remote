# 发布与回滚
产品 SemVer；当前 0.0.2；控制协议 1.0、发现协议 2.0 独立维护。
Issue → 规格/ADR → 分支 → 实现/测试 → PR/review → main；CI artifact 是预览构建，验收后才手动 tag/release。
两个自包含 win-x64 单 EXE：PcRemote.Host.exe 与 PcRemote.App.exe（Controller）。无需目标预装 .NET。
Host 默认入口会安装/启动服务；要求管理员权限时正常 UAC 提升，不能绕过系统权限。安装到 Program Files/PcRemote，注册 PcRemoteHost 自动启动、固定 EXE 的 UDP 45831 LocalSubnet 入站规则和 EventLog source。
--uninstall 停止/移除服务及其规则；保留文件和事件历史。--quiet 仅在已提升的 CI 进程使用；--diagnose 不修改系统。
发布检查：构建、协议/边界测试、格式、两个 single-file publish、Windows runner 服务安装/监听/卸载、双机发现、校验和、已知限制。
当前 unsigned；签名/安装来源策略在正式预览前另写 ADR。屏幕/输入/文件未实现时不得声称可远控。
回滚：管理员停止服务，运行上一版本 Host EXE 重新安装；控制端替换完整 EXE。协议不兼容双方同步回滚。保留用户文件，不递归删除安装目录。
