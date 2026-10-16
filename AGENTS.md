# pc-remote 开发规则
产品是 Windows 11 局域网远控；不加入公网、账号、云端或服务器握手。
先读 docs/SCOPE.md、docs/ARCHITECTURE.md 和相关 ADR；当前接入要求以 ADR 0007 为准。
交互目标：被控端单 EXE 自动安装/启动服务；控制端自动发现后一键控制，无配对、无网卡选择、无逐次授权。
Windows 安装服务正常使用管理员权限；不实现 UAC 绕过。服务与交互用户桌面代理分离，不在 Session 0 采集/注入。
UI 只调用 Controller；组合根可构造具体适配器；Windows API 放 Windows/Host 安装适配层。
修改协议先读 PROTOCOL.md，协议独立版本管理，破坏变更升 major。
新依赖、进程/权限/传输方案变化记录 ADR；避免无关重构。
异步 IO 可取消；禁止吞意外异常、记录键盘/屏幕内容或私密文件数据。
完成需实现、相关测试、错误状态、文档、格式检查与 review；不得伪造验收。
使用 Issue/分支/PR；提交 feat/fix/docs/chore/test 前缀。项目技能按任务读取。
