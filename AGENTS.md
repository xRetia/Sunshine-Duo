# AGENTS.md — Sunshine-Duo 项目铁律与工作规范

## 项目一句话

萌远控Duo（Sunshine-Duo）：一台 Windows 电脑当两台用——基于 Apollo（sunshine.exe）+ RDP Wrapper + SudoVDA 虚拟显示 + ViGEmBus 虚拟手柄，实现主会话（本机键鼠）与游戏会话（Moonlight 串流）双会话隔离并行。本仓库同时是**可分发的运行时目录**（绿色免安装 ZIP 解压即用），根目录运行时布局不能随意变动。

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

```
src\               所有自有源码（唯一放源码的地方）
  SetupWizard\         setup.exe 安装/更新向导（C#/WinForms，dotnet publish 后部署到根目录 setup.exe）
  SunshineDuoUI\         SunshineDuoTray 托盘（C#/WinForms + WebView2 WebUI，dotnet publish 后部署到 bin\SunshineDuoTray）
  SunshineDuoCore\ SunshineDuoCore 守护服务源码（Python + PyInstaller 打包脚本；产物部署到 bin\SunshineDuoCore）
  GameManager\         游戏启动器源码（WPF，单实例 + GameManagerService SYSTEM 服务；发布到 tools\GameManager.exe）
scripts\           安装/卸载/驱动/防火墙脚本（运行时载荷，不是源码，随 ZIP 分发，不进 src\）
setup.exe          安装/更新向导（双击即用；托盘「检查更新」也经它执行更新）
sunshine.exe       Apollo 本体 + zlib1.dll
assets\            WebUI、着色器、图片（sunshine.exe 运行时按自身目录定位，禁止移动）
config\            运行时配置（本机生成的 conf/state/credentials 不入库）
drivers\           SudoVDA 驱动二进制（载荷，不改）
tools\             第三方运行时工具（rdpWrapper/GameManager/easytier 等）
bin\               运行时部署目录：bin\SunshineDuoTray（托盘）、bin\SunshineDuoCore（核心服务 exe + _internal + 日志）
Links\             用户游戏快捷方式目录（GameManager 游戏菜单的数据源）
docs\plan.md      设计文档（历史规划，路径为重构前布局）
```

