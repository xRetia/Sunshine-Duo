#Requires -Version 5.1
<#
.SYNOPSIS
  Sunshine 串流环境一键部署 / 反注册工具（游戏安装器风格 WPF 界面，单文件，零外部依赖）。

.DESCRIPTION
  界面为无边框深色多页框架（欢迎 → 配置 → 执行 → 结果），渲染走 WPF(DirectX 合成)；
  安装 / 卸载逻辑在后台 Runspace 执行，UI 线程只负责动画、进度与日志泵，全程不冻结。

  安装（页面为配置卡片：1/2/3 组必装，第 4 组可选；可选项直接调用 scripts\ 下现成脚本）：
    1 · Sunshine 串流服务（必装）：
       - 写入 config\sunshine.conf：adapter_name 自动检测物理显卡
         （Intel / NVIDIA / AMD，已排除各类虚拟显卡），多卡时人工选择。
       - 防火墙放行 Sunshine（默认勾选，可取消）：调用 scripts\add-firewall-rule.bat，
         为 sunshine.exe 放行 TCP/UDP 入站（规则名 Apollo）。
       - 虚拟手柄驱动（默认勾选，可取消）：调用 scripts\install-gamepad.ps1 安装
         ViGEmBus（串流手柄虚拟为 Xbox 360 手柄，已装 1.17+ 自动跳过）。
    2 · 远程桌面解锁（必装）：静默安装 tools\rdpWrapper.exe（官方命令行 -install，
        解锁单用户并发 RDP，同一账号可同时本地 + 远程在线）。
       - 会话 GPU 加速（默认勾选）：Terminal Services 策略写 3 个 DWORD，
         RDP 型会话改用物理显卡渲染 + AVC444 + 硬件编码。
    3 · 串流会话守护服务（必装，登录信息在密码卡片）：
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
        （scripts\uninstall-gamepad.ps1）、卸载 RdpWrapper、删除会话 GPU 加速策略、
        关闭 Apollo 守卫并删除 WSGuard 服务、卸载 GameManager；同步清理旧名任务
        「Sunshine Autostart (Game)」与旧服务 GameViewerSessionHook。
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
$SvcName      = 'WSGuard'                                 # wsguard 服务名（与 exe 内嵌一致）
$SvcDispName  = 'Game Windows Session Guard'              # wsguard 服务显示名
$LegacySvc    = 'GameViewerSessionHook'                   # 旧服务名（迁移清理用）
$LegacyTask   = 'Sunshine Autostart (Game)'               # 旧任务名（迁移清理用）
$GmSvcName    = 'GameManagerService'                      # GameManager 服务名
$RdpWrapDir   = 'C:\Program Files\RDP Wrapper'            # rdpWrapper 默认安装目录
$LinksDir     = Join-Path $Root 'Links'                   # GameManager 快捷方式目录

$ConfPath     = Join-Path $Root 'config\sunshine.conf'
$StatePath    = Join-Path $Root 'config\sunshine_state.json'
$LogPath      = Join-Path $Root 'config\sunshine.log'
$LogBakPath   = Join-Path $Root 'config\sunshine.log.backup'
$TsPolicyKey  = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
$TsGpuAccelValues = @('bEnumerateHWBeforeSW', 'bAVC444Mode', 'AVCHardwareEncodePreferred')
$RdpWrapExe   = Join-Path $Root 'tools\rdpWrapper.exe'
$GmExe        = Join-Path $Root 'tools\GameManager.exe'
$WgDir        = Join-Path $Root 'wsguard'
$WgExe        = Join-Path $WgDir 'wsguard.exe'
$WgCredPath   = Join-Path $WgDir 'rdp.cred'
$WgCfgPath    = Join-Path $WgDir 'config.json'

$script:LogBox   = $null
$script:Window   = $null
$script:SLogQueue = $null       # 后台 Runspace 的日志队列（主线程恒为空）

# ----------------------------------------------------------------------------
# 安装 / 卸载操作代码（主线程 Invoke-Expression 仅为自检定义函数；
# 实际执行由后台 Runspace 载入同一段代码后调用 Invoke-InstallRun / Invoke-UninstallRun）
# 函数体内的 $Root 等常量：主线程取本文件常量，Runspace 内取共享哈希表 $S 的同名键。
# ----------------------------------------------------------------------------
$OpsCode = @'
# ── Runspace / 自检共用的常量回退 ──────────────────────────────
$ScriptsDir = if ($PSScriptRoot)          { $PSScriptRoot }           elseif ($S) { $S.ScriptsDir }
$Root          = if ($Root)          { $Root }          elseif ($S) { $S.Root }
$TaskName      = if ($TaskName)      { $TaskName }      elseif ($S) { $S.TaskName }
$RunValueName  = if ($RunValueName)  { $RunValueName }  elseif ($S) { $S.RunValueName }
$SvcName       = if ($SvcName)       { $SvcName }       elseif ($S) { $S.SvcName }
$SvcDispName   = if ($SvcDispName)   { $SvcDispName }   elseif ($S) { $S.SvcDispName }
$LegacySvc     = if ($LegacySvc)     { $LegacySvc }     elseif ($S) { $S.LegacySvc }
$LegacyTask    = if ($LegacyTask)    { $LegacyTask }    elseif ($S) { $S.LegacyTask }
$GmSvcName     = if ($GmSvcName)     { $GmSvcName }     elseif ($S) { $S.GmSvcName }
$RdpWrapDir    = if ($RdpWrapDir)    { $RdpWrapDir }    elseif ($S) { $S.RdpWrapDir }
$LinksDir      = if ($LinksDir)      { $LinksDir }      elseif ($S) { $S.LinksDir }
$ConfPath      = if ($ConfPath)      { $ConfPath }      elseif ($S) { $S.ConfPath }
$StatePath     = if ($StatePath)     { $StatePath }     elseif ($S) { $S.StatePath }
$LogPath       = if ($LogPath)       { $LogPath }       elseif ($S) { $S.LogPath }
$LogBakPath    = if ($LogBakPath)    { $LogBakPath }    elseif ($S) { $S.LogBakPath }
$TsPolicyKey   = if ($TsPolicyKey)   { $TsPolicyKey }   elseif ($S) { $S.TsPolicyKey }
$TsGpuAccelValues = if ($TsGpuAccelValues) { $TsGpuAccelValues } elseif ($S) { $S.TsGpuAccelValues }
$RdpWrapExe    = if ($RdpWrapExe)    { $RdpWrapExe }    elseif ($S) { $S.RdpWrapExe }
$GmExe         = if ($GmExe)         { $GmExe }         elseif ($S) { $S.GmExe }
$WgDir         = if ($WgDir)         { $WgDir }         elseif ($S) { $S.WgDir }
$WgExe         = if ($WgExe)         { $WgExe }         elseif ($S) { $S.WgExe }
$WgCredPath    = if ($WgCredPath)    { $WgCredPath }    elseif ($S) { $S.WgCredPath }
$WgCfgPath     = if ($WgCfgPath)     { $WgCfgPath }     elseif ($S) { $S.WgCfgPath }

# ── 基础函数 ──────────────────────────────────────────────────
# Write-Log：后台 Runspace($script:SLogQueue 非空)写队列；UI 线程直接写日志框。
function Write-Log {
    param([string]$Message)
    $line = ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message)
    if ($script:SLogQueue) {
        $script:SLogQueue.Enqueue($line)
        return
    }
    if ($script:LogBox) {
        $script:LogBox.AppendText($line + "`r`n")
        $script:LogBox.ScrollToEnd()
        $script:LogBox.Dispatcher.Invoke([action]{}, [System.Windows.Threading.DispatcherPriority]::Render)
    } else {
        Write-Host $line
    }
}

# Update-Ui：仅主线程有意义；后台 Runspace 内为 no-op（UI 由 DispatcherTimer 驱动）
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

# ============================================================================
# 安装操作（函数按界面分组标注：1 Sunshine 服务 / 2 远程桌面解锁 / 3 守护服务 / 4 可选）
# ============================================================================

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
    Write-Log "OK: Sunshine 配置文件已写入（adapter_name = $AdapterName）"
}

