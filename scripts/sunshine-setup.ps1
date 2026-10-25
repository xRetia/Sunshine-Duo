#Requires -Version 5.1
<#
.SYNOPSIS
  Sunshine 串流环境一键部署 / 反注册工具（WPF 图形界面，单文件，零外部依赖）。

.DESCRIPTION
  安装（界面 4 组：1/2/3 组必装，第 4 组可选；可选项直接调用 scripts\ 下现成脚本）：
    1 · Sunshine 串流服务（必装）：
       - 写入 config\sunshine.conf：adapter_name 自动检测物理显卡
         （Intel / NVIDIA / AMD，已排除各类虚拟显卡），多卡时人工选择。
       - 防火墙放行 Sunshine（默认勾选，可取消）：调用 scripts\add-firewall-rule.bat，
         为 sunshine.exe 放行 TCP/UDP 入站（规则名 Apollo）。
       - 虚拟手柄驱动（默认勾选，可取消）：调用 scripts\install-gamepad.ps1 安装
         ViGEmBus（串流手柄虚拟为 Xbox 360 手柄，已装 1.17+ 自动跳过）。
    2 · 远程桌面解锁（必装）：静默安装 tools\rdpWrapper.exe（官方命令行 -install，
       解锁单用户并发 RDP，同一账号可同时本地 + 远程在线）。
    3 · 串流会话守护服务（必装，登录信息在本组）：
       - 当前用户作为串流用户，密码经 LogonUser 验证；含“开机自动登录”选项
         （无会话时 wsguard 自动 RDP 拉起会话，auto_logon）与虚拟屏分辨率。
       - 注册 wsguard 服务 WSGuard / 显示名 Game Windows Session Guard；密码经 DPAPI
         （本机作用域）加密为 rdp.cred，供 wsguard 在串流断开后自动连接 127.0.0.2
         保持 RDP 虚拟显卡会话活跃。
       - Sunshine 自启 = wsguard Apollo 守卫：写 config.json 的 guard_apollo，tick 检测到
         会话无 sunshine.exe 时以提升令牌（WTSQueryUserToken + TokenLinkedToken +
         CreateProcessAsUserW）在会话内拉起；提升运行可向高完整性窗口/游戏注入输入，
         由 SYSTEM 服务直接创建不进任何 Job（CREATE_BREAKAWAY_FROM_JOB 拉起 steam://、
         detached 应用不报 err 5）。安装时同步清理旧自启机制（Run 键与计划任务）。
    4 · 可选功能：
       - GameManager（tools\GameManager.exe）：可直接启动需要管理员权限的游戏，
         快捷方式目录为 <Sunshine>\Links（写入 tools\GameManager.json 的 LinksFolder）。
       - 解绑所有已配对设备（执行时二次确认）、清空 sunshine.log。
  卸载：与安装分组一一对应的反注册操作（不删除程序文件、配置与凭据）：
        移除防火墙规则（scripts\delete-firewall-rule.bat）、卸载手柄驱动
        （scripts\uninstall-gamepad.ps1）、卸载 RdpWrapper、关闭 Apollo 守卫并删除
        WSGuard 服务、卸载 GameManager；同步清理旧名任务「Sunshine Autostart (Game)」
        与旧服务 GameViewerSessionHook。
  路径全部由脚本所在目录（scripts\ 的父目录）推导，Sunshine 目录可整体移动。

.PARAMETER SelfTest
  自检模式：验证路径、显卡枚举、XAML 解析、DPAPI 加解密、配置模板渲染，
  不做任何系统变更（无需管理员）。

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -STA -File .\sunshine-setup.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File .\sunshine-setup.ps1 -SelfTest
#>
param([switch]$SelfTest)

# ----------------------------------------------------------------------------
# 常量与路径（全部相对脚本位置推导）
# ----------------------------------------------------------------------------
$Root         = Split-Path -Parent $PSScriptRoot          # Sunshine 根目录
$TaskName     = 'Sunshine Autostart'                      # 旧计划任务名（仅迁移清理用）
$RunValueName = 'Sunshine'                                # 旧 HKCU Run 键值名（仅迁移清理用）
$SvcName      = 'WSGuard'                                  # wsguard 服务名（与 exe 内嵌一致）
$SvcDispName  = 'Game Windows Session Guard'               # wsguard 服务显示名
$LegacySvc    = 'GameViewerSessionHook'                    # 旧服务名（迁移清理用）
$LegacyTask   = 'Sunshine Autostart (Game)'                # 旧任务名（迁移清理用）
$GmSvcName    = 'GameManagerService'                      # GameManager 服务名
$RdpWrapDir   = 'C:\Program Files\RDP Wrapper'            # rdpWrapper 默认安装目录
$LinksDir     = Join-Path $Root 'Links'                    # GameManager 快捷方式目录

$ConfPath     = Join-Path $Root 'config\sunshine.conf'
$StatePath    = Join-Path $Root 'config\sunshine_state.json'
$LogPath      = Join-Path $Root 'config\sunshine.log'
$LogBakPath   = Join-Path $Root 'config\sunshine.log.backup'
$RdpWrapExe   = Join-Path $Root 'tools\rdpWrapper.exe'
$GmExe        = Join-Path $Root 'tools\GameManager.exe'
$WgDir        = Join-Path $Root 'wsguard'
$WgExe        = Join-Path $WgDir 'wsguard.exe'
$WgCredPath   = Join-Path $WgDir 'rdp.cred'
$WgCfgPath    = Join-Path $WgDir 'config.json'

$script:LogBox = $null
$script:Window = $null

# ----------------------------------------------------------------------------
# 基础函数
# ----------------------------------------------------------------------------
function Write-Log {
    param([string]$Message)
    $line = ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message)
    if ($script:LogBox) {
        $script:LogBox.AppendText($line + "`r`n")
        $script:LogBox.ScrollToEnd()
        # 在 UI 线程内刷新渲染队列，让日志即时可见
        $script:LogBox.Dispatcher.Invoke([action]{}, [System.Windows.Threading.DispatcherPriority]::Render)
    } else {
        Write-Host $line
    }
}

function Update-Ui {
    if ($script:Window) {
        $script:Window.Dispatcher.Invoke([action]{}, [System.Windows.Threading.DispatcherPriority]::Render)
    }
}

# 用 Windows 登录验证（LogonUser）校验本地用户密码
function Test-UserPassword {
    param([string]$User, [string]$Password)
    if ([string]::IsNullOrEmpty($User) -or [string]::IsNullOrEmpty($Password)) { return $false }
    if (-not ('PwCheck' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class PwCheck {
    [DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern bool LogonUser(string user, string domain, string pass, int logonType, int provider, out IntPtr token);
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);
}
"@
    }
    $domain = $env:USERDOMAIN
    if ([string]::IsNullOrEmpty($domain)) { $domain = '.' }
    $tok = [IntPtr]::Zero
    $ok = [PwCheck]::LogonUser($User, $domain, $Password, 3, 0, [ref]$tok)
    if ($ok) { [void][PwCheck]::CloseHandle($tok); return $true }
    return $false
}

# 枚举物理显卡（排除虚拟显卡），返回 Name/Vendor 对象数组
function Get-PhysicalGpus {
    $virtualRx = 'virtual|paravirtual|basic display|remote|mirage|llvmpipe|hyper-v|indirect|parsec|sunshine|splashtop|todesk|oray|向日葵'
    $list = @()
    $controllers = @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction SilentlyContinue | Sort-Object Name)
    foreach ($c in $controllers) {
        $name = [string]$c.Name
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ($name -match $virtualRx) { continue }
        $vendor = if ($name -match 'nvidia|geforce|quadro|rtx|tesla') { 'NVIDIA' }
                  elseif ($name -match 'amd|radeon') { 'AMD' }
                  elseif ($name -match 'intel|iris|uhd|hd graphics') { 'Intel' }
                  else { 'Other' }
        $list += [PSCustomObject]@{ Name = $name; Vendor = $vendor }
    }
    return ,$list
}

# ----------------------------------------------------------------------------
# 安装操作（函数按界面分组标注：1 Sunshine 服务 / 2 远程桌面解锁 / 3 守护服务 / 4 可选；
# 实际执行顺序由“安装按钮”处理器按组依次调用）
# ----------------------------------------------------------------------------