- 新增自有代码一律放进 `src\` 对应子目录；`scripts\`、`assets\`、`tools\`、`bin\`、`drivers\` 是运行时载荷目录，不放源码。
- 构建产物只落盘到部署目录或 gitignore 目录：托盘 → `bin\SunshineDuoTray\`（源码树内 bin/obj/publish 已 ignore），SunshineDuoCore → `bin\SunshineDuoCore\`；构建现场固定在 `src\SunshineDuoCore\.duocore-build\`。
- `config\` 只有 `apps.json` 入库，其余（conf/state/credentials/covers/日志）均为本机生成，禁止提交。
- 密钥与凭据严禁入库：`config\credentials\`、`bin\SunshineDuoCore\rdp.cred` 等（见 .gitignore）。

## 组件速览

| 组件 | 技术栈 | 作用 |
| --- | --- | --- |
| SunshineDuoCore | Python 3.12（纯 ctypes SCM，不用 pywin32）+ PyInstaller onedir | SYSTEM 守护服务：RDP keepalive（aardwolf 连 127.0.0.2 防会话冻结）、GameViewer hook 守护（内嵌 inject_watch.ps1）、显示保活、按需拉起 easytier 组网 |
| SunshineDuoTray | C#/WinForms（net9.0-windows）+ WebView2 WebUI（src\SunshineDuoUI\webui\） | 托盘：服务控制、WebUI 入口、检查更新（经 setup.exe） |
| SetupWizard | C#/WinForms（net9.0-windows，单文件自包含） | 安装/卸载/更新向导，驱动与防火墙脚本的实际执行者 |
| GameManager | C#/WPF（net9.0-windows，framework-dependent） | 多座位游戏启动器；`Links\*.lnk` 平铺列表，经 steam:// 协议 + GameManagerService 按用户会话拉起 UI |
| Apollo | 第三方（sunshine.exe，禁止改） | 串流服务端本体，两个实例分别服务主会话/游戏会话 |

---

## SunshineDuoCore 详细构造（src\SunshineDuoCore\）

全部逻辑在单文件 `duocore.py`（约 3900 行），SCM 服务管道用裸 ctypes，**禁止 pywin32**（构建环境无关，duocore.spec 有注释说明）。

### 文件清单

| 文件 | 职责 |
| --- | --- |
| `duocore.py` | 全部逻辑：SCM 管道、RDP keepalive、Apollo 双会话守护、hook 守护、显示拓扑、easytier、控制管道 |
| `inject_watch.ps1` | GameViewer hook 注入模板（`__WG_DIR__` 占位符），构建期 UTF-16LE base64 内嵌 |
| `build.ps1` | PyInstaller onedir 打包 + `-Deploy` 部署 + 写 `build.info` |
| `duocore.spec` | 打包图：PIL .pyd 显式收集、Cryptodome 原样随包、runtime hook |
| `hook-runtime-cryptodome.py` | PyInstaller runtime hook：把 `rdp-extra` 插到 sys.path 前面使 Cryptodome 优先 |
| `gen_hook.ps1` / `gen_hook_4_46.ps1` | 两代 GameViewer hook 的汇编生成脚本（mingw binutils `as`/`ld`） |
| `verify.ps1` | 调试用：验证注入事件与会话归属（**会强杀 GameViewerServer**，正常流程禁跑） |
| `requirements-build.txt` / `requirements-rdp.txt` | 构建依赖 / aardwolf 运行库清单（pip --target 装到 `.duocore-build\`） |

### 入口与运行模式

`main()` 分派：`--setup`（交互式写 config + DPAPI 凭据 + icacls 收紧 ACL）、`--displaycheck`（干跑打印显示拓扑决策）、`--display-worker`（会话内显示工作进程）、`--test`（aardwolf 单次 RDP 连接并保持 20s）、`--console`（前台 tick 循环）、无参 = SCM 服务。

- 服务名 `SunshineDuoCore`，`SERVICE_WIN32_OWN_PROCESS`；`StartServiceCtrlDispatcherW` + `RegisterServiceCtrlHandlerExW` + `SetServiceStatus`，控制码只处理 STOP（置 STOP_PENDING + SetEvent 停止事件）。
- 主循环：`WaitForSingleObject(stop_event, interval)` 超时即 `tick()`；停止时释放显示保活、写 `display-worker.stop`。
- 服务注册由 SetupWizard 完成：`sc create ... start= auto` + failure 重启策略（Ops.cs）。

### 子系统

- **SCM 管道**：advapi32（dispatcher/handler/SetServiceStatus/token 系列/CreateProcessAsUserW）+ kernel32（event/wait/process）+ userenv（环境块）。SYSTEM token 与 Game 用户 token（`WTSQueryUserToken` + `TokenLinkedToken` 提升为管理员）分别用于 console/game 会话拉进程。
- **RDP keepalive**：独立 daemon 线程跑 asyncio，aardwolf 无头 RDP 客户端连 `rdp+ntlm-password://<user>:<pwd>@127.0.0.2`（默认 server=127.0.0.2，loopback 别名，不顶用户自己的 MSTSC）。目的：让 Game 会话保持 Active、显示管线不冻结。RDP 单会话——用户 MSTSC 接管后 keepalive 只待命（连接读协程结束即 terminate + 置 disconnected），用户退出后状态回 Disconnected 才重连；`recycle_minutes`（默认 30）主动回收压内存；`_drain_loop` 持续消费输出队列防泄漏。`auto_logon` 默认 false，无 Game 会话时保持 idle。显示保活：会话 Active 且开关打开时持 `SetThreadExecutionState(ES_CONTINUOUS|ES_DISPLAY_REQUIRED)`。
- **GameViewer hook 守护**：`run_ps_guard()` 把 `PS_GUARD_B64` 解码、`__WG_DIR__` 替换为自身目录，`powershell -EncodedCommand` 起单实例子进程。PS 侧（inject_watch.ps1）：读 config 取 `gameviewer_versions` → 解析 GameViewerService exe 路径 → **版本门控**（FileVersion 与通配符 `-like` 匹配，列表为空/缺失即拒绝注入）→ 查 `Global\gv_hook_injected` 事件防重复 → `VirtualAllocEx`+`CreateRemoteThread(LoadLibraryA)` 注入 → **自愈**：GameViewerServer 不在 console 会话则强杀让服务重启到 console。DLL 选择：仅 `4.0.*`→`gv_hook.4.0.dll`、`4.46.*`→`gv_hook.4.46.dll`，缺对应版本二进制**绝不**注入另一代。
- **显示模式工作进程**：独立进程。服务每 tick 检查 `display-worker.json` 心跳（<10s 健康，>60s 判卡死 kill）后经 SYSTEM token + CreateProcessAsUserW 拉到 console 会话跑同一 exe `--display-worker`。文件协议：`display-mode-request.json`（`{id,mode,ts}`）→ worker 按 id 去重、应用拓扑（CCD `SetDisplayConfig`）、写 `display-mode-result.json`、**成功后删除请求文件**（否则 worker 重启会重复应用闪屏）；`display-worker.stop` 为停止信号；`display-disabled.json` 记被 detach 物理屏的原始 DEVMODE。防重复拉起：Global 互斥体 + 会话进程普查 + 冷却 + 滑动窗口熔断。
- **easytier 组网**：`config.json` 的 `easytier` 节（enabled/network/secret/server_mode/peers/ipv4/tcp_hole_punch/extra_args/exe）。每 tick 保活；按**完整映像路径**找已有实例（避免误杀用户自建的），存在则 adopt；stdout/stderr 重定向到 `easytier.out`（>5MB 先删，即子进程日志）；状态查询走 `easytier-cli.exe -o json node` 缓存 4s。
- **Apollo 双会话守护**：服务**管** sunshine 进程。`repo_root()` 从自身目录向上找 `sunshine.exe` 定根（6 层兜底，兼容 bin\ 布局）。console 会话用 SYSTEM token，game 会话用 Game 用户提升 token 打入目标会话 `CreateProcessAsUserW`；每会话 20s 拉起限速（ctrl pipe `restart_apollo` force 可绕过）。每 tick 修 Apollo state 文件的字符串化 bool/num（Apollo 0.4.6 issue #835，不修启动即崩）、强制 `always_use_virtual_display`（console 按显示模式，game 恒 false；该字段是 per-device state 而非 conf 键，WebUI 会覆盖，必须每 tick 重写）。

