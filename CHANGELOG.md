# Changelog

## 0.6.13 — 路由生效诊断

- “Clash 接入”新增目标网址诊断，显示本地规则命中、规则文件一致性和 Clash 当前状态。
- 通过 Clash 代理端口发起 TCP 请求，按本次连接核对实际链路和命中规则。未捕获证据时明确显示无法确认。
- 独立验证 TLS 证书，区分路由问题与证书问题；不发送网页路径、登录参数或浏览器凭据。
- 支持取消检测与超时控制，结果同步写入运行日志。

## 0.6.12 — 安装向导与版本确认

- 首屏展示应用介绍，使用标准欢迎页，无滚动条。
- 按顺序展示安装路径、开始菜单名称、桌面快捷方式和安装确认页。重新安装时同样显示这些选项。
- 确认页显示已有版本、目标版本、运行时与应用运行状态，区分首次安装、升级、重新安装和降级。
- 安装按钮与进度说明跟随操作类型变化；降级和无法识别旧版时给出相应提示。

## 0.6.11 — 安装时自动关闭应用

- 应用正在运行或缩到托盘时，可以直接打开安装向导，在标准页面选择自动关闭后继续安装。
- 将旧版卸载移到应用关闭之后，并保留连续总进度与操作记录。
- 关闭失败或拒绝关闭时，停止旧版移除；更改安装路径时仍检测旧目录中的程序。
- 保留独立卸载的运行保护，并阻止同时运行多个安装器。

## 0.6.10 — 安装语言自动选择

- 每次安装、重新安装和升级均按当前 Windows 显示语言选择安装界面，不再沿用上次安装的语言。
- 取消语言选择弹窗；内置简体中文和英文，未匹配内置语言时回退英文。

## 0.6.9 — 标准安装向导

- 安装程序恢复 Inno Setup 标准现代向导的窗口尺寸、字体、页眉、图片和导航按钮。
- 移除覆盖原生页面的自定义布局，使用标准准备进度页，在进度条下方显示操作记录。
- 保留检查、旧版处理、文件安装与收尾的总进度，以及运行时检测和配置保留行为。

## 0.6.5 — 页面切换与脚本工作区

- 修复 TCP 取消回调与 I/O 完成处理之间的相互等待，避免断开时连接资源迟迟无法释放。
- 页面切换增加约 120 毫秒的渐显过渡，连续切换直接响应最后一次选择；遵守 Windows 动画设置。
- 使用统一页面容器替代隐藏标签栏的原生标签控件，保留页面输入和滚动状态。
- 页面显示、滚动及尺寸变化后合并重绘内部控件，修复局部刷新遗漏。
- 移除脚本工作区的合并复选框和多余留白，自动处理空白输入、代码片段和已有脚本。
- 统一脚本切换标签与编辑区的圆角样式，修复矮窗口下编辑框底部被裁切的问题。
- 小屏幕和高缩放比例下保留脚本编辑高度，空间不足时滚动工作区，避免控件重叠。
- 紧凑导航显示完整的“Clash 接入”，按钮宽度同时计入图标和文字，避免标题裁切。
- 增加页面切换自然重绘、内嵌脚本工作区和矮窗口布局检查。
- 右上角分别显示整体连接失败和 UDP 连接失败，并同步错误原因及托盘图标。
- UDP 辅助程序完成握手后才确认启动成功；执行被拒绝时提示检查虚拟机安全授权，避免误报为普通超时。
- 托盘菜单跟随 Windows 明暗主题，统一菜单间距、分隔线和悬停高亮，并根据连接状态启用相应操作。

## 0.6.4 — Rust 转发内核与并发保护

- 使用 Rust 异步 SSH 内核，移除生产 SSH.NET 依赖；保留指纹校验、TCP 半关闭与界面重连逻辑。
- 自动部署 Linux x86_64/aarch64 静态 UDP 辅助程序，虚拟机无需 Python 或 Rust 运行环境。
- 内部端口使用随机凭据认证，父进程退出时回收转发资源。
- 并发上限默认 512，支持在设置中调整为 64～2048，下次连接生效。UDP 回复队列共享 16 MiB 字节预算。

- 分开限制转发连接与握手连接，为转发满载时的健康探测保留容量。
- 修复接入和关闭之间的竞争条件，以及 UDP 并发注册越过上限的问题。
- 统一健康检查时间参数，覆盖两类探测的耗时和调度余量。
- 校验上游 SOCKS 响应，拒绝错误协议头。
- 连接页显示当前转发数量和过载拒绝次数。日志页支持暂停显示，选中文字时暂停刷新。
- 限制日志单次刷新字符数，缓存规则编译结果，减少界面线程重复工作。
- 新增 512 和 2048 路本机双向数据校验、满载探测和接入关闭竞争测试。另通过真实 OpenSSH 验证 512/2048 条并发通道、UDP 最大数据报和退出回收。

