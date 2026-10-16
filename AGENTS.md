# pc-remote 开发规则
产品是 Windows 11 局域网远控；不加入公网、账号、云端或服务器握手。
先读 docs/SCOPE.md、docs/ARCHITECTURE.md 和相关 ADR。工作范围由 Issue/规格确定。
UI 只调用 Controller；Windows API 封装在适配模块。共享契约不得引用 UI 或操作系统实现。
修改协议先读 docs/PROTOCOL.md；协议版本独立于产品版本，破坏性变更升级 major。
新依赖、跨层调用、进程或传输方案变化必须记录 ADR；避免无关重构。
异步 IO 使用 CancellationToken；禁止吞异常、记录屏幕/键盘内容或文件私密数据。
实现、相关测试、异常状态、文档、格式检查和 review 完成才可标记完成。
提交使用 feat/fix/docs/chore/test 前缀；通过分支和 PR 集成。不得伪造测试、设备或功能状态。
项目技能位于 .agents/skills；按具体任务读取，不全量加载。