# 3 · Sunshine 开机自启 = wsguard Apollo 守卫：写 config.json 的 guard_apollo，
#     并清理旧自启机制（Run 键 / 计划任务）。Apollo 由 wsguard 服务以提升令牌拉起。
function Install-ApolloGuard {
    param([string]$User)
    if (-not (Test-Path (Join-Path $Root 'sunshine.exe'))) {
        throw "未找到 $($Root)\sunshine.exe，请确认脚本位于 Sunshine\scripts 目录"
    }
    if (-not (Test-Path $WgCfgPath)) {
        throw "未找到 $WgCfgPath（wsguard 未安装？）"
    }

    # config.json：设置 guard_apollo=true（保留其余字段，热重载生效）
    $cfg = Get-Content $WgCfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $cfg | Add-Member -NotePropertyName guard_apollo -NotePropertyValue $true -Force
    [IO.File]::WriteAllText($WgCfgPath, ($cfg | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
    Write-Log "OK: wsguard config.json 已设置 guard_apollo=true（Apollo 守卫：提升令牌拉起 + 不进 Job）"

    # 清理旧 Run 键（explorer 启动永不提升，且会与守卫双开冲突）
    $any = $false
    foreach ($k in (Get-ChildItem 'Registry::HKEY_USERS' -ErrorAction SilentlyContinue)) {
        $sid = $k.PSChildName
        if ($sid -like '*_Classes' -or $sid -notlike 'S-1-5-21-*') { continue }
        $runKey = "Registry::HKEY_USERS\$sid\Software\Microsoft\Windows\CurrentVersion\Run"
        if (-not (Test-Path $runKey)) { continue }
        $props = Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue
        if ($props.PSObject.Properties.Name -contains $RunValueName) {
            Remove-ItemProperty -Path $runKey -Name $RunValueName -ErrorAction SilentlyContinue
            Write-Log "OK: 已删除用户 hive [$sid] 的旧 Run 键 [$RunValueName]"
            $any = $true
        }
    }
    # 清理旧计划任务（任务计划 Job 会使 Apollo 的应用 spawn 报 err 5）
    foreach ($n in @($TaskName, $LegacyTask)) {
        if (Get-ScheduledTask -TaskName $n -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName $n -Confirm:$false
            Write-Log "OK: 已删除旧计划任务 [$n]"
            $any = $true
        }
    }
    if (-not $any) { Write-Log '提示: 未发现旧 Run 键/计划任务残留' }
    Write-Log "提示: $User 用户会话就绪后由 wsguard 自动拉起 Sunshine（Game 会话存在 + sunshine.exe 不在时触发）"
}

# 1 · 写入 sunshine.conf（必装：显卡 / 手柄 / UPnP 等默认配置）
function Write-SunshineConf {
    param([string]$AdapterName)
    if ([string]::IsNullOrWhiteSpace($AdapterName)) { throw '未选择显卡（adapter_name）' }
    $confDir = Split-Path -Parent $ConfPath
    if (-not (Test-Path $confDir)) { New-Item -ItemType Directory -Path $confDir -Force | Out-Null }
    $lines = @(
        "adapter_name = $AdapterName",
        'address_family = both',
        'gamepad = x360',
        'install_steam_audio_drivers = disabled',
        'keep_sink_default = disabled',
        'server_cmd = [{"cmd":"bubbles.scr","elevated":false,"name":"Bubbles"}]',
        'upnp = enabled'
    )
    [IO.File]::WriteAllLines($ConfPath, $lines, (New-Object System.Text.UTF8Encoding($false)))
    Write-Log "OK: sunshine.conf 已写入（adapter_name = $AdapterName）"
}

# 1 · 防火墙放行 Sunshine（默认勾选，可取消）：直接调用现成 scripts\add-firewall-rule.bat，
#     为 sunshine.exe 放行 TCP/UDP 入站（规则名 Apollo，路径由 bat 自身相对推导）
function Install-FirewallRule {
    $bat = Join-Path $PSScriptRoot 'add-firewall-rule.bat'
    if (-not (Test-Path $bat)) { throw "未找到 $bat" }
    Write-Log '执行 scripts\add-firewall-rule.bat（放行 TCP/UDP 入站，规则名 Apollo）...'
    Start-Process -FilePath $bat -WindowStyle Hidden -Wait
    $rules = @(Get-NetFirewallRule -DisplayName 'Apollo' -ErrorAction SilentlyContinue)
    if ($rules.Count -ge 1) {
        Write-Log "OK: 防火墙已放行 Sunshine（规则 Apollo，共 $($rules.Count) 条）"
    } else {
        Write-Log '警告: 未检测到规则 Apollo，请手动运行 scripts\add-firewall-rule.bat'
    }
}

# 1 · 虚拟手柄驱动（默认勾选，可取消）：直接调用现成 scripts\install-gamepad.ps1
#     安装 ViGEmBus（串流手柄虚拟为 Xbox 360 手柄；已装 1.17+ 时脚本自身跳过）
function Install-Gamepad {
    $ps1 = Join-Path $PSScriptRoot 'install-gamepad.ps1'
    if (-not (Test-Path $ps1)) { throw "未找到 $ps1" }
    $sys = Join-Path $env:SystemRoot 'System32\drivers\ViGEmBus.sys'
    $had = Test-Path $sys
    Write-Log '执行 scripts\install-gamepad.ps1（ViGEmBus 被动模式安装，结束时可能弹重启提示）...'
    $p = Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $ps1))
    $null = $p.WaitForExit(120000)
    # 安装器为异步被动模式：等驱动文件就位或安装器退出（最多 5 分钟，期间保持界面刷新）
    for ($i = 0; $i -lt 300; $i++) {
        if (-not $had -and (Test-Path $sys)) { break }
        $busy = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'vigembus' })
        if ($had -and $busy.Count -eq 0) { break }
        Start-Sleep -Seconds 1
        Update-Ui
    }
    if (Test-Path $sys) {
        $ver = ''
        try { $ver = (Get-Item $sys).VersionInfo.FileVersion } catch { }
        if ($ver) { Write-Log "OK: ViGEmBus 虚拟手柄驱动已就绪（版本 $ver）" }
        else { Write-Log 'OK: ViGEmBus 虚拟手柄驱动已就绪' }
    } else {
        Write-Log '警告: 未检测到 ViGEmBus.sys，请手动运行 scripts\install-gamepad.ps1 确认'
    }
}

# 4 · 清空状态与日志（可选：解绑所有已配对设备，执行前二次确认）
function Reset-SunshineState {
    if (Test-Path $StatePath) {
        [IO.File]::WriteAllText($StatePath, '{}', (New-Object System.Text.UTF8Encoding($false)))
        Write-Log 'OK: sunshine_state.json 已清空（已配对设备全部移除，Moonlight 客户端需重新配对）'
    } else {
        Write-Log '跳过: sunshine_state.json 不存在'
    }
    if (Test-Path $LogPath) {
        [IO.File]::WriteAllText($LogPath, '')
        Write-Log 'OK: sunshine.log 已清空'
    }
    if (Test-Path $LogBakPath) {
        Remove-Item -LiteralPath $LogBakPath -Force
        Write-Log 'OK: sunshine.log.backup 已删除'
    } else {
        Write-Log '跳过: sunshine.log.backup 不存在'
    }
}

# 2 · rdpWrapper 静默安装（必装，官方命令行方式，>=2.4 支持 -install）
function Install-RdpWrapper {
    if (-not (Test-Path $RdpWrapExe)) { throw "未找到 $RdpWrapExe" }
    Write-Log '执行 rdpWrapper.exe -install（静默安装，解锁单用户并发 RDP）...'
    $p = Start-Process -FilePath $RdpWrapExe -ArgumentList '-install' -PassThru
    if (-not $p.WaitForExit(180000)) {
        try { $p.Kill() } catch { }
        throw 'rdpWrapper 安装超时（180 秒），请手动打开 tools\rdpWrapper.exe 图形界面安装'
    }
    if ($p.ExitCode -ne 0) {
        throw "rdpWrapper -install 返回码 $($p.ExitCode)，请手动打开 tools\rdpWrapper.exe 图形界面安装"
    }
    if (Test-Path $RdpWrapDir) {
        Write-Log "OK: RDP Wrapper 已安装（$RdpWrapDir）"
    } else {
        Write-Log "警告: 未检测到 $RdpWrapDir，请打开 rdpWrapper.exe 图形界面手动确认安装状态"
    }
}

