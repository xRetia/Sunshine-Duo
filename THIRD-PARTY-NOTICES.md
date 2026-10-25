# 第三方组件许可声明（THIRD-PARTY-NOTICES）

本仓库自有代码（`scripts\`、`wsguard\src\`、`gv_hook.dll`、`tools\GameManager.exe` 等）以 **GPL-3.0** 授权，全文见根目录 [LICENSE](LICENSE)。

本仓库捆绑/调用的第三方组件各自保留其原始许可证，汇总如下。许可证全文存放于 [licenses\](licenses) 目录或组件自身目录，均为上游官方原文。

## 一、直接捆绑的组件

| 组件 | 本仓库位置 | 许可证 | 上游项目 |
| --- | --- | --- | --- |
| Apollo 串流服务端（含 Web UI、audio-info / dxgi-info / sunshinesvc 等附属工具） | `sunshine.exe`、`assets\`、`tools\audio-info.exe`、`tools\dxgi-info.exe`、`tools\sunshinesvc.exe` | GPL-3.0 | https://github.com/ClassicOldSong/Apollo |
| RDP Wrapper Library | `tools\rdpWrapper.exe` | Apache-2.0 | https://github.com/stascorp/rdpwrap |
| Game Manager（作者本人项目，随本仓库以 GPL-3.0 授权） | `tools\GameManager.exe` | GPL-3.0 | https://github.com/xRetia/game-manager |
| ViGEm Bus Driver 安装器 | `scripts\vigembus_installer.exe` | BSD-3-Clause | https://github.com/nefarius/ViGEmBus |
| SudoVDA 虚拟显示驱动（上游为 Virtual-Display-Driver 的分支） | `drivers\sudovda\` | MIT | https://github.com/SudoMaker/SudoVDA |
| nefcon 驱动安装工具 | `drivers\sudovda\nefconc.exe` | MIT | https://github.com/nefarius/nefcon |
| zlib 运行库 | `zlib1.dll` | zlib License | https://zlib.net |

对应全文：GPL-3.0 → [LICENSE](LICENSE)；Apache-2.0 → [licenses\apache-2.0.txt](licenses/apache-2.0.txt)；BSD-3-Clause → [licenses\bsd-3-clause_vigembus.txt](licenses/bsd-3-clause_vigembus.txt)；SudoVDA 上游 MIT → [licenses\mit_virtual-display-driver.txt](licenses/mit_virtual-display-driver.txt)；nefcon MIT → [licenses\mit_nefcon.txt](licenses/mit_nefcon.txt)；zlib → [licenses\zlib.txt](licenses/zlib.txt)。

## 二、wsguard 运行库（`wsguard\_internal\`，PyInstaller 打包的 PyPI 依赖）

以 `pip install --target` 安装后打包（清单见 `wsguard\src\requirements-rdp.txt`），版本固定：

| 包 | 版本 | 许可证 |
| --- | --- | --- |
| aardwolf | 0.2.16 | MIT |
| asysocks | 0.2.18 | MIT |
| asyauth | 0.0.23 | MIT |
| unicrypto | 0.0.12 | MPL-2.0 AND MIT |
| minikerberos | 0.4.9 | MIT |
| asn1crypto | 1.5.1 | MIT |
| asn1tools | 0.169.0 | MIT |
| oscrypto | 1.3.0 | MIT |
| cryptography | 50.0.2 | Apache-2.0 OR BSD-3-Clause |
| pycryptodomex | 3.24.1 | BSD-2-Clause / 公有领域双许可 |
| pywin32 | 312 | PSF License（Python 软件基金会） |
| arc4 | 0.5.0 | MIT |
| bitstruct | 8.24.0 | MIT |
| cffi | 2.1.1 | MIT-0 |
| colorama | 0.4.6 | BSD-3-Clause |
| h11 | 0.16.0 | MIT |
| pillow | 13.0.0 | MIT-CMU（HPND） |
| pycparser | 3.12 | BSD-3-Clause |
| pyparsing | 3.3.3 | MIT |
| pyperclip | 1.11.0 | BSD-3-Clause |
| six | 1.17.0 | MIT |
| tqdm | 4.70.1 | MPL-2.0 AND MIT |

其中 aardwolf / asn1crypto / asyauth / asysocks / cryptography / minikerberos / oscrypto / tqdm / unicrypto 的 LICENSE 文件已随 `wsguard\_internal\<包名>.dist-info\` 一并分发；其余包的许可声明以本文件清单为准，原文见各包 PyPI 页面。Python 3.12 运行时以 PyInstaller onedir 方式嵌入，Python 本体采用 PSF License。

## 三、构建期依赖（不入库，`pip install` 安装到 `.wsguard-build\`）

| 包 | 版本 | 许可证 |
| --- | --- | --- |
| PyInstaller | 6.22.3 | GPL-2.0-or-later，附 bootloader 例外（允许用它构建和分发非自由程序） |
| pyinstaller-hooks-contrib | 2026.9 | Apache-2.0 / GPL-2.0-or-later 双许可 |
| altgraph | 0.17.5 | MIT |
| packaging | 26.3 | Apache-2.0 OR BSD-2-Clause |
| pefile | 2024.8.26 | MIT |
| pywin32-ctypes | 0.2.3 | BSD-3-Clause |
| setuptools | 84.2.1 | MIT |

PyInstaller 许可证原文：https://github.com/pyinstaller/pyinstaller/blob/develop/COPYING.txt

## 四、其他说明

- 本仓库按 GPL-3.0 分发 Apollo（未修改的官方发行版），对应源代码见上游仓库；Apollo 及其附属工具的 GPL-3.0 义务（附许可证文本、指明源代码获取方式）通过本文件与 `LICENSE` 履行。
- `tools\GameManager.exe` 为本仓库作者本人的开源项目（xRetia/game-manager），随本仓库以 GPL-3.0 一并授权。
- 网易 UU 远程（GameViewer）**不包含**在本仓库中，wsguard 仅在运行时与其本机已安装的服务交互，不涉及该软件的再分发。
