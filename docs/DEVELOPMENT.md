# 开发与测试

## 环境

- Windows：.NET 8 SDK、Rust（版本固定在 rust-toolchain.toml）、MSVC C++ 编译工具及 Windows SDK。构建 ARM64 Windows 内核还需要 MSVC ARM64 编译工具。
- Linux/WSL：Rust、readelf；完整 SSH 验收需要 OpenSSH 和 Python，ARM64 运行检查使用 QEMU。
- 界面和脚本检查：Node.js；安装包：Inno Setup 7。

运行产品不需要 Rust 编译器或 Python。仓库保存在 Windows 文件系统时，可将 WSL 的 CARGO_TARGET_DIR 放在 Linux 文件系统以降低编译读写开销。

## 构建

先构建 Linux 静态组件：

```sh
rustup target add x86_64-unknown-linux-musl aarch64-unknown-linux-musl
bash scripts/build-native-linux.sh --with-core
```

再在 Windows 构建所需内核和 .NET 界面：

```powershell
rustup target add x86_64-pc-windows-msvc i686-pc-windows-msvc aarch64-pc-windows-msvc
./scripts/build-native.ps1
./build.ps1 -Test
./scripts/build-release.ps1
```

仅调试 x64 可使用 `./scripts/build-native.ps1 -Architectures x64`。原生组件位于 artifacts/native/<rid>，构建时自动复制到 .NET 输出目录的 native 子目录。发布缺少原生清单或 Linux 组件时直接失败。便携 ZIP 和安装包必须一起携带 native 目录。

Cargo.lock 固定传递依赖；使用 --locked 构建。更新依赖后运行 scripts/collect-rust-licenses.py，将第三方许可证随发行包提供。许可证清单不代替依赖漏洞审计。

## 回归

```sh
cargo fmt --manifest-path native/Cargo.toml --all -- --check
cargo clippy --manifest-path native/Cargo.toml --locked --all-targets -- -D warnings
cargo test --manifest-path native/Cargo.toml --locked
python3 tests/test-native-agent.py artifacts/native/agents/h2vm-agent-linux-x64
python3 tests/test-native-agent.py artifacts/native/agents/h2vm-agent-linux-arm64 --runner qemu-aarch64
sudo python3 tests/test-rust-engine.py
```

```powershell
dotnet run --project tests/TransportHarness -c Release -- --local-check artifacts/checks/windows-transport.json
```

Linux SSH 测试创建临时账号、主机密钥和 SSH 服务，并在结束后清理，不连接用户虚拟机。TUN 测试 tests/test-transparent-network.py 还需 root、网络命名空间和 Node.js，应只在可销毁的 CI 环境运行。该测试校验原始目标地址、企业 DNS、TCP/UDP 分流及断线回退。

本地界面测试使用 tests/run-startup-desktop.ps1 的隔离桌面，不切换用户前台窗口。安装程序由用户手动验收，开发检查不运行安装或升级夹具。

安装界面使用 Inno Setup 的标准现代向导。准备阶段通过 `CreateOutputProgressPage` 显示进度，并在 `finally` 中恢复标准页面；操作记录随页面迁移。安装阶段的总进度条沿用原生进度条的位置和尺寸，独立于安装引擎的字节计数器，避免卸载与安装交接时进度倒退。不要重新添加覆盖窗口的面板或单独维护字体、页眉和按钮布局。

运行中应用通过 Inno Setup 的 Restart Manager 页面关闭。`PrepareToInstall` 只检查运行时并记录旧版路径；`[Dirs]` 的 `BeforeInstall` 在自动关闭完成后、生成新卸载数据前移除旧版。不要把卸载移回准备阶段，也不要移到 `[Files]` 回调，否则会分别早于关闭或晚于新卸载数据创建。独立卸载使用 `InitializeUninstall` 保留运行保护，安装启动阶段不再用 `AppMutex` 拦截。

测试以实际行为和故障边界为准，不以断言数量为目标。首次页面布局单独检查，避免反复刷新掩盖错误；各缩放比例保留几何检查，跨 DPI 只执行一次往返。页面检查聚焦当前内容，公共导航和窗口外框不在每页重复遍历。

图标文件检查所有编码尺寸，运行时仅验证小托盘图标和高 DPI 窗口图标的状态显示。源码中是否包含某段设置字符串不作为运行正确的证据。Linux 辅助程序的协议测试在原生构建任务中执行一次；Windows、ARM64、真实 TUN、并发和断线恢复仍分别验证。

版本更新检查使用模拟 HTTP 响应，覆盖版本比较、架构选择、来源与摘要校验、损坏下载、取消和缓存清理。安装程序启动使用模拟回调，测试不执行安装包。实际发布下载可单独验证，但仍不运行安装程序。

## 持续集成

Actions 先比较最近一次成功构建与当前提交，再选择检查。纯文档修改不编译；安装器修改保留应用自检和打包检查，跳过无关的界面与网络回归。仅修改发布版本号不会被当作依赖变化。未知文件、构建脚本、测试或流水线变更，以及无法确定比较基线时，执行全量检查。手动运行流水线也执行全量检查。

Windows 的 x64、x86、ARM64 转发程序与 Linux 辅助程序并行构建，完成后统一生成分发目录和 SHA-256 清单。Rust 缓存按工具链、依赖和目标架构区分，仍从当前源码构建程序；.NET 复用 NuGet 缓存。首次运行需要建立缓存，不能将首次耗时当作缓存后的速度。

发布前保留各架构打包、安装包图标检查和可执行产物自检。已存在的正式版本不会重复发布；未变更打包输入且不发布新版本时，不生成全部安装包。每次执行的检查范围会写入 Actions 摘要。

## 职责与并发

详见 architecture/FORWARDING_ENGINE.md。界面仅管理连接意图、主机信任、设置和显示；RelaySession 管理连接资源与恢复；RelayCoreProcess 管理 Rust 子进程及控制管道。UDP 协议仍保持版本 1，历史 Python 组件移入 tests/fixtures 作为对照，不进入产品。

日志入口限制为 1000 条待显示记录，每次 UI 刷新最多 100 条及 16 KiB，丢弃情况会明确提示。网络任务不得同步等待界面日志更新。取消、资源清理与连接准入必须可并发调用，不能持有状态锁等待网络或进程退出。

版本更新由 `ReleaseUpdater` 读取正式发布信息并流式下载，`UpdateCache` 管理临时文件。界面负责操作反馈和取消。通过校验的安装包保持只读文件句柄，防止启动前被替换。程序关闭窗口并释放单实例互斥量后启动安装器，失败时重新打开主窗口。缓存清理不递归，也不跟随下载目录内的符号链接。