# 4 · GameManager（可选：可直接启动需管理员权限的游戏，快捷方式放 Links 目录）
function Install-GameManager {
    if (-not (Test-Path $GmExe)) { throw "未找到 $GmExe" }
    Write-Log '提示: GameManager 安装完成时会弹出一个英文确认框，点击 OK 即可'
    $svc = Get-Service -Name $GmSvcName -ErrorAction SilentlyContinue
    if ($svc) {
        Write-Log "跳过: 服务 $GmSvcName 已存在（状态 $($svc.Status)）"
    } else {
        Write-Log '执行 GameManager.exe --install-service（SYSTEM 服务、开机自启）...'
        $null = Start-Process -FilePath $GmExe -ArgumentList '--install-service' -PassThru
        $started = $false
        for ($i = 0; $i -lt 30; $i++) {
            Start-Sleep -Seconds 1
            Update-Ui
            $svc = Get-Service -Name $GmSvcName -ErrorAction SilentlyContinue
            if ($svc -and $svc.Status -eq 'Running') { $started = $true; break }
        }
        if (-not $started) { throw "GameManager 服务未能启动，查看 $($Root)\tools\install-log.txt" }
        Write-Log "OK: 服务 $GmSvcName 运行中"
    }
    Write-Log '执行 GameManager.exe --register-all（注册 steam:// 协议，全用户生效）...'
    $p = Start-Process -FilePath $GmExe -ArgumentList '--register-all' -PassThru
    $null = $p.WaitForExit(60000)
    if (Test-Path 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\steam') {
        Write-Log 'OK: steam:// 协议已注册（HKLM）'
    } else {
        Write-Log '警告: 未检测到 HKLM\Classes\steam 协议注册'
    }
    # 快捷方式目录指向 Sunshine\Links（写 GameManager.json + 建目录）
    $gmJsonPath = Join-Path (Split-Path -Parent $GmExe) 'GameManager.json'
    try {
        $gmJson = $null
        if (Test-Path $gmJsonPath) {
            try { $gmJson = Get-Content -LiteralPath $gmJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
        }
        if (-not $gmJson) { $gmJson = New-Object PSObject }
        if ($gmJson.PSObject.Properties['LinksFolder']) { $gmJson.LinksFolder = $LinksDir }
        else { $gmJson | Add-Member -NotePropertyName LinksFolder -NotePropertyValue $LinksDir }
        if (-not $gmJson.PSObject.Properties['StartFullScreen']) {
            $gmJson | Add-Member -NotePropertyName StartFullScreen -NotePropertyValue $true
        }
        $gmJson | ConvertTo-Json | Set-Content -LiteralPath $gmJsonPath -Encoding UTF8
        if (-not (Test-Path $LinksDir)) {
            New-Item -ItemType Directory -Path $LinksDir -Force | Out-Null
        }
        Write-Log "OK: 游戏菜单目录已设为 $LinksDir（把游戏快捷方式放进去）"
    } catch {
        Write-Log "警告: 写入 GameManager.json 失败: $($_.Exception.Message)"
    }
}

# 3 · wsguard 服务（必装，rdp.cred 由密码经 DPAPI 本机作用域加密生成）
function Install-Wsguard {
    param([string]$RdpUser, [string]$Password, [int]$Vw = 0, [int]$Vh = 0, [bool]$AutoLogon = $false)
    if (-not (Test-Path $WgExe)) { throw "未找到 $WgExe" }

    # rdp.cred：密码非空则（重新）生成；留空且已有则沿用
    if (-not [string]::IsNullOrEmpty($Password)) {
        Add-Type -AssemblyName System.Security
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Password)
        $enc = [System.Security.Cryptography.ProtectedData]::Protect(
            $bytes, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
        [IO.File]::WriteAllBytes($WgCredPath, $enc)
        $null = & icacls.exe $WgCredPath /inheritance:r /grant 'SYSTEM:F' /grant 'Administrators:F' 2>&1
        Write-Log "OK: rdp.cred 已生成（$RdpUser 密码，DPAPI 本机加密，仅 SYSTEM/管理员可解）"
    } elseif (Test-Path $WgCredPath) {
        Write-Log '密码留空: 沿用现有 wsguard\rdp.cred'
    } else {
        throw '未提供密码且 rdp.cred 不存在，无法安装 wsguard'
    }

    # config.json：不存在则生成默认配置；存在则同步 guard_apollo 与虚拟屏分辨率（热重载）
    if (Test-Path $WgCfgPath) {
        $cfg = Get-Content $WgCfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $cfg | Add-Member -NotePropertyName guard_apollo -NotePropertyValue $true -Force
        $cfg | Add-Member -NotePropertyName auto_logon -NotePropertyValue ([bool]$AutoLogon) -Force
        if ($Vw -ge 320 -and $Vh -ge 240) {
            $cfg | Add-Member -NotePropertyName width -NotePropertyValue $Vw -Force
            $cfg | Add-Member -NotePropertyName height -NotePropertyValue $Vh -Force
            $resNote = "$Vw x $Vh"
        } else {
            $resNote = '沿用现有'
        }
        [IO.File]::WriteAllText($WgCfgPath, ($cfg | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
        $alNote = if ($AutoLogon) { 'auto_logon=true' } else { 'auto_logon=false' }
        Write-Log "OK: config.json 已同步（guard_apollo=true / $alNote / 分辨率 $resNote，热重载生效）"
    } else {
        $w = if ($Vw -ge 320) { $Vw } else { 1600 }
        $h = if ($Vh -ge 240) { $Vh } else { 720 }
        $cfg = [ordered]@{
            username          = $RdpUser
            password_mode     = 'dpapi'
            cred_path         = $WgCredPath
            server            = '127.0.0.2'
            width             = $w
            height            = $h
            keepalive         = $true
            guard_uu          = $true
            guard_apollo      = $true
            display_keepalive = $true
            auto_logon        = [bool]$AutoLogon
            log_level         = 'warn'
            interval          = 60
            recycle_minutes   = 30
            gameviewer_versions = @('4.0.*')
        }
        $json = $cfg | ConvertTo-Json
        [IO.File]::WriteAllText($WgCfgPath, $json, (New-Object System.Text.UTF8Encoding($false)))
        $alNote2 = if ($AutoLogon) { '开机自动登录开' } else { '开机自动登录关' }
        Write-Log "OK: wsguard\config.json 已生成（默认参数：127.0.0.2 / ${w}x${h} / 30 分钟重建 / Apollo 守卫开 / $alNote2 / 日志仅警告与错误 / GameViewer 4.0.*）"
    }

    # 服务幂等注册
    if (Get-Service -Name $SvcName -ErrorAction SilentlyContinue) {
        Write-Log "已存在服务 $SvcName，先移除再重建..."
        $null = & sc.exe stop $SvcName 2>&1
        Start-Sleep -Seconds 2
        Get-Process -Name 'wsguard' -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        $null = & sc.exe delete $SvcName 2>&1
        Start-Sleep -Seconds 1
    }
    # 清理旧名服务（改名前残留，指向同目录）
    if ($LegacySvc -ne $SvcName -and (Get-Service -Name $LegacySvc -ErrorAction SilentlyContinue)) {
        $null = & sc.exe stop $LegacySvc 2>&1
        Start-Sleep -Seconds 2
        Get-Process -Name 'wsguard' -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        $null = & sc.exe delete $LegacySvc 2>&1
        Start-Sleep -Seconds 1
        Write-Log "已删除旧名服务 $LegacySvc（新名 $SvcName 已取代）"
    }
    $null = & sc.exe create $SvcName binPath= $WgExe start= auto DisplayName= $SvcDispName 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sc create 失败（错误码 $LASTEXITCODE）" }
    $null = & sc.exe description $SvcName 'Keeps the streaming user RDP session alive (headless client) and GameViewer pinned to the console session.' 2>&1
    $null = & sc.exe failure $SvcName reset= 86400 actions= 'restart/60000/restart/60000/restart/60000' 2>&1
    $null = & sc.exe start $SvcName 2>&1
    Start-Sleep -Seconds 3
    Update-Ui
    $svc = Get-Service -Name $SvcName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') {
        Write-Log "OK: 服务 $SvcName 运行中（SYSTEM / 自启动 / 崩溃 1 分钟自重启）"
    } elseif ($svc) {
        throw "服务已注册但状态为 $($svc.Status)，请查看 wsguard\watch.log"
    } else {
        throw "服务 $SvcName 注册失败"
    }
    $watch = Join-Path $WgDir 'watch.log'
    if (Test-Path $watch) {
        Get-Content -LiteralPath $watch -Tail 2 | ForEach-Object { Write-Log "  [watch] $_" }
    }
}

# ----------------------------------------------------------------------------
# 卸载操作（与安装界面分组一一对应，不删除程序文件、配置与凭据）
# ----------------------------------------------------------------------------
# 1 · 移除防火墙放行规则：直接调用现成 scripts\delete-firewall-rule.bat
function Uninstall-FirewallRule {
    $bat = Join-Path $PSScriptRoot 'delete-firewall-rule.bat'
    if (-not (Test-Path $bat)) { throw "未找到 $bat" }
    Write-Log '执行 scripts\delete-firewall-rule.bat（删除规则 Apollo）...'
    Start-Process -FilePath $bat -WindowStyle Hidden -Wait
    $rules = @(Get-NetFirewallRule -DisplayName 'Apollo' -ErrorAction SilentlyContinue)
    if ($rules.Count -eq 0) {
        Write-Log 'OK: 防火墙规则 Apollo 已移除'
    } else {
        Write-Log "警告: 仍有 $($rules.Count) 条 Apollo 规则，请手动运行 scripts\delete-firewall-rule.bat"
    }
}

# 1 · 卸载虚拟手柄驱动：直接调用现成 scripts\uninstall-gamepad.ps1（Win32_Product 方式）
function Uninstall-Gamepad {
    $ps1 = Join-Path $PSScriptRoot 'uninstall-gamepad.ps1'
    if (-not (Test-Path $ps1)) { throw "未找到 $ps1" }
    Write-Log '执行 scripts\uninstall-gamepad.ps1（Win32_Product 卸载，可能耗时数分钟）...'
    $p = Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $ps1))
    if (-not $p.WaitForExit(300000)) {
        try { $p.Kill() } catch { }
        throw 'uninstall-gamepad.ps1 执行超时（5 分钟），请手动运行确认'
    }
    $sys = Join-Path $env:SystemRoot 'System32\drivers\ViGEmBus.sys'
    if (Test-Path $sys) {
        Write-Log '警告: ViGEmBus.sys 仍存在，可能需重启电脑后才能彻底移除'
    } else {
        Write-Log 'OK: ViGEmBus 虚拟手柄驱动已卸载'
    }
}

