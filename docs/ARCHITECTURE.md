# 架构
依赖方向：App → Controller → Contracts；Host → Contracts；Transport → Protocol；Capture/Input/FileService → Contracts；Service 仅为进程边界占位。
Protocol 无 UI/Windows 依赖。组合根在 App，未来在此注入具体实现；不得让 ViewModel 创建 socket。
|模块|职责|禁止|
|---|---|---|
|App|WPF View 与呈现状态|直接联网或调用 Win32|
|Controller|发现、连接、显示会话编排|注入输入到本机|
|Host|授权与会话生命周期|绕过预设权限与密钥校验|
|Service|未来受限 IPC 契约；当前无系统服务|Session 0 采集/输入|
|Protocol|线格式、版本、长度边界|业务与设备操作|
|Transport|接口绑定、TLS、收发、背压|弹 UI 或解释权限|
|Capture|用户会话屏幕帧|联网|
|Input|被授权会话的输入、释放键|提升权限|
|FileService|共享根内文件操作|任意绝对路径访问|
所有异步边界可取消；会话关闭先撤销权限，再停止输入/采集/传输。帧通道有界，屏幕丢旧帧，文件不得静默丢块。
v0.0.1 除 Protocol 与 UI 外是接口骨架，不注册后台任务、不监听、不安装服务。
未来 Host 与 UI 同一交互用户会话运行；若增加 Windows Service，须新增 ADR 与命名管道 ACL、身份验证设计。
