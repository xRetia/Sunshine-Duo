# 规划方案 v3：Console Apollo / SudoVDA / 双会话端口 / 重新架构 setup

生成于 2026-11-19，最后修订 2026-11-20。本轮在 v2 基础上修正：

1. SudoVDA 驱动安装 **与显示模式解耦**（独立开关，显示模式为 4 选 1：仅物理 / 仅虚拟 / 克隆 / 扩展）；
2. Apollo 配置文件（端口族、WebUI 用户名密码、配对证书/状态）由 **`sunshine setup` 统一生成和写入**；
3. `scripts/sunshine-setup.ps1` **推翻重写**：安装（初始化）+ 卸载 + 配置 + 系统托盘四合一；
4. 两个 Apollo 共享应用列表与证书，但**各自独立保存配对状态与 Moonlight 主机名称**（`sunshine_state-console.json` / `sunshine_state-game.json`；`主会话` / `游戏会话`）；
5. Console Apollo 以 SYSTEM 外壳运行、接管 Console 会话、SudoVDA 由 Console Apollo 使用；Game Apollo 接管 RDP 拉起的游戏会话。

---

## 1. 整体架构

```
┌───────────────────────────────────────────────────────┐
│  WSGuard (SYSTEM 服务)                                 │
│                                                       │
│  ┌─────────────────────┐    ┌─────────────────────┐   │
│  │ guard_apollo        │    │ guard_apollo_console│   │
│  │ + guard_uu + rdp    │    │                   │   │
│  │ Road to Game session│    │ Road to Console     │   │
│  └───────────┬─────────┘    └─────────┬───────────┘   │
│              │                        │               │
│        Game user token         SYSTEM token           │
│        (wsguard在Game会话拉起)   (wsguard在Console会话)  │
│          ┌────▼───┐               ┌────▼───┐          │
│          │Apollo  │               │Apollo  │           │
│          │(次)    │               │(主)    │           │
│          └────┬───┘               └────┬───┘           │
│               │                        │                │
│         RDP 显示驱动             SudoVDA 虚拟显示器      │
│         (aardwolf keepalive)         (仅Console)         │
│                                                       │
│  共享 apps/证书；state/名称各自独立                        │
│  config/apps.json + credentials/*                         │
│  config/sunshine_state-console.json / -game.json          │
└───────────────────────────────────────────────────────┘
```

**Console（主 Apollo）**：从 SYSTEM 进程 `WTSGetActiveConsoleSessionId()` 对应的控制台会话窗口拉起，SYSTEM 外壳 + 提升令牌，接管 Console 物理显示器 + SudoVDA 虚拟显示器。端口族 A。

**Game（次 Apollo）**：沿用现有 `WTSQueryUserToken(Game)+TokenLinkedToken`，提升至 Game 用户会话的 Apollo，接管 RDP 设备。端口族 B。两个 Apollo 共用 `config/` 目录（`apps.json` 应用列表、`credentials/` 证书/私钥），但通过各自 conf 的 `file_state` / `sunshine_name` 分开配对状态与显示名称，Moonlight 里看到两台独立主机。

---

## 2. 新增 Windows 服务守护模型

### 2.1 WSGuard 替换旧守卫

从 "Game 会话 + Apollo + keepalive + UU hook" 修改为 **"双路 Apollo 守卫 + 双路配置"** 。

```python
# config.json 新增字段
{
    ...
    "guard_apollo_console": true,          # 是否开启 Console 会话 Apollo（SYSTEM 特权）
    "guard_apollo_game": true,             # 是否开启 Game 会话 Apollo（用户提升令牌）
    "console_conf_path": "config/sunshine.conf",       # 主 Apollo 配置文件
    "game_conf_path": "config/sunshine-game.conf",     # 次 Apollo 配置文件
    "console_port": 48100,                # 主 Apollo 端口族
    "game_port": 48200,                   # 次 Apollo 端口族
    "default_display_mode": "extended",   # 仅物理 / 仅虚拟 / 克隆 / 扩展
    "install_sudovda": true,              # 驱动安装标志（由 setup 控制）
}
```