### 配置与状态文件

- `config.json`（exe 旁，每 tick 热加载；utf-8-sig）：`username`（Game 用户）、`password_mode=dpapi`/`password_plain`、`cred_path`、`server=127.0.0.2`、`width/height`、`keepalive/guard_uu/guard_apollo/guard_apollo_console`、`console_conf_path`/`game_conf_path`（相对路径按 repo_root 展开）、`display_keepalive=true`、`interval=60`、`recycle_minutes=30`、`gameviewer_versions=['4.0.*','4.46.*']`、`auto_logon=false`、`log_max_kb=100`、`log_keep=4`、`pair_watch=true`、`log_level='warn'`、`easytier{...}`、热键开关（`win_from_ralt` 等）、`hide_cursor_on_stream`。运行期追加键：`default_display_mode`、`display_exclusive`、`sudovda_resolutions`。
- `rdp.cred`：DPAPI（CryptProtectData）二进制 blob；`--setup` 写后 `icacls /inheritance:r /grant SYSTEM:F /grant Administrators:F`。
- 其余状态文件：`display-worker.json`（心跳）、`display-worker.stop`、`display-mode-request/result.json`、`display-disabled.json`、`cursor-hidden.marker`、`easytier.out`。

### 日志与控制接口

- `watch.log`（exe 旁）：Python 侧负责轮转（超 `log_max_kb` 改名 `watch-YYYYMMDD_HHMMSS.log`，保留 `log_keep` 份）；PS 侧只 `Add-Content` 追加并自读 `log_level` 决定详略，**禁止 Clear-Content**（会互删对方刚写的内容）。
- 控制命名管道 `\\.\pipe\SunshineDuoCore-ctrl`：字节流双工，每连接一条 JSON 行请求/一行 JSON 响应；SDDL 放行 Everyone+SYSTEM 供非提权托盘连接。命令：`ping/tick/restart_apollo/set_display_mode/set_hotkeys/set_hide_cursor/set_resolution/poll_pair_events/sessions/easytier_status/easytier_set`。托盘实现见 GuardPipe.cs；SetupWizard 也用它发 `{"cmd":"tick"}`。

### 约束与坑（源码注释明示）

