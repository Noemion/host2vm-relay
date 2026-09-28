# 安装、配置与运行依赖

## Windows 客户端

安装包按 x64、x86 和 ARM64 分别提供，每个架构对应一个 EXE 文件。安装版不包含运行时，需要同架构的 .NET 8 Windows Desktop Runtime（桌面运行时）。支持正式版 8.0.x，建议使用最新补丁。

普通 .NET Runtime、ASP.NET Core Runtime、.NET Framework 和 .NET 6、7 均不能替代。仅安装 .NET 9 或 10 也不满足本版要求。无需安装开发用的 SDK。

安装器使用当前用户权限。确认安装后，先检查微软安装器登记的运行时及实际文件，再卸载旧版。缺少依赖时显示微软下载地址并保留旧版，不自动下载或安装运行时。自定义路径下未注册的 .NET 不属于安装器的检测范围。

便携 ZIP 仍包含运行时，可用于离线环境。Windows 客户端不需要 Python 或 Node.js。升级前从托盘退出旧程序，用户配置和目录定位信息会保留。程序和安装包目前未签名。

## 网络准备

Windows 端需要 Clash Verge Rev／Mihomo，启用规则模式、TUN、自动路由、DNS 劫持，并将实际虚拟机 IP 加入 TUN 路由排除。应用本身不替用户修改 Windows 路由、防火墙和 Clash 的受管 TUN 字段。集成验证采用固定版本 Mihomo v1.19.31；旧核心需要检查 AND、PASS、fallback 以及 Fake-IP rule 模式支持情况。

虚拟机支持 Linux x86_64 和 aarch64，需要 SSH 允许命令执行与 TCP 转发，并能够访问目标网络。UDP 静态辅助程序由软件自动部署到用户缓存目录，无需 Python 或 Rust 环境。组件通过已校验指纹的 SSH 会话启动，不额外开放端口或安装服务。UDP 域名需要当前 Clash DNS 能解析，不能把仅存在于 VM hosts 中的名称视为自动支持。协议和回退范围见 `UDP_SUPPORT.md`。

### 麒麟执行授权

麒麟 KYSEC 等执行控制可能要求用户先在虚拟机中允许辅助程序运行，即使文件已有 Linux 执行权限。辅助程序位于 `~/.cache/host2vm-relay/<SHA-256>/agent`。可选择以下方式：

- **允许当前程序**：核对程序来源后，在虚拟机的执行授权弹窗中点击“允许”，也可按[麒麟官方常见问题](https://www.kylinos.cn/upload/1/kycms/20250617/1934877956515139584.pdf)在安全中心管理执行白名单。辅助程序内容更新后，可能需要重新授权。
- **关闭后续执行授权提示**：在连接页“麒麟执行授权”中点击“复制关闭命令”，然后在麒麟虚拟机终端粘贴并执行：

  ```sh
  sudo /usr/sbin/setstatus -f exectl off -p
  ```

  此命令永久关闭系统级执行控制，影响所有程序，重启后仍生效。请确认符合设备管理要求。软件仅提供说明和复制按钮，不自动执行该命令。

可运行 `/usr/sbin/getstatus` 查看当前状态，`exec control: off` 表示执行控制已关闭。如需恢复，在虚拟机安全中心选择适合的执行控制模式；若原先为弹窗授权模式，可执行 `sudo /usr/sbin/setstatus -f exectl warning -p` 恢复该模式。

执行被拒绝或等待授权时，界面显示 UDP 连接失败及原因；TCP 会话可用时仍保留 TCP 转发。完成授权后，启用自动重连的会话会尝试恢复 UDP，也可手动断开后重新连接。这类启动失败发生在 SSH 链路中，重新粘贴 Clash 脚本不能解决。

## 应用 Clash 脚本

生成结果会先经过 JavaScript 静态语法检查，不执行用户代码。检查不能替代 Clash 的实际执行，也不能保证所有作用域错误、运行时错误或分流问题均可发现。

复制或保存脚本不代表脚本已经生效。本软件暂不能自动应用 Clash 脚本或重启内核。请将完整结果粘贴到当前订阅的扩展脚本中，保存并重新应用配置。若仍未生效，在 Clash 界面重启内核；服务模式下若仍读取旧规则，请退出并重新打开 Clash，然后重新应用配置。重新打开目标网页，在 Clash 连接列表确认命中 Host2VMRelay。

规则刷新接口返回成功仅表示请求已被接受。服务模式可能使用规则文件副本，不能据此认定最新规则已经生效。

## 配置目录

默认：Windows 实际“文档/Host2VMRelay/settings.json”，支持文档重定向。设置页可更换目录；迁移当前已经保存的配置，不隐式保存尚在编辑的表单和规则。原目录保留；目标已有配置时要求确认并备份再替换。完成目标写入和校验后才更新定位信息。

固定定位文件：`%LOCALAPPDATA%\Host2VMRelay\storage.json`，仅保存 schema 和绝对目录路径。自定义目录不可用或配置缺失时启动明确报错，不静默创建空白账号。恢复该目录后重试；需要人工恢复默认时先备份定位文件和两处 settings.json，再调整定位信息。

Clash 的两个规则文件仍在 Clash 自身的数据目录下，不随应用配置迁移。加密凭据绑定当前 Windows 用户，跨用户复制后需重新输入密码或私钥口令。安装、卸载都不删除这些用户配置。

## 版本升级

从 v0.5.0 或更早版本升级到 v0.6.x 后，必须使用“合并已有脚本”导入旧版完整脚本，复制生成结果到 Clash 并应用。这样才会接入 UDP 节点、按协议的规则和健康回退组。只更换 EXE 无法更新此前粘贴的 JavaScript。

## 本地构建与发布

日常构建需要 .NET 8 SDK、Rust 和 MSVC 编译工具；应用/脚本/DPI 测试需要 Node.js；制作安装包需要 Inno Setup 7。Python 仅用于执行自动化验收脚本。原生组件构建方法见 `DEVELOPMENT.md`。

```powershell
.\build.ps1
.\build.ps1 -Sync -Run
.\build.ps1 -Test
.\build.ps1 -Package -Install
```

`artifacts/` 保存所有构建、发布和检查产物。GitHub Actions 中 Windows 检查、安装包图标检查与 Linux 实际 TUN 网络验收都通过后，新版本才允许发布。旧的同版本正式 Release 保留，不覆盖。手动 Run workflow 当前只构建，不发布。

云端 Windows 检查不等同于真实用户 Windows 11 TUN、ARM64 硬件、公司网络及物理多 DPI 显示器验收。