# 3 · 反向操作：关闭 Apollo 守卫自启（config.json guard_apollo=false，热重载生效），
#     并清理旧自启机制残留（Run 键 / 计划任务）。wsguard 服务与显示保活功能不受影响。
function Uninstall-ApolloGuard {
    $any = $false
    if (Test-Path $WgCfgPath) {
        try {
            $cfg = Get-Content $WgCfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $cfg | Add-Member -NotePropertyName guard_apollo -NotePropertyValue $false -Force
            [IO.File]::WriteAllText($WgCfgPath, ($cfg | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
            Write-Log 'OK: config.json 已设置 guard_apollo=false（Apollo 守卫已关闭，热重载生效）'
            $any = $true
        } catch {
            Write-Log "警告: 无法更新 config.json: $($_.Exception.Message)"
        }
    }
    # 清理旧 Run 键残留（explorer 启动永不提升，且会与守卫双开冲突）
    foreach ($k in (Get-ChildItem 'Registry::HKEY_USERS' -ErrorAction SilentlyContinue)) {
        $sid = $k.PSChildName
        if ($sid -like '*_Classes' -or $sid -notlike 'S-1-5-21-*') { continue }
        $runKey = "Registry::HKEY_USERS\$sid\Software\Microsoft\Windows\CurrentVersion\Run"
        if (-not (Test-Path $runKey)) { continue }
        $props = Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue
        if ($props.PSObject.Properties.Name -contains $RunValueName) {
            Remove-ItemProperty -Path $runKey -Name $RunValueName -ErrorAction SilentlyContinue
            Write-Log "OK: 已删除用户 hive [$sid] 的 Run 键 [$RunValueName]"
            $any = $true
        }
    }
    # 清理旧计划任务残留（任务计划 Job 会使 Apollo 的应用 spawn 报 err 5）
    foreach ($n in @($TaskName, $LegacyTask)) {
        if (Get-ScheduledTask -TaskName $n -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName $n -Confirm:$false
            Write-Log "OK: 已删除计划任务 [$n]"
            $any = $true
        }
    }
    if (-not $any) { Write-Log '跳过: 无 Apollo 守卫配置与旧自启残留' }
    Write-Log '提示: 运行中的 sunshine.exe 不受影响，可手动关闭'
}

# 3 · 删除 wsguard 服务（程序文件与 rdp.cred 保留）
function Uninstall-Wsguard {
    $any = $false
    foreach ($n in @($SvcName, $LegacySvc)) {
        $svc = Get-Service -Name $n -ErrorAction SilentlyContinue
        if (-not $svc) { continue }
        $null = & sc.exe stop $n 2>&1
        for ($i = 0; $i -lt 10; $i++) {
            if (-not (Get-Process -Name 'wsguard' -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Seconds 1
            Update-Ui
        }
        Get-Process -Name 'wsguard' -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        $null = & sc.exe delete $n 2>&1
        if (Get-Service -Name $n -ErrorAction SilentlyContinue) {
            Write-Log "警告: sc delete $n 失败，请稍后手动执行: sc delete $n"
        } else {
            Write-Log "OK: 服务 $n 已删除（wsguard 程序文件与 rdp.cred 均保留）"
        }
        $any = $true
    }
    if (-not $any) { Write-Log '跳过: wsguard 服务不存在（可能已卸载）' }
    Write-Log '提示: 与 UU 远程的会话兼容组件仍驻留内存，重启电脑或重启 UU 服务后彻底清除'
}

# 2 · 卸载 rdpWrapper（官方命令行 -uninstall）
function Uninstall-RdpWrapper {
    if (-not (Test-Path $RdpWrapExe)) { throw "未找到 $RdpWrapExe" }
    if (-not (Test-Path $RdpWrapDir)) { Write-Log "跳过: $RdpWrapDir 不存在（未安装）"; return }
    Write-Log '执行 rdpWrapper.exe -uninstall ...'
    Write-Log '提示: 卸载会重启终端服务，若正通过 RDP 远程操作本机，连接可能短暂中断'
    $p = Start-Process -FilePath $RdpWrapExe -ArgumentList '-uninstall' -PassThru
    if (-not $p.WaitForExit(180000)) {
        try { $p.Kill() } catch { }
        throw 'rdpWrapper 卸载超时，请手动打开 tools\rdpWrapper.exe 图形界面卸载'
    }
    if (Test-Path $RdpWrapDir) {
        throw "卸载后 $RdpWrapDir 仍存在，请手动打开 rdpWrapper.exe 确认"
    }
    Write-Log 'OK: RDP Wrapper 已卸载'
}

# 4 · 卸载 GameManager（可选组件）
function Uninstall-GameManager {
    if (-not (Test-Path $GmExe)) { throw "未找到 $GmExe" }
    Write-Log '提示: GameManager 卸载时会弹出英文确认框，点击 OK 即可'
    $svc = Get-Service -Name $GmSvcName -ErrorAction SilentlyContinue
    if ($svc) {
        Write-Log '执行 GameManager.exe --uninstall-service ...'
        $null = Start-Process -FilePath $GmExe -ArgumentList '--uninstall-service' -PassThru
        $gone = $false
        for ($i = 0; $i -lt 30; $i++) {
            Start-Sleep -Seconds 1
            Update-Ui
            if (-not (Get-Service -Name $GmSvcName -ErrorAction SilentlyContinue)) { $gone = $true; break }
        }
        if ($gone) { Write-Log "OK: 服务 $GmSvcName 已移除" }
        else { Write-Log "警告: $GmSvcName 仍存在，请手动执行 sc delete $GmSvcName" }
    } else {
        Write-Log "跳过: 服务 $GmSvcName 不存在"
    }
    Write-Log '执行 GameManager.exe --unregister-all（取消 steam:// 协议注册）...'
    $p = Start-Process -FilePath $GmExe -ArgumentList '--unregister-all' -PassThru
    $null = $p.WaitForExit(60000)
    Get-Process -Name 'GameManager' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    if (Test-Path 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\steam') {
        Write-Log '警告: HKLM\Classes\steam 仍存在（也可能属于 Steam 本体，请自行确认）'
    } else {
        Write-Log 'OK: steam:// 协议注册已移除'
    }
}

# ----------------------------------------------------------------------------
# XAML（界面）
# ----------------------------------------------------------------------------
$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Sunshine 串流环境部署工具" Width="800" Height="720"
        MinWidth="760" MinHeight="660" WindowStartupLocation="CenterScreen"
        Background="#EEF1F6">
  <DockPanel>
    <Border DockPanel.Dock="Top" Background="#0F172A" Padding="16,12">
      <StackPanel>
        <TextBlock Text="Sunshine 串流环境部署工具" Foreground="White" FontSize="17" FontWeight="Bold"/>
        <StackPanel Orientation="Horizontal" Margin="0,5,0,0">
          <TextBlock Text="程序位置：" Foreground="#94A3B8" FontSize="11" VerticalAlignment="Center"/>
          <TextBlock x:Name="lblRoot" Foreground="#CBD5E1" FontSize="11" VerticalAlignment="Center"/>
        </StackPanel>
      </StackPanel>
    </Border>
    <TabControl x:Name="tabs" Margin="10,8,10,10">
      <TabItem Header=" 安装 ">
        <ScrollViewer VerticalScrollBarVisibility="Auto">
          <StackPanel Margin="10">
            <GroupBox Header="1 · Sunshine 串流服务（必装）">
              <StackPanel Margin="6">
                <CheckBox x:Name="chkConf" IsChecked="True" IsEnabled="False"
                          Content="写入默认配置（显卡配置 / 手柄 / UPnP 等）"/>
                <StackPanel Orientation="Horizontal" Margin="0,6,0,0">
                  <TextBlock Text="渲染显卡选择：" VerticalAlignment="Center"/>
                  <ComboBox x:Name="cmbGpu" Width="360"/>
                </StackPanel>
                <TextBlock x:Name="lblGpuInfo" Foreground="#64748B" FontSize="11"
                           Margin="0,4,0,0" TextWrapping="Wrap" Text="检测中..."/>
                <CheckBox x:Name="chkFirewall" IsChecked="True" Margin="0,8,0,0"
                          Content="防火墙放行 Sunshine（TCP / UDP 入站，规则名 Apollo）"/>
                <CheckBox x:Name="chkGamepad" IsChecked="True" Margin="0,6,0,0"
                          Content="虚拟手柄驱动（ViGEmBus）"/>
                <TextBlock FontSize="11" Foreground="#64748B" Margin="20,2,0,0" TextWrapping="Wrap"
                           Text="已装 1.17 或更新版本时自动跳过"/>
              </StackPanel>
            </GroupBox>
            <GroupBox Header="2 · 远程桌面解锁（必装）" Margin="0,8,0,0">
              <StackPanel Margin="6">
                <CheckBox x:Name="chkRdpWrap" IsChecked="True" IsEnabled="False"
                          Content="RdpWrapper：允许同一电脑可同时登录主用户和串流用户"/>
              </StackPanel>
            </GroupBox>
            <GroupBox Header="3 · 串流会话守护服务（必装）" Margin="0,8,0,0">
              <StackPanel Margin="6">
                <StackPanel Orientation="Horizontal">
                  <TextBlock Text="当前用户：" VerticalAlignment="Center" FontWeight="Bold"/>
                  <TextBlock x:Name="lblUser" VerticalAlignment="Center" Margin="4,0,0,0"/>
                </StackPanel>
                <TextBlock Foreground="#DC2626" FontSize="11" Margin="0,4,0,0" TextWrapping="Wrap"
                           Text="当前用户将设置为串流用户；如需更换用户，请先登录对应用户会话再运行本工具。"/>
                <StackPanel Orientation="Horizontal" Margin="0,6,0,0">
                  <TextBlock Text="登录密码：" VerticalAlignment="Center"/>
                  <PasswordBox x:Name="pwdRdp" Width="200"/>
                  <Button x:Name="btnVerifyPw" Content="验证" Width="60" Margin="6,0,0,0"/>
                  <TextBlock x:Name="lblPwState" Margin="8,0,0,0" VerticalAlignment="Center"
                             FontSize="11" Foreground="#64748B"/>
                </StackPanel>
                <TextBlock x:Name="lblCredState" Foreground="#64748B" FontSize="11" Margin="0,4,0,0"/>
                <CheckBox x:Name="chkAutoLogon" IsChecked="True" Margin="0,8,0,0"
                          Content="开机自动登录串流用户（无人值守）"/>
                <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
                  <TextBlock Text="虚拟屏分辨率：" VerticalAlignment="Center"/>
                  <TextBox x:Name="txtWidth" Width="64" Text="1600" VerticalAlignment="Center"/>
                  <TextBlock Text=" × " VerticalAlignment="Center"/>
                  <TextBox x:Name="txtHeight" Width="64" Text="720" VerticalAlignment="Center"/>
                  <TextBlock Text="（宽 × 高）" Foreground="#64748B" FontSize="11"
                             VerticalAlignment="Center" Margin="6,0,0,0"/>
                </StackPanel>
                <CheckBox x:Name="chkWsguard" IsChecked="True" IsEnabled="False" Margin="0,8,0,0"
                          Content="WSGuard：启用串流会话守护服务（断开串流后自动保持虚拟屏在线，并开机自启 Sunshine）"/>
                <TextBlock x:Name="lblWgWarn" Foreground="#DC2626" FontSize="11"
                           Margin="0,4,0,0" TextWrapping="Wrap" Text="" Visibility="Collapsed"/>
              </StackPanel>
            </GroupBox>
            <GroupBox Header="4 · 可选功能" Margin="0,8,0,0">
              <StackPanel Margin="6">
                <CheckBox x:Name="chkGameMgr"
                          Content="游戏菜单（可直接启动需要管理员权限的游戏，建议安装）"/>
                <TextBlock FontSize="11" Foreground="#64748B" Margin="20,2,0,0" TextWrapping="Wrap"
                           Text="装好后把游戏快捷方式放进 Sunshine 目录里的 Links 文件夹"/>
                <CheckBox x:Name="chkReset" Margin="0,6,0,0"
                          Content="解绑所有已配对设备（手机 / 平板 / 电视需重新配对）"/>
                <TextBlock Foreground="#DC2626" FontSize="11" Margin="20,2,0,0" TextWrapping="Wrap"
                           Text="点开始后会再问一次，确认才执行。"/>
              </StackPanel>
            </GroupBox>
            <Button x:Name="btnInstall" Content="开 始 安 装" Height="36" Margin="0,12,0,0"
                    Background="#2563EB" Foreground="White" FontWeight="Bold" FontSize="13"
                    BorderThickness="0"/>
          </StackPanel>
        </ScrollViewer>
      </TabItem>
      <TabItem Header=" 卸载 ">
        <ScrollViewer VerticalScrollBarVisibility="Auto">
          <StackPanel Margin="10">
            <GroupBox Header="1 · Sunshine 串流服务">
              <StackPanel Margin="6">
                <CheckBox x:Name="chkUFirewall" IsChecked="True" Margin="0,2"
                          Content="移除防火墙放行规则（Apollo）"/>
                <CheckBox x:Name="chkUGamepad" Margin="0,2"
                          Content="卸载虚拟手柄驱动（ViGEmBus，其他程序依赖它时请勿勾选）"/>
              </StackPanel>
            </GroupBox>
            <GroupBox Header="2 · 远程桌面解锁" Margin="0,8,0,0">
              <StackPanel Margin="6">
                <CheckBox x:Name="chkURdpWrap" Margin="0,2"
                          Content="卸载 RdpWrapper（恢复系统默认的单会话限制）"/>
              </StackPanel>
            </GroupBox>
            <GroupBox Header="3 · 串流会话守护服务" Margin="0,8,0,0">
              <StackPanel Margin="6">
                <CheckBox x:Name="chkUWsguard" IsChecked="True" Margin="0,2"
                          Content="卸载 WSGuard 服务并取消 Sunshine 开机自启（保留密码与设置）"/>
              </StackPanel>
            </GroupBox>
            <GroupBox Header="4 · 可选功能" Margin="0,8,0,0">
              <StackPanel Margin="6">
                <CheckBox x:Name="chkUGameMgr" Margin="0,2"
                          Content="卸载游戏菜单（GameManager）"/>
              </StackPanel>
            </GroupBox>
            <TextBlock Foreground="#64748B" FontSize="11" Margin="2,8,2,0" TextWrapping="Wrap"
                       Text="卸载不会删除 Sunshine 本体和任何游戏存档，也不删除配置与已保存的密码。卸载远程桌面解锁或手柄驱动时屏幕可能闪一下，属正常现象；若正远程操作本机，连接会断开重连一次；手柄驱动可能需重启电脑后才彻底移除。"/>
            <Button x:Name="btnUninstall" Content="开 始 卸 载" Height="36" Margin="0,12,0,0"
                    Background="#DC2626" Foreground="White" FontWeight="Bold" FontSize="13"
                    BorderThickness="0"/>
          </StackPanel>
        </ScrollViewer>
      </TabItem>
      <TabItem Header=" 运行日志 ">
        <TextBox x:Name="txtLog" IsReadOnly="True" FontFamily="Consolas" FontSize="11.5"
                 Background="#0F172A" Foreground="#D1E3F8" BorderThickness="0" Padding="6"
                 VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Auto"
                 TextWrapping="NoWrap" Margin="2,4,2,6"/>
      </TabItem>
    </TabControl>
  </DockPanel>
</Window>
'@

# ----------------------------------------------------------------------------
# 自检模式（不做任何系统变更）
# ----------------------------------------------------------------------------
function Invoke-SelfTest {
    $script:SelfTestFail = 0
    function Check([string]$Title, [scriptblock]$Body) {
        try {
            $r = & $Body
            if ($r -is [bool] -and -not $r) { throw 'returned false' }
            Write-Host "  [PASS] $Title"
        } catch {
            $script:SelfTestFail++
            Write-Host "  [FAIL] $Title :: $($_.Exception.Message)"
        }
    }

    Write-Host "== Sunshine Setup SelfTest =="
    Write-Host "Root = $Root"

    Check '根目录定位（sunshine.exe 存在）' { Test-Path (Join-Path $Root 'sunshine.exe') }
    Check 'tools\rdpWrapper.exe 存在'      { Test-Path $RdpWrapExe }
    Check 'tools\GameManager.exe 存在'     { Test-Path $GmExe }
    Check 'wsguard\wsguard.exe 存在'       { Test-Path $WgExe }
    Check 'scripts\add-firewall-rule.bat 存在'    { Test-Path (Join-Path $PSScriptRoot 'add-firewall-rule.bat') }
    Check 'scripts\delete-firewall-rule.bat 存在' { Test-Path (Join-Path $PSScriptRoot 'delete-firewall-rule.bat') }
    Check 'scripts\install-gamepad.ps1 存在'      { Test-Path (Join-Path $PSScriptRoot 'install-gamepad.ps1') }
    Check 'scripts\uninstall-gamepad.ps1 存在'    { Test-Path (Join-Path $PSScriptRoot 'uninstall-gamepad.ps1') }
    Check 'scripts\vigembus_installer.exe 存在'  { Test-Path (Join-Path $PSScriptRoot 'vigembus_installer.exe') }
    Check 'config 目录存在'                { Test-Path (Join-Path $Root 'config') }

    $gpus = Get-PhysicalGpus
    Write-Host "  [INFO] 物理显卡枚举（已过滤虚拟显卡）：$($gpus.Count) 个"
    foreach ($g in $gpus) { Write-Host "         - $($g.Name)  [$($g.Vendor)]" }
    Check '物理显卡枚举非空' { $gpus.Count -ge 1 }

    # wsguard 版本检查：读 build.info（build.ps1 部署时生成；旧版无此文件）
    $script:WgNewStyle = $false
    try {
        $info = Get-Content (Join-Path $WgDir 'build.info') -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
        $script:WgNewStyle = ($info.service_name -eq 'WSGuard') -and ($info.style -eq 'dynamic-path')
        if ($script:WgNewStyle) {
            Write-Host "  [INFO] wsguard.exe 为新版（服务名 WSGuard / 动态路径，built $($info.built)）"
        }
    } catch { }
    if (-not $script:WgNewStyle) {
        Write-Host '  [WARN] wsguard.exe 似乎是旧版（无 build.info 或版本不匹配），建议用 wsguard\src\build.ps1 -Deploy 重打包部署'
    }
    Check 'wsguard.exe 为新版（WSGuard / 动态路径）' { $script:WgNewStyle -eq $true }

    # XAML 解析
    Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
    $win = $null
    Check 'XAML 解析' {
        $script:win = [Windows.Markup.XamlReader]::Parse($xaml)
        $script:win -is [System.Windows.Window]
    }
    foreach ($n in @('lblRoot','lblUser','txtLog','tabs','chkConf','cmbGpu','lblGpuInfo',
                     'chkFirewall','chkGamepad','chkRdpWrap','chkAutoLogon','chkWsguard',
                     'pwdRdp','btnVerifyPw','lblPwState','lblCredState','lblWgWarn',
                     'chkGameMgr','chkReset','btnInstall',
                     'chkUFirewall','chkUGamepad','chkURdpWrap','chkUWsguard','chkUGameMgr','btnUninstall')) {
        Check "控件 $n" { $script:win.FindName($n) -ne $null }
    }

    # DPAPI roundtrip（LocalMachine 作用域，与 wsguard 解密方式兼容）
    Check 'DPAPI 加解密 roundtrip' {
        Add-Type -AssemblyName System.Security
        $plain = 'Test-Password-123'
        $enc = [System.Security.Cryptography.ProtectedData]::Protect(
            [Text.Encoding]::UTF8.GetBytes($plain), $null,
            [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
        $dec = [System.Security.Cryptography.ProtectedData]::Unprotect(
            $enc, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
        [Text.Encoding]::UTF8.GetString($dec) -eq $plain
    }

    # LogonUser 密码验证类型可加载（不实际验证）
    Check 'LogonUser 验证类型加载' {
        if (-not ('PwCheck' -as [type])) {
            Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class PwCheck {
    [DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern bool LogonUser(string user, string domain, string pass, int logonType, int provider, out IntPtr token);
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);
}
"@
        }
        ('PwCheck' -as [type]) -ne $null
    }

    # 显卡列表拼接（不使用 -f，免疫特殊字符）
    Check '显卡列表拼接（字符串拼接免疫特殊字符）' {
        $fake = @(
            [PSCustomObject]@{ Name = 'AMD Radeon 780M {Graphics}'; Vendor = 'AMD' },
            [PSCustomObject]@{ Name = 'NVIDIA GeForce {RTX} 4060'; Vendor = 'NVIDIA' }
        )
        $labels = @()
        foreach ($g in $fake) {
            $labels += ([string]$g.Name + '  [' + [string]$g.Vendor + ']')
        }
        ($labels.Count -eq 2) -and ($labels[0].Contains('AMD')) -and ($labels[1].Contains('NVIDIA'))
    }

    # Apollo 守卫配置项构建（guard_apollo + 清理旧机制列表）
    Check 'Apollo 守卫配置项构建' {
        $names = @($TaskName, $LegacyTask)
        ($names.Count -eq 2) -and ($names -contains 'Sunshine Autostart (Game)')
    }

    # conf 模板渲染（写入 .temp 预览，不动真实文件）
    Check 'sunshine.conf 模板渲染' {
        $dir = Join-Path $Root '.temp'
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $preview = Join-Path $dir 'sunshine.conf.preview'
        $adapter = if ($gpus.Count) { $gpus[0].Name } else { 'Test GPU' }
        $lines = @(
            "adapter_name = $adapter",
            'address_family = both',
            'gamepad = x360',
            'install_steam_audio_drivers = disabled',
            'keep_sink_default = disabled',
            'server_cmd = [{"cmd":"bubbles.scr","elevated":false,"name":"Bubbles"}]',
            'upnp = enabled'
        )
        [IO.File]::WriteAllLines($preview, $lines, (New-Object System.Text.UTF8Encoding($false)))
        $txt = [IO.File]::ReadAllText($preview)
        ($txt -match 'adapter_name') -and ($txt -match 'bubbles\.scr') -and ($txt -match 'upnp = enabled')
    }

    # config.json 模板
    Check 'wsguard config.json 模板' {
        $cfg = [ordered]@{
            username = 'Game'; password_mode = 'dpapi'; cred_path = $WgCredPath
            server = '127.0.0.2'; width = 1600; height = 720
            keepalive = $true; guard_uu = $true; guard_apollo = $true; display_keepalive = $true
            auto_logon = $true; log_level = 'warn'
            interval = 60; recycle_minutes = 30
            gameviewer_versions = @('4.0.*')
        }
        $j = $cfg | ConvertTo-Json
        ($j -match '127\.0\.0\.2') -and ($j -match 'dpapi') -and ($j -match 'guard_apollo') -and ($j -match 'auto_logon') -and ($j -match 'log_level') -and ($j -match 'gameviewer_versions')
    }

    if ($script:SelfTestFail -gt 0) {
        Write-Host "== SelfTest FAILED ($($script:SelfTestFail) 项) =="
        exit 1
    }
    Write-Host "== SelfTest ALL PASS =="
    exit 0
}

# ----------------------------------------------------------------------------
# 入口
# ----------------------------------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($SelfTest) {
    Invoke-SelfTest
}

# GUI 需要管理员（注册服务 / 防火墙规则 / 驱动安装）；非管理员时自我提权重启
if (-not $isAdmin) {
    Write-Host '需要管理员权限，正在请求 UAC...'
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-File', ('"{0}"' -f $PSCommandPath))
    exit
}

# ---- 加载 GUI ----
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$window = [Windows.Markup.XamlReader]::Parse($xaml)
$script:Window = $window

$ui = @{}
foreach ($n in @('lblRoot','lblUser','tabs','txtLog','chkConf','cmbGpu','lblGpuInfo',
                 'chkFirewall','chkGamepad','chkRdpWrap','chkAutoLogon','chkWsguard',
                 'pwdRdp','btnVerifyPw','lblPwState','lblCredState','lblWgWarn',
                 'chkGameMgr','chkReset','btnInstall',
                 'chkUFirewall','chkUGamepad','chkURdpWrap','chkUWsguard','chkUGameMgr','btnUninstall')) {
    $ui[$n] = $window.FindName($n)
}
$script:LogBox = $ui['txtLog']

# ---- 初始化界面状态 ----
$ui['lblRoot'].Text = $Root

# 当前登录用户（提权后 WindowsIdentity 仍是原用户；回退链：USERNAME -> COMPUTERNAME）
$script:CurUser = $env:USERNAME
try {
    $script:CurUser = [Environment]::UserDomainName + '\' + [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $script:CurUser = ($script:CurUser -split '\\')[-1]
} catch { }
$ui['lblUser'].Text = $script:CurUser

$gpus = Get-PhysicalGpus
$script:GpuNames = @($gpus | ForEach-Object { $_.Name })
foreach ($g in $gpus) {
    $label = [string]$g.Name + '  [' + [string]$g.Vendor + ']'
    $null = $ui['cmbGpu'].Items.Add($label)
}
if ($script:GpuNames.Count -eq 1) {
    $ui['lblGpuInfo'].Text = '已自动选中检测到的显卡。'
} elseif ($script:GpuNames.Count -gt 1) {
    $ui['lblGpuInfo'].Text = '电脑里有多块显卡，请选择用来串流的那块。'
} else {
    $ui['lblGpuInfo'].Text = '没有检测到可用显卡，请先确认显卡驱动已安装，再重新打开本工具。'
}
# 预选当前 conf 里已有的 adapter_name
$curAd = $null
try {
    $cur = (Get-Content -LiteralPath $ConfPath -Encoding UTF8 -ErrorAction Stop |
        Where-Object { $_ -match '^\s*adapter_name\s*=\s*(.+)$' } | Select-Object -First 1)
    if ($cur -match '=\s*(.+)$') { $curAd = $Matches[1].Trim() }
} catch { }
if ($curAd) {
    $idx = [array]::IndexOf([string[]]($script:GpuNames | ForEach-Object { $_.ToLower() }), $curAd.ToLower())
    if ($idx -ge 0) { $ui['cmbGpu'].SelectedIndex = $idx }
}

# 预填当前 wsguard config.json 里的虚拟屏分辨率
try {
    $wg = Get-Content -LiteralPath $WgCfgPath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
    if ($wg.width)  { $ui['txtWidth'].Text  = [string][int]$wg.width }
    if ($wg.height) { $ui['txtHeight'].Text = [string][int]$wg.height }
} catch { }
if (-not $curAd -and $script:GpuNames.Count -ge 1) { $ui['cmbGpu'].SelectedIndex = 0 }

# rdp.cred 状态（密码输入在第 3 组“串流会话守护服务”）
if (Test-Path $WgCredPath) {
    $ui['lblCredState'].Text = '已保存过密码，留空则继续用原来的'
} else {
    $ui['lblCredState'].Text = '请输入登录密码并点“验证”'
}
$script:PwVerified = $false

# wsguard 版本检查：读 build.info；旧版 exe 不兼容新服务名，提示并默认不勾选
$wgNewStyle = $false
try {
    $info = Get-Content (Join-Path $WgDir 'build.info') -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
    $wgNewStyle = ($info.service_name -eq 'WSGuard') -and ($info.style -eq 'dynamic-path')
} catch { }
if (-not $wgNewStyle) {
    $ui['lblWgWarn'].Text = 'wsguard.exe 是旧版本，需要先用 wsguard\src\build.ps1 重新打包后才能安装。'
    $ui['lblWgWarn'].Visibility = 'Visible'
    $ui['chkWsguard'].IsChecked = $false
}

# ---- 密码验证按钮 ----
$ui['btnVerifyPw'].Add_Click({
    $pw = $ui['pwdRdp'].Password
    if ([string]::IsNullOrEmpty($pw)) {
        $ui['lblPwState'].Text = '请先输入密码'
        $ui['lblPwState'].Foreground = [System.Windows.Media.Brushes]::DarkOrange
        $script:PwVerified = $false
        return
    }
    $ui['lblPwState'].Text = '验证中...'
    $ui['lblPwState'].Foreground = [System.Windows.Media.Brushes]::Gray
    if (Test-UserPassword -User $script:CurUser -Password $pw) {
        $ui['lblPwState'].Text = '密码正确'
        $ui['lblPwState'].Foreground = [System.Windows.Media.Brushes]::Green
        $script:PwVerified = $true
        Write-Log "密码验证通过（用户：$($script:CurUser)）"
    } else {
        $ui['lblPwState'].Text = '密码不对，请重新输入'
        $ui['lblPwState'].Foreground = [System.Windows.Media.Brushes]::Red
        $script:PwVerified = $false
    }
})

# ---- 安装按钮 ----
$ui['btnInstall'].Add_Click({
    # 组装执行步骤：1/2/3 组必装，第 4 组按勾选（成员检测，执行顺序见下方各 if 块）
    $steps = @('conf', 'rdpwrap', 'wsguard')
    if ($ui['chkFirewall'].IsChecked) { $steps += 'firewall' }
    if ($ui['chkGamepad'].IsChecked)  { $steps += 'gamepad' }
    if ($ui['chkGameMgr'].IsChecked)  { $steps += 'gamemgr' }
    if ($ui['chkReset'].IsChecked)    { $steps += 'reset' }

    $user = $script:CurUser
    if (-not $user) {
        [void][System.Windows.MessageBox]::Show($window, '无法获取当前用户名，请重试。', '错误',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Error)
        return
    }
    # 守护服务是必装项；旧版 wsguard.exe 时界面上会被置为未勾选，此时禁止安装
    if (-not $ui['chkWsguard'].IsChecked) {
        [void][System.Windows.MessageBox]::Show($window,
            'wsguard.exe 是旧版本，请先用 wsguard\src\build.ps1 -Deploy 重新打包后再安装。', '无法安装',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Error)
        return
    }
    # 密码验证（守护服务必装，验证通过才能继续）
    $pw = $ui['pwdRdp'].Password
    if (-not [string]::IsNullOrEmpty($pw)) {
        Write-Log "验证用户 $user 的登录密码..."
        if (-not (Test-UserPassword -User $user -Password $pw)) {
            [void][System.Windows.MessageBox]::Show($window, '密码不正确，请重新输入后再安装。', '密码验证失败',
                [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Error)
            return
        }
        $script:PwVerified = $true
        $ui['lblPwState'].Text = '密码正确'
        $ui['lblPwState'].Foreground = [System.Windows.Media.Brushes]::Green
        Write-Log 'OK: 密码验证通过'
    } elseif (-not (Test-Path $WgCredPath)) {
        [void][System.Windows.MessageBox]::Show($window, '请先在“串流会话守护服务”一栏输入登录密码并点“验证”。', '需要密码',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information)
        return
    }
    if (-not (Get-LocalUser -Name $user -ErrorAction SilentlyContinue)) {
        [void][System.Windows.MessageBox]::Show($window, "本机不存在用户「$user」，请检查用户名。", '错误',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Error)
        return
    }
    if ($ui['cmbGpu'].SelectedIndex -lt 0) {
        [void][System.Windows.MessageBox]::Show($window, '请先选择渲染显卡。', '提示',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information)
        return
    }
    # 解绑已配对设备：二次确认
    if ($steps -contains 'reset') {
        $r = [System.Windows.MessageBox]::Show($window,
            "确定清空已配对设备吗？`n清空后所有 Moonlight 客户端都需要重新配对。", '确认',
            [System.Windows.MessageBoxButton]::YesNo, [System.Windows.MessageBoxImage]::Warning)
        if ($r -ne [System.Windows.MessageBoxResult]::Yes) {
            $steps = @($steps | Where-Object { $_ -ne 'reset' })
            Write-Log '用户取消：跳过状态与日志清理'
        }
    }

    # 切到「运行日志」选项卡，执行过程实时可见
    $ui['tabs'].SelectedIndex = 2
    $window.Cursor = [System.Windows.Input.Cursors]::Wait
    $results = New-Object System.Collections.ArrayList
    try {
        # ── 1 · Sunshine 串流服务 ──
        Write-Log '── [1/安装] sunshine.conf 默认配置 ──'
        try {
            $adapter = $script:GpuNames[$ui['cmbGpu'].SelectedIndex]
            Write-SunshineConf -AdapterName $adapter
            [void]$results.Add('[OK] 默认配置')
        } catch { Write-Log "失败: $($_.Exception.Message)"; [void]$results.Add('[失败] 默认配置') }

        if ($steps -contains 'firewall') {
            Write-Log '── [1/安装] 防火墙放行 Sunshine ──'
            try { Install-FirewallRule; [void]$results.Add('[OK] 防火墙放行') }
            catch { Write-Log "失败: $($_.Exception.Message)"; [void]$results.Add('[失败] 防火墙放行') }
        }
        if ($steps -contains 'gamepad') {
            Write-Log '── [1/安装] 虚拟手柄驱动（ViGEmBus）──'
            try { Install-Gamepad; [void]$results.Add('[OK] 虚拟手柄') }
            catch { Write-Log "失败: $($_.Exception.Message)"; [void]$results.Add('[失败] 虚拟手柄') }
        }

        # ── 2 · 远程桌面解锁 ──
        Write-Log '── [2/安装] RDP Wrapper 远程桌面解锁 ──'
        try { Install-RdpWrapper; [void]$results.Add('[OK] 远程桌面解锁') }
        catch { Write-Log "失败: $($_.Exception.Message)"; [void]$results.Add('[失败] 远程桌面解锁') }

        # ── 3 · 串流会话守护服务 ──
        # 虚拟屏分辨率（守护服务用，先解析一次）
        $script:Vw = 0; $script:Vh = 0
        $wTxt = '{0}' -f $ui['txtWidth'].Text; $hTxt = '{0}' -f $ui['txtHeight'].Text
        if ($wTxt -match '^\s*(\d{3,5})\s*$') { $script:Vw = [int]$Matches[1] }
        if ($hTxt -match '^\s*(\d{3,5})\s*$') { $script:Vh = [int]$Matches[1] }
        if (($script:Vw -lt 320) -or ($script:Vh -lt 240) -or ($script:Vw -gt 7680) -or ($script:Vh -gt 4320)) {
            $script:Vw = 0; $script:Vh = 0
            Write-Log "跳过: 虚拟屏分辨率输入无效 [$wTxt x $hTxt]，沿用现有配置"
        } else {
            Write-Log "虚拟屏分辨率: $($script:Vw) x $($script:Vh)"
        }
        Write-Log "── [3/安装] WSGuard 串流会话守护服务（用户：$user）──"
        try {
            Install-Wsguard -RdpUser $user -Password $ui['pwdRdp'].Password -Vw $script:Vw -Vh $script:Vh -AutoLogon ([bool]$ui['chkAutoLogon'].IsChecked)
            [void]$results.Add('[OK] WSGuard 守护服务')
        } catch { Write-Log "失败: $($_.Exception.Message)"; [void]$results.Add('[失败] WSGuard 守护服务') }
        Write-Log '── [3/安装] Sunshine 开机自启（Apollo 守卫）──'
        try { Install-ApolloGuard -User $user; [void]$results.Add('[OK] 开机自启') }
        catch { Write-Log "失败: $($_.Exception.Message)"; [void]$results.Add('[失败] 开机自启') }

        # ── 4 · 可选功能 ──
        if ($steps -contains 'gamemgr') {
            Write-Log '── [4/安装] GameManager 游戏菜单 ──'
            try { Install-GameManager; [void]$results.Add('[OK] 游戏菜单') }
            catch { Write-Log "失败: $($_.Exception.Message)"; [void]$results.Add('[失败] 游戏菜单') }
        }
        if ($steps -contains 'reset') {
            Write-Log '── [4/安装] 解绑所有已配对设备 ──'
            try { Reset-SunshineState; [void]$results.Add('[OK] 已解绑全部设备') }
            catch { Write-Log "失败: $($_.Exception.Message)"; [void]$results.Add('[失败] 解绑设备') }
        }
        if (Get-Process -Name 'sunshine' -ErrorAction SilentlyContinue) {
            Write-Log '提示: Sunshine 正在运行，需要重启 Sunshine 后新配置才生效'
        }
    } finally {
        $window.Cursor = [System.Windows.Input.Cursors]::Arrow
    }
    $summary = ($results -join "`n")
    [void][System.Windows.MessageBox]::Show($window, "安装执行完毕：`n`n$summary`n`n详细过程见「运行日志」选项卡。", '完成',
        [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information)
})

# ---- 卸载按钮 ----
$ui['btnUninstall'].Add_Click({
    # 与安装界面分组一一对应
    $steps = @()
    if ($ui['chkUFirewall'].IsChecked) { $steps += 'firewall' }
    if ($ui['chkUGamepad'].IsChecked)  { $steps += 'gamepad' }
    if ($ui['chkURdpWrap'].IsChecked)  { $steps += 'rdpwrap' }
    if ($ui['chkUWsguard'].IsChecked)  { $steps += 'wsguard' }
    if ($ui['chkUGameMgr'].IsChecked)  { $steps += 'gamemgr' }
    if (-not $steps) {
        [void][System.Windows.MessageBox]::Show($window, '请至少勾选一个卸载项。', '提示',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information)
        return
    }
    $r = [System.Windows.MessageBox]::Show($window,
        "确定执行反注册吗？`n$($steps -join ', ')`n（不删除任何程序文件、配置与凭据）", '确认',
        [System.Windows.MessageBoxButton]::YesNo, [System.Windows.MessageBoxImage]::Question)
    if ($r -ne [System.Windows.MessageBoxResult]::Yes) { return }

    # 切到「运行日志」选项卡，执行过程实时可见
    $ui['tabs'].SelectedIndex = 2
    $window.Cursor = [System.Windows.Input.Cursors]::Wait
    $results = New-Object System.Collections.ArrayList
    try {
        # ── 1 · Sunshine 串流服务 ──
        if ($steps -contains 'firewall') {
            Write-Log '── [1/卸载] 移除防火墙放行规则 ──'
            try { Uninstall-FirewallRule; [void]$results.Add('[OK] 已移除防火墙规则') }
            catch { Write-Log "失败: $($_.Exception.Message)"; [void]$results.Add('[失败] 防火墙规则') }
        }
        if ($steps -contains 'gamepad') {
            Write-Log '── [1/卸载] 虚拟手柄驱动（ViGEmBus）──'
            try { Uninstall-Gamepad; [void]$results.Add('[OK] 已卸载虚拟手柄驱动') }
            catch { Write-Log "失败: $($_.Exception.Message)"; [void]$results.Add('[失败] 虚拟手柄驱动') }
        }

        # ── 2 · 远程桌面解锁 ──
        if ($steps -contains 'rdpwrap') {
            Write-Log '── [2/卸载] RDP Wrapper ──'
            try { Uninstall-RdpWrapper; [void]$results.Add('[OK] 已卸载远程桌面解锁') }
            catch { Write-Log "失败: $($_.Exception.Message)"; [void]$results.Add('[失败] 远程桌面解锁') }
        }

        # ── 3 · 串流会话守护服务 ──
        if ($steps -contains 'wsguard') {
            Write-Log '── [3/卸载] 取消 Sunshine 开机自启（Apollo 守卫）──'
            try { Uninstall-ApolloGuard; [void]$results.Add('[OK] 已取消开机自启') }
            catch { Write-Log "失败: $($_.Exception.Message)"; [void]$results.Add('[失败] 取消自启') }
            Write-Log '── [3/卸载] WSGuard 服务 ──'
            try { Uninstall-Wsguard; [void]$results.Add('[OK] 已卸载守护服务') }
            catch { Write-Log "失败: $($_.Exception.Message)"; [void]$results.Add('[失败] 守护服务') }
        }

        # ── 4 · 可选功能 ──
        if ($steps -contains 'gamemgr') {
            Write-Log '── [4/卸载] GameManager ──'
            try { Uninstall-GameManager; [void]$results.Add('[OK] 已卸载游戏菜单') }
            catch { Write-Log "失败: $($_.Exception.Message)"; [void]$results.Add('[失败] 游戏菜单') }
        }
    } finally {
        $window.Cursor = [System.Windows.Input.Cursors]::Arrow
    }
    $summary = ($results -join "`n")
    [void][System.Windows.MessageBox]::Show($window, "卸载执行完毕：`n`n$summary`n`n详细过程见「运行日志」选项卡。", '完成',
        [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information)
})

Write-Log "Sunshine 部署工具就绪（根目录 $Root）"
$null = $window.ShowDialog()
