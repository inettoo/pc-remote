# LAN 协议
产品 0.0.3；控制帧协议 2.0 与发现协议 2.0 独立版本。

## 控制帧 2.0
4 字节无符号大端 UTF-8 JSON payload 长度，1..1,048,576 字节，分配前验证。
Envelope 必填 major=2、minor=0、requestId=非空 UUID、type=1..64 字符、payload=非 null JSON；未知版本、截断/超限/坏 JSON 失败。
TCP 45832，TLS 1.3 + ALPN pc-remote/2；无账号、口令、配对或批准消息。信任同一直接 LAN，TLS 不承诺首次身份认证；无外部服务器。
消息：
- Controller→Host：Input(RemoteInput)、Ping({})、Disconnect({})。
- Agent→Host→Controller：ScreenFrame(DesktopImage)、DesktopUnavailable(SessionNotice)。
- Host→Agent：Input(RemoteInput)、Disconnect({})。
RemoteInput：kind 数字枚举 0 Move、1 LeftDown、2 LeftUp、3 RightDown、4 RightUp、5 Wheel、6 KeyDown、7 KeyUp、8 ReleaseAll；x/y 0..65535；key 1..254（键事件）；delta 为 -1200..1200 且是 120 的倍数（滚轮）。未使用字段默认 0。
DesktopImage：width/height 1..1920，像素积≤3,686,400，jpeg 为 base64 字节数组，解码后 4..600,000 字节；声明尺寸必须匹配 JPEG 头。
SessionNotice：text 必填、非空、最多 256 字符。未知控制消息/未定义 kind 拒绝；不接受可执行命令或任意文件路径。
Controller 每 3 秒 Ping；Host 控制读取和代理消息最长 10 秒，客户端帧读取最长 15 秒。两秒无新画面遮罩并释放输入，队列溢出断开释放，不静默丢 key-up。
发现协议与屏幕编码后续独立演进；破坏变更升级对应 major。

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
