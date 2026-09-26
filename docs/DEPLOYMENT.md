# 安装与运行依赖

## 面向用户

运行 `Host2VMRelay-0.3.0-Setup.exe`。这是离线安装包，自动选择 x64、x86 或 ARM64 原生程序，安装到当前 Windows 用户的程序目录，提供开始菜单入口、可选桌面快捷方式及卸载入口。无需管理员权限，不会联网下载运行时，也不会在安装时编译。

每种架构的程序都包含 .NET 8 Desktop Runtime、SSH.NET、BouncyCastle 和其他托管依赖。无需预装 .NET、Python、Node.js、Visual Studio 或 OpenSSH 客户端。便携 ZIP 解压后也可直接运行。

发布使用 .NET 的 **self-contained + single-file**，不是把 WinForms 强制转换成完全静态的原生机器码。原生运行时随 EXE 打包，启动时会解压到用户临时目录的 `.net` 缓存；因此用户临时目录必须可写。WinForms 不启用裁剪或实验性的 Native AOT，以保留反射、GUI 和 SSH 算法所需代码。

## Windows 范围

- 目标：Windows 10（1607 / build 14393 或更新）及 Windows 11 的桌面环境；建议使用仍在维护并安装系统更新的版本。
- x64、x86、ARM64 分别打包。安装器根据操作系统架构选择，无需用户判断。
- 不支持 Windows 7/8/8.1、Windows Server Core 或非 Windows 客户端；远端主机只需提供 SSH TCP 转发服务。
- 基础 Win32、系统加密 API、系统证书库和系统网络栈仍由 Windows 提供，不能脱离操作系统打包。
- 当前环境可实测 Windows 11 x64 及其 x86 子系统。ARM64 包可以构建和检查架构，但需 ARM64 设备补充验收；不能把架构生成成功等同于全平台实测。

Clash Verge/Mihomo 和虚拟机是外部网络环境，不随应用安装，不会替用户改动它们。要实现透明分流仍需运行 Clash TUN 并导入应用生成的脚本。

## 配置与升级

配置保存在当前用户“文档\\Host2VMRelay”目录；目录不存在时程序自动创建。首次启动新版本时，如果该目录还没有配置而 `%LOCALAPPDATA%\\Host2VMRelay\\settings.json` 存在，会自动复制旧配置。卸载仅删除程序文件，不删除用户配置。跨用户迁移密码密文无效，需要重新输入。

自包含运行时不会自动使用系统上较新的 .NET。维护者应使用更新后的 .NET SDK 重新发布，及时纳入运行时安全修复；.NET 8 的支持期结束前需要升级到后续 LTS。安装包尚未使用代码签名证书签名。

## 从源码构建

日常编译只需要 .NET 8 SDK；制作安装包还需要 Inno Setup 7。首次 NuGet 还原需要网络。所有本地构建与发布产物统一写入仓库根目录的 `artifacts/`。安装后的用户电脑不需要这些工具。

```powershell
.\build.ps1
.\build.ps1 -Sync -Run
.\build.ps1 -Package -Iscc 'C:\Program Files\Inno Setup 7\ISCC.exe'
```

也可用 `-DotNet` 指定 SDK 路径，用 `-OutputDirectory` 指定输出目录；`-SkipInstaller` 仅生成三种架构的便携包。发布选项固定在 `src/Properties/PublishProfiles/Standalone.pubxml` 中。

GitHub Actions 在 main 分支构建成功后，为尚未发布的项目版本创建Release，并上传安装包、便携包及校验文件。已存在的同版本 Release 不会被覆盖；发布新版本前需更新项目和安装脚本中的版本号。

参考：[.NET 单文件部署](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)、[Windows 支持范围](https://learn.microsoft.com/en-us/dotnet/core/install/windows)。
