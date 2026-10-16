# 架构
|模块|职责|
|---|---|
|App|控制端 WPF；组合根构造适配器，无网络业务|
|Controller|自动发现、多网卡内部合并、连接与呈现状态编排|
|Host|单 EXE 引导/安装器、ServiceBase 服务宿主|
|Service|服务/用户桌面代理边界契约，后续接入代理生命周期|
|Contracts|平台中立能力接口与数据模型|
|Protocol|独立控制帧 3.0 与发现协议 2.0，不含操作系统 API|
|Transport|有界 UDP/TCP、LAN 策略与设备目录|
|Windows|网卡枚举、IP Helper 路由、用户令牌/代理启动|
|Capture|交互用户会话屏幕采集|
|Input|交互用户会话键鼠注入与释放|
|FileService|用户根目录约束、受限列表、上传事务与完整性提交|
依赖：App → Controller + Windows（仅组合根）；Controller → Contracts/Transport；Host → Windows/Contracts；Windows → Transport/Contracts；Transport → Protocol/Contracts；Capture/Input/FileService/Service → Contracts。
自动扫描在 Controller 启动，后台接口变化无需用户配置；Transport 的 socket 可注入替身，错误/取消时所有任务与 socket 都被观察和释放。
Host 默认入口安装至 Program Files/PcRemote/PcRemote.Host.exe，启动 PcRemoteHost；SCM 使用 --service，--uninstall 移除服务及自身规则；--diagnose 不修改系统。
ServiceBase 在 LocalSystem / Session 0 中运行。当前处理受限 UDP 发现与 TCP/TLS 桌面代理桥；后续 Capture/Input 必须在用户代理执行，不直接放进服务。
v0.0.3 已实现 Host --agent 用户代理，随机 pipe/目标用户 ACL/双向 PID 验证；Capture 经 IScreenCapture，Input 经 IInputSink。真实桌面矩阵待验收，详见 ADR 0008。
无逐次授权/配对口令的行为由 ADR 0007 明确覆盖旧设计；不把发现报文当身份认证。

Host 的 DesktopSessionServer 只在通过 LAN 边界后启动代理，转发白名单消息；Controller 的 RemoteDesktopClient 负责 TLS、心跳和有界输入队列。WPF RemoteWindow 负责坐标/键事件呈现、JPEG 头检查与最新帧渲染。Capture/Input 为 Windows 桌面适配器，拥有各自原生调用，不依赖 WPF。

v0.0.4：Protocol → Contracts（共用文件限制/路径规则）；FileService → Contracts；Host → FileService。文件处理在用户代理的 AgentFileTransfers，服务仅转发 FileMessages 白名单。Controller 的 RemoteFileClient 负责单操作邮箱、进度与本地原子下载，FileBrowserViewModel 编排界面，FileWindow 仅做文件对话框和视图事件。网络、哈希和磁盘任务离开 UI 线程，视图模型状态回到 UI 上下文。
