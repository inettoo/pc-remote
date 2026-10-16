# 发布与回滚
产品 SemVer：当前 0.0.1；Protocol major/minor 在代码和 PROTOCOL.md 独立同步。
工作流：Issue → 范围/ADR → 分支 → 实现与测试 → PR/review → main。每阶段完成后再进入下一版本。
CI 构建不是发布；只有验收通过的 commit 才手动创建 vX.Y.Z annotated tag 与 GitHub Release。
发布前检查 CHANGELOG、协议兼容、真实 Windows 验收、无凭据/私密日志、Release publish、SHA-256。
首版 framework-dependent win-x64，需要 .NET 10 Desktop Runtime；不安装系统服务，不添加自启动或防火墙规则。
CI 上传可测试 zip 作为构建产物，保留 14 天；正式签名/安装器在 0.1.0 前新增 ADR，未签名须明确标注。
回滚：双方退出，恢复上一套完整目录；协议 major 不兼容时双方同步回滚；保持用户文件不变。不得自动覆盖文件。
