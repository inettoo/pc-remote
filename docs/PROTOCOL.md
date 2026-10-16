# LAN 协议
产品 0.0.2；控制帧协议 1.0 与发现协议 2.0 独立版本。

## 控制帧 1.0
4 字节无符号大端 payload 长度 + UTF-8 JSON Envelope；长度 1..1,048,576，分配前验证。
必填 major=1、minor=0、requestId(非空 UUID)、type(1..64 字符)、payload(非 null JSON)。
未知版本、截断、超限、畸形报文失败。目前只实现编解码，没有 TCP 控制监听。
未来连接是控制端与 Host 直连，不使用任何注册/中继/账号服务器。依 ADR 0007，无账号/口令/逐次批准，信任整个直接局域网；TLS 加密与自动证书连续性策略另记 ADR，不宣称提供首次连接的账号身份认证。
屏幕、输入、文件 payload 尚未定义，不能提前上线使用。破坏变更升 major；minor 只添加可协商的可选能力。

## UDP Discovery 2.0
每个数据报为 1..512 字节 UTF-8 JSON；无长度前缀。所有字段必填，拒绝未知和重复字段，最多嵌套 4 层。
字段：magic="pc-remote-lan"、major=2、minor=0、kind、scanId、instanceId、name。
- probe：kind="probe"，scanId=控制端每轮随机非空 UUID，instanceId=空 UUID，name=""。
- host：kind="host"，回传 scanId，instanceId=服务本次运行非空 UUID，name=1..64 字符无控制字符的本机设备名。
控制端对每个内部选择的 LAN 地址，绑定临时端口，每 2 秒向该子网 UDP 45831 广播 probe。TTL=1，发送指定接口。
Host 服务只回复通过同接口/子网/直接路由校验的来源，将 host 单播至 probe 的来源地址和端口，不响应 host。不携带账号、路径、密钥或权限。
控制端只接收同接口/子网/直接路由且 scanId 匹配的 host。来源 IP 取 socket，不接受报文自报。
接收最多 64 包/秒，设备最多 128 项，以来源 IP 去重、7 秒过期；版本不符/截断/畸形静默丢弃。设备 ID/名称未经认证。
局域网接口定期自动重读，发生变化自动重建，退出释放 socket。与公告草案 1.0 不兼容，因此升发现 major=2。
