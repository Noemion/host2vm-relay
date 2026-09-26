# 开发指南

## 构建命令

在 main 上开发。Windows 构建需要 .NET 8 SDK；脚本回归需要 Node.js；安装包需要 Inno Setup 7。

```powershell
.\build.ps1
.\build.ps1 -Sync -Run
.\build.ps1 -Test
.\build.ps1 -Portable
.\build.ps1 -Package -Install
```

需要测试本机 UDP 传输时额外准备 Python，并运行：

```powershell
dotnet run --project tests/TransportHarness/TransportHarness.csproj -c Release -- --local-check artifacts/checks/windows-transport.json
```

所有产物在 `artifacts/`：assets（应用/安装器 ICO）、build（普通 bin/obj）、transport（跨平台测试夹具）、checks（JSON、截图、日志、源码快照）、publish（三架构程序）、release（Setup/ZIP/SHA256SUMS）。不向源码目录写入构建产物。用户配置不是构建产物。

## 模块

`MainForm.Networking.cs` 负责认证、主机指纹、探活、恢复和路径提示。`RelaySocksServer` 提供仅监听 loopback 的 SOCKS5 入口；TCP 交给 SSH.NET 动态转发，UDP 交给 `UdpTunnel`。后者以带边界和会话编号的帧经 SSH exec 的 stdin/stdout 与嵌入 Python 组件通信。Python 端使用普通 UDP 套接字，队列、会话数、最大帧和空闲时间均有限制。健康 URI 由本机入口处理，绝不访问公网 `.invalid` 域名。

`ClashScript` 保留原配置后加入 TCP/UDP 分组。不可用时选择 PASS，让原规则继续匹配。PASS 必须是 fallback 组第一个成员：它的专用探测失败，可用中继获选；所有成员失败时第一个 PASS 才能让原有策略接管。不要改成 DIRECT、REJECT 或把数组次序倒过来。

`SettingsLocation` 的固定 bootstrap 仅保存路径。迁移先写入目标、校验再更新定位文件；失败不改变当前活动目录，旧配置保留。诊断覆盖目录不读取用户 bootstrap，也不迁移真实凭据。

`ScriptComposer` 不执行 JavaScript。v2 分隔用户区域和受校验的托管区域，允许用户区增量编辑。v1 只有已知规范模板才能无损升级；未知尾部代码或托管修改显式报错。文本规范化为 LF，Windows 编辑框显示为 CRLF。测试执行 C# 实际导出的 JavaScript，不用测试端独立拼接器冒充生成器验收。

`UiTheme` 使用 ContentScale=0.8 与静态 SpacingScale=0.6。9.6pt 是新的设计基准，系统有效 DPI 和 PerMonitorV2 保持不变。不要在 WinForms 自动缩放后再整体乘一次 0.8。五个页面及脚本窗口继续验证 100/125/150/175/200%，原生与注入结果分开。

## 验收与发布

`build.ps1 -Test` 执行 C# 自检、旧脚本增量合成、TUN 字段、配置迁移、ICO 格式和 DPI 回归。安装包构建后通过 Win32 资源提取与 Shell API 检查真正的安装器 EXE，而不是仅检查配置字符串。

Linux 网络验收必须运行在可销毁、有 root 权限的 CI 测试机：`sudo python3 tests/test-transparent-network.py`。它创建隔离命名空间、临时 SSH 密钥、真实 TUN 与 UDP/TCP 服务；密钥不写入上传目录。普通客户端不包含代理配置，两个路径用不同返回值证实；结束后清理命名空间和夹具。Mihomo 来自固定发行版本，并校验发行 API 给出的 SHA-256。不要在日常生产主机运行这个网络测试。

更新 csproj、安装器默认版本、manifest 和 RELEASE_NOTES 后推送 main。只有 Windows 和 network 两个任务成功才能发布。已有正式 Release 不替换附件。物理 DPI、多显示器、真实 Windows TUN 和公司业务协议验收范围需明确记录，不能用构建成功代替。