## 0.6.3 — 显示修复与连接管理

- 恢复系统原生滚动，修复控件残影和错位；修复高 DPI 下的重复缩放。
- 新增界面缩放、字体、连接通知、日志和重连间隔设置。窗口及托盘图标显示连接状态。
- 在后台清理连接资源并独立执行健康检查，批量刷新有界日志队列。
- 脚本工作区整合到主窗口，支持完整脚本、自定义函数和代码片段，并检查语法。
- 修复健康检查的连接复用问题，记录转发连接结束原因。

- 安装包按架构拆分，不再捆绑运行时。卸载旧版前检查同架构的 .NET 8 桌面运行时；便携包保留运行时。

- 安装器在用户确认安装后处理版本升级。升级和安装期间显示平滑推进的进度条，并在进度条下方保留操作记录。
- 操作记录显示实际执行的命令、文件处理路径和快捷方式路径。升级期间持续读取旧版卸载日志，显示删除操作及错误信息。
- 卸载失败时停止安装并显示原因。确认安装前取消操作会保留旧版。
- 安装窗口采用 105% 的基础尺寸，缩小默认留白。操作记录使用横向和纵向滚动，保留完整路径。

## 0.6.2 — Remove obsolete DNS test scaffolding

- 删除没有入口调用、已由 TUN 端到端验收覆盖的独立 DNS 检查，以及脚本回归中不再使用的 Mihomo 配置输出。
- 保持 v0.6.1 的网络、配置和架构修复不变；继续通过同一发布验收流程。


## 0.6.1 — Transport reliability and maintainability

- 修复 TCP 单方向异常后另一方向等待造成的连接槽位泄漏，保留正常半关闭响应。
- 修复 UDP 单向收流仍在空闲期限后断开；DNS 改为有界后台解析，慢解析不再阻塞其他流量和探测。
- 配置修改先持久化候选值再发布，保存失败不再使未保存规则或信任信息进入活动状态。
- 提取可取消的 RelaySession，生产界面和 SSH 验收复用会话健康与 UDP 恢复逻辑；补充异常回归与生命周期说明。
- UI 验收在不可交互桌面明确报告 BLOCKED。


## 0.6.0 — Transparent UDP, recovery and compact controls

- Add bounded SOCKS5 UDP associations over a session-scoped SSH/Python bridge; retain the original TCP forwarding path.
- Independently check TCP/UDP health and resume the host's original rule matching through PASS when a transport fails, including after process termination.
- Add warning/recovery feedback, background reconnection and independent UDP recovery; manual disconnect remains stopped.
- Document and test the distinction between existing corporate DNS resolution and unsupported automatic UDP resolution of VM-only hosts aliases.
- Add configurable profile storage with safe migration, destination backups, persistence and explicit unavailable-path errors.
- Upgrade script composition to owned/user sections with managed-region conflict checks and migration from known v0.4.0–v0.5.0 output.
- Reduce content sizing to 80% and tighten whitespace; retain five-scale DPI checks and add the settings page.
- Build a compatible setup ICO, inspect the actual EXE and Shell icons, and label universal installers explicitly.
- Gate releases on Windows checks and Linux real TUN/SSH failover acceptance; retain native Windows UDP checks and structured evidence.

## 0.5.0 — Refined desktop workspace

- Replace the plain tabbed form with adaptive sidebar navigation, grouped cards and a consistent graphite, off-white and muted-green visual language.
- Add rounded input frames around native controls, primary/secondary action styles, keyboard focus feedback and a persistent connection badge.
- Switch navigation to a top bar in narrow windows without removing destinations or shrinking text; retain 12-point body and editor fonts.
- Reorganize connection settings, disclose private-key fields only when needed, and show normalized rule counts and unsaved changes inline.
- Present Clash integration as three steps with expandable TUN instructions and a copy action for the VM route exclusion.
- Rebuild the script dialog as a responsive two-column workspace with vertically stacked editors on narrow windows and a separate action row.
- Add log copy, explicit export and clear operations; clearing logs does not alter connection settings.
- Fix fractional-DPI height rounding in borderless native inputs using actual font metrics and reduce secondary chrome in compact windows.
- Expand 100/125/150/175/200 DPI regression coverage to navigation, private-key disclosure, rule summaries, expanded guidance and usable editor viewport height.
- Preserve SSH authentication, current-user secret storage, local rule providers and managed TUN script behaviour. Native high-DPI and physical multi-monitor acceptance remain separately documented.

