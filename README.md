# Host2VM Relay

预览版：v0.2.0。C# / WinForms，提供自动选择 x64、x86、ARM64 的离线安装包，以及各架构便携版。无需预装 Python、.NET 或 OpenSSH 客户端。

A Windows desktop app that transparently routes selected host requests through a virtual machine using SSH tunnels and domain/IP rules, with Clash TUN integration.

宿主机到虚拟机的按规则透明转发。远端只需提供允许 TCP 转发的 SSH 服务，不限于麒麟；当前 GUI 客户端支持 Windows。安装与运行环境详见 [DEPLOYMENT.md](DEPLOYMENT.md)。

## 首次使用

1. 如果目标网络需要 VPN，先在虚拟机连接它。保证 SSH 的有效配置允许 `AllowTcpForwarding yes`、`DisableForwarding no` 和相应 `PermitOpen`。
2. 停止原来 PowerShell 中运行的 SSH 隧道，以免占用 1080 端口。
3. 双击 `Host2VMRelay.exe`。虚拟机地址示例为 `192.168.229.10:22`，请填写自己的 SSH 用户名。首次运行规则列表为空，请自行添加目标。
4. 选择密码登录并输入密码，或者选择私钥免密登录并选择私钥文件（不是 `.pub` 文件）。先在服务器的 `authorized_keys` 中配置公钥；本应用不会自动修改服务器。支持常见的 OpenSSH 私钥，不读取 Windows ssh-agent，也不自动继承 `.ssh/config`。加密私钥需要输入口令。
5. 默认勾选加密保存密码/口令。点击连接后，凭据通过 Windows DPAPI CurrentUser 加密保存在 `%LOCALAPPDATA%\Host2VMRelay\settings.json`，正常重启保留。不能把该密文复制到其他 Windows 用户直接使用。私钥文件只引用，不复制。取消保存并再次连接会清除保存的密文。
6. 首次连接确认服务器 SHA256 指纹。可在虚拟机用 `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub -E sha256` 核对对应主机密钥；若协商了 RSA/ECDSA，应核对相应公钥。已信任服务器的指纹变化会拒绝连接。
7. 「域名 / IP」页每行一个目标。点击保存，规则通过本地规则服务提供给 Clash，通常约 15 秒更新一次。域名规则变更后，旧 DNS 缓存和已建立的连接可能需重建。
8. 「接入 Clash」页复制脚本，在当前订阅的「编辑扩展脚本」中替换并应用。这保留本次用户原脚本中的 mieru UDP 设置，其他自定义脚本逻辑需手动合并。首次接入以及本机 SOCKS5 端口变更需要重新复制脚本，平常修改域名/IP 无需重复操作。
9. Clash 使用规则模式，开启 TUN、自动路由；TUN 的路由排除包含当前虚拟机的单个 IP（如 `192.168.229.10/32`；IPv6 使用 `/128`），DNS 劫持包含 `any:53` 和 `tcp://any:53`。程序脚本也写入这些字段，但 Verge 界面设置可能覆盖它们。需要支持 `fake-ip-filter-mode: rule` 的 Mihomo 内核，本机 v1.19.31 已通过配置校验。
10. 生成脚本为公司域名优先启用 Fake-IP，保留已有 Fake-IP 过滤规则。若 Chrome 自定义安全 DNS，先改用系统 DNS。必要时清除系统 DNS 缓存。浏览器登录跳转域名也需要加入规则。

## 规则格式

```text
code.example.com
*.example.com
10.20.30.40
10.20.30.0/24
2001:db8::1
```

普通域名精确匹配；`*.` / `+.` 同时匹配根域名和其子域名。不填写 `https://`、端口或网页路径。IP 规则适用于对应地址的 TCP 连接，包括 SSH。

## 后台运行

关闭窗口会缩到托盘，隧道和规则服务继续运行。右键托盘图标可打开、连接、断开或退出。默认自动重连，只有成功连接过后才会在断线时尝试重连。不会自动配置开机启动。程序退出后，Clash 缓存中的公司规则仍然存在，但公司节点不可用；不会自动回退到其他代理。恢复连接需重新打开本应用并点击连接。

本机 SOCKS5 默认监听 `127.0.0.1:1080`，规则服务固定监听 `127.0.0.1:17861`，不向局域网开放。需要关闭占用这些端口的其他实例。规则服务不要求管理员权限。底部最近读取时间表示规则被读取，不是目标网站连通性证明。

## 私钥认证失败

先确认目标用户和私钥对应的公钥，再检查服务器目录所有者及权限。通常用户的 `~/.ssh` 为 700、`~/.ssh/authorized_keys` 为 600，归目标用户所有；主目录不能对其他用户开放写权限。认证被拒绝与网络转发是不同问题。不要通过关闭 SSH 的权限检查来规避错误权限。

应用仅转发 TCP，不支持 UDP。宿主机必须能通过所配置的 IP 连接虚拟机。VMware Host-only 网卡的一组示例是宿主机 `192.168.229.1/24`、虚拟机 `192.168.229.10/24`；这不是强制网段。应用不改动 Windows 路由、防火墙或 Clash 原始订阅。

## 构建与验证

需要 .NET 8 SDK。

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 -p:PublishProfile=Standalone -o publish
```

`Host2VMRelay.exe --self-test <结果文件>` 验证规则、DPAPI 加密、设置存储、HTTP 规则服务。
`node test-script.cjs <配置文件>` 验证脚本保留原规则、重复应用、DNS 过滤模式转换，并生成独立内核测试配置。

依赖 SSH.NET 2026.0.0（MIT）；Windows .NET 运行时随自包含发行包附带。
