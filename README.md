# Sunshine-Duo —— Sunshine 串流环境（便携部署包）

开箱即用的 Sunshine 串流环境：双击 `setup.bat` 图形化一键安装 / 卸载，内置 RDP 并发解锁、串流会话守护（WSGuard）、防火墙放行、虚拟手柄驱动、游戏菜单（GameManager）等组件。所有路径由脚本位置相对推导，整个目录可任意移动。

```powershell
git clone https://github.com/xRetia/Sunshine-Duo.git
```

## 快速开始

1. 双击 `setup.bat`（自动请求 UAC 管理员权限）。
2. 按界面 4 组选项确认（1/2/3 组必装，第 4 组可选）：
   - **1 · Sunshine 串流服务**：写入默认配置（显卡 / 手柄 / UPnP）+ 渲染显卡选择 + 防火墙放行（可选）+ 虚拟手柄驱动 ViGEmBus（可选，虚拟为 Xbox 360 手柄）
   - **2 · 远程桌面解锁**：RdpWrapper，允许同一电脑同时登录主用户和串流用户
   - **3 · 串流会话守护服务**：串流用户与密码（LogonUser 验证）、开机自动登录、虚拟屏分辨率、WSGuard 服务（断开串流后自动保活虚拟屏、开机自启并拉起 Sunshine）
   - **4 · 可选功能**：游戏菜单（可直接启动需要管理员权限的游戏，快捷方式放 `Links\`，目录不入库）、解绑所有已配对设备
3. 点「开始安装」，执行过程实时见「运行日志」选项卡。

卸载：同一界面切到「卸载」选项卡，执行反向反注册（不删除程序文件、配置与凭据）。

## 目录结构

| 目录 / 文件 | 说明 |
| --- | --- |
| `setup.bat` | 一键安装入口（实际逻辑在 `scripts\sunshine-setup.ps1`） |
| `sunshine.exe` / `zlib1.dll` | Sunshine 本体（Apollo） |
| `LICENSE` / `licenses\` / `THIRD-PARTY-NOTICES.md` | 许可证：自有代码 GPL-3.0，第三方组件许可声明 |
| `config\` | Sunshine 配置（`credentials\`、`sunshine_state.json` 为本机生成，不入库） |
| `scripts\` | 安装 / 卸载 / 防火墙 / 手柄驱动等脚本 |
| `tools\` | rdpWrapper、GameManager 等工具 |
| `wsguard\` | 串流会话守护服务（`wsguard.exe` + `_internal\` 运行库 + 配置） |
| `wsguard\src\` | wsguard 源码与重打包脚本（PyInstaller） |
| `drivers\` | 虚拟显示驱动（sudovda） |
| `assets\` | Sunshine 资源文件 |
| `Links\` | 游戏菜单快捷方式目录（GameManager 用，本机游戏快捷方式不入库） |

## 重新构建 wsguard（可选）

需要 **Python 3.12（x64）与 pip**。构建依赖不随仓库分发，首次构建时自动 pip 安装到 `.wsguard-build\`（已加入 .gitignore）：

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

## 不入库的文件（.gitignore）

- **日志**：`*.log`、`*.log.backup`、`tools\install-log.txt`
- **密钥与凭据**：
  - `config\credentials\`（Sunshine 配对 CA 证书与私钥）
  - `config\sunshine_state.json`（已配对客户端状态）
  - `wsguard\rdp.cred`（DPAPI 本机加密的串流用户密码）
- **本机生成的运行配置**：`config\sunshine.conf`（含显卡型号）、`wsguard\config.json`（含本机绝对路径）、`tools\GameManager.json`（安装时按实际路径重写）
- **构建现场**：`.wsguard-build\`（pip 依赖可随时重装，见上一节）
- **临时文件**：`.temp\`、`__pycache__\`、`*.pyc`
- **游戏快捷方式**：`Links\`（指向本机已安装的游戏，他人克隆后无效，自行创建）

## 许可证

本仓库自有代码（安装脚本、wsguard、gv_hook 等）以 **GPL-3.0** 授权，全文见 [LICENSE](LICENSE)。

仓库内捆绑的第三方组件各自保留原始许可证：

| 组件 | 许可证 |
| --- | --- |
| Apollo（sunshine.exe 及附属工具） | GPL-3.0 |
| RDP Wrapper | Apache-2.0 |
| GameManager（作者本人的开源项目） | GPL-3.0 |
| ViGEmBus 驱动 | BSD-3-Clause |
| SudoVDA 虚拟显示驱动 | MIT |
| nefcon | MIT |
| zlib | zlib License |
| wsguard 的 Python 依赖（aardwolf 等） | MIT / BSD / Apache-2.0 / MPL-2.0 / PSF 等 |

完整清单、版本号与许可证全文见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 与 [licenses\](licenses) 目录。

## 自检

不做任何系统变更，验证路径、显卡枚举、界面 XAML、DPAPI 加解密等：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -STA -File scripts\sunshine-setup.ps1 -SelfTest
```
