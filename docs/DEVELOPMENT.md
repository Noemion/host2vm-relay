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

`MainForm.Networking.cs` 负责界面输入、主机指纹确认、用户重连意图和路径提示。`RelaySession` 独立拥有 SSH、TCP 转发、UDP 启动/恢复和取消生命周期；Linux TransportHarness 复用同一会话服务。连接参数是快照，停止后的旧异步结果不能重新发布到界面。`RelaySocksServer` 提供仅监听 loopback 的 SOCKS5 入口；TCP 交给 SSH.NET 动态转发，UDP 交给 `UdpTunnel`。后者以带边界和会话编号的帧经 SSH exec 的 stdin/stdout 与嵌入 Python 组件通信。Python 端使用普通 UDP 套接字，队列、会话数、最大帧和空闲时间均有限制。健康 URI 由本机入口处理，绝不访问公网 `.invalid` 域名。

`ClashScript` 保留原配置后加入 TCP/UDP 分组。不可用时选择 PASS，让原规则继续匹配。PASS 必须是 fallback 组第一个成员：它的专用探测失败，可用中继获选；所有成员失败时第一个 PASS 才能让原有策略接管。不要改成 DIRECT、REJECT 或把数组次序倒过来。

`SettingsLocation` 的固定 bootstrap 仅保存路径。迁移先写入目标、校验再更新定位文件；失败不改变当前活动目录，旧配置保留。诊断覆盖目录不读取用户 bootstrap，也不迁移真实凭据。

`ScriptComposer` 不执行 JavaScript。v2 分隔用户区域和受校验的托管区域，允许用户区增量编辑。v1 只有已知规范模板才能无损升级；未知尾部代码或托管修改显式报错。文本规范化为 LF，Windows 编辑框显示为 CRLF。测试执行 C# 实际导出的 JavaScript，不用测试端独立拼接器冒充生成器验收。

`UiTheme` 默认使用 ContentScale=0.8、SpacingScale=0.6 和 9 pt 字体。用户可分别设置内容缩放与字体，显示设置在下次启动时生效。系统有效 DPI 和 PerMonitorV2 保持不变。不要在 WinForms 自动缩放后再整体乘一次 0.8。页面使用原生滚动，禁止通过手动移动内容实现滚动。五个页面及脚本窗口继续验证 100/125/150/175/200%，原生与注入结果分开。

## 验收与发布

`build.ps1 -Test` 执行 C# 自检、旧脚本增量合成、TUN 字段、配置迁移、ICO 格式和 DPI 回归。安装包构建后通过 Win32 资源提取与 Shell API 检查真正的安装器 EXE，而不是仅检查配置字符串。

Linux 网络验收必须运行在可销毁、有 root 权限的 CI 测试机：`sudo python3 tests/test-transparent-network.py`。它创建隔离命名空间、临时 SSH 密钥、真实 TUN 与 UDP/TCP 服务；密钥不写入上传目录。普通客户端不包含代理配置，两个路径用不同返回值证实；结束后清理命名空间和夹具。Mihomo 来自固定发行版本，并校验发行 API 给出的 SHA-256。不要在日常生产主机运行这个网络测试。

更新 csproj、安装器默认版本、manifest 和 RELEASE_NOTES 后推送 main。只有 Windows 和 network 两个任务成功才能发布。已有正式 Release 不替换附件。物理 DPI、多显示器、真实 Windows TUN 和公司业务协议验收范围需明确记录，不能用构建成功代替。