### 2.2 `launch_apollo(session_id, config, elevate=True)`

- 利用 Apollo 自身语法 `sunshine.exe <conf-path>`（代码确认 `322220: [options] [/path/to/configuration_file]`），传入路径即可；
- WSGuard 先在 tick 中检测对应会话中是否已运行 Apollo（通过 `EnumProcesses`+`ProcessIdToSessionId`+`endswith(\sunshine.exe)`）；
- **无须用户介入**：服务通过 `CreateProcessAsUserW` 启动。

### 2.3 Console / Game 会话隔离

只用 WTS query。避免 `guard_apollo_console` 在 Console 没登录时启动（服务在 Windows 启动时本身运行于 Session 0，必须避免错误拉起）。

---

## 3. 全新架构的 `sunshine setup` 向导（推翻重写）

### 3.1 职责矩阵

| 模块 | setup 负责 | 服务（WSGuard）负责 |
|------|-----------|-------------------|
| 运行环境 | 驱动安装/卸载、防火墙、显卡、RDP Wrapper、ViGEmBus | 保活、拉起 |
| Apollo 配置 | 生成两个 conf、端口族、WebUI 用户名密码（`--creds`）、共享 apps/证书、各自独立的 state 与 Moonlight 名称 | 读 conf 启动 |
| 配对 | 引导用户在 Web UI 完成配对，生成两份 `sunshine_state-*.json`；旧共享 state 首次安装迁移到两份 | 按 conf 的 `file_state` 读数并做类型自愈 |
| 托盘 | 常驻托盘、状态、关闭/开启 Apollo、快速切换端口/显示模式 | — |
| 配置面板 | 后期修改：用户/密码/端口/显示模式/守护开关 | — |

### 3.2 界面蓝图（WPF）

```
Welcome
  ├─ 总览：部署目录 / 当前用户 / 显卡 / 安装状态（服务 & Apollo 运行状态）
  ├─ 向导（安装/卸载/配置 三合一）：
  │   ① 基础硬件：显卡 + 驱动（SudoVDA / ViGEmBus） + RDP Wrapper + 防火墙
  │   ② 用户与凭证：串流用户密码（可选 DPAPI 加密）+ 网络/配对
  │   ③ 端口与显示：主会话端口族 + 游戏会话端口族 + 显示模式（4选1）+ SudoVDA独立开关
  │   ④ 确认 & 安装
  └─ 可选功能：游戏菜单等等
  底部：系统托盘
```
- 串流用户（第 2 步）从**当前登录用户**改为**可编辑**，方便从 dfc643 桌面安装时选择 `Game` 账户；改用户自动让密码验证失效，并校验该用户为本机存在用户

### 3.3 系统托盘

在托盘菜单中提供：
- 显示主窗口（恢复向导）
- 守护服务开关（启动/停止 WSGuard）
- 主 Apollo / 次 Apollo 运行状态
- 快速开启串流（清理桌面环境）
- 退出（后台保留）

托盘使用 WinForms NotifyIcon 或 WPF `SystemTray` 控件。

### 3.4 `sunshine setup` 与 Apollo WebUI 凭证

- setup 在安装时分别调用 `sunshine.exe <conf> --creds <username> <password>`，把同样的账号密码写进两个 conf 各自的 state 文件；Web UI 登录凭证不再共享一份文件。
- setup 复用 `credentials/cacert.pem/cakey.pem`（或由 Apollo 启动时自动生成），两个实例用同一 CA；配对记录则按实例分开。
- **配对**：首次 Moonlight 连上后确认对应 `sunshine_state-*.json` 中 `named_devices` 出现，setup 即可结束向导；两台主机（主会话 / 游戏会话）需分别配对。

