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

## 职责与并发

详见 architecture/FORWARDING_ENGINE.md。界面仅管理连接意图、主机信任、设置和显示；RelaySession 管理连接资源与恢复；RelayCoreProcess 管理 Rust 子进程及控制管道。UDP 协议仍保持版本 1，历史 Python 组件移入 tests/fixtures 作为对照，不进入产品。

日志入口限制为 1000 条待显示记录，每次 UI 刷新最多 100 条及 16 KiB，丢弃情况会明确提示。网络任务不得同步等待界面日志更新。取消、资源清理与连接准入必须可并发调用，不能持有状态锁等待网络或进程退出。
