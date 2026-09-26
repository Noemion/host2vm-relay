# 开发指南

## 目录结构

```text
src/                         Windows 桌面应用（唯一项目）
  Host2VMRelay.csproj         项目与构建入口
  Program.cs                 程序启动入口
  Configuration/             配置读取与保存
  Security/                  Windows DPAPI 密码加密
  Networking/                规则解析
  Integration/               Clash 扩展脚本与本地规则文件
  UI/                        主窗口与交互
  Diagnostics/               发行程序内置自检
  Properties/PublishProfiles/ 自包含发布配置
tests/                       Clash 脚本与 DNS 集成检查
scripts/                     构建和打包脚本
packaging/                   Windows 安装器定义
docs/                        开发、部署和验证说明
licenses/                    随安装包分发的第三方许可
artifacts/                   所有本地构建、检查、发布产物，不提交到 Git
```

## 构建与运行

在 Windows 上安装 .NET 8 SDK，从仓库根目录执行：

```powershell
.\build.ps1

# 拉取最新代码后编译并运行
.\build.ps1 -Sync -Run

# 生成便携包和安装包
.\build.ps1 -Package -Iscc 'C:\\Program Files\\Inno Setup 7\\ISCC.exe'
```

.NET 项目入口为 `src/Host2VMRelay.csproj`，可直接用 Visual Studio 2022 打开，并安装“.NET 桌面开发”工作负载。

## 检查

以下命令在仓库根目录执行。脚本检查需要 Node.js，仅开发时使用。

```powershell
New-Item -ItemType Directory -Force artifacts/checks
$app = '.\src\bin\Release\net8.0-windows\Host2VMRelay.exe'
$result = Join-Path $PWD 'artifacts/checks/self-test.txt'
$check = Start-Process $app -ArgumentList "--self-test `"$result`"" -Wait -PassThru
Get-Content $result
if ($check.ExitCode -ne 0) { throw 'Self-test failed' }
node tests/test-script.cjs artifacts/checks/mihomo.json
```

程序保留 `--self-test` 诊断入口，用于验证实际发行 EXE 的规则、加密存储与 Clash 本地规则文件，检查逻辑放在 `Diagnostics/SelfTest.cs`。

可选 DNS 集成检查：用独立 Mihomo 实例加载生成的 `mihomo.json`，监听其中指定的本机 DNS 端口 `10553`，再运行 `node tests/test-dns.cjs`。不要将测试配置覆盖到日常使用的 Clash 中。

## 制作安装包

```powershell
.\scripts\build-release.ps1 -Iscc 'C:\Program Files\Inno Setup 7\ISCC.exe'
```

输出位于 `artifacts/release/`。详细参数、运行依赖与支持范围见 [部署说明](DEPLOYMENT.md)，已有测试结果见 [验证记录](VALIDATION.md)。

GitHub Actions 使用同一套项目、检查脚本和打包入口。同一正式版本的 Release 附件保留不变；发布新版本时更新版本号和发布说明。