1. 禁 pywin32——SCM 裸 ctypes，构建环境无关设计。
2. `repo_root()` 必须向上找 `sunshine.exe`：bin\ 迁移后 dirname 是 bin\ 不是根，早期由此静默失效。
3. `SDC_TOPOLOGY_INTERNAL` 禁用：整机级操作会改写 Game 会话 RDP 显示堆栈导致失败（历史事故）。
4. `ChangeDisplaySettingsEx` 无法重新 attach 已 detach 的源（AMD 驱动返回失败），必须走 CCD `SetDisplayConfig`。
5. 零活跃源拓扑 Windows 不接受：物理屏必须先恢复再 detach 虚拟屏。
6. Apollo 的 `ensure_only_display` 不可用（虚拟屏刚建时校验失败被 reset 取消），显示拓扑由本服务独占负责。
7. 同一时刻只有一个 RDP 会话，用户 MSTSC 必然顶掉 keepalive，只能待命不能抢。
8. 配对检测是被动日志尾部监听，首次见到文件从尾读起（避免开机噪声误弹窗）。
9. 服务运行中禁止覆盖二进制（半更新起不来）；`verify.ps1` 不进正常安装流程。

## SunshineDuoUI（托盘）详细构造（src\SunshineDuoUI\）

C#/WinForms 托盘，界面层已收敛为 WebView2 WebUI（旧 WinForms 界面层已删，托盘菜单/通知/PairForm 仍由 WinForms 承担）。

### 文件清单

| 文件 | 职责 |
| --- | --- |
| `Program.cs` | 入口：单实例 mutex + 第二实例唤起管道 |
| `TrayContext.cs` | ApplicationContext 主控：托盘图标/菜单/双轮询/UI 管道服务端/配对弹窗调度 |
| `Paths.cs` | 根推导、conf 解析与端口读取、`CredentialReset`（sunshine.exe --creds 离线改密） |
| `AppConfig.cs` | trayapp.json 模型 + DPAPI 加解密 WebPass |
| `ServiceControl.cs` | sc.exe 查询/启停 SunshineDuoCore 服务 |
| `GuardPipe.cs` | 连 SunshineDuoCore 控制管道的 JSON 客户端 |
| `AutoStart.cs` | HKCU Run 键自启 |
| `UpdateCheck.cs` | .VERSION 远端比对 + .GITPROXY 镜像 + 拉起 setup.exe |
| `WebUiForm.cs` | WebView2 主界面（无边框 1020×680），承载全部 RPC |
| `WebViewForm.cs` | Apollo 自带 WebUI 弹窗（自动登录/注 cookie） |
| `WebViewEnvironment.cs` | 共享 WebView2 环境单例 + 运行时缺失处理 |
| `ApolloClient.cs` | Apollo HTTP API（login/pin/clients） |
| `PairForm.cs` | 纯 WinForms 配对弹窗（4 格 PIN） |
| `NetInfo.cs` | 本机 IP（UDP 路由探测）+ WAN IP（api.ip.sb） |
| `TrayIcons.cs` | sun.png 动态合成三态托盘角标（绿✓/黄!/灰×） |
| `UiKit.cs` | 调色板/Theme 解析/旧控件工厂（历史遗留，PairForm 在用） |

### 入口与生命周期

`Main` 取 `Mutex("SunshineDuoTray")`——非首实例向命名管道 `SunshineDuoTray-ui` 写一行 `open` 后退出；首实例跑 `TrayContext`：建菜单 → NotifyIcon（双击=主界面）→ 2s 会话状态轮询 + 1.5s 配对事件轮询 → 启动 UI 管道服务端。退出唯一路径：菜单「退出」（关主界面窗口不退出）。

### 核心机制

