# 开发指南

## Windows 11 快速构建

编译需要 .NET 8 SDK；执行脚本测试还需要 Node.js。制作安装包需要 Inno Setup 7。使用 Windows PowerShell 5.1 或 PowerShell 7，在仓库根目录运行：

```powershell
.\build.ps1                    # 编译
.\build.ps1 -Sync -Run         # git pull --ff-only、编译、运行
.\build.ps1 -Test              # 编译、自检、脚本回归、缩放布局截图
.\build.ps1 -Portable          # 三种架构的便携包，不需要 Inno Setup
.\build.ps1 -Package           # 便携包与安装包，自动寻找 Inno Setup 7
.\build.ps1 -Package -Install  # 打包后安装，保留用户配置
```

也可显式传入 `-Iscc 'C:\Program Files\Inno Setup 7\ISCC.exe'`。首次克隆：

```powershell
git clone https://github.com/Noemion/host2vm-relay.git
cd host2vm-relay
.\build.ps1 -Test
```

执行策略阻止脚本时，可仅对当前调用使用：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

## 输出目录

```text
artifacts/
  assets/       从 SVG 生成的多尺寸 ICO
  build/bin/    常规编译结果
  build/obj/    中间文件
  checks/       自检结果、实际生成的脚本、模拟缩放截图
  publish/      win-x64 / win-x86 / win-arm64 自包含程序
  release/      安装包、便携包、校验值
```

`Directory.Build.props` 在项目加载早期指定输出路径，并排除旧的 bin/obj 残留，避免旧编译文件被重复编译。`src/Assets/Host2VMRelay.svg` 是图标源文件；`scripts/build-icon.ps1` 使用 Windows 自带的 System.Drawing 生成 ICO，不依赖额外图形工具。

## 界面与脚本

`UI/MainForm.cs` 管理主窗口和生命周期；Layout、Networking、Diagnostics 分别保留布局、连接和测试逻辑。所有窗体使用 96 DPI 设计基准与 DPI 自动缩放，字体使用 point，不再固定为像素。窗口按工作区限制外框尺寸，长内容保持可滚动。

`ScriptComposer` 只包装与提取 JavaScript 文本，不执行脚本。原脚本置于独立词法作用域，实际执行由 Clash 的 JavaScript 引擎完成，然后接入 Host2VMRelay。生成格式保存 UTF-16 字符长度，用于再次导入时准确提取原脚本；用户直接修改生成文件中的原始内容导致长度变化时，需要重新提供原始脚本。不是通用 JavaScript 语法检查器或 YAML 合并器。

自检导出 `artifacts/checks/script-cases.json`，Node.js 回归测试执行 C# 生成器的真实产物。测试覆盖辅助函数、箭头入口、Unicode、返回新对象、原地修改、早返回、异常传播、旧节点名迁移、DNS 模式和重复生成。

`--smoke --ui-scale=150 <截图路径>` 使用独立配置目录和规则文件，不能读取或迁移用户账号、密码，也不修改用户正在使用的 Clash 规则。缩放参数仅用于模拟布局，不声称改变了 Windows 系统 DPI。真实的 125%/150%/200% 及跨屏切换仍需要在目标设备验收。
