# 架构
|模块|职责|
|---|---|
|App|控制端 WPF；组合根构造适配器，无网络业务|
|Controller|自动发现、多网卡内部合并、连接与呈现状态编排|
|Host|单 EXE 引导/安装器、ServiceBase 服务宿主|
|Service|服务/用户桌面代理边界契约，后续接入代理生命周期|
|Contracts|平台中立能力接口与数据模型|
|Protocol|独立控制帧 1.0 与发现协议 2.0，不含操作系统 API|
|Transport|有界 UDP/TCP、LAN 策略与设备目录|
|Windows|网卡枚举、IP Helper 直接路由验证|
|Capture|交互用户会话屏幕采集|
|Input|交互用户会话键鼠注入与释放|
|FileService|受限共享根内文件操作|
依赖：App → Controller + Windows（仅组合根）；Controller → Contracts/Transport；Host → Windows/Contracts；Windows → Transport/Contracts；Transport → Protocol/Contracts；Capture/Input/FileService/Service → Contracts。
自动扫描在 Controller 启动，后台接口变化无需用户配置；Transport 的 socket 可注入替身，错误/取消时所有任务与 socket 都被观察和释放。
Host 默认入口安装至 Program Files/PcRemote/PcRemote.Host.exe，启动 PcRemoteHost；SCM 使用 --service，--uninstall 移除服务及自身规则；--diagnose 不修改系统。
ServiceBase 在 LocalSystem / Session 0 中运行。当前只处理受限 UDP 发现；后续 Capture/Input 必须在用户代理执行，不直接放进服务。
当前没有桌面代理实现，不宣称能控制远程电脑。代理 IPC 需限定 System/目标用户 ACL、绑定会话身份、限制消息与大小；后续 ADR 细化。
无逐次授权/配对口令的行为由 ADR 0007 明确覆盖旧设计；不把发现报文当身份认证。