- **Paths**：从 `AppContext.BaseDirectory` 逐级向上找含 `sunshine.exe` 的目录定根；静态构造读 core 的 `config.json` 取 conf 路径与 guard 开关；会话 WebPort = conf `port=` + 1。
- **ServiceControl**：`sc.exe query` 不提权；启停走 `UseShellExecute+Verb=runas` 标准 UAC（90s 超时，取消码 1223 →「已取消提权」）。托盘自身不经 gsudo——gsudo 铁律约束的是 agent/脚本操作，UAC 弹窗是用户交互路径。
- **GuardPipe**：连 `\\.\pipe\SunshineDuoCore-ctrl`，一命令一响应（≤4096B，连接 3s 超时）：`poll_pair_events`（取走即清的配对事件队列）、`restart_apollo`、`set_display_mode`、`set_hotkeys`、`set_hide_cursor`、`set_resolution`、`easytier_status/easytier_set`（secret=null=不改密钥语义）。
- **UpdateCheck**：本地版本=根 `.VERSION`；远端 GitHub raw；国内（UTC+8）先在线拉 `.GITPROXY` 做镜像前缀失败退直连；两 URL 全失败时保留上次结果，**不把网络故障伪装成无更新**；结果缓存 10 分钟。执行更新 = 拉起 `setup.exe --update`；WebUI 的 `openSetup` 不带 update 参数走安装/修复模式（服务注册幂等）。
- **WebView2 WebUI**：`WebViewEnvironment` 提供进程级单例环境（主界面与 Apollo 弹窗共用一个浏览器进程组），userDataFolder=`%LocalAppData%\SunshineDuoTray\WebView2`；运行时缺失 → 原生弹窗引导官方 Bootstrapper 下载，装完可即时重试不重启。主界面经虚拟主机 `https://sunduo.local` 映射本地 `webui\`，URL 带 `?v=Ticks` 防缓存；RPC 走 PostWebMessage `{id,method,args}`↔`{id,ok,result}` + 事件推送，约 25 个方法（init/poll/easytier*/sendPin/setDisplayMode/savePorts/applyCredentials/toggleService/openWebUi/checkUpdate/runUpdate 等）。`WebViewForm` 直接开 `https://127.0.0.1:{port}` 的 Apollo 原生 WebUI，注入 auth cookie，/login 页注入 JS 自动提交。**主界面加载本地 webui，Apollo WebUI 只在弹窗里用。**
- **ApolloClient**：目标 `https://127.0.0.1:{webPort}`，自签证书全放行，按端口缓存 auth cookie。TCP Probe 测活（不触发登录）、login、pin、clients/list、clients/update（**新设备默认仅 list|view 权限，配对后必须补 PermAll 全权**，失败要提示去 WebUI 手动授权）、unpair。
- **配对双路**：被动=1.5s 轮 `poll_pair_events` 弹 PairForm（弹窗期跳过轮询，事件按 target 合并防连环弹窗）；主动=菜单/WebUI，流程为 ListClients(before)→SendPin→ListClients(after)→对新增 UUID 授全权。
- **AutoStart**：HKCU Run 键 `SunshineDuoTray`=`"{ProcessPath}"`，仅用户级不提权。

### 配置与日志

- `trayapp.json`（exe 旁，**本机生成不入库**）：`ConsolePort:48100`、`GamePort:48200`、`WebUser`、`WebPassEnc`（DPAPI base64）、`AutoStart:false`、`Theme:"system"`。写入方：setAutoStart/savePorts/applyCredentials/setTheme 四个 RPC。端口仅作 conf 读不到时的回退，改端口后必须经 GuardPipe 重启对应 Apollo。
- 日志：仅 `bin\SunshineDuoTray\trayapp-ui.log`（主界面打开失败、RPC 错误、JS window.onerror 上报）。无框架化日志。

### 约束与坑

1. 托盘是 setup.exe 的父进程：更新清场时祖先进程只能 `Kill()` 自身、其余整树终止（见 ProcGuard）。
2. WebView2 会缓存 index.html——导航必须换查询串，否则见旧文案。
3. `openWebUi` 回调内不能直接 ShowDialog（阻塞 WebView2 事件泵致新窗停 about:blank），须 BeginInvoke 推迟一拍。
4. WebView2 运行时缺失只弹一次框，`_creating` 不缓存失败结果，装完可重试。
5. 凭证重置不走 Apollo API，用 `sunshine.exe --creds` 离线写各 conf 的 state，写后需重启 Apollo。
6. csproj 图标用内嵌资源（sun.png/sun.ico EmbeddedResource），webui 以 Content Link 保持目录结构随发布产物走。

## SetupWizard 详细构造（src\SetupWizard\）

安装/更新/卸载向导。app.manifest `requireAdministrator`（**不经 gsudo**——自身已是管理员，直接 sc/reg/icacls；gsudo 铁律约束的是 agent/脚本操作）。无静默安装模式（UI 恒交互）。

### 文件清单

