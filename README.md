# Host2VMRelay

让 Windows 宿主机把**指定域名和 IP 的 TCP／单播 UDP 请求交给虚拟机转发**，未命中的流量保持原有分流。支持 SSH 密码／私钥、主机指纹校验、当前用户加密凭据、托盘、自动重连、按协议回退宿主机，以及旧版完整 Clash 脚本的增量更新。

## 安装与准备

从 [Releases](https://github.com/Noemion/host2vm-relay/releases/latest) 下载带 `win-universal` 的通用安装包，或对应的 x64／x86／ARM64 便携包。Windows 端不需另装 .NET、Python、Node.js 或编译器。

Windows 使用 Clash Verge Rev／Mihomo TUN。虚拟机需要 Linux／兼容 POSIX shell 的 SSH 服务、允许命令执行和 TCP 转发，并能够访问目标网络。UDP 功能还需要虚拟机 `python3`；程序会复用已校验的 SSH 会话启动内存中的标准库组件，不安装服务、不使用 root、不额外开放 VM 监听端口。

## 快速使用

1. 在“连接”页填写虚拟机 IP、SSH 端口和用户名，选择密码／私钥，核对首次连接的主机指纹。需要 UDP 时保持“透明转发 UDP”开启。
2. 在“转发规则”页每行填写目标并保存：`code.example.com`、`*.example.com`、`10.20.30.40`、`10.20.30.0/24`。`#` 为注释；不要填写协议、服务端口或 URL 路径。
3. “Clash 接入”中直接生成，或选择“合并已有脚本”粘贴上个版本的完整脚本／原始 JavaScript。生成并复制后，整体替换当前订阅的扩展脚本，保存并应用。
4. 在 Clash 设置中选择规则模式，开启 TUN 与自动路由；DNS 劫持保存 `any:53`、`tcp://any:53`；路由排除填写实际虚拟机 IPv4 的 `/32` 或 IPv6 的 `/128`，**不要排除需要转发的目标**。

普通客户端仍访问原域名、IP 和端口，不需要单独设置 SOCKS 或改成 localhost。UDP 域名需当前 Clash DNS 能解析（如企业 DNS）；仅在虚拟机 hosts 文件中存在的名称不能自动通过 Mihomo SOCKS UDP 解析。此点与 TCP 的远端解析存在差异，详见 [UDP 说明](docs/UDP_SUPPORT.md)。浏览器自定义安全 DNS 可能绕过期望的分流；规则还应包括登录跳转域名。

## 不可用时自动回退

虚拟机优先但不是唯一出口。TCP 与 UDP 独立检测；UDP 组件失败时 UDP 回退，正常的 TCP 继续经过虚拟机。虚拟机失联时两者恢复宿主机**原有 Clash 规则**，不是把全部流量改为 DIRECT，也不是关闭整个 TUN。现有 Mieru 的 UDP 设置继续保留。

启用自动重连时通道恢复后重新使用虚拟机；手动断开不会触发重连。状态变化通过界面、日志及去重托盘 warning 提示。健康 fallback 组还覆盖程序异常退出后残留的规则文件。切换存在检测周期；已有 TCP/UDP 会话可能需要客户端重试。本来仅虚拟机可达的服务不能保证宿主机回退后仍可达。

UDP 支持单播请求与回包，不支持广播、组播和 SOCKS 分片。数据报在 SSH/TCP 中封装，延迟特性不同于原生 UDP；不承诺所有 QUIC、实时音视频或多媒体协议都已经实测。

## 增量合成与 TUN 设置

生成格式分为用户区和托管区，程序不执行用户 JavaScript。再次导入完整脚本时提取并保留用户区，只更新托管实现；已知的 v0.4.0～v0.5.0 格式可迁移，新格式允许直接编辑用户区。未知托管修改、损坏标记、截断和无法归属的尾部代码会报告冲突，不静默丢弃。原始脚本必须提供同步 `main(config, profileName)`；语法和运行结果由 Clash 校验。这不是 YAML 订阅合并器。

TUN 开关、协议栈、设备名、自动路由、路由排除、自动选择网卡、DNS 劫持、严格路由、MTU 等界面字段沿用 Clash 传入值，不由生成器强制覆盖。若其他全局扩展也改写受管字段，应分别移除冲突。升级新程序后仍需重新生成脚本，已粘贴的旧文本不会自动更新。

## 配置与界面

默认配置目录是 Windows 实际“文档/Host2VMRelay”（支持重定向）。在**设置 → 配置存储 → 更改目录**中迁移已保存配置；已有目标需确认后先备份再替换，原目录保留。路径定位文件是 `%LOCALAPPDATA%\Host2VMRelay\storage.json`，只保存路径，不保存凭据。自定义目录不可用时明确报错，不偷偷切换到空白配置。卸载不删除配置；跨 Windows 用户迁移后需重新输入凭据。

界面采用紧凑卡片、9.6pt 正文和编辑字体，内容尺寸约为 v0.5.0 的 80%。Windows DPI 感知保持 PerMonitorV2；侧边／顶部导航、五个页面和脚本工作区按可用宽度重排。Alt+1～Alt+5 切换页面；Ctrl+Tab 循环；脚本工作区 Ctrl+Enter 生成并复制。

## 开发与验收

```powershell
.\build.ps1 -Sync -Run
.\build.ps1 -Test
.\build.ps1 -Package -Install
```

构建与检查产物统一位于 `artifacts/`。Actions 包含 Windows 编译、脚本／配置／图标／五档 DPI、本机 UDP 套接字检查，以及 Linux 独立网络中的真实 Mihomo TUN＋SSH 回退验收，两侧检查通过后才发布新版本。模拟 DPI 不等于物理高 DPI／跨显示器验收，Linux TUN 检查也不替代真实 Windows 公司网络测试。

详见 [开发](docs/DEVELOPMENT.md)、[部署](docs/DEPLOYMENT.md)、[UDP 与回退](docs/UDP_SUPPORT.md)、[DPI 验收](docs/DPI_VALIDATION.md)。