## 0.4.3 — Script preview rendering acceptance

- Normalize CR, LF and CRLF to Windows hard line breaks when displaying imported or generated scripts.
- Use the same preview preparation path for normal operation and tests; validate native edit-control line counts and lossless source extraction.
- Reset preview caret and report a generated result after stale-output invalidation is resolved.
- Retain the full 100/125/150/175/200 DPI matrix and the native-monitor validation guard introduced in 0.4.2.

## 0.4.2 — DPI acceptance and high-scale layout fixes

- Add independent 100%, 125%, 150%, 175% and 200% layout acceptance with DPI message injection, text metrics, caption-fit, overlap, scroll reachability, focus and repeated DPI round-trip assertions.
- Add strict native monitor checks and a negative guard that refuses native 200% certification on a 96-DPI desktop. Native and injected evidence remain distinct.
- Keep explicit title and editor fonts proportional to the form font across DPI changes.
- Fix inaccessible rule-save controls caused by fill docking inside a scrollable tab; wrap credential guidance separately.
- Produce exact requested icon dimensions, including 28/56 pixels at 175%, and update the script dialog icon on DPI changes.
- Capture painted desktop windows rather than un-clipped DrawToBitmap reconstructions; retain JSON metrics and screenshots under artifacts/checks.

## 0.4.1 — Clash settings compatibility and release maintenance

- Publish the managed TUN settings fix previously available only in main and Actions artifacts.
- Preserve incoming Clash Verge GUI-owned TUN values, including when importing legacy scripts that mutate arrays or replace the TUN object.
- Keep unrelated user configuration and custom TUN options intact; configure DNS hijacking and VM route exclusions in the Clash settings interface.
- Add regression coverage for managed TUN fields and clarify script regeneration after upgrading.
- Upgrade official workflow actions to Node.js 24 runtimes and pin their release commits; keep Node.js 22 for project script tests.
- Synchronize application, installer and manifest versions and show explicit published/skipped release summaries.

## 0.4.0 — Readable desktop UI and complete script generation

- Add one integrated application icon, generated from a versioned vector source in nine sizes.
- Use DPI scaling, point-sized fonts and wrapping/scrolling layouts for laptop displays.
- Add optional existing-script import, full merged preview, one-click generation/copy and script export.
- Preserve original JavaScript scope and entry-point behavior; replace generated wrappers on regeneration.
- Keep connected rules active when copying a script and isolate diagnostic user data.
- Expand script, icon and synthetic scaled-layout regression checks.

## 0.3.0 — Windows workflow and Clash integration

- Replace the loopback HTTP rule provider with a local file provider.
- Disable forwarding rules while the SSH tunnel is disconnected or the application exits.
- Store settings in Documents/Host2VMRelay and migrate existing LocalAppData settings.
- Route build output to artifacts/ and add a root PowerShell build command.
- Uninstall an existing installation before installing a new version.
- Standardize the visible application name as Host2VMRelay.

## 0.2.1 — Project cleanup

- Organize source, tests, documentation and packaging into dedicated directories.
- Standardize application identifiers and remove prototype configuration migration.
- Use Host2VMRelay credential encryption parameters; previously saved secrets must be entered again.
- Add a direct installer download link and derive package versions from the project.

## 0.2.0 — Offline installer

- Introduce Host2VMRelay with an offline Windows installer.
- Bundle the complete .NET desktop runtime and managed dependencies for x64, x86 and ARM64.
- Add a universal per-user installer with architecture detection, shortcuts and uninstall support.
- Fix publishing options in a versioned profile; keep trimming and Native AOT disabled for WinForms compatibility.
- Include source build scripts, third-party notices, portable packages and SHA-256 checksums.

## 0.1.0 — Initial release

- Windows GUI for SSH SOCKS5 forwarding, tray operation and reconnect.
- Windows DPAPI encrypted credential storage and private-key authentication.
- Editable domain, IP and CIDR rules served locally to Clash Verge Rev.
- Fake-IP configuration for remote DNS through SSH forwarding.
- Fix: generate the TUN bypass from the configured VM IP instead of a fixed subnet.
- Fix: restore connection controls after a disconnect when reconnect is disabled.
- Fix: disable the private-key browse control while connected.
- Self-tests use an ephemeral port so they do not conflict with the running application.
- Public source defaults contain no personal connection details; diagnostic URL is configurable.
