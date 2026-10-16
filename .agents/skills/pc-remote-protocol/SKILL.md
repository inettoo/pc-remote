---
name: pc-remote-protocol
description: 修改 pc-remote 线格式、消息或兼容性时使用。
---

读 docs/PROTOCOL.md 和 Protocol 项目。先定义 payload、尺寸、权限与错误语义，再改实现。同步独立协议版本及拒绝旧/未知版本测试。长度校验必须先于分配；不把未经批准的消息交给输入/文件模块。
