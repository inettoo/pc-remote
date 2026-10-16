---
name: pc-remote-architecture
description: 修改 pc-remote 模块边界、进程或依赖时使用。
---

先读 docs/ARCHITECTURE.md 与 docs/decisions。检查引用方向；App 只经 Controller 编排。新 Windows 能力放入对应适配器。重大决策添加带背景、替代方案、后果的 ADR；不要安装系统服务作为占位。
