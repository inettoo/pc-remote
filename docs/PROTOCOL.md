# LAN Protocol 1.0
产品 0.0.1；协议版本 1.0 独立管理。当前仅实现通用帧，不实现连接握手。
帧：4 字节无符号大端 payload 长度 + UTF-8 JSON Envelope。长度 1..1,048,576；上限是整个 JSON。
Envelope 必填 major、minor、requestId(UUID 非空)、type(1..64 字符)、payload(JSON，不能 null)。
首版 major 必须等于 1，minor 仅接受 0；未来引入协商之前不宽松接受高版本。未知 type 由会话分派层拒绝。
截断、零长度、超限、无效 JSON、版本不匹配全部失败；分配前验证长度。无压缩、无多路复用。
后续控制消息：Hello/Capabilities、SessionRequest/Approved/Rejected、PermissionChanged、Disconnect；
后续能力：StartStream/Frame、InputEvent、ListDirectory/FileChunk/TransferComplete，**尚未定义 payload，不得上线使用**。
添加 payload/能力先修订此文档、版本与兼容测试；major 变化代表不兼容，minor 只增加协商后的可选能力。
后续 TLS 1.3 直连，无账号/服务器；临时证书通过预配置高熵密钥的证书绑定/认证方案抵御中间人；无需 Host 每次确认，身份校验与预设权限分离。具体认证算法须在 0.0.3 的 ADR 中确定，禁止自创加密或直接以 TLS 自签名证书加明文密钥替代。
发现报文不携带敏感信息，限制大小/频率；连接请求拒绝和超时即关闭。完整网络设计在 0.0.3 前完善并测试。