发布说明遵循阮一峰的[中文技术文档写作规范](https://github.com/ruanyf/document-style-guide)。使用简短、客观的陈述句，统一中文标点及中英文间距。首次出现的英文缩写应注明中文含义。明确区分本版变更与历史版本修复，分别说明升级步骤、验证范围和使用限制。修改已发布版本的说明时，仅更新说明正文，保留标签、附件和原始发布时间。

## 异常处理契约

- 安装器的行为、布局分别位于 `packaging/InstallerFlow.iss` 和 `packaging/InstallerAppearance.iss`。仅在 `PrepareToInstall` 中处理旧版，禁止在窗口创建前卸载。进度按阶段及实际文件进度推进，使用 Windows 原生动画，禁止按时间伪造进度。日志从独立卸载日志共享读取，只处理完整行；界面最多保留 500 行。安装运行测试交由用户执行，默认构建不生成或运行测试安装器。验收记录见 `docs/INSTALLER_ACCEPTANCE.md`。

- `DuplexRelay` 等待两个复制任务结束。正常 EOF 仅关闭目标写端，允许反向响应继续；异常或取消则关闭两个套接字并取消另一任务，之后才释放 SOCKS 准入槽位。
- UDP association 的两分钟空闲时间由两个方向成功传输共同刷新。DNS 等待、无效包和队列拒绝不算成功传输。测试注入一秒空闲时间，用真实 socket 验证持续下行与真正空闲的区别。
- Python DNS 使用四个 daemon worker、32 个排队请求、最多 32 个待解析目标、每目标八个数据报；逻辑超时两秒，成功结果缓存 60 秒，最多 128 项。系统 getaddrinfo 本身不可强制取消，超时/关闭后的结果不再投递；即使 worker 阻塞，也不阻塞数字 IP、已有会话和健康探测。UDP 过载允许丢包，不增长无界队列。
- `Settings.SaveUpdated` 复制候选配置及 HostKeys，先原子写入，再由调用方替换活动引用；失败时内存和磁盘保持原值。规则保存与 Clash 同步失败分别报告。新增可变引用字段时必须同步扩展候选复制逻辑。
- SSH 连接使用可取消的 ConnectAsync。会话停止会取消 UDP 启动/探测，并停用监听器。后台循环独立检查连接，界面只读取快照。刷新操作串行化；后台 UDP 重建不阻塞 TCP 检查。`RelayHealthTiming` 集中定义三秒轮询间隔、三秒探测超时及十二秒有效期。有效期覆盖等待间隔、SSH 和 UDP 两次探测，并留三秒调度余量。明确的探测失败立即处理；缺少刷新时最迟在有效期结束后失效，Clash 的实际切换还取决于自身探测周期。

- SOCKS 入口最多接纳 160 个连接，其中转发最多占用 128 个。其余容量用于握手及健康探测。握手最长八秒，超过转发上限返回 SOCKS 失败，不排队等待。健康探测仍可能受到握手洪泛影响，不能视为独立管理端口。关闭与接入在同一锁内检查状态，锁外取消和关闭套接字。
- UDP 注册的检查和插入在同一锁内完成，最多保留 128 个关联。数据转发使用异步套接字和有界队列；SSH.NET 的通道读写仍可能占用工作线程，不能宣称整个链路完全无阻塞。
- 日志队列最多保留 1000 条，单条最多 4096 个字符。界面每批最多消费 100 条且不超过 16384 个字符。暂停显示只影响消费端，溢出时丢弃较早记录并显示提示。

审查结论、并发边界及待完成的发布验收见 [质量审查记录](QUALITY_REVIEW.md)。

本机故障回归：`dotnet run --project tests/TransportHarness/TransportHarness.csproj -c Release -- --local-check artifacts/checks/windows-transport.json`，包含 TCP 两侧 reset、半关闭延迟响应、资源回收、SSH 启动取消、UDP 单向推送及空闲关闭。`python tests/test-udp-helper.py` 额外覆盖慢 DNS、超时结果丢弃、关闭后的解析结果、缓存与退出。

UI 验收需要可见且能获得焦点的交互桌面。隐藏窗口、锁屏或不可访问桌面报告 BLOCKED，不能将这种执行当作 DPI/截图通过；保留原生 DPI 与消息注入验收的区别。