| 文件 | 职责 |
| --- | --- |
| `Program.cs` | 入口：CLI 子模式分发 + 前置检测 |
| `MainForm.cs` | 无边框主窗体（左导航+右分页）+ RunPump 后台泵 |
| `WizardPage.cs` / `Pages1.cs` / `Pages2.cs` | 4 步安装向导 / 总览关于页 / 执行结果页 |
| `UninstallPage.cs` / `UpdatePage.cs` | 卸载勾选页 / 更新页 |
| `Ops.cs` | 安装/卸载操作全集（OpsStep 工厂，顺序执行已装跳过） |
| `Detect.cs` | 组件检测 + Services 查询 |
| `Opts.cs` / `Paths.cs` | 选项 DTO / 根推导与路径常量 |
| `GpuEnum.cs` / `WinLogon.cs` / `SudoVda.cs` | 显卡枚举 / 密码验证 / 虚拟显卡驱动 |
| `ProcGuard.cs` / `Updater.cs` / `ReplaceProbe.cs` / `ProcessLab.cs` | 祖先识别安全杀进程 / 更新主体 / 占用替换实验 / 清场练兵 |
| `SelfTest.cs` / `WebView2Gate.cs` / `Shot.cs` / `ConsoleReporter.cs` / `Ui.cs` | 自检 / WebView2 阻断弹窗 / UI 截图自检 / 无头 reporter / 控件工厂 |

### 入口与运行模式

CLI 分支：`--selftest`（免管理员只读自检）、`--replace-probe`、`--process-lab [--rounds N]`、`--shots <dir>`；`--update` 直接进更新页并自动检查。双击/命令行/计划任务都触发 UAC，Program 内仍二次校验 IsAdmin。无单实例互斥锁。前置检测（入口阻断）：OS ≥ 17134 → .NET 9 桌面运行时 → WebView2（WebView2Gate 阻断弹窗+下载跳转，入口与开始安装前各调一次）→ SunshineDuoCore.exe 存在（缺失按杀软误删提示）。

### 向导页面流

总览页列组件检测 → 4 步向导：**01 模式**双会话/单会话；**02 账户**双会话=游戏用户名+密码（`LogonUser` 异步验证，改动即失效；可留空沿用现有 rdp.cred），单会话=当前用户免密；右列网页控制台账号（密码≥6 位）；**03 连接**显卡下拉（物理卡过滤）、主/游戏端口默认 48100/48200（1024-65520 且**基址差≥50**，网页控制台=端口+1）、托盘快捷方式开关；单会话才显示游戏启动器开关。**04 摘要**列已装跳过项。隐藏恒定项：Gamepad/GpuAccel 恒 true、SudovdaResolutions 8 档预设、AutoLogon 恒 true。

### 安装操作分解（Ops.cs，顺序执行、已装自动跳过）

conf → reset(可选) → gpuaccel(仅双会话) → firewall → gamepad → rdpwrap(仅双会话) → **core** → vda → gamemgr → tray：

- **conf**：直接写 `config\sunshine.conf`（adapter_name、port、file_state、mirror），无 sunshine setup 调用。
- **gpuaccel**：HKLM Terminal Services 三个 DWORD；**firewall**：`scripts\add-firewall-rule.bat` 经 cmd.exe。
- **gamepad**：`scripts\install-gamepad.ps1` + 轮询 ViGEmBus.sys；**rdpwrap**：`tools\rdpWrapper.exe -install`。
- **core**：停串流进程 → 写 game conf（改 port、去 dd_*/output_name、file_state=game）→ `sunshine.exe --creds` 设 WebUI 账号 → 两份 state uniqueid 去重 → Game 密码 DPAPI 写 `rdp.cred` + icacls 收紧 → 写 core `config.json`（guard 开关、端口、default_display_mode、sudovda_resolutions、gameviewer_versions）→ 幂等重建服务（先 stop+delete 再 `sc create binPath start= auto` + failure 重启策略 + `sc start`）→ 命名管道发 tick → TCP 探测端口+1 → InstallApolloGuard（清旧 Run 键与计划任务）。
- **vda**：SudoVda.Install（见下）；**gamemgr**：`GameManager.exe --install-service` + `--register-all`（steam 协议）+ GameManager.json；**tray**：复制托盘 exe → 写 trayapp.json → 启动 → WScript.Shell 建 .lnk → 按 WTS Console 用户 SID 写 Run 自启。

### 专门模块

