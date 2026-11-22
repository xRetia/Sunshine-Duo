# AGENTS.md — Sunshine-Duo 项目铁律

## 提权（铁律，任何情况下必须遵守）

所有需要提权（UAC/管理员）的操作，一律使用 `gsudo64.exe`，并先开启缓存：

```powershell
gsudo64 cache on -p 0 -d -1   # 开缓存：-p 0 允许任意进程复用，-d -1 直到注销前有效
gsudo64 <提权命令>             # 之后所有 gsudo64 提权不再弹 UAC
```

- 禁止直接运行需要管理员权限的命令（sc.exe、reg.exe、Stop-Service、icacls、takeown、taskkill 系统进程等）。
- 禁止使用不带缓存的 gsudo/gsudo64 调用——那会弹 UAC 打断用户。
- 缓存会话在开机后需重新开启；发现 gsudo 卡住等待 UAC，先停手，改用 `gsudo64 cache on -p 0 -d -1` 重建缓存（该命令本身若弹 UAC，属一次性成本，等用户批准）。
- 用完不需要时可用 `gsudo64 -k` 撤销全部缓存会话。

## 目录结构（源码与运行时分离）

本仓库同时是可分发的运行时目录（绿色免安装 ZIP 解压即用），根目录的运行时布局不能随意变动：

```
src\               所有自有源码（唯一放源码的地方）
  trayapp\         SunshineDuoTray 托盘（C#/WinForms，dotnet publish 后部署到 bin\SunshineDuoTray）
  SunshineDuoCore\ SunshineDuoCore 守护服务源码（Python + PyInstaller 打包脚本；产物部署到 bin\SunshineDuoCore）
scripts\           安装/卸载/驱动/防火墙脚本（运行时载荷，不是源码，随 ZIP 分发，不进 src\）
sunshine.exe       Apollo 本体 + zlib1.dll
assets\            WebUI、着色器、图片（sunshine.exe 运行时按自身目录定位，禁止移动）
config\            运行时配置（本机生成的 conf/state/credentials 不入库）
drivers\           SudoVDA 驱动二进制（载荷，不改）
tools\             第三方运行时工具（rdpWrapper/GameManager 等）
bin\               运行时部署目录：bin\SunshineDuoTray（托盘）、bin\SunshineDuoCore（核心服务 exe + _internal + 日志）
Links\             用户游戏快捷方式目录
setup.bat          一键安装入口（双击它，调用 scripts\sunshine-setup.ps1）
docs\plan.md      设计文档（历史规划，路径为重构前布局）
```

- 新增自有代码一律放进 `src\` 对应子目录；`scripts\`、`assets\`、`tools\`、`bin\` 是运行时载荷目录，不放源码。
- 构建产物只落盘到部署目录或 gitignore 目录：托盘 → `bin\SunshineDuoTray\`（源码树内 bin/obj/publish 已 ignore），SunshineDuoCore → `bin\SunshineDuoCore\`；构建现场固定在 `src\SunshineDuoCore\.duocore-build\`。
- `config\` 只有 `apps.json` 入库，其余（conf/state/credentials/covers/日志）均为本机生成，禁止提交。

## 路径推导（铁律）

- 仓库内路径一律动态推导，禁止把 `D:\Library\Sunshine` 等绝对路径写死到代码/脚本里（trayapp、SunshineDuoCore、setup 同此要求）。
- 各处推导基准（移动目录时必须同步修正）：
  - `scripts\*.bat` / `autostart-hidden.vbs`：`%~dp0..`（父目录）= 仓库根；
  - `scripts\sunshine-setup.ps1`：`$Root = Split-Path -Parent $PSScriptRoot`；
  - `src\SunshineDuoCore\build.ps1`：仓库根 = `$PSScriptRoot` 上两级，部署目标 = `仓库根\bin\SunshineDuoCore`；
  - `src\SunshineDuoCore\gen_hook_4_46.ps1`：仓库根 = `$PSScriptRoot` 上三级；
  - `src\trayapp`：运行期由 `Paths.cs` 从 exe 向上找 `sunshine.exe` 定根；csproj 图标用 `..\..\assets\...`（依赖"源码恰好在根下两级"这一深度）。
- 已安装机器上服务注册使用动态路径（`bin\SunshineDuoCore\build.info` 的 `style: dynamic-path`），整个目录可整体移动。