### 3.5 端口分配（双会话）

```
主会话（Console）端口族为：[ 48100 ]  说明：Console Apollo WebUI + Moonlight 流入的端口族。
游戏会话（Game）端口族为：[ 48200 ]  说明：Game Apollo WebUI + Moonlight 串流连入。

约束：两个端口族不能重叠（RTSP = port+21，需差 ≥ 22），默认相距 100
来源：从主会话自动填充（当前用户 + 控制台会话）
来源：手动指定（可填 Duo、Game 等任意用户）
显示模式：仅物理 / 仅虚拟 / 克隆 / 扩展（不需修改端口）
SudoVDA 驱动：独立开关（是否安装，不影响端口）
```

如何写文件？

- `config/sunshine.conf`（主）: `port = 48100`
- `config/sunshine-game.conf`（次）: `port = 48200`
- 两个 conf 其他参数从单一模板复制（adapter_name / 手柄 / 服务等）

主会话不一定是 Duo 或 Game。配置端口时，支持三角色：
- 默认以控制台会话（Console）用户为准；
- setup 从用户选择的串流账户（可能为 "Duo"/"Game" 等）做源；
- 手工覆盖。

### 3.6 SudoVDA 驱动安装

```powershell
function Install-SudoVDA {
    # 驱动自 drivers\sudovda\ 目录，调用 nefcon 安装证书 + 建设备节点 + 装驱动
    $p = Start-Process "$root\drivers\sudovda\install.bat" -Wait -PassThru
    ...
}
```

独立开关在向导第 3 步（显示模式旁边）：`[x] 安装 SudoVDA 虚拟显示驱动`。与显示模式解耦。

### 3.7 显示模式（仅控制台）

四种模式的**意图**为：仅物理 / 仅虚拟 / 克隆 / 扩展；实际虚拟显示器由 Apollo 在客户端开串流时创建、断流删除，模式语义随之成立：

- 仅物理（镜像）：抓物理主屏，桌面端设备不强制走虚拟屏；
- 仅虚拟（独占）：客户端串流（含「桌面」App）时只保留 SudoVDA 虚拟显示器、物理显示器自动断开，断流后恢复——由 WSGuard 在运行期按虚拟屏存在与否执行（wsguard.py `DisplayExclusive`）；
- 扩展：物理显示器照常保留，虚拟显示器作为扩展桌面；
- 克隆：语义并入物理主屏路径。

要点：Apollo 0.4.6 对 virtual display 会话不应用 `dd_configuration_option`（且启动后的 `display_device::reset_persistence()` 会取消其退避重试），拓扑切换因此不再写进 `sunshine.conf`；桌面端设备记录 `always_use_virtual_display=true` 由 WSGuard 每轮强制（该标记在 Apollo 0.4.6 是 per-device 记录，不是 conf 键）。

---

## 4. 文件改动清单

| 路径 | 角色 | 改动 |
| --- | --- | --- |
| `scripts/sunshine-setup.ps1` | **重写** | 向导 + 安装 + 卸载 + 托盘 + Apollo 配置 + 凭证 + 端口 + 显示模式 |
| `wsguard/src/wsguard.py` | 核心服务 | 双 Apollo 守卫 + 双 conf 路径 + Console 会话日志 |
| `wsguard/config.json` | 配置 | 新增：`guard_apollo_console`, `console_conf_path`, `game_conf_path`, `console_port`, `game_port`, `install_sudovda`, `default_display_mode` |
| `config/sunshine.conf` | 主 Apollo 配置 | 新增 `port`（由 setup 写） |
| `config/sunshine-game.conf` | 次 Apollo 配置 | 新增 `port` |
| `drivers/sudovda/*` | 驱动 | 不变（新增 `install-sudovda.bat`/`uninstall-sudovda.bat` 调用 nefcon） |
| `scripts/install-sudovda.bat`, `uninstall-sudovda.bat` | 新增 | 封装驱动安装/卸载 |
| `scripts/add-firewall-rule.bat` | 修改 | 支持双端口族范围 |
| `README.md` | 文档 | 双 Apollo + SudoVDA + 端口分配说明 |
| 托盘 UI | 新增 | WPF 系统托盘集成，管理功能 |

