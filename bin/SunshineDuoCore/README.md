# EasyTier（第三方运行时载荷）

内网穿透功能的组网核心，由 SunshineDuoCore 服务按需拉起（非随服务常驻）。
本目录与 SunshineDuoCore.exe 同级（bin\SunshineDuoCore\），随服务整体分发/移动。

- 来源：https://github.com/EasyTier/EasyTier/releases/tag/v2.6.4
  （官方资产 easytier-windows-x86_64-v2.6.4.zip）
- 许可：Apache-2.0（见上游仓库 LICENSE；本目录未附全文）
- 抓取日期：2026-11-26
- SHA-256（见各文件）：
  - easytier-core.exe da7eb2d24b5416f3d3407636949e964a0750e3f9dc53a828cb6799a57ead445d
  - easytier-cli.exe  d8783e851e944b44a9b71b39fd02f227ec0a2a82b3165c55ead5dd32dcde53a1
  - wintun.dll        e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce
  - WinDivert64.sys   8da085332782708d8767bcace5327a6ec7283c17cfb85e40b03cd2323a90ddc2
  - Packet.dll        c7c03a87eac7243ccbe331554624b18803010b740e311fc8cfddb573096eacac
- 官方压缩包内的 easytier-web.exe / easytier-web-embed.exe 未随包（托盘即管理界面，不需要 Web 控制台）。
- 文件用途：easytier-core.exe 组网核心；easytier-cli.exe 手工查看虚拟网状态；
  wintun.dll / WinDivert64.sys / Packet.dll 为 TUN 模式依赖的虚拟网卡与抓包组件。
- 升级方法：下载新版本官方 zip，替换本目录文件，更新本 README 的版本、日期与 SHA-256。
- 服务侧配置（config.json `easytier` 节）：
  - `enabled` 随服务自启/保活；`network` 网络名（留空首次启用用计算机名）
  - `secret` 网络密钥；`server_mode` public=公共服务器（默认
    tcp://easytier.zcm.cloudns.ch:11010）| custom=指定节点（`peers` 填 -p 地址）
  - `ipv4` 留空=DHCP 自动分配，填写则 -i 手动指定
  - `tcp_hole_punch` 默认 false → 传 --disable-tcp-hole-punching
  - `extra_args` 高级手工附加参数；`exe` 覆盖二进制路径
