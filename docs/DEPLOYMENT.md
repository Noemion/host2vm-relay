# 安装、配置与运行依赖

## Windows 客户端

运行 `Host2VMRelay-0.6.0-win-universal-Setup.exe`。安装器携带 x64、x86、ARM64 三套程序并按系统选择；便携 ZIP 则按架构分别提供。应用依赖和 .NET 桌面运行时以 self-contained single-file 方式包含在 EXE 内，使用者无需编译，也无需在 Windows 安装 Python 或 Node.js。不是可脱离 Windows 的完全静态原生程序；临时目录需要可写。程序和安装包目前未签名。

安装器使用当前用户权限。升级前从托盘退出旧程序；检测到同一用户的旧安装时先卸载后安装，保留配置文件和目录定位信息。Setup 的文件名明确标注 universal，不能把它误认为单个 AnyCPU 原生二进制。

## 网络准备

Windows 端需要 Clash Verge Rev／Mihomo，启用规则模式、TUN、自动路由、DNS 劫持，并将实际虚拟机 IP 加入 TUN 路由排除。应用本身不替用户修改 Windows 路由、防火墙和 Clash 的受管 TUN 字段。集成验证采用固定版本 Mihomo v1.19.31；旧核心需要检查 AND、PASS、fallback 以及 Fake-IP rule 模式支持情况。

虚拟机需要 Linux／兼容 POSIX shell 的 SSH 环境，允许命令执行与 TCP 转发，能够访问目标网络；UDP 还要求 `python3` 可用。组件通过已校验指纹的 SSH 会话启动，不额外开放端口或安装服务。UDP 域名需要当前 Clash DNS 能解析，不能把仅存在于 VM hosts 中的名称视为自动支持。协议和回退范围见 `UDP_SUPPORT.md`。

## 配置目录

默认：Windows 实际“文档/Host2VMRelay/settings.json”，支持文档重定向。设置页可更换目录；迁移当前已经保存的配置，不隐式保存尚在编辑的表单和规则。原目录保留；目标已有配置时要求确认并备份再替换。完成目标写入和校验后才更新定位信息。

固定定位文件：`%LOCALAPPDATA%\Host2VMRelay\storage.json`，仅保存 schema 和绝对目录路径。自定义目录不可用或配置缺失时启动明确报错，不静默创建空白账号。恢复该目录后重试；需要人工恢复默认时先备份定位文件和两处 settings.json，再调整定位信息。

Clash 的两个规则文件仍在 Clash 自身的数据目录下，不随应用配置迁移。加密凭据绑定当前 Windows 用户，跨用户复制后需重新输入密码或私钥口令。安装、卸载都不删除这些用户配置。

## 版本升级

从 v0.5.0 或更早版本升级到 v0.6.0 后，必须使用“合并已有脚本”导入旧版完整脚本，复制生成结果到 Clash 并应用。这样才会接入 UDP 节点、按协议的规则和健康回退组。只更换 EXE 无法更新此前粘贴的 JavaScript。

## 本地构建与发布

日常构建需要 .NET 8 SDK；应用/脚本/DPI 测试需要 Node.js；制作安装包需要 Inno Setup 7。Windows 本机传输测试额外用 Python 作为测试夹具，不是客户端运行依赖。

```powershell
.\build.ps1
.\build.ps1 -Sync -Run
.\build.ps1 -Test
.\build.ps1 -Package -Install
```

`artifacts/` 保存所有构建、发布和检查产物。GitHub Actions 中 Windows 检查、安装包图标检查与 Linux 实际 TUN 网络验收都通过后，新版本才允许发布。旧的同版本正式 Release 保留，不覆盖。手动 Run workflow 当前只构建，不发布。

云端 Windows 检查不等同于真实用户 Windows 11 TUN、ARM64 硬件、公司网络及物理多 DPI 显示器验收。