---

## 5. 风险与注意事项

- **SYSTEM 外壳**：`CreateProcessAsUserW` 接管 Console 会话。需要用户登录状态；若没有用户会话，WTS Query 失败，守卫应退避，不强行拉起。
- **UAC 处理**：主 Apollo 作为 SYSTEM 执行，已经足够处理 UAC 会话的弹框；次 Apollo 无。
- **显示多路**：如果 `SudoVDA` 驱动未安装，虚拟显示器不可用。setup 可在安装前检测。
- **端口重叠**：需确定 Apollo 两个端口的扩展字段（最高 48100 ~ 48121 族）；建议两个端口差 ≥ 22。
- **两个 Apollo 的 state 写锁**：`file_state` 已按实例拆开（Console / Game 各写各的），不再争同一 `sunshine_state.json`；WSGuard 在拉起前还会把两份 state 中被 Web UI 写成字符串的布尔/数字归一化回原生类型，避免 0.4.6 启动崩溃。
- **证书共享**：`credentials/cacert.pem/cakey.pem` 共用，但已配对设备记录在各自的 state 文件里；Moonlight 两台主机分别配对，名称由 `sunshine_name` 决定。
- **防火墙**：需要在安装时放行两个端口族（不只是单个 Apollo 默认48100）；`add-firewall-rule.bat` 改为范围放行。
- **托盘**：PowerShell 托盘需要用 WPF/WinForms，考虑兼容性问题。

---

## 6. 实施顺序建议

1. **重写 `sunshine-setup.ps1`**（安装/卸载/配置/托盘）；逐步实现；
2. **`wsguard.py`**（双 Apollo 守卫 + 双 conf）；
3. **两个 conf + 两种端口 + 两份 state** 写法和共享证书；
4. **托盘功能 + WebUI 凭证**（`--creds`）；
5. **说明文档 + README**。

---

## 7. 决策点（需要再确认）

1. 两个 Apollo 是否始终运行，还是 Game Apollo 只在用户串流时开启？如果一直是双 Apollo Active，需要确认对物理显示器的性能影响。
2. 配对流程：setup 生成但需要登录 WebUI 进行 Moonlight 配对；操作是否在 setup 结束前完成？
3. 已确认：WebUI 的 `cert`/`pkey` 共用凭据目录没有观察到冲突；配对状态与 Moonlight 主机名称必须分开（否则两台主机争写同一 state，配对异常），由各 conf 的 `file_state` / `sunshine_name` 控制。

---

## 8. 实施进度（已完成 → 途中）

| 步骤 | 任务 | 状态 |
| --- | --- | --- |
| 1 | `wsguard.py`：Console Apollo SYSTEM 守卫 + `--config` conf 参数 | **已完成** |
| 2 | SudoVDA 安装/卸载 batch 封装 | **已完成** |
| 3 | `sunshine-setup.ps1` 重写（SudoVDA 开关、显示模式 4 选 1、双端口分配、游戏 conf） | **已完成** |
| 4 | `wsguard/config.json` 新增双 Apollo 配置项 | **已完成** |
| 5 | 系统托盘（WPF NotifyIcon） | 待实现 |
| 6 | WebUI 用户名密码 `--creds` 集成 | **已完成** |
| 7 | 防火墙已为程序级规则，无需修改 | 已完成 |
| 8 | README 文档更新 | 已完成 |

后续优化：主用户/游戏用户分组（步骤 2 引导）、`LogonUser` 验证由后台线程运行不再卡界面、托盘管理。

---

*文档随进度自动更新。*