- **Detect**：OS 版本、.NET9（多根探测，路径从环境变量取防 Program Files 重定向）、WebView2（官方 API 防 EdgeUpdate 残留误判）、ViGEmBus、RdpWrapper、GpuPolicy、防火墙、SudoVDA 状态、各服务。向导内失败仅显示徽标不阻断，阻断集中在入口。
- **GpuEnum**：WMI + 虚拟显卡名正则过滤（含向日葵/todesk 等）+ 厂商归类。
- **WinLogon.cs**：advapi32 `LogonUser`（NETWORK 类型）异步验证；源码里类名被脱敏为 `WinLogon`。
- **SudoVda**：全程系统 API（X509Store 装 Root+TrustedPublisher → SetupDi 建 root 设备节点 → `UpdateDriverForPlugAndPlayDevices` → 轮询状态）；问题码 0x1F（31）=「装好待重启」视作成功，0x16=Failed；卸载走 DIF_REMOVE。

### 更新流程（Updater.cs + ProcGuard.cs）

读 .VERSION（GitHub raw，UTC+8 走 .GITPROXY 镜像）→ 下载版本化 zip（失败回退 latest 别名）→ 解压校验含 sunshine.exe → **清场**：先记服务原态，停 SunshineDuoCore/旧服务/GameViewerService/GameManagerService，再按 PID 轮杀进程清单 30 秒；**ProcGuard 先取祖先 PID 集：祖先只 Kill 自身、其余整树终止**——托盘是 setup.exe 父进程，整树终止会抛「包含调用进程」致托盘永远杀不掉；有存活即中止防半更新 → **MirrorUpdate**：只覆盖同名+新增不删 target 独有，config\ 已有文件跳过（apps.json 例外），被占用文件走「改名挪开」（Windows 不允许覆写已映射映像）→ 恢复：`sc start` 后用 ServiceController 后验（**sc 返回码≠最终态**）、按原态拉起服务与托盘。MirrorUpdate 失败也先恢复再抛。

### 卸载流程（UninstallPage.cs，8 个开关反序执行）

防火墙 delete → uninstall-gamepad → rdpWrapper -uninstall → gpuaccel 注册表删 → UnApolloGuard + UnCoreService → 可选 SudoVda.Uninstall → GameManager 卸载 → UnTray（杀进程、删 Run 键与 .lnk、删托盘 exe）。**保留**：程序文件、config\、rdp.cred、trayapp.json、配对 state。

### 约束与坑

1. RunPump 的 SynchronizationContext 不能在字段初始化器捕获（控件未建 → Post 静默丢弃），必须在 UI 线程 Start。
2. SudoVDA 状态码 31 属「装好待重启」，视作成功；VdaRestartPending 每次安装重置防误弹。
3. 不能只杀服务名：服务外孤儿与注入 gv_hook 的 GameViewerService 会占文件。
4. PrintWindow 对从未显示的窗体得到空图，必须屏外真实显示一帧（Shot.cs）。
5. 端口基址差≥50 防 WebUI（端口+1）重叠。
6. 包装器卸载要求目录消失否则判未完成。

---

## 构建与发布

构建依赖：.NET 9 SDK（三个 C# 项目）、Python 3.12 x64 + PyInstaller（SunshineDuoCore）。

