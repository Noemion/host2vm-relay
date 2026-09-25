# Host2VM Relay

让 Windows 宿主机把**指定域名和 IP 的访问请求交给虚拟机转发**，其他流量继续使用原有规则。适合通过虚拟机访问特定网络资源的场景。

支持密码或私钥登录、加密保存密码、托盘后台运行和断线重连。远端支持任何提供 SSH TCP 转发服务的系统。

## 使用前准备

- 宿主机：Windows 10 / 11，已安装 [Clash Verge Rev](https://github.com/clash-verge-rev/clash-verge-rev)（[下载页面](https://github.com/clash-verge-rev/clash-verge-rev/releases)）。
- 虚拟机：已启用 SSH 服务并允许 TCP 转发，能访问你需要的网站或服务器。
- 宿主机能够通过虚拟机 IP 连接它的 SSH 服务。

## 快速开始

### 1. 安装

[下载安装包](https://github.com/Noemion/host2vm-relay/releases/download/v0.2.1/Host2VMRelay-0.2.1-Setup.exe)，双击安装。其他架构便携包见 [Releases](https://github.com/Noemion/host2vm-relay/releases/latest) 页底部的 **Assets**。

安装包自动选择系统架构，**不需要另外安装 .NET、Python 或编译器**。

### 2. 连接虚拟机

打开应用，在「连接」页填写虚拟机 IP、SSH 端口（通常为 `22`）和用户名。选择密码登录，或选择已配置好的私钥文件，然后点击「连接虚拟机」。首次连接时核对并确认服务器指纹。

### 3. 添加转发目标

在「域名 / IP」页，每行填写一个需要经过虚拟机访问的地址，然后点击「保存规则」。例如：

```text
code.example.com
*.example.com
10.20.30.40
10.20.30.0/24
```

分别表示：一个域名、该域名及所有子域名、一个 IP、一个网段。不要填写 `https://`、端口或网页路径。

### 4. 接入 Clash

1. 在应用「接入 Clash」页点击「复制 Clash 扩展脚本」。
2. 打开 Clash Verge Rev，右键当前订阅 →「编辑扩展脚本」，粘贴、保存并重新应用订阅。已有自定义脚本时需先合并。
3. 选择**规则模式**，开启 **TUN** 和**自动路由**；按照应用中的提示设置 DNS 劫持，并将虚拟机 IP 加入 TUN 路由排除。

例如，虚拟机 IP 是 `192.168.229.10`，路由排除填写 `192.168.229.10/32`。不要排除需要转发的目标 IP。

现在可以在宿主机正常打开目标网页，或直接 SSH 连接目标服务器。在 Clash 的连接列表中，可确认请求是否经过 `Host2VM Relay`。

## 日常使用

- 修改目标后点击「保存规则」，通常约 15 秒生效，无需重复粘贴脚本。
- 关闭窗口会缩到托盘并继续运行；右键托盘图标可退出。
- 使用期间保持虚拟机、Clash 和本应用运行。
- 当前仅转发 TCP，适用于网页和 SSH，不支持 UDP。

更多安装、构建和兼容性信息见 [部署说明](docs/DEPLOYMENT.md)，测试范围见 [验证记录](docs/VALIDATION.md)。

源码目录与开发步骤见 [开发指南](docs/DEVELOPMENT.md)。
