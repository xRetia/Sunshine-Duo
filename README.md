# Sunshine-Duo

Duo = **一台电脑，双会话并行**。Console 会话与游戏串流会话完全隔离、互不干扰：

| | Console 会话 | 游戏会话（串流） |
| --- | --- | --- |
| 使用者 | 本机用户 / UU 远程 | Moonlight 客户端 |
| 显示 | 物理显示器 | 虚拟屏（分辨率自选） |
| 输入 | 本机键鼠 | 串流手柄（虚拟 Xbox 360） |

```powershell
git clone https://github.com/xRetia/Sunshine-Duo.git
```

## Duo 的特点

- **双会话完全隔离**：家人在客厅用 Moonlight 串流打游戏的同时，你可以继续在本机键鼠办公——键盘、鼠标、手柄、显示、音频各归各的会话，互不抢焦点、互不干扰。
- **与网易 UU 远程（GameViewer）兼容**：设置与 UU 远程协同——UU 远程串流 console 会话负责日常远控和办公，Sunshine-Duo 串流游戏会话负责游戏；两边可同时连接同一台机器，各管各的会话，不冲突。
- **手柄即插即用**：串流端手柄虚拟为 Xbox 360 手柄（ViGEmBus），主机级体验，支持多手柄，且不影响本机键鼠。
- **游戏菜单**：GameManager 直接启动需要管理员权限的游戏，串流端打开即玩，无需先在本机登录。

## 内置组件

- **RDP 并发解锁**（RdpWrapper）：允许同一电脑同时登录主用户和串流用户——双会话的技术前提
- **串流会话守护服务**（WSGuard）：断开串流后自动保活虚拟屏（下次连接即开即用）、开机自启并自动拉起 Sunshine
- **虚拟显示驱动**（SudoVDA）：游戏会话使用独立虚拟屏，分辨率任意指定
- **一键安装/卸载**：双击 `setup.bat` 图形界面安装；所有路径由脚本位置相对推导，整个目录可任意移动

## 快速开始

1. 双击 `setup.bat`（自动请求 UAC 管理员权限）。
2. 按界面 4 组选项确认（1/2/3 组必装，第 4 组可选）：
   - **1 · Sunshine 串流服务**：写入默认配置（显卡 / 手柄 / UPnP）+ 渲染显卡选择 + 防火墙放行（可选）+ 虚拟手柄驱动 ViGEmBus（可选）
   - **2 · 远程桌面解锁**：RdpWrapper，允许同一电脑可同时登录主用户和串流用户
   - **3 · 串流会话守护服务**：串流用户与密码（LogonUser 验证）、开机自动登录、虚拟屏分辨率、WSGuard 服务
   - **4 · 可选功能**：游戏菜单（快捷方式放 `Links\` 目录）、解绑所有已配对设备
3. 点「开始安装」，执行过程实时见「运行日志」选项卡。

卸载：同一界面切到「卸载」选项卡，执行反向反注册（不删除程序文件、配置与凭据）。

## 目录结构

| 目录 / 文件 | 说明 |
| --- | --- |
| `setup.bat` | 一键安装入口（实际逻辑在 `scripts\sunshine-setup.ps1`） |
| `sunshine.exe` / `zlib1.dll` | Sunshine 本体（Apollo） |
| `config\` | Sunshine 配置 |
| `scripts\` | 安装 / 卸载 / 防火墙 / 手柄驱动等脚本 |
| `tools\` | rdpWrapper、GameManager 等工具 |
| `wsguard\` | 串流会话守护服务（`wsguard.exe` + `_internal\` 运行库；源码在 `wsguard\src\`） |
| `drivers\` | 虚拟显示驱动（sudovda） |
| `assets\` | Sunshine 资源文件 |
| `Links\` | 游戏快捷方式目录（把你自己的游戏快捷方式放进去） |
| `LICENSE` / `licenses\` / `THIRD-PARTY-NOTICES.md` | 许可证：自有代码 GPL-3.0，第三方组件许可声明 |

## 重新构建 wsguard（可选）

需要 **Python 3.12（x64）与 pip**。构建依赖不随仓库分发，首次构建时自动 pip 安装到 `.wsguard-build\`：

```powershell
cd wsguard\src
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Deploy
```

也可手动准备构建环境（两种方式效果相同）：

```powershell
python -m pip install --target ..\..\.wsguard-build\build-tools -r requirements-build.txt
python -m pip install --target ..\..\.wsguard-build\rdp-lib      -r requirements-rdp.txt
```

依赖清单见 `wsguard\src\requirements-build.txt`（PyInstaller 工具链）与 `wsguard\src\requirements-rdp.txt`（aardwolf RDP 运行库及其依赖树）。

## 许可证

本仓库自有代码（安装脚本、wsguard、GameManager 等）以 **GPL-3.0** 授权，全文见 [LICENSE](LICENSE)。

仓库内捆绑的第三方组件各自保留原始许可证：

| 组件 | 项目地址 | 许可证 |
| --- | --- | --- |
| Apollo（sunshine.exe 及附属工具） | https://github.com/ClassicOldSong/Apollo | GPL-3.0 |
| RDP Wrapper | https://github.com/stascorp/rdpwrap | Apache-2.0 |
| GameManager | https://github.com/xRetia/game-manager | GPL-3.0 |
| ViGEmBus 驱动 | https://github.com/nefarius/ViGEmBus | BSD-3-Clause |
| SudoVDA 虚拟显示驱动 | https://github.com/SudoMaker/SudoVDA | MIT |
| nefcon | https://github.com/nefarius/nefcon | MIT |
| zlib | https://zlib.net | zlib License |
| wsguard 的 Python 依赖（aardwolf 等） | https://pypi.org（清单见 wsguard\src\requirements-rdp.txt） | MIT / BSD / Apache-2.0 / MPL-2.0 / PSF 等 |

完整清单、版本号与许可证全文见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 与 [licenses\](licenses) 目录。

## 自检

不做任何系统变更，验证路径、显卡枚举、界面 XAML、DPAPI 加解密等：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -STA -File scripts\sunshine-setup.ps1 -SelfTest
```
