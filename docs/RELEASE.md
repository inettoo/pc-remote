# 发布与回滚
产品 SemVer；当前 0.0.4；控制协议 3.0、发现协议 2.0 独立维护。
Issue → 规格/ADR → 分支 → 实现/测试 → PR/review → main；CI artifact 是预览构建，验收后才手动 tag/release。
两个自包含 win-x64 单 EXE：PcRemote.Host.exe 与 PcRemote.App.exe（Controller）。无需目标预装 .NET。
Host 默认入口会安装/启动服务；要求管理员权限时正常 UAC 提升，不能绕过系统权限。安装到 Program Files/PcRemote，注册 PcRemoteHost 自动启动、固定 EXE 的 UDP 45831 与 TCP 45832 LocalSubnet 入站规则和 EventLog source。
--uninstall 停止/移除服务及其规则；保留文件和事件历史。--quiet 仅在已提升的 CI 进程使用；--diagnose 不修改系统。
发布检查：构建、协议/边界测试、格式、两个 single-file publish、Windows runner 服务安装/监听/卸载、双机发现、校验和、已知限制。
当前 unsigned；签名/安装来源策略在正式预览前另写 ADR。未完成的功能和双机/DPI/桌面权限矩阵必须明确列出；基线不等于已达到性能目标。
回滚：管理员停止服务，运行上一版本 Host EXE 重新安装；控制端替换完整 EXE。协议不兼容双方同步回滚。保留用户文件，不递归删除安装目录。

控制端与被控端必须使用相同控制协议的版本；3.0 不接受 2.0。文件测试中的跳过项必须单独记录，不能计算为通过。正式 v0.1.0 前仍需真实双机/桌面状态/性能验收，开发 EXE 不能代替正式签名发布。