- **SunshineDuoCore**：`powershell -NoProfile -ExecutionPolicy Bypass -File src\SunshineDuoCore\build.ps1 [-Deploy] [-Python <路径>]`
  - 构建现场在 `src\SunshineDuoCore\.duocore-build\`（build-tools + rdp-lib 按 requirements-*.txt 用 pip --target 安装，已就绪则复用）。
  - 打包时把 `inject_watch.ps1` 以 UTF-16LE base64 注入 `duocore_final.py` 的 `__PS_B64__` 占位符，运行时再以 `__WG_DIR__` 替换为自身目录。
  - `-Deploy` 部署到 `bin\SunshineDuoCore\`（exe 改名 SunshineDuoCore.exe + robocopy 镜像 `_internal`）；**SunshineDuoCore 进程运行中会拒绝部署**，先停服再部署。
  - 部署目录必须保留两代 hook 二进制：`gv_hook.4.0.dll` + `gv_hook.4.46.dll`（对应 GameViewer 版本，缺一即构建失败）。
- **托盘**：`dotnet publish src\SunshineDuoUI\SunshineDuoTray.csproj -c Release -r win-x64 --self-contained true`，产物复制到 `bin\SunshineDuoTray\SunshineDuoTray.exe`，`webui\` 一并复制到 `bin\SunshineDuoTray\webui\`。
- **SetupWizard**：`dotnet publish src\SetupWizard\SunshineDuoSetup.csproj -c Release -r win-x64 --self-contained true`，产物复制为根目录 `setup.exe`。
- **发布**：改 `.VERSION`（如 `v1.1.1`）推到 main 即触发 `.github/workflows/release.yml`——自动构建三件套、`scripts\pack-release.ps1` 打包 `moedesk_{VERSION}_win64.zip` 并发 GitHub Release。**不要手工跑 pack-release 代替 CI**。
- `bin\SunshineDuoCore\` 内的 easytier 系列是第三方载荷（v2.6.4，SHA-256 记录在该目录 README.md），升级需替换文件并同步更新 README。

## 路径推导（铁律）

- 仓库内路径一律动态推导，禁止把 `D:\Library\Sunshine` 等绝对路径写死到代码/脚本里（trayapp、SunshineDuoCore、setup 同此要求）。
- 各处推导基准（移动目录时必须同步修正）：
  - `scripts\*.bat` / `autostart-hidden.vbs`：`%~dp0..`（父目录）= 仓库根；
  - `src\SetupWizard`：运行期由 `Paths.cs` 从 exe 向上找 `sunshine.exe` 定根；
  - `src\SunshineDuoCore\build.ps1`：仓库根 = `$PSScriptRoot` 上两级，部署目标 = `仓库根\bin\SunshineDuoCore`；
  - `src\SunshineDuoCore\gen_hook_4_46.ps1`：仓库根 = `$PSScriptRoot` 上三级；
  - `src\SunshineDuoUI`：运行期由 `Paths.cs` 从 exe 向上找 `sunshine.exe` 定根；csproj 图标用 `..\..\assets\...`（依赖"源码恰好在根下两级"这一深度）；
  - `duocore.py` 运行期同样从自身目录向上找 `sunshine.exe` 定根（bin\ 迁移后 dirname 不是仓库根，必须向上走）。
- 已安装机器上服务注册使用动态路径（`bin\SunshineDuoCore\build.info` 的 `style: dynamic-path`），整个目录可整体移动。
- `duocore.py` 的 `repo_root()` 有 6 层向上搜索兜底逻辑，改动时保持对 bin\ 布局、src\ 树、迁移前布局三种位置的兼容。

## 代码与文件约定

- 编码：`.ps1` / `.cs` / `.py` / `.md` 带 BOM 的 UTF-8 是项目现状（含中文注释），新文件保持一致；`.bat` 保持纯 ASCII（避免 cmd 代码页问题）。
- C# 统一 net9.0-windows、`Nullable enable`、`ImplicitUsings enable`、单文件自包含发布（GameManager 例外：framework-dependent WPF）。
- Python 服务侧禁止引入 pywin32——SCM 服务管道用裸 ctypes（duocore.spec 有注释说明，这是有意为之的环境无关设计）。
- gv_hook 的汇编生成脚本（gen_hook*.ps1）依赖 binutils 的 `as`/`ld`（mingw-w64 x86_64）；这是注入型组件，仅在需要适配新 GameViewer 版本时动。
- 日志约定：SunshineDuoCore 用 `watch.log`（轮转归档 `watch-YYYYMMDD_HHMMSS.log`，`config.json` 的 `log_level` 默认 `warn`，热加载）；内嵌 PS 脚本与 Python 侧共用该文件，**不要在 PS 侧再 Clear-Content**（会互删对方刚写的内容）。
- `inject_watch.ps1` 是模板：`__WG_DIR__` 占位符运行时替换；直接跑源文件会拿到字面量路径，调试要考虑这一点。

## 运行时注意事项

- 更新/替换服务二进制前必须先停对应服务（SunshineDuoCore / GameViewerService / GameManagerService），并确认目标 PID 全部退出再覆盖（半更新会导致服务起不来）。
- 托盘是 setup.exe 的父进程：更新流程中托盘不能 Kill 自己所在的整棵进程树（ProcGuard 只终止自身）。
- `verify.ps1`、`gen_hook*.ps1`、`inject_watch.ps1` 属调试/构建辅助，不要在正常安装流程里执行 verify.ps1（它会强杀 GameViewerServer）。
- `tools\GameManager.json`、`bin\SunshineDuoCore\config.json` 等由安装时按本机生成，手工改动只做临时调试，不要提交。
