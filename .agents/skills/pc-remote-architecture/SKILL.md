---
name: pc-remote-architecture
description: 修改 pc-remote 模块边界、进程或依赖时使用。
---

先读 docs/ARCHITECTURE.md 与 docs/decisions。检查引用方向；App 只经 Controller 编排。新 Windows 能力放入对应适配器。重大决策添加带背景、替代方案、后果的 ADR；遵循 ADR 0007 的单 EXE 自动安装服务设计；服务不直接操作 Session 0 桌面，Capture/Input 交给用户会话代理。