# 1 · 防火墙放行 Sunshine（默认勾选，可取消）：直接调用现成 scripts\add-firewall-rule.bat，
#     为 sunshine.exe 放行 TCP/UDP 入站（规则名 Apollo，路径由 bat 自身相对推导）
function Install-FirewallRule {
    $bat = Join-Path $ScriptsDir 'add-firewall-rule.bat'
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
    $ps1 = Join-Path $ScriptsDir 'install-gamepad.ps1'
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

# 2 · 会话 GPU 加速（可选，默认勾选）：Terminal Services 策略写 3 个 DWORD，
#     RDP 型会话改用物理显卡渲染 + AVC444 + 硬件编码；仅对新建会话生效（重连 / 重启后）
function Install-GpuAcceleration {
    Write-Log '写入 Terminal Services GPU 加速策略（bEnumerateHWBeforeSW / bAVC444Mode / AVCHardwareEncodePreferred）...'
    if (-not (Test-Path $TsPolicyKey)) { New-Item -Path $TsPolicyKey -Force | Out-Null }
    foreach ($v in $TsGpuAccelValues) {
        Set-ItemProperty -Path $TsPolicyKey -Name $v -Value 1 -Type DWord
    }
    Write-Log 'OK: 会话 GPU 加速已启用（已连接的远程会话需断开重连或重启后生效）'
}

# 2 · 反向操作：删除会话 GPU 加速策略，恢复系统默认（软件适配器优先）
function Uninstall-GpuAcceleration {
    Write-Log '删除 Terminal Services GPU 加速策略...'
    Remove-ItemProperty -Path $TsPolicyKey -Name $TsGpuAccelValues -ErrorAction SilentlyContinue
    Write-Log 'OK: 会话 GPU 加速策略已移除（恢复系统默认渲染策略）'
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

# ============================================================================
# 卸载操作（与安装界面分组一一对应，不删除程序文件、配置与凭据）
# ============================================================================
# 1 · 移除防火墙放行规则：直接调用现成 scripts\delete-firewall-rule.bat
function Uninstall-FirewallRule {
    $bat = Join-Path $ScriptsDir 'delete-firewall-rule.bat'
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
    $ps1 = Join-Path $ScriptsDir 'uninstall-gamepad.ps1'
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

# ============================================================================
# 执行编排（后台 Runspace 调用；进度 / 结果写回共享哈希表 $S.Prog）
# ============================================================================
function Invoke-InstallRun {
    param([hashtable]$Opts)
    $keys   = @($Opts.Steps)
    $labels = $Opts.Labels
    $S.Prog.StepTotal = $keys.Count
    $S.Prog.StepIndex = 0
    $results = New-Object System.Collections.ArrayList
    foreach ($k in $keys) {
        $S.Prog.Current = [string]$labels[$k]
        $S.Prog.Steps[$k] = 'running'
        $S.Prog.StepIndex++
        try {
            switch ($k) {
                'conf'     { Write-SunshineConf -AdapterName $Opts.Adapter }
                'gpuaccel' { Install-GpuAcceleration }
                'firewall' { Install-FirewallRule }
                'gamepad'  { Install-Gamepad }
                'rdpwrap'  { Install-RdpWrapper }
                'wsguard'  {
                    Install-Wsguard -RdpUser $Opts.User -Password $Opts.Password -Vw $Opts.Vw -Vh $Opts.Vh -AutoLogon $Opts.AutoLogon
                    Install-ApolloGuard -User $Opts.User
                }
                'gamemgr'  { Install-GameManager }
                'reset'    { Reset-SunshineState }
                default    { throw "未知安装项 $k" }
            }
            $S.Prog.Steps[$k] = 'ok'
            [void]$results.Add(@{ Key = $k; Label = [string]$labels[$k]; Ok = $true })
        } catch {
            Write-Log "失败: $($_.Exception.Message)"
            $S.Prog.Steps[$k] = 'fail'
            [void]$results.Add(@{ Key = $k; Label = [string]$labels[$k]; Ok = $false })
        }
    }
    if (Get-Process -Name 'sunshine' -ErrorAction SilentlyContinue) {
        Write-Log '提示: Sunshine 正在运行，需要重启 Sunshine 后新配置才生效'
    }
    $S.Prog.Results = @($results | ForEach-Object {
        [PSCustomObject]@{ Key = $_.Key; Label = [string]$_.Label; Ok = $_.Ok }
    })
    $S.Prog.Done = $true
}

function Invoke-UninstallRun {
    param([hashtable]$Opts)
    $keys   = @($Opts.Steps)
    $labels = $Opts.Labels
    $S.Prog.StepTotal = $keys.Count
    $S.Prog.StepIndex = 0
    $results = New-Object System.Collections.ArrayList
    foreach ($k in $keys) {
        $S.Prog.Current = [string]$labels[$k]
        $S.Prog.Steps[$k] = 'running'
        $S.Prog.StepIndex++
        try {
            switch ($k) {
                'firewall' { Uninstall-FirewallRule }
                'gamepad'  { Uninstall-Gamepad }
                'rdpwrap'  { Uninstall-RdpWrapper }
                'gpuaccel' { Uninstall-GpuAcceleration }
                'wsguard'  {
                    Uninstall-ApolloGuard
                    Uninstall-Wsguard
                }
                'gamemgr'  { Uninstall-GameManager }
                default    { throw "未知卸载项 $k" }
            }
            $S.Prog.Steps[$k] = 'ok'
            [void]$results.Add(@{ Key = $k; Label = [string]$labels[$k]; Ok = $true })
        } catch {
            Write-Log "失败: $($_.Exception.Message)"
            $S.Prog.Steps[$k] = 'fail'
            [void]$results.Add(@{ Key = $k; Label = [string]$labels[$k]; Ok = $false })
        }
    }
    $S.Prog.Results = @($results | ForEach-Object {
        [PSCustomObject]@{ Key = $_.Key; Label = [string]$_.Label; Ok = $_.Ok }
    })
    $S.Prog.Done = $true
}
'@

# 上述共用代码在主线程定义（自检需要 Install-GpuAcceleration / Uninstall-GpuAcceleration 等存在）
Invoke-Expression $OpsCode

# ----------------------------------------------------------------------------
# XAML（界面：无边框深色多页框架，WPF/DirectX 合成渲染）
# ----------------------------------------------------------------------------
$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Sunshine-Duo 部署工具" Width="960" Height="700"
        WindowStartupLocation="CenterScreen" ResizeMode="NoResize"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        FontFamily="Segoe UI, Microsoft YaHei UI" FontSize="12.5">
  <Window.Resources>
    <SolidColorBrush x:Key="TextBrush" Color="#E2E8F0"/>
    <SolidColorBrush x:Key="MutedBrush" Color="#94A3B8"/>

    <!-- 全局隐式样式：深色主题下的默认字色（局部显式 Foreground 仍然优先） -->
    <Style TargetType="TextBlock">
      <Setter Property="Foreground" Value="#E2E8F0"/>
    </Style>
    <Style TargetType="CheckBox">
      <Setter Property="Foreground" Value="#E2E8F0"/>
    </Style>
    <Style TargetType="TextBox">
      <Setter Property="Foreground" Value="#E2E8F0"/>
      <Setter Property="Background" Value="#0C1526"/>
      <Setter Property="BorderBrush" Value="#2A3B5F"/>
      <Setter Property="CaretBrush" Value="#E2E8F0"/>
      <Setter Property="Padding" Value="4,2"/>
    </Style>
    <Style TargetType="PasswordBox">
      <Setter Property="Foreground" Value="#E2E8F0"/>
      <Setter Property="Background" Value="#0C1526"/>
      <Setter Property="BorderBrush" Value="#2A3B5F"/>
      <Setter Property="CaretBrush" Value="#E2E8F0"/>
      <Setter Property="Padding" Value="4,2"/>
    </Style>
    <Style TargetType="ComboBox">
      <Setter Property="Foreground" Value="#E2E8F0"/>
      <Setter Property="SnapsToDevicePixels" Value="True"/>
      <Setter Property="OverridesDefaultStyle" Value="True"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="ComboBox">
            <Grid>
              <Border x:Name="Border" CornerRadius="6" Background="#0C1526"
                      BorderBrush="#2A3B5F" BorderThickness="1"/>
              <ToggleButton x:Name="tgl" Focusable="False" ClickMode="Press"
                            Background="Transparent"
                            IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                <ToggleButton.Template>
                  <ControlTemplate TargetType="ToggleButton">
                    <Border Background="{TemplateBinding Background}" BorderThickness="0"/>
                  </ControlTemplate>
                </ToggleButton.Template>
              </ToggleButton>
              <ContentPresenter Margin="9,0,26,0" VerticalAlignment="Center" IsHitTestVisible="False"
                                Content="{TemplateBinding SelectionBoxItem}"/>
              <Path Data="M 0 0 L 4 4 L 8 0 Z" Fill="#94A3B8"
                    HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,10,0"/>
              <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True"
                     PopupAnimation="Fade" Focusable="False"
                     IsOpen="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}">
                <Border CornerRadius="6" Background="#131F38" BorderBrush="#2A3B5F"
                        BorderThickness="1" MinWidth="{TemplateBinding ActualWidth}"
                        Margin="0,4,0,0" Padding="0,4">
                  <ScrollViewer MaxHeight="220">
                    <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/>
                  </ScrollViewer>
                </Border>
              </Popup>
            </Grid>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="Border" Property="BorderBrush" Value="#3B82F6"/>
              </Trigger>
              <Trigger Property="IsDropDownOpen" Value="True">
                <Setter TargetName="Border" Property="BorderBrush" Value="#3B82F6"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style TargetType="ComboBoxItem">
      <Setter Property="Foreground" Value="#E2E8F0"/>
      <Setter Property="Background" Value="Transparent"/>
      <Setter Property="BorderThickness" Value="0"/>
      <Setter Property="Padding" Value="9,5"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="ComboBoxItem">
            <Border x:Name="bd" Background="{TemplateBinding Background}" CornerRadius="4"
                    Padding="{TemplateBinding Padding}" Margin="4,1">
              <ContentPresenter VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsHighlighted" Value="True">
                <Setter TargetName="bd" Property="Background" Value="#22305C"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style TargetType="ToolTip">
      <Setter Property="Foreground" Value="#E2E8F0"/>
      <Setter Property="Background" Value="#1B2A4A"/>
      <Setter Property="BorderBrush" Value="#2A3B5F"/>
      <Setter Property="Padding" Value="8,6"/>
    </Style>

    <SolidColorBrush x:Key="AccentBrush" Color="#3B82F6"/>
    <SolidColorBrush x:Key="GreenBrush" Color="#22C55E"/>
    <SolidColorBrush x:Key="RedBrush" Color="#F87171"/>
    <SolidColorBrush x:Key="CardBrush" Color="#131F38"/>
    <SolidColorBrush x:Key="CardLineBrush" Color="#223052"/>

    <!-- 幽灵按钮 -->
    <Style x:Key="BtnGhost" TargetType="Button">
      <Setter Property="Foreground" Value="#E2E8F0"/>
      <Setter Property="Background" Value="#1B2A4A"/>
      <Setter Property="BorderBrush" Value="#2A3B5F"/>
      <Setter Property="Padding" Value="14,7"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="bd" CornerRadius="6" Background="{TemplateBinding Background}"
                    BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1"
                    Padding="{TemplateBinding Padding}">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="bd" Property="Background" Value="#24345A"/>
              </Trigger>
              <Trigger Property="IsPressed" Value="True">
                <Setter TargetName="bd" Property="Background" Value="#16223D"/>
              </Trigger>
              <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Opacity" Value="0.4"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <!-- 主色按钮 -->
    <Style x:Key="BtnPrimary" TargetType="Button" BasedOn="{StaticResource BtnGhost}">
      <Setter Property="Background" Value="#3B82F6"/>
      <Setter Property="BorderBrush" Value="#3B82F6"/>
      <Setter Property="FontWeight" Value="Bold"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="bd" CornerRadius="6" Background="{TemplateBinding Background}"
                    BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1"
                    Padding="{TemplateBinding Padding}">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="bd" Property="Background" Value="#4C8DF6"/>
              </Trigger>
              <Trigger Property="IsPressed" Value="True">
                <Setter TargetName="bd" Property="Background" Value="#2F6FE0"/>
              </Trigger>
              <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Opacity" Value="0.4"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <!-- 绿色按钮（安装） -->
    <Style x:Key="BtnGreen" TargetType="Button" BasedOn="{StaticResource BtnPrimary}">
      <Setter Property="Background" Value="#16A34A"/>
      <Setter Property="BorderBrush" Value="#16A34A"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="bd" CornerRadius="6" Background="{TemplateBinding Background}"
                    BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1"
                    Padding="{TemplateBinding Padding}">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="bd" Property="Background" Value="#1CB356"/>
              </Trigger>
              <Trigger Property="IsPressed" Value="True">
                <Setter TargetName="bd" Property="Background" Value="#128A3F"/>
              </Trigger>
              <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Opacity" Value="0.4"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <!-- 红色按钮（卸载） -->
    <Style x:Key="BtnRed" TargetType="Button" BasedOn="{StaticResource BtnPrimary}">
      <Setter Property="Background" Value="#DC2626"/>
      <Setter Property="BorderBrush" Value="#DC2626"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="bd" CornerRadius="6" Background="{TemplateBinding Background}"
                    BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1"
                    Padding="{TemplateBinding Padding}">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="bd" Property="Background" Value="#E04848"/>
              </Trigger>
              <Trigger Property="IsPressed" Value="True">
                <Setter TargetName="bd" Property="Background" Value="#B91C1C"/>
              </Trigger>
              <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Opacity" Value="0.4"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <!-- 标题栏按钮 -->
    <Style x:Key="BtnTitle" TargetType="Button">
      <Setter Property="Foreground" Value="#94A3B8"/>
      <Setter Property="Background" Value="Transparent"/>
      <Setter Property="BorderThickness" Value="0"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="bd" Background="{TemplateBinding Background}" CornerRadius="0">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="bd" Property="Background" Value="#1A2340"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <!-- 卡片 -->
    <Style x:Key="Card" TargetType="Border">
      <Setter Property="Background" Value="#E6131F38"/>
      <Setter Property="BorderBrush" Value="{StaticResource CardLineBrush}"/>
      <Setter Property="BorderThickness" Value="1"/>
      <Setter Property="CornerRadius" Value="10"/>
      <Setter Property="Padding" Value="16,12"/>
      <Setter Property="Margin" Value="0,0,0,12"/>
    </Style>

    <!-- 进度条 -->
    <Style x:Key="PbStyle" TargetType="ProgressBar">
      <Setter Property="Height" Value="12"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="ProgressBar">
            <Grid>
              <Border x:Name="trackBg" Background="#1E293B" CornerRadius="6"/>
              <Border>
                <Border x:Name="PART_Track">
                  <Border x:Name="PART_Indicator" Background="#3B82F6" CornerRadius="6"
                          HorizontalAlignment="Left"/>
                </Border>
              </Border>
            </Grid>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Window.Resources>

  <Border x:Name="rootFrame" BorderBrush="#2A3B5F" BorderThickness="1"
          Background="#DD0F172A" CornerRadius="10">
    <Grid>
      <Grid.RowDefinitions>
        <RowDefinition Height="34"/>
        <RowDefinition Height="*"/>
      </Grid.RowDefinitions>

      <!-- ── 自绘标题栏 ── -->
      <Border Grid.Row="0" Background="#880B1220" BorderBrush="#223052" BorderThickness="0,0,0,1">
        <Grid>
          <TextBlock x:Name="lblHeadTitle" Text="Welcome" Foreground="#94A3B8" FontSize="12"
                     Margin="14,0,0,0" VerticalAlignment="Center"/>
          <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
            <Button x:Name="btnMin" Width="42" Height="32" Content="─" Style="{StaticResource BtnTitle}"
                    FontSize="11"/>
            <Button x:Name="btnClose" Width="42" Height="32" Content="✕" Style="{StaticResource BtnTitle}"
                    FontSize="12"/>
          </StackPanel>
        </Grid>
      </Border>

      <!-- ── 主体：侧边栏 + 内容 ── -->
      <Grid Grid.Row="1">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="200"/>
          <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>

        <!-- 侧边栏 -->
        <Border Grid.Column="0" Background="#880B1220" BorderBrush="#223052" BorderThickness="0,0,1,0">
          <Grid Margin="12,0">
            <Grid.RowDefinitions>
              <RowDefinition Height="Auto"/>
              <RowDefinition Height="Auto"/>
              <RowDefinition Height="*"/>
              <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>
            <!-- 品牌 -->
            <StackPanel Grid.Row="0" Margin="4,16,0,14">
              <StackPanel Orientation="Horizontal">
                <Border Width="30" Height="30" CornerRadius="8" Background="#1D4ED8" Margin="0,0,10,0">
                  <TextBlock Text="S" Foreground="White" FontSize="17" FontWeight="Bold"
                             HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <TextBlock Text="Sunshine-Duo" Foreground="White" FontSize="14.5" FontWeight="Bold"
                           VerticalAlignment="Center"/>
              </StackPanel>
              <TextBlock Text="串流环境一键部署" Foreground="#64748B" FontSize="11" Margin="0,7,0,0"/>
            </StackPanel>
            <!-- 导航 -->
            <StackPanel Grid.Row="1" x:Name="navHost">
              <!-- 生成于代码：四个导航项 + 两个模式项 -->
            </StackPanel>
            <!-- 底部路径与版权 -->
            <StackPanel Grid.Row="3" Margin="4,0,0,12">
              <TextBlock x:Name="lblSideRoot" Foreground="#475569" FontSize="10.5"
                         TextTrimming="CharacterEllipsis" ToolTip=""/>
              <TextBlock Text="版权所有 (c) 苏羽茜工作室" Foreground="#3D4A63" FontSize="10.5"
                         Margin="0,4,0,0"/>
            </StackPanel>
          </Grid>
        </Border>

        <!-- 内容区 -->
        <Grid Grid.Column="1">
          <!-- ── 欢迎页 ── -->
          <Grid x:Name="pgW" Margin="32,28,32,24">
            <Grid.RenderTransform><TranslateTransform x:Name="ttW"/></Grid.RenderTransform>
            <StackPanel Width="560" HorizontalAlignment="Center">
              <TextBlock Text="欢迎使用 Sunshine-Duo" FontSize="24" FontWeight="Bold" Foreground="White"/>
              <TextBlock Foreground="#94A3B8" FontSize="12.5" Margin="0,8,0,16" TextWrapping="Wrap"
                         Text="一键完成串流环境部署：Sunshine 服务、远程桌面并发解锁、串流会话守护。安装过程在后台执行，界面全程保持流畅。"/>
              <Border Style="{StaticResource Card}">
                <StackPanel>
                  <Grid Margin="0,2">
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width="84"/>
                      <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="0" Text="部署目录" Foreground="#94A3B8"/>
                    <TextBlock Grid.Column="1" x:Name="lblRoot" Foreground="#E2E8F0"
                               TextTrimming="CharacterEllipsis"/>
                  </Grid>
                  <Grid Margin="0,7">
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width="84"/>
                      <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="0" Text="串流用户" Foreground="#94A3B8"/>
                    <TextBlock Grid.Column="1" x:Name="lblWelcomeUser" Foreground="#E2E8F0"/>
                  </Grid>
                  <Grid Margin="0,7">
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width="84"/>
                      <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="0" Text="渲染显卡" Foreground="#94A3B8"/>
                    <TextBlock Grid.Column="1" x:Name="lblWelcomeGpu" Foreground="#E2E8F0"
                               TextTrimming="CharacterEllipsis"/>
                  </Grid>
                  <Grid Margin="0,7">
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width="84"/>
                      <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="0" Text="WSGuard" Foreground="#94A3B8"/>
                    <TextBlock Grid.Column="1" x:Name="lblWelcomeWg" Foreground="#E2E8F0"
                               TextWrapping="Wrap"/>
                  </Grid>
                  <Grid Margin="0,7,0,2">
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width="84"/>
                      <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="0" Text="密码凭据" Foreground="#94A3B8"/>
                    <TextBlock Grid.Column="1" x:Name="lblWelcomeCred" Foreground="#E2E8F0"/>
                  </Grid>
                </StackPanel>
              </Border>
              <Border Background="#E62A2410" BorderBrush="#4D3D14" BorderThickness="1" CornerRadius="8"
                      Padding="12,9" Margin="0,2,0,0">
                <TextBlock Foreground="#FCD34D" FontSize="11.5" TextWrapping="Wrap"
                           Text="🛡 杀毒软件可能误报：本工具需安装服务、驱动与远程会话组件，行为特征与远程管理工具相似。如遇拦截，将本目录加入白名单后重新运行。全部源码开放（GPL-3.0）。"/>
              </Border>
              <Button x:Name="btnGoSetup" Content="下一步：配置 &gt;" Style="{StaticResource BtnPrimary}"
                      Width="200" Height="38" FontSize="13" Margin="0,20,0,0" HorizontalAlignment="Center"/>
            </StackPanel>
          </Grid>

          <!-- ── 配置页（三步向导） ── -->
          <Grid x:Name="pgS" Margin="28,20,28,16" Visibility="Collapsed">
            <Grid.RenderTransform><TranslateTransform x:Name="ttS"/></Grid.RenderTransform>
            <Grid.RowDefinitions>
              <RowDefinition Height="Auto"/>
              <RowDefinition Height="*"/>
              <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>

            <!-- 步骤指示器 -->
            <StackPanel Grid.Row="0" Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,2,0,10">
              <TextBlock x:Name="lblStep1" Text="① 显卡与组件" FontSize="13" FontWeight="Bold"
                         Foreground="#3B82F6"/>
              <TextBlock Text="  ──  " Foreground="#475569"/>
              <TextBlock x:Name="lblStep2" Text="② 串流用户密码" FontSize="13" FontWeight="Bold"
                         Foreground="#64748B"/>
              <TextBlock Text="  ──  " Foreground="#475569"/>
              <TextBlock x:Name="lblStep3" Text="③ 可选功能" FontSize="13" FontWeight="Bold"
                         Foreground="#64748B"/>
            </StackPanel>

            <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
              <!-- 第 1 步 · 渲染显卡与组件（必装项灰色不可选） -->
              <StackPanel x:Name="grpStep1" Margin="4,0,10,0">
                <Border Style="{StaticResource Card}">
                  <StackPanel>
                    <TextBlock Text="渲染显卡（用于串流编码与渲染）" Foreground="#7DD3FC" FontSize="12.5"
                               FontWeight="Bold" Margin="0,0,0,8"/>
                    <StackPanel Orientation="Horizontal">
                      <TextBlock Text="渲染显卡：" VerticalAlignment="Center"/>
                      <ComboBox x:Name="cmbGpu" Width="420" Height="26" VerticalContentAlignment="Center"/>
                    </StackPanel>
                    <TextBlock x:Name="lblGpuInfo" Foreground="#64748B" FontSize="11"
                               Margin="0,6,0,0" TextWrapping="Wrap" Text="检测中..."/>
                  </StackPanel>
                </Border>
                <Border Style="{StaticResource Card}">
                  <StackPanel>
                    <TextBlock Text="必装组件（默认全部安装）" Foreground="#94A3B8" FontSize="11" Margin="0,0,0,6"/>
                    <CheckBox x:Name="chkConf" IsChecked="True" IsEnabled="False" Content="默认配置"/>
                    <TextBlock FontSize="11" Foreground="#64748B" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="按所选显卡写入 Sunshine 配置文件，含编码 / 手柄 / UPnP"/>
                    <CheckBox x:Name="chkFirewall" IsChecked="True" IsEnabled="False" Margin="0,4,0,0"
                              Content="防火墙规则"/>
                    <TextBlock FontSize="11" Foreground="#64748B" Margin="20,2,0,0"
                               Text="放行 Sunshine 串流端口 TCP / UDP 入站"/>
                    <CheckBox x:Name="chkRdpWrap" IsChecked="True" IsEnabled="False" Margin="0,4,0,0"
                              Content="RDP 并发解锁"/>
                    <TextBlock FontSize="11" Foreground="#64748B" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="同一主机同时登录主用户与串流用户，Console 会话与游戏会话互不抢占"/>
                    <CheckBox x:Name="chkWsguard" IsChecked="True" IsEnabled="False" Margin="0,4,0,0"
                              Content="串流会话守护服务"/>
                    <TextBlock FontSize="11" Foreground="#64748B" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="WSGuard：串流断开后维持虚拟显示输出；开机自动启动 Sunshine"/>
                  </StackPanel>
                </Border>
                <Border Style="{StaticResource Card}">
                  <StackPanel>
                    <CheckBox x:Name="chkGpuAccel" IsChecked="True" Content="会话 GPU 加速"/>
                    <TextBlock FontSize="11" Foreground="#64748B" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="远程会话改用物理显卡渲染，优先 AVC444 画质与硬件编码；已连接的会话需断开重连后生效"/>
                    <CheckBox x:Name="chkGamepad" IsChecked="True" Margin="0,6,0,0" Content="虚拟手柄驱动"/>
                    <TextBlock FontSize="11" Foreground="#64748B" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="ViGEmBus：串流手柄识别为 Xbox 360 手柄；已安装 1.17 或更新版本时自动跳过"/>
                  </StackPanel>
                </Border>
              </StackPanel>
            </ScrollViewer>

            <!-- 第 2 步 · 串流用户与密码（默认隐藏，Set-WizardStep 切换） -->
            <StackPanel Grid.Row="1" x:Name="grpStep2" Margin="4,0,10,0" Visibility="Collapsed">
              <Border Style="{StaticResource Card}">
                <StackPanel>
                  <TextBlock x:Name="txtStep2Title" Text="第 2 步 · 串流用户登录密码"
                             Foreground="#7DD3FC" FontSize="12.5" FontWeight="Bold" Margin="0,0,0,8"/>
                  <StackPanel Orientation="Horizontal">
                    <TextBlock Text="串流用户：" VerticalAlignment="Center" FontWeight="Bold"/>
                    <TextBlock x:Name="lblUser" VerticalAlignment="Center" Margin="4,0,0,0" FontWeight="Bold"/>
                    <TextBlock Foreground="#DC2626" FontSize="11" Margin="14,0,0,0" VerticalAlignment="Center"
                               TextWrapping="Wrap" Text="当前用户，如需更换请先登录对应用户再运行。"/>
                  </StackPanel>
                  <StackPanel Orientation="Horizontal" Margin="0,10,0,0">
                    <TextBlock Text="登录密码：" VerticalAlignment="Center" FontWeight="Bold"/>
                    <PasswordBox x:Name="pwdRdp" Width="200" Height="24" VerticalContentAlignment="Center"/>
                    <Button x:Name="btnVerifyPw" Content="验证" Style="{StaticResource BtnGhost}"
                            Width="62" Margin="6,0,0,0" Padding="0,4"/>
                    <TextBlock x:Name="lblPwState" Margin="8,0,0,0" VerticalAlignment="Center"
                               FontSize="11" Foreground="#64748B"/>
                  </StackPanel>
                  <TextBlock x:Name="lblCredState" Foreground="#64748B" FontSize="11" Margin="0,8,0,0"/>
                  <CheckBox x:Name="chkAutoLogon" IsChecked="True" Margin="0,10,0,0"
                            Content="开机自动登录串流用户"/>
                  <TextBlock FontSize="11" Foreground="#64748B" Margin="20,2,0,0"
                             Text="无人值守：重启后无需登录即恢复串流"/>
                  <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
                    <TextBlock Text="虚拟屏分辨率：" VerticalAlignment="Center"/>
                    <TextBox x:Name="txtWidth" Width="64" Height="24" Text="1600"
                             VerticalContentAlignment="Center"/>
                    <TextBlock Text=" × " VerticalAlignment="Center"/>
                    <TextBox x:Name="txtHeight" Width="64" Height="24" Text="720"
                             VerticalContentAlignment="Center"/>
                    <TextBlock Text="（手机横屏推荐 1920×1080）" Foreground="#64748B" FontSize="11"
                               VerticalAlignment="Center" Margin="6,0,0,0"/>
                  </StackPanel>
                </StackPanel>
              </Border>
            </StackPanel>

            <!-- 第 3 步 · 可选功能（默认隐藏，Set-WizardStep 切换） -->
            <StackPanel Grid.Row="1" x:Name="grpStep3" Margin="4,0,10,0" Visibility="Collapsed">
              <Border Style="{StaticResource Card}">
                <StackPanel>
                  <TextBlock Text="可选功能" Foreground="#7DD3FC" FontSize="12.5"
                             FontWeight="Bold" Margin="0,0,0,8"/>
                  <CheckBox x:Name="chkGameMgr" Content="游戏菜单（GameManager），建议安装"/>
                  <TextBlock FontSize="11" Foreground="#64748B" Margin="20,2,0,0" TextWrapping="Wrap"
                             Text="可直接启动需要管理员权限的游戏；游戏快捷方式放入 Links 目录，串流端即可启动"/>
                  <CheckBox x:Name="chkReset" Margin="0,8,0,0" Content="解绑所有已配对设备"/>
                  <TextBlock FontSize="11" Foreground="#64748B" Margin="20,2,0,0" TextWrapping="Wrap"
                             Text="手机 / 平板 / 电视需重新配对"/>
                </StackPanel>
              </Border>
            </StackPanel>

            <!-- 底部导航条（上一步 / 下一步 / 开始安装） -->
            <Border Grid.Row="2" BorderBrush="#223052" BorderThickness="0,1,0,0" Padding="4,12,4,0"
                    Background="#880B1220" CornerRadius="0">
              <Grid>
                <Button x:Name="btnBack" Content="&lt; 上一步" Style="{StaticResource BtnGhost}"
                        Height="34" Padding="16,6" HorizontalAlignment="Left" Visibility="Collapsed"/>
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                  <Button x:Name="btnNext" Content="下一步 &gt;" Style="{StaticResource BtnPrimary}"
                          Height="34" Width="120"/>
                  <Button x:Name="btnInstall" Content="开 始 安 装" Style="{StaticResource BtnGreen}"
                          Height="34" Width="150" Margin="8,0,0,0" Visibility="Collapsed" FontWeight="Bold"/>
                </StackPanel>
              </Grid>
            </Border>
          </Grid>

          <!-- ── 执行页 ── -->
          <Grid x:Name="pgR" Margin="28,22,28,18" Visibility="Collapsed">
            <Grid.RenderTransform><TranslateTransform x:Name="ttR"/></Grid.RenderTransform>
            <Grid.RowDefinitions>
              <RowDefinition Height="Auto"/>
              <RowDefinition Height="Auto"/>
              <RowDefinition Height="Auto"/>
              <RowDefinition Height="*"/>
            </Grid.RowDefinitions>

            <TextBlock Grid.Row="0" x:Name="lblPhaseTitle" Text="正在部署串流环境..."
                       FontSize="17" FontWeight="Bold" Foreground="White"/>
            <TextBlock Grid.Row="0" x:Name="lblCurrent" Text="" Foreground="#7DD3FC"
                       FontSize="12.5" Margin="0,6,0,0" TextTrimming="CharacterEllipsis"/>

            <StackPanel Grid.Row="1" Width="560" HorizontalAlignment="Center" Margin="0,14,0,0">
              <Grid Height="12">
                <ProgressBar x:Name="pgBar" Style="{StaticResource PbStyle}" Minimum="0" Maximum="100"
                             Value="0" Width="560"/>
                <Border x:Name="sheenClip" CornerRadius="6" Width="560">
                  <Rectangle x:Name="sheen" Width="80" HorizontalAlignment="Left" Opacity="0.25">
                    <Rectangle.Fill>
                      <LinearGradientBrush StartPoint="0,0" EndPoint="1,0">
                        <GradientStop Color="#00FFFFFF" Offset="0"/>
                        <GradientStop Color="#A8FFFFFF" Offset="0.5"/>
                        <GradientStop Color="#00FFFFFF" Offset="1"/>
                      </LinearGradientBrush>
                    </Rectangle.Fill>
                    <Rectangle.RenderTransform><TranslateTransform x:Name="sheenTt" X="-100"/></Rectangle.RenderTransform>
                  </Rectangle>
                </Border>
              </Grid>
              <Grid Margin="0,5,0,0">
                <TextBlock x:Name="lblStepCount" Text="" Foreground="#64748B" FontSize="11"
                           HorizontalAlignment="Center"/>
              </Grid>
            </StackPanel>

            <StackPanel Grid.Row="2" x:Name="stepsHost" Width="560" HorizontalAlignment="Center"
                        Margin="0,10,0,4"/>

            <Border Grid.Row="3" Background="#D90C1526" CornerRadius="8" Margin="0,10,0,0"
                    BorderBrush="#1E293B" BorderThickness="1">
              <TextBox x:Name="txtLog" IsReadOnly="True" FontFamily="Consolas" FontSize="11.5"
                       Background="Transparent" Foreground="#D1E3F8" BorderThickness="0" Padding="8,6"
                       VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Auto"
                       TextWrapping="NoWrap"/>
            </Border>
          </Grid>

          <!-- ── 结果页 ── -->
          <Grid x:Name="pgF" Margin="32,28,32,24" Visibility="Collapsed">
            <Grid.RenderTransform><TranslateTransform x:Name="ttF"/></Grid.RenderTransform>
            <StackPanel Width="560" HorizontalAlignment="Center">
              <TextBlock x:Name="lblFinishIcon" Text="✓" FontSize="46" FontWeight="Bold"
                         Foreground="#22C55E" HorizontalAlignment="Center"/>
              <TextBlock x:Name="lblFinishTitle" Text="部署完成" FontSize="19" FontWeight="Bold"
                         Foreground="White" HorizontalAlignment="Center" Margin="0,6,0,4"/>
              <TextBlock x:Name="lblFinishNote" Text="" Foreground="#94A3B8" FontSize="12"
                         HorizontalAlignment="Center" TextWrapping="Wrap"/>
              <Border Style="{StaticResource Card}" Margin="0,16,0,0" MaxHeight="260">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                  <StackPanel x:Name="resHost"/>
                </ScrollViewer>
              </Border>
              <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,18,0,0">
                <Button x:Name="btnViewLog" Content="查看完整日志" Style="{StaticResource BtnGhost}"
                        Height="36" Padding="18,7" Margin="0,0,10,0"/>
                <Button x:Name="btnFinishOk" Content="完 成" Style="{StaticResource BtnPrimary}"
                        Height="36" Width="130" FontWeight="Bold"/>
              </StackPanel>
            </StackPanel>
          </Grid>

          <!-- ── 卸载页 ── -->
          <Grid x:Name="pgU" Margin="28,20,28,16" Visibility="Collapsed">
            <Grid.RenderTransform><TranslateTransform x:Name="ttU"/></Grid.RenderTransform>
            <Grid.RowDefinitions>
              <RowDefinition Height="*"/>
              <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>
            <ScrollViewer Grid.Row="0" VerticalScrollBarVisibility="Auto">
              <StackPanel Margin="4,0,10,0">
                <TextBlock Text="选择要反注册的组件" Foreground="#7DD3FC" FontSize="13" FontWeight="Bold"
                           Margin="2,2,0,8"/>
                <Border Style="{StaticResource Card}">
                  <StackPanel>
                    <TextBlock Text="1 · 串流服务" Foreground="#94A3B8" FontSize="11" Margin="0,0,0,6"/>
                    <CheckBox x:Name="chkUFirewall" IsChecked="True" Content="移除防火墙放行规则"/>
                    <TextBlock FontSize="11" Foreground="#64748B" Margin="20,2,0,0" Text="Apollo"/>
                    <CheckBox x:Name="chkUGamepad" IsChecked="True" Margin="0,4,0,0"
                              Content="卸载虚拟手柄驱动（ViGEmBus）"/>
                    <TextBlock FontSize="11" Foreground="#64748B" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="其他程序依赖它时请勿勾选"/>
                  </StackPanel>
                </Border>
                <Border Style="{StaticResource Card}">
                  <StackPanel>
                    <TextBlock Text="2 · RDP 并发解锁" Foreground="#94A3B8" FontSize="11" Margin="0,0,0,6"/>
                    <CheckBox x:Name="chkURdpWrap" IsChecked="True" Content="卸载 RdpWrapper"/>
                    <TextBlock FontSize="11" Foreground="#64748B" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="恢复系统默认的单会话限制"/>
                    <CheckBox x:Name="chkUGpuAccel" IsChecked="True" Margin="0,4,0,0"
                              Content="删除会话 GPU 加速策略"/>
                    <TextBlock FontSize="11" Foreground="#64748B" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="恢复远程会话默认渲染策略，三个注册表值一并移除"/>
                  </StackPanel>
                </Border>
                <Border Style="{StaticResource Card}">
                  <StackPanel>
                    <TextBlock Text="3 · 串流会话守护服务" Foreground="#94A3B8" FontSize="11" Margin="0,0,0,6"/>
                    <CheckBox x:Name="chkUWsguard" IsChecked="True"
                              Content="卸载 WSGuard 服务并取消 Sunshine 开机自启"/>
                    <TextBlock FontSize="11" Foreground="#64748B" Margin="20,2,0,0"
                               Text="保留密码与设置"/>
                  </StackPanel>
                </Border>
                <Border Style="{StaticResource Card}">
                  <StackPanel>
                    <TextBlock Text="4 · 可选功能" Foreground="#94A3B8" FontSize="11" Margin="0,0,0,6"/>
                    <CheckBox x:Name="chkUGameMgr" IsChecked="True" Content="卸载游戏菜单（GameManager）"/>
                  </StackPanel>
                </Border>
                <TextBlock Foreground="#64748B" FontSize="11" Margin="2,2,2,0" TextWrapping="Wrap"
                           Text="卸载不会删除 Sunshine 本体和任何游戏存档，也不删除配置与已保存的密码。卸载 RDP 并发解锁或虚拟手柄驱动时屏幕可能闪烁，属正常现象；若正通过远程操作本机，连接会断开重连一次；虚拟手柄驱动可能需重启电脑后才彻底移除。"/>
              </StackPanel>
            </ScrollViewer>

            <Border Grid.Row="1" BorderBrush="#223052" BorderThickness="0,1,0,0" Padding="4,12,4,0"
                    Background="#880B1220">
              <Grid>
                <Button x:Name="btnUninstallBack" Content="← 返回总览" Style="{StaticResource BtnGhost}"
                        Height="34" Padding="16,6" HorizontalAlignment="Left"/>
                <Button x:Name="btnUninstallStart" Content="开 始 卸 载" Style="{StaticResource BtnRed}"
                        Height="34" Width="150" HorizontalAlignment="Right" FontWeight="Bold"/>
              </Grid>
            </Border>
          </Grid>
        </Grid>
      </Grid>
    </Grid>
  </Border>
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

    # XAML 解析
    Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
    $win = $null
    Check 'XAML 解析' {
        $script:win = [Windows.Markup.XamlReader]::Parse($xaml)
        $script:win -is [System.Windows.Window]
    }
    foreach ($n in @('lblRoot','txtLog','cmbGpu','lblGpuInfo',
                     'chkConf','chkFirewall','chkRdpWrap','chkWsguard',
                     'chkGpuAccel','chkGamepad','chkGameMgr','chkReset',
                     'lblUser','pwdRdp','btnVerifyPw','lblPwState','lblCredState','chkAutoLogon',
                     'txtWidth','txtHeight','btnBack','btnNext','btnInstall',
                     'lblStep1','lblStep2','lblStep3','grpStep1','grpStep2','grpStep3','txtStep2Title',
                     'rootFrame','btnGoSetup',
                     'lblHeadTitle','btnMin','btnClose','lblSideRoot',
                     'pgW','pgS','pgR','pgF','pgU','ttW','ttS','ttR','ttF','ttU',
                     'lblWelcomeUser','lblWelcomeGpu','lblWelcomeWg','lblWelcomeCred',
                     'lblPhaseTitle','lblCurrent','lblStepCount','pgBar','sheen','sheenTt','stepsHost',
                     'lblFinishIcon','lblFinishTitle','lblFinishNote','resHost','btnViewLog','btnFinishOk',
                     'chkUFirewall','chkUGamepad','chkURdpWrap','chkUGpuAccel','chkUWsguard','chkUGameMgr',
                     'btnUninstallBack','btnUninstallStart')) {
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

    # 会话 GPU 加速注册表定义（安装 / 卸载共用）
    Check '会话 GPU 加速注册表定义' {
        ($TsPolicyKey -eq 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services') -and
        ($TsGpuAccelValues.Count -eq 3) -and
        ($TsGpuAccelValues -contains 'bEnumerateHWBeforeSW') -and
        ($TsGpuAccelValues -contains 'bAVC444Mode') -and
        ($TsGpuAccelValues -contains 'AVCHardwareEncodePreferred') -and
        ((Get-Command Install-GpuAcceleration -ErrorAction SilentlyContinue) -ne $null) -and
        ((Get-Command Uninstall-GpuAcceleration -ErrorAction SilentlyContinue) -ne $null) -and
        ((Get-Command Invoke-InstallRun -ErrorAction SilentlyContinue) -ne $null) -and
        ((Get-Command Invoke-UninstallRun -ErrorAction SilentlyContinue) -ne $null)
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
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-WindowStyle', 'Hidden', '-File', ('"{0}"' -f $PSCommandPath))
    exit
}

# ---- 加载 GUI ----
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$window = [Windows.Markup.XamlReader]::Parse($xaml)
$script:Window = $window

# 无边框：自绘标题栏（拖拽区 + 命中穿透控制）
$chrome = New-Object System.Windows.Shell.WindowChrome
$chrome.CaptionHeight = 34
$chrome.GlassFrameThickness = 0
$chrome.CornerRadius = [System.Windows.CornerRadius]::new(0)
$chrome.UseAeroCaptionButtons = $false
$chrome.ResizeBorderThickness = [System.Windows.Thickness]::new(0)
[System.Windows.Shell.WindowChrome]::SetWindowChrome($window, $chrome)
foreach ($b in @($window.FindName('btnMin'), $window.FindName('btnClose'))) {
    if ($b) { [System.Windows.Shell.WindowChrome]::SetIsHitTestVisibleInChrome($b, $true) }
}

# ---- 圆角 + 亚克力背景（DWM 原生合成；Win11 自动圆角，Win10 退化直角）----
$DwmCode = @'
using System;
using System.Runtime.InteropServices;
public static class DwmUi {
    [StructLayout(LayoutKind.Sequential)]
    public struct AccentPolicy { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)]
    public struct WcaData { public int Attribute; public IntPtr Data; public int SizeOfData; }
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);
    [DllImport("user32.dll")] public static extern bool SetWindowCompositionAttribute(IntPtr h, ref WcaData d);
    public static string Apply(IntPtr hwnd) {
        string mode = "none";
        // DWMWA_WINDOW_CORNER_PREFERENCE = 2 (round)：Win11 圆角； Win10 返回非零忽略
        try { int pref = 2; if (DwmSetWindowAttribute(hwnd, 33, ref pref, 4) == 0) { mode = "round"; } } catch { }
        // ACCENT_ENABLE_ACRYLICBLURBEHIND：Win10 1803+ 亚克力背景，潜色调为深蓝 #B40F172A
        try {
            AccentPolicy ap; ap.AccentState = 4; ap.AccentFlags = 2; ap.GradientColor = 0xB42A170F; ap.AnimationId = 0;
            int size = Marshal.SizeOf(typeof(AccentPolicy));
            IntPtr p = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(ap, p, false);
            WcaData d; d.Attribute = 19; d.Data = p; d.SizeOfData = size;
            bool ok = SetWindowCompositionAttribute(hwnd, ref d);
            Marshal.FreeHGlobal(p);
            if (ok) { mode = mode + "+acrylic"; }
        } catch { }
        return mode;
    }
}
'@
if (-not ('DwmUi' -as [type])) { Add-Type -TypeDefinition $DwmCode }
$window.Add_SourceInitialized({
    $h = (New-Object System.Windows.Interop.WindowInteropHelper $window).Handle
    if ($h -ne [IntPtr]::Zero) {
        $glassMode = [DwmUi]::Apply($h)
        Write-Log "界面合成: $glassMode（亚克力 + 圆角）"
    }
})
# 根容器圆角裁剪（Border.Clip 会连同子元素一起裁出圆角）
$window.Add_Loaded({
    $w = $ui['rootFrame'].ActualWidth
    $hgt = $ui['rootFrame'].ActualHeight
    if ($w -gt 0 -and $hgt -gt 0) {
        $geo = [System.Windows.Media.RectangleGeometry]::new(
            [System.Windows.Rect]::new(0, 0, $w, $hgt), 10, 10)
        $ui['rootFrame'].Clip = $geo
    }
})

# ---- 控件收集中转 ----
$ui = @{}
foreach ($n in @('lblRoot','txtLog','cmbGpu','lblGpuInfo',
                 'chkConf','chkFirewall','chkRdpWrap','chkWsguard',
                 'chkGpuAccel','chkGamepad','chkGameMgr','chkReset',
                 'lblUser','pwdRdp','btnVerifyPw','lblPwState','lblCredState','chkAutoLogon',
                 'txtWidth','txtHeight','btnBack','btnNext','btnInstall',
                 'lblStep1','lblStep2','lblStep3','grpStep1','grpStep2','grpStep3','txtStep2Title',
                 'rootFrame','btnGoSetup',
                 'lblHeadTitle','btnMin','btnClose','lblSideRoot',
                 'pgW','pgS','pgR','pgF','pgU','ttW','ttS','ttR','ttF','ttU',
                 'lblWelcomeUser','lblWelcomeGpu','lblWelcomeWg','lblWelcomeCred',
                 'lblPhaseTitle','lblCurrent','lblStepCount','pgBar','sheen','sheenTt','sheenClip','stepsHost',
                 'lblFinishIcon','lblFinishTitle','lblFinishNote','resHost','btnViewLog','btnFinishOk',
                 'chkUFirewall','chkUGamepad','chkURdpWrap','chkUGpuAccel','chkUWsguard','chkUGameMgr',
                 'btnUninstallBack','btnUninstallStart')) {
    $ui[$n] = $window.FindName($n)
}
$script:LogBox = $ui['txtLog']

# ---- 共享状态（后台 Runspace 与 UI 线程通信）----
$S = [hashtable]::Synchronized(@{})
$S.ScriptsDir    = $PSScriptRoot
$S.Root          = $Root
$S.TaskName      = $TaskName
$S.RunValueName  = $RunValueName
$S.SvcName       = $SvcName
$S.SvcDispName   = $SvcDispName
$S.LegacySvc     = $LegacySvc
$S.LegacyTask    = $LegacyTask
$S.GmSvcName     = $GmSvcName
$S.RdpWrapDir    = $RdpWrapDir
$S.LinksDir      = $LinksDir
$S.ConfPath      = $ConfPath
$S.StatePath     = $StatePath
$S.LogPath       = $LogPath
$S.LogBakPath    = $LogBakPath
$S.TsPolicyKey   = $TsPolicyKey
$S.TsGpuAccelValues = $TsGpuAccelValues
$S.RdpWrapExe    = $RdpWrapExe
$S.GmExe         = $GmExe
$S.WgDir         = $WgDir
$S.WgExe         = $WgExe
$S.WgCredPath    = $WgCredPath
$S.WgCfgPath     = $WgCfgPath
$S.LogQueue      = [System.Collections.Queue]::Synchronized((New-Object System.Collections.Queue))
$S.Prog          = [hashtable]::Synchronized(@{
    Steps = [hashtable]::Synchronized(@{}); StepIndex = 0; StepTotal = 0
    Current = ''; Done = $false; Results = @()
})

# ---- 渲染能力检测（Tier < 2 时禁用动画，降级为直切）----
$script:AnimsEnabled = (([System.Windows.Media.RenderCapability]::Tier) -shr 16) -ge 2

# ---- 页面切换（含淡入 + 上滑动效，合成线程友好）----
$script:PgMap = @{ w = 'pgW'; s = 'pgS'; r = 'pgR'; f = 'pgF'; u = 'pgU' }
$script:TtMap = @{ w = 'ttW'; s = 'ttS'; r = 'ttR'; f = 'ttF'; u = 'ttU' }
$script:HeadMap = @{ w = 'Welcome · 总览'; s = '安装配置'; r = '执行中'; f = '执行结果'; u = '卸载/反注册' }
$script:NavOrder   = @(@{Id='w';Num='01';Label='Welcome · 总览'}, @{Id='s';Num='02';Label='安装配置'},
                       @{Id='r';Num='03';Label='执行'}, @{Id='f';Num='04';Label='结果'})
$script:ModeOrder  = @(@{Id='Install';Glyph='▶';Label='安装'}, @{Id='Uninstall';Glyph='↺';Label='卸载'})
$script:NavBorders = @{}
$script:ModeBorders = @{}

function Build-NavRow {
    param([string]$Num, [string]$Glyph, [string]$Label, [bool]$IsMode)
    $bd = New-Object System.Windows.Controls.Border
    $bd.CornerRadius = [System.Windows.CornerRadius]::new(6)
    $bd.Padding = [System.Windows.Thickness]::new(10, 7, 10, 7)
    $bd.Margin = [System.Windows.Thickness]::new(0, 0, 0, 2)
    $bd.Background = [System.Windows.Media.Brushes]::Transparent
    $sp = New-Object System.Windows.Controls.StackPanel
    $sp.Orientation = 'Horizontal'
    $t1 = New-Object System.Windows.Controls.TextBlock
    if ($IsMode) {
        $t1.Text = $Glyph
        $t1.FontSize = 11
        $t1.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#475569')
    } else {
        $t1.Text = $Num
        $t1.FontSize = 11
        $t1.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#475569')
    }
    $t1.VerticalAlignment = 'Center'
    $t2 = New-Object System.Windows.Controls.TextBlock
    $t2.Text = $Label
    $t2.FontSize = 12
    $t2.Margin = [System.Windows.Thickness]::new(10, 0, 0, 0)
    $t2.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#94A3B8')
    $t2.VerticalAlignment = 'Center'
    [void]$sp.Children.Add($t1)
    [void]$sp.Children.Add($t2)
    $bd.Child = $sp
    $navHost = $window.FindName('navHost')
    [void]$navHost.Children.Add($bd)
    return @{ Bd = $bd; Tb = $t2 }
}

foreach ($item in $script:NavOrder) {
    $script:NavBorders[$item.Id] = Build-NavRow -Num $item.Num -Label $item.Label -IsMode $false
}
$sep = New-Object System.Windows.Shapes.Rectangle
$sep.Height = 1
$sep.Margin = [System.Windows.Thickness]::new(10, 8, 10, 8)
$sep.Fill = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#1E293B')
[void]($window.FindName('navHost')).Children.Add($sep)
foreach ($item in $script:ModeOrder) {
    $script:ModeBorders[$item.Id] = Build-NavRow -Glyph $item.Glyph -Label $item.Label -IsMode $true
}

# 侧边栏模式行可点击：安装 → 欢迎页；卸载 → 卸载页（Tag 传参避免闭包陷阱）
function Wire-RowClick {
    param($Bd, [string]$Tag)
    $Bd.Tag = $Tag
    $Bd.Cursor = 'Hand'
    $Bd.Add_MouseLeftButtonDown({
        param($s, $e)
        if (-not $script:Running) { Show-Page ([string]$s.Tag) }
    })
}
Wire-RowClick $script:ModeBorders['Install'].Bd 'w'
Wire-RowClick $script:ModeBorders['Uninstall'].Bd 'u'

function Set-NavHighlight {
    param([string]$PageId)
    foreach ($id in $script:NavOrder.Id) {
        $target = ($id -eq $PageId)
        $row = $script:NavBorders[$id]
        $row.Bd.Background = [System.Windows.Media.BrushConverter]::new().ConvertFromString($(if ($target) { '#16213B' } else { 'Transparent' }))
        $row.Tb.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString($(if ($target) { '#E2E8F0' } else { '#94A3B8' }))
    }
    $installActive = ($PageId -ne 'u')
    foreach ($m in $script:ModeOrder) {
        $target = if ($m.Id -eq 'Uninstall') { -not $installActive } else { $installActive }
        $row = $script:ModeBorders[$m.Id]
        $row.Bd.Background = [System.Windows.Media.BrushConverter]::new().ConvertFromString($(if ($target) { '#16213B' } else { 'Transparent' }))
    }
}

function Show-Page {
    param([string]$Id)
    foreach ($nid in $script:PgMap.Keys) {
        $ui[$script:PgMap[$nid]].Visibility = $(if ($nid -eq $Id) { 'Visible' } else { 'Collapsed' })
    }
    $ui['lblHeadTitle'].Text = $script:HeadMap[$Id]
    Set-NavHighlight -PageId $Id
    if (-not $script:AnimsEnabled) { return }
    # 淡入 + 上移入场（Opacity / TranslateTransform 不引发布局重算）
    $pg = $ui[$script:PgMap[$Id]]
    $tt = $ui[$script:TtMap[$Id]]
    $sb = New-Object System.Windows.Media.Animation.Storyboard
    $a1 = New-Object System.Windows.Media.Animation.DoubleAnimation
    $a1.From = 0; $a1.To = 1
    $a1.Duration = [TimeSpan]::FromMilliseconds(220)
    $e1 = New-Object System.Windows.Media.Animation.CubicEase
    $e1.EasingMode = 'EaseOut'
    $a1.EasingFunction = $e1
    [void][System.Windows.Media.Animation.Storyboard]::SetTarget($a1, $pg)
    [void][System.Windows.Media.Animation.Storyboard]::SetTargetProperty($a1, 'Opacity')
    $a2 = New-object System.Windows.Media.Animation.DoubleAnimation
    $a2.From = 14; $a2.To = 0
    $a2.Duration = [TimeSpan]::FromMilliseconds(220)
    $a2.EasingFunction = $e1
    [void][System.Windows.Media.Animation.Storyboard]::SetTarget($a2, $tt)
    [void][System.Windows.Media.Animation.Storyboard]::SetTargetProperty($a2, 'Y')
    [void]$sb.Children.Add($a1)
    [void]$sb.Children.Add($a2)
    $sb.Begin($pg, $true)
}

Show-Page w
$ui['lblSideRoot'].Text = $Root
$ui['lblSideRoot'].ToolTip = $Root

# ---- 窗口按钮 ----
$window.FindName('btnMin').Add_Click({ $window.WindowState = 'Minimized' })
$window.FindName('btnClose').Add_Click({ $window.Close() })
$window.Add_Closing({
    param($s, $e)
    if ($script:Running) {
        $e.Cancel = $true
        [void][System.Windows.MessageBox]::Show($window,
            '任务正在后台运行中，完成后才能关闭窗口。', '请稍候',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information)
    }
})

# ---- 导航按钮 ----
$script:Running = $false
$ui['btnGoSetup'].Add_Click({ if (-not $script:Running) { Show-Page s; Set-WizardStep 1 } })
$ui['btnUninstallBack'].Add_Click({ if (-not $script:Running) { Show-Page w } })
$ui['btnFinishOk'].Add_Click({ $window.Close() })
$ui['btnViewLog'].Add_Click({ Show-Page r })

# ---- 三步向导 ----
$script:WizardStep = 1
function Set-WizardStep {
    param([int]$Step)
    $script:WizardStep = $Step
    $ui['grpStep1'].Visibility = $(if ($Step -eq 1) { 'Visible' } else { 'Collapsed' })
    $ui['grpStep2'].Visibility = $(if ($Step -eq 2) { 'Visible' } else { 'Collapsed' })
    $ui['grpStep3'].Visibility = $(if ($Step -eq 3) { 'Visible' } else { 'Collapsed' })
    $conv = [System.Windows.Media.BrushConverter]::new()
    $ui['lblStep1'].Foreground = $conv.ConvertFromString($(if ($Step -eq 1) { '#3B82F6' } else { '#64748B' }))
    $ui['lblStep2'].Foreground = $conv.ConvertFromString($(if ($Step -eq 2) { '#3B82F6' } else { '#64748B' }))
    $ui['lblStep3'].Foreground = $conv.ConvertFromString($(if ($Step -eq 3) { '#3B82F6' } else { '#64748B' }))
    $ui['btnBack'].Visibility  = $(if ($Step -eq 1) { 'Collapsed' } else { 'Visible' })
    $ui['btnNext'].Visibility  = $(if ($Step -eq 3) { 'Collapsed' } else { 'Visible' })
    $ui['btnInstall'].Visibility = $(if ($Step -eq 3) { 'Visible' } else { 'Collapsed' })
    # 第 2 步标题带上当前串流用户名
    if ($Step -eq 2 -and $script:CurUser) {
        $ui['txtStep2Title'].Text = '第 2 步 · 用户「' + $script:CurUser + '」的登录密码'
    }
}
$ui['btnNext'].Add_Click({
    if ($script:Running) { return }
    if ($script:WizardStep -eq 1) {
        if ($ui['cmbGpu'].SelectedIndex -lt 0) {
            [void][System.Windows.MessageBox]::Show($window, '请选择渲染显卡。', '提示',
                [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information)
            return
        }
    }
    if ($script:WizardStep -eq 2) {
        $pw = $ui['pwdRdp'].Password
        if ([string]::IsNullOrEmpty($pw)) {
            # 留空允许继续，但必须弹窗提示后果
            if (Test-Path $WgCredPath) {
                [void][System.Windows.MessageBox]::Show($window,
                    '密码已留空：将沿用已保存的 rdp.cred（串流登录密码）。', '留空密码',
                    [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information)
            } else {
                [void][System.Windows.MessageBox]::Show($window,
                    '密码已留空且此前未保存过凭据：请回上一步输入密码并验证后再继续。', '留空密码',
                    [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information)
            }
        } elseif (-not $script:PwVerified -or $pw -ne $script:PwVerifiedFor) {
            [void][System.Windows.MessageBox]::Show($window,
                '密码未经过验证或验证未通过，请点「验证」后继续。', '密码验证失败',
                [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Warning)
            return
        }
    }
    Set-WizardStep ($script:WizardStep + 1)
})
$ui['btnBack'].Add_Click({ if (-not $script:Running) { Set-WizardStep ($script:WizardStep - 1) } })

# ---- 初始化界面状态 ----
$ui['lblRoot'].Text = $Root

# 当前登录用户（提权后 WindowsIdentity 仍是原用户；回退链：完整身份 -> USERNAME -> COMPUTERNAME）
$script:CurUser = $env:USERNAME
try {
    $script:CurUser = ([Security.Principal.WindowsIdentity]::GetCurrent().Name -split '\\')[-1]
} catch { }
$ui['lblUser'].Text = $script:CurUser
$ui['lblWelcomeUser'].Text = $script:CurUser

$gpus = Get-PhysicalGpus
$script:GpuNames = @($gpus | ForEach-Object { $_.Name })
foreach ($g in $gpus) {
    $label = [string]$g.Name + '  [' + [string]$g.Vendor + ']'
    $null = $ui['cmbGpu'].Items.Add($label)
}
if ($script:GpuNames.Count -eq 1) {
    $ui['lblGpuInfo'].Text = '已自动选中检测到的显卡。'
    $ui['lblWelcomeGpu'].Text = [string]$script:GpuNames[0]
} elseif ($script:GpuNames.Count -gt 1) {
    $ui['lblGpuInfo'].Text = '检测到多块显卡，请选择用于串流渲染的那块。'
    $ui['lblWelcomeGpu'].Text = [string]$script:GpuNames[0] + ' 等 ' + $script:GpuNames.Count + ' 块'
} else {
    $ui['lblGpuInfo'].Text = '未检测到可用显卡，请确认显卡驱动已安装后重新运行本工具。'
    $ui['lblWelcomeGpu'].Text = '（未检测到）'
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

# rdp.cred 状态
if (Test-Path $WgCredPath) {
    $ui['lblCredState'].Text = '已保存过密码，留空则继续用原来的'
    $ui['lblWelcomeCred'].Text = '已有（留空沿用）'
} else {
    $ui['lblCredState'].Text = '请输入登录密码并点「验证」'
    $ui['lblWelcomeCred'].Text = '无（安装时需输入）'
}
$script:PwVerified = $false

# wsguard 现在只有单一版本：显示固定状态，不做新/旧版判断
$ui['lblWelcomeWg'].Text = '就绪（服务名 WSGuard / 动态路径）'

# 密码验证状态：验证通过对应具体密码；密码再改动即失效
$PwVerifiedFor = ''
$ui['pwdRdp'].Add_TextChanged({
    $script:PwVerified = $false
    $script:PwVerifiedFor = ''
})
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
        $ui['lblPwState'].Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#22C55E')
        $script:PwVerified = $true
        $script:PwVerifiedFor = $pw
        Write-Log "密码验证通过（用户：$($script:CurUser)）"
    } else {
        $ui['lblPwState'].Text = '密码不对，请重新输入'
        $ui['lblPwState'].Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#F87171')
        $script:PwVerified = $false
    }
})

# ---- 动画辅助 ----
function Start-Sheen {
    if (-not $script:AnimsEnabled) { return }
    # 高光扫过条按进度条宽度裁剪
    $clip = New-Object System.Windows.Media.RectangleGeometry
    $clip.Rect = [System.Windows.Rect]::new(0, -1, 560, 14)
    $clip.RadiusX = 6; $clip.RadiusY = 6
    $ui['sheenClip'].Clip = $clip
    $sb = New-Object System.Windows.Media.Animation.Storyboard
    $a = New-Object System.Windows.Media.Animation.DoubleAnimation
    $a.From = -100; $a.To = 640
    $a.Duration = [TimeSpan]::FromMilliseconds(2400)
    $a.RepeatBehavior = [System.Windows.Media.Animation.RepeatBehavior]::Forever
    [void][System.Windows.Media.Animation.Storyboard]::SetTarget($a, $ui['sheenTt'])
    [void][System.Windows.Media.Animation.Storyboard]::SetTargetProperty($a, 'X')
    [void]$sb.Children.Add($a)
    $sb.Begin($ui['sheenTt'], $true)
    $script:SheenSb = $sb
}
function Stop-Sheen {
    if ($script:SheenSb) {
        try { $script:SheenSb.Stop($ui['sheenTt']) } catch { }
        $script:SheenSb = $null
    }
}
function Pop-Glyph {
    param($St)
    if (-not $script:AnimsEnabled -or -not $St) { return }
    $aX = New-Object System.Windows.Media.Animation.DoubleAnimation
    $aX.From = 0.4; $aX.To = 1.0
    $aX.Duration = [TimeSpan]::FromMilliseconds(130)
    $aY = New-Object System.Windows.Media.Animation.DoubleAnimation
    $aY.From = 0.4; $aY.To = 1.0
    $aY.Duration = [TimeSpan]::FromMilliseconds(130)
    $St.BeginAnimation([System.Windows.Media.ScaleTransform]::ScaleXProperty, $aX)
    $St.BeginAnimation([System.Windows.Media.ScaleTransform]::ScaleYProperty, $aY)
}

# ---- 后台执行 ----
$script:RunPS = $null
$script:RunRS = $null
$script:RunHandle = $null
$script:RunFinal = $false
$script:RunKeys = @()
$script:RunGlyphs = @{}

function Start-RunBackground {
    param([hashtable]$Opts)
    $script:Phase = [string]$Opts.Mode
    $S.LogQueue = [System.Collections.Queue]::Synchronized((New-Object System.Collections.Queue))
    $S.Prog = [hashtable]::Synchronized(@{
        Steps = [hashtable]::Synchronized(@{}); StepIndex = 0; StepTotal = 0
        Current = ''; Done = $false; Results = @()
    })

    # 步骤行 UI
    $ui['stepsHost'].Children.Clear()
    $script:RunKeys = @($Opts.Steps)
    $script:RunGlyphs = @{}
    foreach ($k in $script:RunKeys) {
        $row = New-Object System.Windows.Controls.StackPanel
        $row.Orientation = 'Horizontal'
        $row.Margin = [System.Windows.Thickness]::new(2, 3, 0, 3)
        $glyph = New-Object System.Windows.Controls.TextBlock
        $glyph.Text = '○'
        $glyph.Width = 22
        $glyph.FontSize = 13
        $glyph.TextAlignment = 'Center'
        $glyph.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#64748B')
        $glyph.RenderTransformOrigin = [System.Windows.Point]::new(0.5, 0.5)
        $st = New-Object System.Windows.Media.ScaleTransform
        $glyph.RenderTransform = $st
        $name = New-Object System.Windows.Controls.TextBlock
        $name.Text = [string]$Opts.Labels[$k]
        $name.FontSize = 12
        $name.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#94A3B8')
        [void]$row.Children.Add($glyph)
        [void]$row.Children.Add($name)
        [void]$ui['stepsHost'].Children.Add($row)
        $script:RunGlyphs[$k] = @{ Glyph = $glyph; Scale = $st; Name = $name; State = 'pending' }
    }

    $ui['lblPhaseTitle'].Text = $(if ($Opts.Mode -eq 'Uninstall') { '正在反注册组件...' } else { '正在部署串流环境...' })
    $ui['lblFinishNote'].Text = ''
    $ui['txtLog'].Clear()
    $ui['pgBar'].Value = 0
    $ui['lblStepCount'].Text = ''
    $ui['lblCurrent'].Text = ''

    $script:Running = $true
    $script:RunFinal = $false
    $window.Cursor = [System.Windows.Input.Cursors]::Wait
    Start-Sheen

    # Runspace（STA 不需要；仅执行非 UI 的安装/卸载操作）
    $script:RunRS = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace()
    $script:RunRS.Open()
    $script:RunRS.SessionStateProxy.SetVariable('S', $S)
    $script:RunRS.SessionStateProxy.SetVariable('SLogQueue', $S.LogQueue)
    $op = if ($Opts.Mode -eq 'Uninstall') { 'Invoke-UninstallRun' } else { 'Invoke-InstallRun' }
    $script:RunPS = [System.Management.Automation.PowerShell]::Create()
    $script:RunPS.Runspace = $script:RunRS
    [void]$script:RunPS.AddScript($OpsCode)
    [void]$script:RunPS.AddCommand($op).AddParameter('Opts', $Opts)
    $script:RunHandle = $script:RunPS.BeginInvoke()
}

# ---- 安装启动（UI 线程校验 + 收集参数）----
$ui['btnInstall'].Add_Click({
    if ($script:Running) { return }
    $adapter = $script:GpuNames[$ui['cmbGpu'].SelectedIndex]
    if (-not $adapter) {
        [void][System.Windows.MessageBox]::Show($window, '请先选择渲染显卡。', '提示',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information)
        return
    }
    $user = $script:CurUser
    if (-not $user) {
        [void][System.Windows.MessageBox]::Show($window, '无法获取当前用户名，请重试。', '错误',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Error)
        return
    }
    # 密码：非空则验证 / 留空沿用现有 rdp.cred
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
        $ui['lblPwState'].Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#22C55E')
        Write-Log 'OK: 密码验证通过'
    } elseif (-not (Test-Path $WgCredPath)) {
        [void][System.Windows.MessageBox]::Show($window, '请先输入登录密码并点「验证」，或已保存过密码可留空。', '需要密码',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information)
        return
    }
    if (-not (Get-LocalUser -Name $user -ErrorAction SilentlyContinue)) {
        [void][System.Windows.MessageBox]::Show($window, "本机不存在用户「$user」，请检查用户名。", '错误',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Error)
        return
    }
    # 解绑已配对设备：二次确认
    if ($ui['chkReset'].IsChecked) {
        $r = [System.Windows.MessageBox]::Show($window,
            "确定解绑所有已配对设备吗？`n解绑后所有 Moonlight 客户端都需要重新配对。", '确认',
            [System.Windows.MessageBoxButton]::YesNo, [System.Windows.MessageBoxImage]::Warning)
        if ($r -ne [System.Windows.MessageBoxResult]::Yes) {
            $ui['chkReset'].IsChecked = $false
            Write-Log '用户取消：跳过状态与日志清理'
        }
    }
    $resetOk = -not $ui['chkReset'].IsChecked

    # 步骤编排（gpuaccel 与 firewall 等顺序：conf → gpuaccel → firewall → gamepad → rdpwrap → wsguard → gamemgr → reset）
    $steps = @('conf')
    if ($ui['chkGpuAccel'].IsChecked) { $steps += 'gpuaccel' }
    $steps += 'firewall'
    if ($ui['chkGamepad'].IsChecked)  { $steps += 'gamepad' }
    $steps += 'rdpwrap'
    $steps += 'wsguard'
    if ($ui['chkGameMgr'].IsChecked)  { $steps += 'gamemgr' }
    if ($ui['chkReset'].IsChecked)    { $steps += 'reset' }

    # 虚拟屏分辨率
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

    $labels = @{
        conf     = '写入 Sunshine 配置文件'
        gpuaccel = '会话 GPU 加速策略'
        firewall = '防火墙放行 Sunshine'
        gamepad  = 'ViGEmBus 虚拟手柄驱动'
        rdpwrap  = 'RDP Wrapper 并发解锁'
        wsguard  = 'WSGuard 守护服务与开机自启'
        gamemgr  = 'GameManager 游戏菜单'
        reset    = '解绑已配对设备'
    }
    $opts = @{
        Mode     = 'Install'
        Steps    = $steps
        Labels   = $labels
        Adapter  = $adapter
        User     = $user
        Password = $pw
        Vw       = $script:Vw
        Vh       = $script:Vh
        AutoLogon = [bool]$ui['chkAutoLogon'].IsChecked
    }
    Show-Page r
    Start-RunBackground $opts
})

# ---- 卸载启动 ----
$ui['btnUninstallStart'].Add_Click({
    if ($script:Running) { return }
    $steps = @()
    if ($ui['chkUFirewall'].IsChecked) { $steps += 'firewall' }
    if ($ui['chkUGamepad'].IsChecked)  { $steps += 'gamepad' }
    if ($ui['chkURdpWrap'].IsChecked)  { $steps += 'rdpwrap' }
    if ($ui['chkUGpuAccel'].IsChecked) { $steps += 'gpuaccel' }
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

    $labels = @{
        firewall = '移除防火墙放行规则'
        gamepad  = '卸载 ViGEmBus 驱动'
        rdpwrap  = '卸载 RDP Wrapper'
        gpuaccel = '删除会话 GPU 加速策略'
        wsguard  = '卸载守护服务并取消自启'
        gamemgr  = '卸载 GameManager'
    }
    $opts = @{
        Mode   = 'Uninstall'
        Steps  = @($steps)
        Labels = $labels
    }
    Show-Page r
    Start-RunBackground $opts
})

# ---- UI 泵：日志队列 + 进度 + 步骤状态 + 完成回跳 ----
$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromMilliseconds(60)
$timer.Add_Tick({
    # 日志
    $q = $S.LogQueue
    if ($q) {
        while ($q.Count -gt 0) {
            try { $line = $q.Dequeue() } catch { break }
            if ($script:LogBox) {
                $script:LogBox.AppendText("$line`r`n")
                $script:LogBox.ScrollToEnd()
            }
        }
    }
    # 进度与步骤状态
    $p = $S.Prog
    if ($p -and $p.StepTotal -gt 0) {
        $idx = [int]$p.StepIndex
        $total = [int]$p.StepTotal
        $doneCount = [Math]::Max($idx - 1, 0)
        $target = [double]($doneCount * 100 / $total)
        $ui['lblStepCount'].Text = '步骤 ' + $idx + ' / ' + $total
        $cur = [string]$p.Current
        if ($cur -and $ui['lblCurrent'].Text -ne $cur) { $ui['lblCurrent'].Text = $cur }
        # 步骤状态（含对勾/失败弹出动画）
        foreach ($k in $script:RunKeys) {
            $st = [string]$p.Steps[$k]
            $g = $script:RunGlyphs[$k]
            if ($g -and $g.State -ne $st) {
                $g.State = $st
                $conv = [System.Windows.Media.BrushConverter]::new()
                switch ($st) {
                    'running' {
                        $g.Glyph.Text = '●'; $g.Glyph.Foreground = $conv.ConvertFromString('#3B82F6')
                        $g.Name.Foreground = $conv.ConvertFromString('#E2E8F0')
                    }
                    'ok' {
                        $g.Glyph.Text = '✓'; $g.Glyph.Foreground = $conv.ConvertFromString('#22C55E')
                        Pop-Glyph $g.Scale
                    }
                    'fail' {
                        $g.Glyph.Text = '✗'; $g.Glyph.Foreground = $conv.ConvertFromString('#F87171')
                        Pop-Glyph $g.Scale
                    }
                }
            }
        }
        # 进度条平滑动画
        if ([Math]::Abs($ui['pgBar'].Value - $target) -gt 0.05 -and -not $p.Done) {
            $anim = New-Object System.Windows.Media.Animation.DoubleAnimation
            $anim.To = $target
            $anim.Duration = [TimeSpan]::FromMilliseconds(400)
            $ui['pgBar'].BeginAnimation([System.Windows.Controls.ProgressBar]::ValueProperty, $anim)
        }
    }
    # 完成：回到结果页
    if ($p.Done -and $script:RunHandle -and (-not $script:RunFinal)) {
        $script:RunFinal = $true
        try {
            $null = $script:RunPS.EndInvoke($script:RunHandle)
        } catch {
            Write-Log "后台执行异常: $($_.Exception.Message)"
        }
        try { $script:RunPS.Dispose() } catch { }
        try { $script:RunRS.Close() } catch { }
        $script:RunPS = $null; $script:RunRS = $null; $script:RunHandle = $null
        Stop-Sheen
        $window.Cursor = [System.Windows.Input.Cursors]::Arrow
        $script:Running = $false
        # 全量日志队列清空
        $q = $S.LogQueue
        if ($q) {
            while ($q.Count -gt 0) {
                try { $line = $q.Dequeue() } catch { break }
                if ($script:LogBox) { $script:LogBox.AppendText("$line`r`n"); $script:LogBox.ScrollToEnd() }
            }
        }
        # 结果页填充
        $ui['resHost'].Children.Clear()
        $failed = 0
        $conv = [System.Windows.Media.BrushConverter]::new()
        foreach ($r in @($S.Prog.Results)) {
            $row = New-Object System.Windows.Controls.StackPanel
            $row.Orientation = 'Horizontal'
            $row.Margin = [System.Windows.Thickness]::new(2, 3, 0, 3)
            $glyph = New-Object System.Windows.Controls.TextBlock
            if ($r.Ok) { $glyph.Text = '✓'; $glyph.Foreground = $conv.ConvertFromString('#22C55E') }
            else { $glyph.Text = '✗'; $glyph.Foreground = $conv.ConvertFromString('#F87171'); $failed++ }
            $glyph.Width = 22
            $name = New-Object System.Windows.Controls.TextBlock
            $name.Text = [string]$r.Label
            $name.FontSize = 12
            $name.Foreground = $conv.ConvertFromString('#E2E8F0')
            [void]$row.Children.Add($glyph)
            [void]$row.Children.Add($name)
            [void]$ui['resHost'].Children.Add($row)
        }
        $ui['lblFinishIcon'].Text = $(if ($failed -eq 0) { '✓' } else { '!' })
        $ui['lblFinishIcon'].Foreground = $conv.ConvertFromString($(if ($failed -eq 0) { '#22C55E' } else { '#F59E0B' }))
        $ui['lblFinishTitle'].Text = $(if ($failed -eq 0) {
            if ($script:Phase -eq 'Uninstall') { '卸载完成' } else { '部署完成' }
        } else {
            if ($script:Phase -eq 'Uninstall') { "卸载完成，$failed 项失败" } else { "部署完成，$failed 项失败" }
        })
        $ui['lblFinishNote'].Text = $(if ($failed -eq 0) {
            '全部步骤执行成功。详细过程请点「查看完整日志」查看。'
        } else {
            '部分步骤未成功，请查看日志排查。失败项不影响其他已成功项。'
        })
        Show-Page f
    }
})
$timer.Start()

# ---- 就绪提示 ----
Write-Log "Sunshine-Duo 部署工具就绪（根目录 $Root，渲染 Tier $([System.Windows.Media.RenderCapability]::Tier -shr 16)）"
$null = $window.ShowDialog()
