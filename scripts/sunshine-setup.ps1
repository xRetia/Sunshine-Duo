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
    2 · 用户与密码（第二步是分组的：Console 主用户 + 游戏用户）：
       - Console 主用户：默认显示当前登录用户（不可取消对勾），SYSTEM 服务直接接管 Console 会话，不要求密码。
       - 游戏用户（RDP 串流）：必须填写的用户名 + 密码，密码经 LogonUser 验证；
         游戏会话由此用户的提升令牌拉起，开机自动登录有会话时 RDP 保活。
       - 远程桌面解锁（必装）：静默安装 tools\rdpWrapper.exe（官方命令行 -install，
         解锁单用户并发 RDP，同一账号可同时本地 + 远程在线）。
       - 会话 GPU 加速（默认勾选）：Terminal Services 策略写 3 个 DWORD，
         RDP 型会话改用物理显卡渲染 + AVC444 + 硬件编码。
    3 · 串流会话守护服务（必装，登录信息在密码卡片）：
       - 游戏用户作为串流用户，密码经 LogonUser 验证；含“开机自动登录”选项
         （无会话时 SunshineDuoCore 自动 RDP 拉起会话，auto_logon）与虚拟屏分辨率。
       - 注册 SunshineDuoCore 服务 / 显示名 Sunshine Duo 核心服务；密码经 DPAPI
         （本机作用域）加密为 rdp.cred，供 SunshineDuoCore 在串流断开后自动连接 127.0.0.2
         保持 RDP 虚拟显卡会话活跃。
       - Sunshine 自启 = SunshineDuoCore Apollo 守卫：写 config.json 的 guard_apollo，tick 检测到
         会话无 sunshine.exe 时以提升令牌（WTSQueryUserToken + TokenLinkedToken +
         CreateProcessAsUserW）在会话内拉起；提升运行可向高完整性窗口/游戏注入输入，
         由 SYSTEM 服务直接创建不进任何 Job（CREATE_BREAKAWAY_FROM_JOB 拉起 steam://、
         detached 应用不报 err 5）。安装时同步清理旧自启机制（Run 键与计划任务）。
    4 · 可选功能：
       - GameManager（tools\GameManager.exe）：可直接启动需要管理员权限的游戏，
         快捷方式目录为 <Sunshine>\Links（写入 tools\GameManager.json 的 LinksFolder）。
       - 解绑两个 Apollo 各自的已配对设备（执行时二次确认）、清空两份日志。
    5 · WebUI 后台账号（独立一页）：用户名 + 密码 + 确认密码，与 RDP 登录账号分开；
       两个 Apollo 各自保存凭证（初始相同），安装完成页显示后台地址与凭证。
  卸载：与安装分组一一对应的反注册操作（不删除程序文件、配置与凭据）：
        移除防火墙规则（scripts\delete-firewall-rule.bat）、卸载手柄驱动
        （scripts\uninstall-gamepad.ps1）、卸载 RdpWrapper、删除会话 GPU 加速策略、
        关闭 Apollo 守卫并删除 SunshineDuoCore 服务、卸载 GameManager；同步清理旧名任务
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
$SvcName      = 'SunshineDuoCore'                                 # 核心服务名（与 exe 内嵌一致）
$SvcDispName  = 'Sunshine Duo 核心服务'              # 核心服务显示名
$LegacySvc    = 'GameViewerSessionHook'                   # 旧服务名（迁移清理用）
$LegacyTask   = 'Sunshine Autostart (Game)'               # 旧任务名（迁移清理用）
$GmSvcName    = 'GameManagerService'                      # GameManager 服务名
$RdpWrapDir   = 'C:\Program Files\RDP Wrapper'            # rdpWrapper 默认安装目录
$LinksDir     = Join-Path $Root 'Links'                   # GameManager 快捷方式目录

$ConfPath     = Join-Path $Root 'config\sunshine.conf'
$LegacyStatePath  = Join-Path $Root 'config\sunshine_state.json'
$ConsoleStatePath = Join-Path $Root 'config\sunshine_state-console.json'
$GameStatePath    = Join-Path $Root 'config\sunshine_state-game.json'
$LogPath      = Join-Path $Root 'config\sunshine.log'
$LogBakPath   = Join-Path $Root 'config\sunshine.log.backup'
$TsPolicyKey  = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
$TsGpuAccelValues = @('bEnumerateHWBeforeSW', 'bAVC444Mode', 'AVCHardwareEncodePreferred')
$RdpWrapExe   = Join-Path $Root 'tools\rdpWrapper.exe'
$GmExe        = Join-Path $Root 'tools\GameManager.exe'
$WgDir        = Join-Path $Root 'bin\SunshineDuoCore'
$WgExe        = Join-Path $WgDir 'SunshineDuoCore.exe'
$WgCredPath   = Join-Path $WgDir 'rdp.cred'
$WgCfgPath    = Join-Path $WgDir 'config.json'
$GameConfPath = Join-Path $Root 'config\sunshine-game.conf'

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
$LegacyStatePath = if ($LegacyStatePath) { $LegacyStatePath } elseif ($S) { $S.LegacyStatePath }
$ConsoleStatePath = if ($ConsoleStatePath) { $ConsoleStatePath } elseif ($S) { $S.ConsoleStatePath }
$GameStatePath    = if ($GameStatePath)    { $GameStatePath }    elseif ($S) { $S.GameStatePath }
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
$GameConfPath  = if ($GameConfPath)  { $GameConfPath }  elseif ($S) { $S.GameConfPath }

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

# 3 · Sunshine 开机自启 = SunshineDuoCore Apollo 守卫：写 config.json 的 guard_apollo，
#     并清理旧自启机制（Run 键 / 计划任务）。Apollo 由 SunshineDuoCore 服务以提升令牌拉起。
function Install-ApolloGuard {
    param([string]$User, [bool]$GameEnabled = $true)
    if (-not (Test-Path (Join-Path $Root 'sunshine.exe'))) {
        throw "未找到 $($Root)\sunshine.exe，请确认脚本位于 Sunshine\scripts 目录"
    }
    if (-not (Test-Path $WgCfgPath)) {
        throw "未找到 $WgCfgPath（SunshineDuoCore 未安装？）"
    }

    # config.json：设置 guard_apollo（游戏会话）与 guard_apollo_console（主会话）
    # 单会话模式下：console 始终启用，game 由 GameEnabled 控制
    $cfg = Get-Content $WgCfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $cfg | Add-Member -NotePropertyName guard_apollo -NotePropertyValue ([bool]$GameEnabled) -Force
    $cfg | Add-Member -NotePropertyName guard_apollo_console -NotePropertyValue $true -Force
    [IO.File]::WriteAllText($WgCfgPath, ($cfg | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
    $guardNote = if ($GameEnabled) { 'guard_apollo=true' } else { 'guard_apollo=false（单会话模式：仅主会话）' }
    Write-Log "OK: bin\SunshineDuoCore\config.json 已设置 $guardNote（Apollo 守卫：提升令牌拉起 + 不进 Job）"

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
    $gameFlag = if ($GameEnabled) { '（含游戏会话）' } else { '（单会话模式：无游戏会话）' }
    Write-Log "提示: $User 用户会话就绪后由 SunshineDuoCore 自动拉起 Sunshine$gameFlag（主会话存在 + sunshine.exe 不在时触发）"
}

# 1 · 写入 sunshine.conf（必装：显卡 / 手柄 / UPnP 等默认配置）
function Write-SunshineConf {
    param([string]$AdapterName, [int]$Port = 0, [string]$DisplayMode = 'mirror',
          [string]$SudovdaRes = '')
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
        'upnp = enabled',
        'locale = zh',
        'sunshine_name = 主会话',
        "file_state = $ConsoleStatePath"
    )
    if ($Port -gt 0) { $lines += "port = $Port" }
    $lines += "log_path = $(Join-Path $confDir 'sunshine-console.log')"
    # 显示拓扑（虚拟屏独占/断开物理屏）不在配置阶段写死：Apollo 0.4.6 对 virtual
    # display 会话不应用 dd_configuration_option，且启动后的
    # display_device::reset_persistence() 会取消退避重试。由 SunshineDuoCore 运行期
    # 按"虚拟屏是否存在"自动执行（见 SunshineDuoCore 的 DisplayExclusive 逻辑）。
    [IO.File]::WriteAllLines($ConfPath, $lines, (New-Object System.Text.UTF8Encoding($false)))
    $portNote = if ($Port -gt 0) { ", port = $Port" } else { '' }
    $modeNote = switch ($DisplayMode) { 'only' { ', 独占虚拟屏' } 'extend' { ', 扩展虚拟屏' } default { ', 镜像物理屏' } }
    Write-Log "OK: Sunshine 配置文件已写入（adapter_name = $AdapterName$portNote$modeNote）"
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

# 4 · 迁移旧共享 state：首次升级时把已有配对复制到两个实例各自的 state
function Split-LegacySunshineState {
    if (-not (Test-Path $LegacyStatePath)) { return }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    foreach ($target in @($ConsoleStatePath, $GameStatePath)) {
        if (Test-Path $target) { continue }
        $json = [IO.File]::ReadAllText($LegacyStatePath, [Text.Encoding]::UTF8)
        [IO.File]::WriteAllText($target, $json, $utf8)
        Write-Log "OK: 已把旧配对数据迁移到 $(Split-Path -Leaf $target)"
    }
}

# Moonlight 按 root.uniqueid 识别主机；旧 state 拆分后需避免两实例身份相同。
function Ensure-DistinctUniqueIds {
    $ids = @{}
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    foreach ($pair in @(@{ n = '控制台'; p = $ConsoleStatePath }, @{ n = '游戏'; p = $GameStatePath })) {
        if (-not (Test-Path $pair.p)) { continue }
        try {
            $obj = Get-Content -LiteralPath $pair.p -Raw -Encoding UTF8 | ConvertFrom-Json
        } catch {
            throw "无法读取 $(Split-Path -Leaf $pair.p)：$($_.Exception.Message)"
        }
        if (-not $obj) { $obj = New-Object PSObject }
        if (-not $obj.PSObject.Properties['root']) {
            $obj | Add-Member -NotePropertyName root -NotePropertyValue (New-Object PSObject) -Force
        }
        $uid = [string]$obj.root.uniqueid
        if ([string]::IsNullOrEmpty($uid) -or $uid -notmatch '^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$') {
            $uid = [guid]::NewGuid().ToString()
            $obj.root | Add-Member -NotePropertyName uniqueid -NotePropertyValue $uid -Force
            [IO.File]::WriteAllText($pair.p, ($obj | ConvertTo-Json -Depth 10), $utf8)
            $ids[$uid] = $pair.n
            Write-Log "OK: $($pair.n)主机身份已生成（uniqueid $uid）"
            continue
        }
        if ($ids.ContainsKey($uid)) {
            $new = [guid]::NewGuid().ToString()
            $obj.root.uniqueid = $new
            [IO.File]::WriteAllText($pair.p, ($obj | ConvertTo-Json -Depth 10), $utf8)
            Write-Log "OK: $($pair.n)主机身份与$($ids[$uid])重复，已换成 $new（Moonlight 才能同时显示两个主机）"
        } else {
            $ids[$uid] = $pair.n
            Write-Log "OK: $($pair.n)主机身份（uniqueid $uid）"
        }
    }
}

# 4 · 清空状态与日志（可选：解绑所有已配对设备，执行前二次确认）
function Reset-SunshineState {
    foreach ($state in @($ConsoleStatePath, $GameStatePath, $LegacyStatePath)) {
        if (Test-Path $state) {
            [IO.File]::WriteAllText($state, '{}', (New-Object System.Text.UTF8Encoding($false)))
            Write-Log "OK: $(Split-Path -Leaf $state) 已清空（已配对设备全部移除，Moonlight 客户端需重新配对）"
        } else {
            Write-Log "跳过: $(Split-Path -Leaf $state) 不存在"
        }
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

# 3 · SunshineDuoCore 服务（必装，rdp.cred 由密码经 DPAPI 本机作用域加密生成）
function Install-SunshineDuoCore {
    param([string]$RdpUser, [string]$Password, [int]$Vw = 0, [int]$Vh = 0, [bool]$AutoLogon = $false,
          [string]$DisplayMode = 'mirror', [bool]$DisplayExclusive = $false, [int]$PortMain = 48100, [int]$PortGame = 48200,
          [string]$ConsoleConf = 'config\sunshine.conf', [string]$GameConf = 'config\sunshine-game.conf',
          [string[]]$SudovdaResList = @(), [bool]$GameEnabled = $true)
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
        Write-Log '密码留空: 沿用现有 bin\SunshineDuoCore\rdp.cred'
    } else {
        throw '未提供密码且 rdp.cred 不存在，无法安装 SunshineDuoCore'
    }

    # config.json：不存在则生成默认配置；存在则同步 Apollo 守卫与显示模式/独占开关（热重载）
    # GameEnabled=false 表示单会话模式：仅设 guard_apollo_console（由 Install-ApolloGuard 写入），guard_apollo 留空或 false
    if (Test-Path $WgCfgPath) {
        $cfg = Get-Content $WgCfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($GameEnabled) {
            $cfg | Add-Member -NotePropertyName guard_apollo -NotePropertyValue $true -Force
        }
        $cfg | Add-Member -NotePropertyName guard_apollo_console -NotePropertyValue $true -Force
        $cfg | Add-Member -NotePropertyName auto_logon -NotePropertyValue ([bool]$AutoLogon) -Force
        $cfg | Add-Member -NotePropertyName default_display_mode -NotePropertyValue $DisplayMode -Force
        $cfg | Add-Member -NotePropertyName display_exclusive -NotePropertyValue ([bool]$DisplayExclusive) -Force
        $cfg | Add-Member -NotePropertyName console_port -NotePropertyValue $PortMain -Force
        $cfg | Add-Member -NotePropertyName game_port -NotePropertyValue $PortGame -Force
        if ($SudovdaResList.Count -gt 0) {
            $cfg | Add-Member -NotePropertyName sudovda_resolutions -NotePropertyValue @($SudovdaResList) -Force
        }
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
            console_port      = $PortMain
            game_port         = $PortGame
            default_display_mode = $DisplayMode
            display_exclusive = [bool]$DisplayExclusive
            auto_logon        = [bool]$AutoLogon
            log_level         = 'warn'
            interval          = 60
            recycle_minutes   = 30
            gameviewer_versions = @('4.0.*', '4.46.*')
        }
        if ($SudovdaResList.Count -gt 0) { $cfg.sudovda_resolutions = @($SudovdaResList) }
        $json = $cfg | ConvertTo-Json
        [IO.File]::WriteAllText($WgCfgPath, $json, (New-Object System.Text.UTF8Encoding($false)))
        $alNote2 = if ($AutoLogon) { '开机自动登录开' } else { '开机自动登录关' }
        Write-Log "OK: bin\SunshineDuoCore\config.json 已生成（默认参数：127.0.0.2 / ${w}x${h} / 30 分钟重建 / Apollo 守卫开 / $alNote2 / 日志仅警告与错误 / GameViewer 4.0.*、4.46.*）"
    }

    # 服务幂等注册
    if (Get-Service -Name $SvcName -ErrorAction SilentlyContinue) {
        Write-Log "已存在服务 $SvcName，先移除再重建..."
        $null = & sc.exe stop $SvcName 2>&1
        Start-Sleep -Seconds 2
        Get-Process -Name 'SunshineDuoCore' -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        $null = & sc.exe delete $SvcName 2>&1
        Start-Sleep -Seconds 1
    }
    # 清理旧名服务（改名前残留，指向同目录）
    if ($LegacySvc -ne $SvcName -and (Get-Service -Name $LegacySvc -ErrorAction SilentlyContinue)) {
        $null = & sc.exe stop $LegacySvc 2>&1
        Start-Sleep -Seconds 2
        Get-Process -Name 'SunshineDuoCore' -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        $null = & sc.exe delete $LegacySvc 2>&1
        Start-Sleep -Seconds 1
        Write-Log "已删除旧名服务 $LegacySvc（新名 $SvcName 已取代）"
    }
    $null = & sc.exe create $SvcName binPath= $WgExe start= auto DisplayName= $SvcDispName 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sc create 失败（错误码 $LASTEXITCODE）" }
    $null = & sc.exe description $SvcName 'Keeps the streaming user RDP session alive (headless client) and GameViewer pinned to the console session.' 2>&1
    $null = & sc.exe failure $SvcName reset= 86400 actions= 'restart/60000/restart/60000/restart/60000' 2>&1
    $startedAt = Get-Date
    $null = & sc.exe start $SvcName 2>&1
    Start-Sleep -Seconds 3
    Update-Ui
    $svc = Get-Service -Name $SvcName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') {
        Write-Log "OK: 服务 $SvcName 运行中（SYSTEM / 自启动 / 崩溃 1 分钟自重启）"
    } elseif ($svc) {
        throw "服务已注册但状态为 $($svc.Status)，请查看 bin\SunshineDuoCore\watch.log"
    } else {
        throw "服务 $SvcName 注册失败"
    }
    # 只回显本次服务启动后新增的日志行：watch.log 是累积的，直接 tail 会把
    # 上一次安装遗留的旧错误当成这次的问题展示出来
    $watch = Join-Path $WgDir 'watch.log'
    if (Test-Path $watch) {
        $lines = @(Get-Content -LiteralPath $watch -ErrorAction SilentlyContinue)
        $new = @()
        foreach ($l in $lines) {
            if ($l -match '^\s*(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})') {
                $ts = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss', $null)
                if ($ts -ge $startedAt) { $new += $l }
            }
        }
        if ($new.Count -gt 0) {
            $new | Select-Object -Last 6 | ForEach-Object { Write-Log "  [watch] $_" }
        }
    }
}

# 3 · 生成游戏会话（次 Apollo）配置文件（从主 conf 复制，只覆盖端口与显示相关）
function Write-GameConf {
    param([string]$ConsoleConfPath, [string]$GameConfPath, [int]$PortGame, [string]$DisplayMode = 'physical',
          [int]$Vw = 0, [int]$Vh = 0)
    $read = Get-Content -LiteralPath $ConsoleConfPath -Encoding UTF8
    if (-not $read) { throw "主配置文件 [$ConsoleConfPath] 未找到" }
    $lines = @($read) | Where-Object { $_ -notmatch '^\s*log_path\s*=' }
    $lines += "log_path = $(Join-Path (Split-Path -Parent $GameConfPath) 'sunshine-game.log')"
    if ($PortGame -gt 0) {
        # 覆盖或删除已有 port 行（防止主 conf 不带 port 首次生成失效）
        $lines = $lines | Where-Object { $_ -notmatch '^\s*port\s*=' }
        $lines += "port = $PortGame"
    }
    # Game 会话走 RDP 显示驱动：剥离 Console 专属的虚拟屏 / dd_* 配置，避免误改 RDP 会话显示
    # 名称与 state 必须各自独立：Moonlight 显示两个主机，配对设备互不争写
    $lines = $lines | Where-Object {
        $_ -notmatch '^\s*(dd_\w+|always_use_virtual_display|isolated_virtual_display_option|output_name_windows|sunshine_name|file_state)\s*='
    }
    $lines += 'sunshine_name = 游戏会话'
    $lines += "file_state = $GameStatePath"
    [IO.File]::WriteAllLines($GameConfPath, $lines, (New-Object System.Text.UTF8Encoding($false)))
    $portNote = if ($PortGame -gt 0) { ", port = $PortGame" } else { '' }
    Write-Log "OK: 游戏会话配置文件已生成 [$GameConfPath]$portNote（独立 state，共享 apps/证书）"
}

# 3 · 生成 WebUI 登录凭证（`sunshine.exe <conf> --creds user password`，写入该 conf 的 state）
function Set-WebUICredentials {
    param([string]$User, [string]$Password, [bool]$GameEnabled = $true, [int]$PortMain = 0, [int]$PortGame = 0)
    $exe = Join-Path $Root 'sunshine.exe'
    if (-not (Test-Path $exe)) { throw "未找到 $exe" }
    $confs = @($ConfPath)
    if ($GameEnabled) { $confs += $GameConfPath }
    foreach ($conf in $confs) {
        if (-not (Test-Path $conf)) { throw "未找到配置文件 $conf，无法设置 WebUI 凭证" }
        $argLine = '"{0}" --creds "{1}" "{2}"' -f $conf, $User, $Password
        $p = Start-Process -FilePath $exe -ArgumentList $argLine -WindowStyle Hidden -PassThru -Wait
        if ($p.ExitCode -ne 0) {
            throw "sunshine --creds 返回码 $($p.ExitCode)（$conf），请手动在 Web UI 设置用户名和密码"
        }
    }
    $gameNote = if ($GameEnabled) { '（控制台与游戏会话各存一份，用户名/密码相同）' } else { '（仅主会话，控制台凭证）' }
    Write-Log ('OK: Apollo WebUI 凭证已设置' + $gameNote)
    $addr = '提示: WebUI 后台地址 https://localhost:' + ($PortMain + 1) + '（控制台）'
    if ($GameEnabled) { $addr += ' / https://localhost:' + ($PortGame + 1) + '（游戏会话）' }
    Write-Log ($addr + '，用户名 ' + $User)
}

# 3 · 可选驱动：SudoVDA 虚拟显示器（独立于显示模式开关，仅决定是否安装驱动）
function Install-SudoVDA {
    $bat = Join-Path $Root 'scripts\install-sudovda.bat'
    if (-not (Test-Path $bat)) { throw "未找到 $bat" }
    Write-Log '执行 scripts\install-sudovda.bat（安装 SudoVDA 虚拟显示驱动）...'
    $p = Start-Process -FilePath $bat -WindowStyle Hidden -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "install-sudovda.bat 返回码 $($p.ExitCode)（证书=1 / 建设备节点=2 / 装驱动=3）" }
    # PnP 枚举与就绪可能滞后，「需重启生效」是合法状态：出现即成功，只有始终不出现才算失败
    $dev = @()
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $dev = @(Get-PnpDevice | Where-Object {
            $_.Present -and
            (($_.InstanceId -match 'sudovda') -or ((@($_.HardwareID) -join ';') -match 'sudovda'))
        })
        if ($dev.Count -gt 0 -and $dev[0].Status -eq 'OK') { break }
    }
    if ($dev.Count -eq 0) {
        throw 'SudoVDA 设备 10 秒内未出现（root\sudomaker\sudovda）；若设备管理器已有此设备但提示需重启，重启电脑即生效'
    }
    if ($dev[0].Status -ne 'OK') {
        Write-Log ('注意: SudoVDA 设备已出现，状态 {0}（{1}）；若设备管理器提示需重启，重启电脑后生效' -f $dev[0].Status, $dev[0].Problem)
        return
    }
    Write-Log "OK: SudoVDA 虚拟显示驱动已就绪（$($dev[0].FriendlyName)，Status=$($dev[0].Status)）"
}
function Uninstall-SudoVDA {
    $bat = Join-Path $Root 'scripts\uninstall-sudovda.bat'
    if (-not (Test-Path $bat)) { throw "未找到 $bat" }
    Write-Log '执行 scripts\uninstall-sudovda.bat（卸载 SudoVDA 虚拟显示驱动）...'
    $p = Start-Process -FilePath $bat -WindowStyle Hidden -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "uninstall-sudovda.bat 返回码 $($p.ExitCode)" }
    # remove-device-node 自身吞掉非零码，只以设备是否还在为准；移除同样可能滞后，轮询确认
    $dev = @()
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $dev = @(Get-PnpDevice | Where-Object {
            $_.Present -and
            (($_.InstanceId -match 'sudovda') -or ((@($_.HardwareID) -join ';') -match 'sudovda'))
        })
        if ($dev.Count -eq 0) { break }
    }
    if ($dev.Count -gt 0) {
        Write-Log "注意: SudoVDA 设备仍在（$($dev[0].InstanceId)，Status=$($dev[0].Status)）；remove-device-node 未生效，重启电脑后再卸载一次即可移除"
        return
    }
    Write-Log 'OK: SudoVDA 虚拟显示驱动已卸载'
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
#     并清理旧自启机制残留（Run 键 / 计划任务）。SunshineDuoCore 服务与显示保活功能不受影响。
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

# 3 · 删除 SunshineDuoCore 服务（程序文件与 rdp.cred 保留）
function Uninstall-SunshineDuoCore {
    $any = $false
    foreach ($n in @($SvcName, $LegacySvc)) {
        $svc = Get-Service -Name $n -ErrorAction SilentlyContinue
        if (-not $svc) { continue }
        $null = & sc.exe stop $n 2>&1
        for ($i = 0; $i -lt 10; $i++) {
            if (-not (Get-Process -Name 'SunshineDuoCore' -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Seconds 1
            Update-Ui
        }
        Get-Process -Name 'SunshineDuoCore' -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        $null = & sc.exe delete $n 2>&1
        if (Get-Service -Name $n -ErrorAction SilentlyContinue) {
            Write-Log "警告: sc delete $n 失败，请稍后手动执行: sc delete $n"
        } else {
            Write-Log "OK: 服务 $n 已删除（SunshineDuoCore 程序文件与 rdp.cred 均保留）"
        }
        $any = $true
    }
    if (-not $any) { Write-Log '跳过: SunshineDuoCore 服务不存在（可能已卸载）' }
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

# 5 · Sunshine-Duo 托盘（部署 / 快捷方式 / 开机自启，可选组件）
# 自启写入「当前登录用户」的 Run 键（按 SID 定位 hive，与托盘内置「设置」同名同值，互相覆盖一致）
function Get-ConsoleUserInfo {
    $name = try { (Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).UserName } catch { $null }
    if (-not $name) { return $null }
    try {
        $sid = (New-Object System.Security.Principal.NTAccount($name)).Translate(
            [System.Security.Principal.SecurityIdentifier]).Value
        $pk = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid"
        $profile = (Get-ItemProperty -Path $pk -ErrorAction Stop).ProfileImagePath
    } catch { return $null }
    if (-not $profile) { return $null }
    $profile = [Environment]::ExpandEnvironmentVariables($profile)
    return [PSCustomObject]@{
        Name      = $name
        Sid       = $sid
        Desktop   = Join-Path $profile 'Desktop'
        StartMenu = Join-Path $profile 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs'
        RunKey    = "Registry::HKEY_USERS\$sid\Software\Microsoft\Windows\CurrentVersion\Run"
    }
}

function Find-TrayAppExe {
    $cands = @(
        (Join-Path $Root 'src\trayapp\bin\Release\net9.0-windows\win-x64\publish\SunshineDuoTray.exe'),
        (Join-Path $Root 'src\trayapp\SunshineDuoTray.exe'),
        (Join-Path $Root 'src\trayapp\publish\SunshineDuoTray.exe'),
        (Join-Path $Root 'bin\SunshineDuoTray\SunshineDuoTray.exe')
    )
    foreach ($c in $cands) { if (Test-Path -LiteralPath $c) { return $c } }
    return $null
}

function Install-TrayApp {
    param([bool]$DesktopShortcut, [bool]$StartShortcut, [bool]$AutoStart,
          [int]$PortMain = 0, [int]$PortGame = 0, [string]$WebUser = '', [bool]$StartNow = $false)
    $deployDir = Join-Path $Root 'bin\SunshineDuoTray'
    $deployExe = Join-Path $deployDir 'SunshineDuoTray.exe'
    $src = Find-TrayAppExe
    if (-not $src) {
        Write-Log '警告: 未找到 SunshineDuoTray.exe（先在 src\trayapp 运行 dotnet publish），已跳过托盘部署'
        return
    }
    Get-Process -Name 'SunshineDuoTray' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $deployDir -Force | Out-Null
    if ([IO.Path]::GetFullPath($src) -ne [IO.Path]::GetFullPath($deployExe)) {
        Copy-Item -LiteralPath $src -Destination $deployExe -Force
    }
    Write-Log "OK: 托盘应用已部署 $deployExe"

    # 同步托盘配置：端口与后台用户名跟本次安装保持一致；已加密的 WebUI 密码与自启设置保留
    # 单会话模式（PortGame = 0）也写：托盘据此知道游戏会话不存在
    if ($PortMain -gt 0) {
        $trayJsonPath = Join-Path $deployDir 'trayapp.json'
        try {
            $tcfg = $null
            if (Test-Path -LiteralPath $trayJsonPath) {
                $tcfg = Get-Content -LiteralPath $trayJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
            }
            if (-not $tcfg) { $tcfg = New-Object PSObject }
            $tcfg | Add-Member -NotePropertyName ConsolePort -NotePropertyValue ([int]$PortMain) -Force
            $tcfg | Add-Member -NotePropertyName GamePort -NotePropertyValue ([int]$PortGame) -Force
            if (-not [string]::IsNullOrEmpty($WebUser)) {
                $tcfg | Add-Member -NotePropertyName WebUser -NotePropertyValue $WebUser -Force
            }
            if (-not ($tcfg.PSObject.Properties.Name -contains 'WebPassEnc')) {
                $tcfg | Add-Member -NotePropertyName WebPassEnc -NotePropertyValue '' -Force
            }
            if (-not ($tcfg.PSObject.Properties.Name -contains 'AutoStart')) {
                $tcfg | Add-Member -NotePropertyName AutoStart -NotePropertyValue ([bool]$AutoStart) -Force
            }
            [IO.File]::WriteAllText($trayJsonPath, ($tcfg | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
            Write-Log "OK: 托盘配置已同步 $trayJsonPath（Console $PortMain / Game $PortGame）"
        } catch {
            Write-Log "警告: 写入 trayapp.json 失败: $($_.Exception.Message)"
        }
    }

    if ($StartNow) {
        try {
            Start-Process -FilePath $deployExe -WorkingDirectory $deployDir
            Write-Log 'OK: Sunshine-Duo 托盘已启动'
        } catch {
            Write-Log "警告: 托盘启动失败: $($_.Exception.Message)"
        }
    }

    $u = Get-ConsoleUserInfo
    if (-not $u) {
        Write-Log '警告: 未能确定当前登录用户，快捷方式/开机自启已跳过'
        return
    }
    if ($DesktopShortcut -or $StartShortcut) {
        $ws = New-Object -ComObject WScript.Shell
        foreach ($p in @(
                @{ On = $DesktopShortcut; Dir = $u.Desktop },
                @{ On = $StartShortcut; Dir = $u.StartMenu })) {
            if (-not $p.On) { continue }
            if (-not (Test-Path -LiteralPath $p.Dir)) { New-Item -ItemType Directory -Path $p.Dir -Force | Out-Null }
            $lnkPath = Join-Path $p.Dir '萌远控 Duo.lnk'
            $lnk = $ws.CreateShortcut($lnkPath)
            $lnk.TargetPath = $deployExe
            $lnk.WorkingDirectory = $deployDir
            $lnk.IconLocation = "$deployExe,0"
            $lnk.Description = '萌远控 Duo：会话状态与常用操作'
            $lnk.Save()
            Write-Log "OK: 已创建快捷方式 $lnkPath"
        }
    }
    if ($AutoStart) {
        try {
            if (-not (Test-Path $u.RunKey)) { New-Item -Path $u.RunKey -Force | Out-Null }
            New-ItemProperty -Path $u.RunKey -Name 'SunshineDuoTray' -Value ('"{0}"' -f $deployExe) `
                -PropertyType String -Force | Out-Null
            Write-Log "OK: 已配置开机自启（用户 $($u.Name)）"
        } catch {
            Write-Log "警告: 开机自启设置失败: $($_.Exception.Message)"
        }
    }
}

function Uninstall-TrayApp {
    $any = $false
    Get-Process -Name 'SunshineDuoTray' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    foreach ($k in (Get-ChildItem 'Registry::HKEY_USERS' -ErrorAction SilentlyContinue)) {
        $sid = $k.PSChildName
        if ($sid -like '*_Classes' -or $sid -notlike 'S-1-5-21-*') { continue }
        $runKey = "Registry::HKEY_USERS\$sid\Software\Microsoft\Windows\CurrentVersion\Run"
        if (Test-Path $runKey) {
            $props = Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue
            if ($props.PSObject.Properties.Name -contains 'SunshineDuoTray') {
                Remove-ItemProperty -Path $runKey -Name 'SunshineDuoTray' -ErrorAction SilentlyContinue
                Write-Log "OK: 已删除用户 hive [$sid] 的托盘自启键"
                $any = $true
            }
        }
        $pk = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid"
        try {
            $profile = [Environment]::ExpandEnvironmentVariables(
                (Get-ItemProperty -Path $pk -ErrorAction Stop).ProfileImagePath)
            foreach ($lnk in @(
                    (Join-Path $profile 'Desktop\萌远控 Duo.lnk'),
                    (Join-Path $profile 'Desktop\Sunshine-Duo 托盘.lnk'),
                    (Join-Path $profile 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs\萌远控 Duo.lnk'),
                    (Join-Path $profile 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Sunshine-Duo 托盘.lnk'))) {
                if (Test-Path -LiteralPath $lnk) {
                    Remove-Item -LiteralPath $lnk -Force -ErrorAction SilentlyContinue
                    Write-Log "OK: 已删除快捷方式 $lnk"
                    $any = $true
                }
            }
        } catch { }
    }
    # 程序目录：删 exe，保留 trayapp.json（端口与登录配置）
    $deployDir = Join-Path $Root 'bin\SunshineDuoTray'
    $exe = Join-Path $deployDir 'SunshineDuoTray.exe'
    if (Test-Path -LiteralPath $exe) {
        Remove-Item -LiteralPath $exe -Force -ErrorAction SilentlyContinue
        Write-Log "OK: 已删除 $exe（保留 trayapp.json）"
        $any = $true
    }
    if (-not $any) { Write-Log '提示: 未发现 Sunshine-Duo 托盘残留' }
}

# 停止并结束 SunshineDuoCore / Sunshine(双 Apollo) / 托盘进程。
# 安装与卸载都先走这里：SunshineDuoCore 判断 Apollo 是否存活只看"会话里有没有 sunshine.exe"，
# 不看它跑的是哪份 conf/端口；不清场的话，旧端口实例会被判为"仍在运行"，
# 新配置的 Apollo 不会被拉起（表现为"装完 X 端口是 Main、Y 端口不在线"）。
function Stop-StreamingProcesses {
    foreach ($n in @($SvcName, $LegacySvc)) {
        if (Get-Service -Name $n -ErrorAction SilentlyContinue) {
            $null = & sc.exe stop $n 2>&1
        }
    }
    Start-Sleep -Seconds 1
    $killed = @()
    foreach ($n in @('SunshineDuoCore', 'sunshine', 'SunshineDuoTray')) {
        $procs = @(Get-Process -Name $n -ErrorAction SilentlyContinue)
        if ($procs.Count -gt 0) {
            $procs | Stop-Process -Force -ErrorAction SilentlyContinue
            $killed += ("{0} x{1}" -f $n, $procs.Count)
        }
    }
    if ($killed.Count -gt 0) {
        Start-Sleep -Seconds 2
        Write-Log ("OK: 已停止旧进程（{0}）" -f ($killed -join '、'))
    } else {
        Write-Log '提示: 无 SunshineDuoCore / Sunshine / 托盘进程在运行'
    }
}

# 经 \\.\pipe\SunshineDuoCore-ctrl 让 SunshineDuoCore 立即执行一次 tick（不等 60s 周期），
# 服务启动后数秒内即可把两个 Apollo 拉起来。
function Send-SunshineDuoCoreTick {
    param([int]$TimeoutSec = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
                '.', 'SunshineDuoCore-ctrl', [System.IO.Pipes.PipeDirection]::InOut)
            $pipe.Connect(2000)
            try {
                $writer = New-Object IO.StreamWriter($pipe)
                $writer.NewLine = "`n"
                $writer.AutoFlush = $true
                $writer.WriteLine('{"cmd":"tick"}')
                $reader = New-Object IO.StreamReader($pipe)
                $resp = $reader.ReadLine()
                if ($resp -match '"ok"\s*:\s*true') {
                    Write-Log 'OK: 已触发 SunshineDuoCore 立即拉起 Apollo'
                    return
                }
                Write-Log "警告: SunshineDuoCore tick 响应异常: $resp"
                return
            } finally {
                $pipe.Dispose()
            }
        } catch {
            Start-Sleep -Seconds 2
        }
    }
    Write-Log '警告: 未能连接 SunshineDuoCore 控制管道（服务可能未就绪），Apollo 将在下一个 tick 周期拉起'
}

# 等待两个 Apollo 的 WebUI 端口（conf 端口 +1）开始监听，装完页/后台立即可用
function Wait-ApolloWeb {
    param([int]$PortMain, [int]$PortGame, [int]$TimeoutSec = 90)
    # 目标列表：控制台必等；游戏会话仅在 PortGame > 0 时纳入
    $targets = @(@{ n = '控制台'; v = $PortMain })
    if ($PortGame -gt 0) { $targets += @{ n = '游戏'; v = $PortGame } }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $up = @{}
    while ((Get-Date) -lt $deadline) {
        foreach ($p in $targets) {
            if ($up.ContainsKey($p.n)) { continue }
            $c = New-Object Net.Sockets.TcpClient
            try {
                if ($c.ConnectAsync('127.0.0.1', $p.v).Wait(1200) -and $c.Connected) {
                    $up[$p.n] = $true
                    Write-Log ("OK: {0} Apollo 已就绪（WebUI https://localhost:{1}）" -f $p.n, $p.v)
                }
            } catch { } finally { $c.Close() }
        }
        if ($up.Count -ge $targets.Count) { return }
        Start-Sleep -Seconds 2
    }
    foreach ($p in $targets) {
        if (-not $up.ContainsKey($p.n)) {
            Write-Log ("警告: {0} Apollo WebUI 端口 {1} 未就绪（该会话未登录或启动较慢，稍后由 SunshineDuoCore 自动拉起）" -f $p.n, $p.v)
        }
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
                'conf'     { Write-SunshineConf -AdapterName $Opts.Adapter -Port $Opts.PortMain -DisplayMode $Opts.DisplayMode -SudovdaRes $Opts.SudovdaRes; Split-LegacySunshineState }
                # note: Write-GameConf is performed by the SunshineDuoCore step (dual Apollo ports)
                'gpuaccel' { Install-GpuAcceleration }
                'firewall' { Install-FirewallRule }
                'gamepad'  { Install-Gamepad }
                'rdpwrap'  { Install-RdpWrapper }
                'SunshineDuoCore'  {
                    Stop-StreamingProcesses
                    if ($Opts.PortGame -gt 0) { Write-GameConf -ConsoleConfPath $ConfPath -GameConfPath $GameConfPath -PortGame $Opts.PortGame -DisplayMode $Opts.DisplayMode -Vw $Opts.Vw -Vh $Opts.Vh }
                    # 先写两份 state 的凭证与主机身份，再启动服务；避免守卫拉起 Apollo 后回写覆盖
                    Set-WebUICredentials -User $Opts.WebUser -Password $Opts.WebUiPassword -GameEnabled $Opts.GameEnabled -PortMain $Opts.PortMain -PortGame $Opts.PortGame
                    Ensure-DistinctUniqueIds
                    Install-SunshineDuoCore -RdpUser $Opts.User -Password $Opts.Password -Vw $Opts.Vw -Vh $Opts.Vh -AutoLogon $Opts.AutoLogon -DisplayMode $Opts.DisplayMode -DisplayExclusive ($Opts.DisplayMode -eq 'only') -PortMain $Opts.PortMain -PortGame $Opts.PortGame -SudovdaResList $Opts.SudovdaResList -GameEnabled $Opts.GameEnabled
                    # 服务就绪后立即触发 tick 拉起两个 Apollo，并等 WebUI 端口就绪
                    Send-SunshineDuoCoreTick
                    Wait-ApolloWeb -PortMain ($Opts.PortMain + 1) -PortGame $(if ($Opts.PortGame -gt 0) { $Opts.PortGame + 1 } else { 0 })
                    Install-ApolloGuard -User $Opts.User -GameEnabled $Opts.GameEnabled
                }
                'sudovda'  { Install-SudoVDA }
                'gamemgr'  { Install-GameManager }
                'tray'     { Install-TrayApp -DesktopShortcut ([bool]$Opts.TrayDesktop) -StartShortcut ([bool]$Opts.TrayStart) -AutoStart ([bool]$Opts.TrayAuto) -StartNow $true -PortMain $Opts.PortMain -PortGame $Opts.PortGame -WebUser $Opts.WebUser }
                'reset'    { Stop-StreamingProcesses; Reset-SunshineState }
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
    if ((Get-Process -Name 'sunshine' -ErrorAction SilentlyContinue) -and -not $S.Prog.Steps['SunshineDuoCore']) {
        Write-Log '提示: Sunshine 正在运行，需要重启 Sunshine 后新配置才生效'
    }
    $S.Prog.Results = @($results | ForEach-Object {
        [PSCustomObject]@{ Key = $_.Key; Label = [string]$_.Label; Ok = $_.Ok }
    })
    $S.Prog.Done = $true
}

function Invoke-UninstallRun {
    param([hashtable]$Opts)
    # 卸载一律先停服务并结束 SunshineDuoCore / Sunshine / 托盘进程，再执行反注册
    Stop-StreamingProcesses
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
                'SunshineDuoCore'  {
                    Uninstall-ApolloGuard
                    Uninstall-SunshineDuoCore
                }
                'sudovda'  { Uninstall-SudoVDA }
                'gamemgr'  { Uninstall-GameManager }
                'trayapp'  { Uninstall-TrayApp }
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
    <Style TargetType="RadioButton">
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
              <TextBlock Text="串流环境一键部署" Foreground="#94A3B8" FontSize="11" Margin="0,7,0,0"/>
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
                    <TextBlock Grid.Column="0" Text="核心服务" Foreground="#94A3B8"/>
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

          <!-- ── 配置页（五步向导） ── -->
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
              <TextBlock x:Name="lblStep2" Text="② 串流用户" FontSize="13" FontWeight="Bold"
                         Foreground="#94A3B8"/>
              <TextBlock Text="  ──  " Foreground="#475569"/>
              <TextBlock x:Name="lblStep3" Text="③ 虚拟显示器" FontSize="13" FontWeight="Bold"
                         Foreground="#94A3B8"/>
              <TextBlock Text="  ──  " Foreground="#475569"/>
              <TextBlock x:Name="lblStep4" Text="④ 端口与选项" FontSize="13" FontWeight="Bold"
                         Foreground="#94A3B8"/>
              <TextBlock Text="  ──  " Foreground="#475569"/>
              <TextBlock x:Name="lblStep5" Text="⑤ 后台账号" FontSize="13" FontWeight="Bold"
                         Foreground="#94A3B8"/>
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
                    <TextBlock x:Name="lblGpuInfo" Foreground="#94A3B8" FontSize="11"
                               Margin="0,6,0,0" TextWrapping="Wrap" Text="检测中..."/>
                  </StackPanel>
                </Border>
                <Border Style="{StaticResource Card}">
                  <StackPanel>
                    <TextBlock Text="必装组件（默认全部安装）" Foreground="#94A3B8" FontSize="11" Margin="0,0,0,6"/>
                    <CheckBox x:Name="chkConf" IsChecked="True" IsEnabled="False" Content="默认配置"/>
                    <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="按所选显卡写入 Sunshine 配置文件，含编码 / 手柄 / UPnP"/>
                    <CheckBox x:Name="chkFirewall" IsChecked="True" IsEnabled="False" Margin="0,4,0,0"
                              Content="防火墙规则"/>
                    <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,2,0,0"
                               Text="放行 Sunshine 串流端口 TCP / UDP 入站"/>
                    <CheckBox x:Name="chkRdpWrap" IsChecked="True" IsEnabled="False" Margin="0,4,0,0"
                              Content="RDP 并发解锁"/>
                    <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="同一主机同时登录主用户与串流用户，Console 会话与游戏会话互不抢占"/>
                    <CheckBox x:Name="chkWsguard" IsChecked="True" IsEnabled="False" Margin="0,4,0,0"
                              Content="串流会话守护服务"/>
                    <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="SunshineDuoCore：串流断开后维持虚拟显示输出；开机自动启动 Sunshine"/>
                  </StackPanel>
                </Border>
                <Border Style="{StaticResource Card}">
                  <StackPanel>
                    <CheckBox x:Name="chkGpuAccel" IsChecked="True" Content="会话 GPU 加速"/>
                    <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="远程会话改用物理显卡渲染，优先 AVC444 画质与硬件编码；已连接的会话需断开重连后生效"/>
                    <CheckBox x:Name="chkGamepad" IsChecked="True" Margin="0,6,0,0" Content="虚拟手柄驱动"/>
                    <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="ViGEmBus：串流手柄识别为 Xbox 360 手柄；已安装 1.17 或更新版本时自动跳过"/>
                  </StackPanel>
                </Border>
              </StackPanel>
            </ScrollViewer>

            <!-- 第 2 步 · 主用户（Console）｜游戏用户（RDP）左右两栏 -->
            <StackPanel Grid.Row="1" x:Name="grpStep2" Margin="4,0,10,0" Visibility="Collapsed">
              <TextBlock x:Name="txtStep2Title" Text="第 2 步 · 设置串流用户"
                         Foreground="#7DD3FC" FontSize="14" FontWeight="Bold" Margin="2,0,0,10"/>
              <Border Style="{StaticResource Card}" Margin="0,0,0,12">
                <StackPanel>
                  <TextBlock Text="安装模式" Foreground="#7DD3FC" FontSize="12.5"
                             FontWeight="Bold" Margin="0,0,0,8"/>
                  <RadioButton x:Name="rdoDualSession" GroupName="SessionMode" IsChecked="True"
                               Margin="0,4,0,0" Content="双会话（Console + 游戏会话，推荐）"/>
                  <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,2,0,0" TextWrapping="Wrap"
                             Text="主会话 + 游戏会话：两个 Apollo 实例，手机 / 平板可连游戏会话串流；需 RDP 并发解锁"/>
                  <RadioButton x:Name="rdoConsoleOnly" GroupName="SessionMode" Margin="0,10,0,0"
                               Content="仅主会话（Console 单机串流）"/>
                  <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,2,0,0" TextWrapping="Wrap"
                             Text="仅安装主会话：不装游戏会话与 RDP 并发解锁，适合只在本地主机串流；日后可重跑向导补装游戏会话"/>
                </StackPanel>
              </Border>
              <Grid>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width="250"/>
                  <ColumnDefinition Width="*"/>
                </Grid.ColumnDefinitions>

                <!-- 左栏 · 主用户（Console 会话，只读身份卡） -->
                <Border Grid.Column="0" Style="{StaticResource Card}" Margin="0,0,14,12" Padding="0">
                  <StackPanel>
                    <Border Height="4" CornerRadius="10,10,0,0" Background="#3B82F6"/>
                    <StackPanel Margin="18,16,18,16">
                      <TextBlock Text="主用户" Foreground="#7DD3FC" FontSize="15" FontWeight="Bold"/>
                      <TextBlock Text="Console 会话" Foreground="#94A3B8" FontSize="11" Margin="0,2,0,16"/>
                      <Border Width="64" Height="64" CornerRadius="32" Background="#16233F"
                              BorderBrush="#2A3B5F" BorderThickness="1" HorizontalAlignment="Left">
                        <TextBlock x:Name="lblConsoleAvatar" Text="?" FontSize="26" FontWeight="Bold"
                                   Foreground="#38BDF8" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                      </Border>
                      <TextBlock x:Name="txtConsoleUser" Foreground="White" FontSize="17" FontWeight="Bold"
                                 Margin="0,12,0,0" TextTrimming="CharacterEllipsis"/>
                      <Border Background="#14251A" BorderBrush="#1F5F35" BorderThickness="1" CornerRadius="11"
                              Padding="9,4" HorizontalAlignment="Left" Margin="0,12,0,0">
                        <StackPanel Orientation="Horizontal">
                          <TextBlock Text="✓" Foreground="#22C55E" FontSize="11" FontWeight="Bold"
                                     VerticalAlignment="Center"/>
                          <TextBlock Text="SYSTEM 接管 · 无需密码" Foreground="#22C55E" FontSize="11"
                                     FontWeight="Bold" Margin="5,0,0,0" VerticalAlignment="Center"/>
                        </StackPanel>
                      </Border>
                      <TextBlock Foreground="#94A3B8" FontSize="10.5" Margin="0,14,0,0" TextWrapping="Wrap"
                                 Text="Console 会话由系统服务直接接管物理显示器或虚拟屏，无需登录密码。"/>
                    </StackPanel>
                  </StackPanel>
                </Border>

                <!-- 右栏 · 游戏用户（RDP 串流，表单卡） -->
                <Border x:Name="gameUserCard" Grid.Column="1" Style="{StaticResource Card}" Margin="0,0,0,12" Padding="0">
                  <StackPanel>
                    <Border Height="4" CornerRadius="10,10,0,0" Background="#22C55E"/>
                    <StackPanel Margin="20,16,20,16">
                      <TextBlock Text="游戏用户" Foreground="#7DD3FC" FontSize="15" FontWeight="Bold"/>
                      <TextBlock Text="RDP 串流会话 · 手机 / 平板连这个会话" Foreground="#94A3B8"
                                 FontSize="11" Margin="0,2,0,16"/>
                      <TextBlock Text="用户名" Foreground="#94A3B8" FontSize="11" Margin="0,0,0,4"/>
                      <StackPanel Orientation="Horizontal">
                        <TextBox x:Name="txtStreamUser" Width="200" Height="28" FontSize="12.5"
                                 VerticalContentAlignment="Center"/>
                        <TextBlock Text="本机账户，如 Game" Foreground="#94A3B8" FontSize="11"
                                   VerticalAlignment="Center" Margin="12,0,0,0"/>
                      </StackPanel>
                      <TextBlock Text="登录密码" Foreground="#94A3B8" FontSize="11" Margin="0,14,0,4"/>
                      <StackPanel Orientation="Horizontal">
                        <PasswordBox x:Name="pwdRdp" Width="200" Height="28" VerticalContentAlignment="Center"/>
                        <Button x:Name="btnVerifyPw" Content="验证" Style="{StaticResource BtnGhost}"
                                Height="28" Width="72" Margin="8,0,0,0" Padding="0,2"/>
                        <TextBlock x:Name="lblPwState" Margin="12,0,0,0" VerticalAlignment="Center"
                                   FontSize="11" Foreground="#94A3B8"/>
                      </StackPanel>
                      <TextBlock x:Name="lblCredState" Foreground="#94A3B8" FontSize="11" Margin="0,8,0,0"/>
                      <CheckBox x:Name="chkAutoLogon" IsChecked="True" Margin="0,12,0,0"
                                Content="开机自动登录游戏用户"/>
                      <TextBlock FontSize="10.5" Foreground="#94A3B8" Margin="20,2,0,0"
                                 Text="无人值守：重启后无需登录即恢复串流"/>
                      <StackPanel Orientation="Horizontal" Margin="0,14,0,0">
                        <TextBlock Text="虚拟屏分辨率" Foreground="#94A3B8" FontSize="11"
                                   VerticalAlignment="Center" Margin="0,0,8,0"/>
                        <TextBox x:Name="txtWidth" Width="60" Height="26" Text="1600"
                                 VerticalContentAlignment="Center"/>
                        <TextBlock Text=" × " VerticalAlignment="Center" Foreground="#94A3B8"/>
                        <TextBox x:Name="txtHeight" Width="60" Height="26" Text="720"
                                 VerticalContentAlignment="Center"/>
                        <TextBlock Text="手机横屏推荐 1920×1080" Foreground="#94A3B8" FontSize="10.5"
                                   VerticalAlignment="Center" Margin="10,0,0,0"/>
                      </StackPanel>
                    </StackPanel>
                  </StackPanel>
                </Border>
              </Grid>
            </StackPanel>

            <!-- 第 3 步 · 虚拟显示器（SudoVDA）（默认隐藏，Set-WizardStep 切换） -->
            <StackPanel Grid.Row="1" x:Name="grpStep3" Margin="4,0,10,0" Visibility="Collapsed">
              <TextBlock Text="第 3 步 · 虚拟显示器（SudoVDA）"
                         Foreground="#7DD3FC" FontSize="14" FontWeight="Bold" Margin="2,0,0,10"/>
              <Grid>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width="330"/>
                  <ColumnDefinition Width="*"/>
                </Grid.ColumnDefinitions>

                <!-- 左栏 · 驱动与模式 -->
                <Border Grid.Column="0" Style="{StaticResource Card}" Margin="0,0,14,12" Padding="0">
                  <StackPanel>
                    <Border Height="4" CornerRadius="10,10,0,0" Background="#3B82F6"/>
                    <StackPanel Margin="18,16,18,16">
                      <TextBlock Text="驱动与显示方式" Foreground="#7DD3FC" FontSize="15" FontWeight="Bold"/>
                      <TextBlock Text="Console 主会话的虚拟显示器" Foreground="#94A3B8" FontSize="11" Margin="0,2,0,14"/>
                      <CheckBox x:Name="chkSudoVDA" Margin="0,0,0,4" Content="安装 SudoVDA 驱动"/>
                      <TextBlock FontSize="10.5" Foreground="#94A3B8" Margin="20,0,0,12"
                                 Text="未安装驱动时仅可选择「镜像」模式"/>
                      <RadioButton x:Name="svMirror" GroupName="SudovdaMode" IsChecked="True" Margin="0,4,0,0"
                                   Foreground="#E2E8F0" Content="镜像 · 串流物理主屏"/>
                      <TextBlock FontSize="10.5" Foreground="#94A3B8" Margin="20,0,0,0" TextWrapping="Wrap"
                                 Text="串流画面 = Console 物理显示器内容，不创建虚拟显示器"/>
                      <RadioButton x:Name="svOnly" GroupName="SudovdaMode" Margin="0,10,0,0"
                                   Content="独占 · 仅虚拟显示器"/>
                      <TextBlock FontSize="10.5" Foreground="#94A3B8" Margin="20,0,0,0" TextWrapping="Wrap"
                                 Text="只激活虚拟显示器，物理显示器全部关闭（熄屏串流）"/>
                      <RadioButton x:Name="svExtend" GroupName="SudovdaMode" Margin="0,10,0,0"
                                   Content="扩展 · 物理屏 + 虚拟屏"/>
                      <TextBlock FontSize="10.5" Foreground="#94A3B8" Margin="20,0,0,0" TextWrapping="Wrap"
                                 Text="物理显示器保留，虚拟显示器作为独立扩展桌面"/>
                      <TextBlock x:Name="svModeDesc" Text="" Foreground="#94A3B8" FontSize="11"
                                 Margin="0,14,0,0" TextWrapping="Wrap"/>
                    </StackPanel>
                  </StackPanel>
                </Border>

                <!-- 右栏 · 分辨率列表 -->
                <Border Grid.Column="1" Style="{StaticResource Card}" Margin="0,0,0,12" Padding="0">
                  <StackPanel>
                    <Border Height="4" CornerRadius="10,10,0,0" Background="#22C55E"/>
                    <StackPanel Margin="20,16,20,16">
                      <TextBlock Text="虚拟屏分辨率" Foreground="#7DD3FC" FontSize="15" FontWeight="Bold"/>
                      <TextBlock Text="格式 1920x1080@60 · 选中项为串流分辨率" Foreground="#94A3B8"
                                 FontSize="11" Margin="0,2,0,12"/>
                      <ListBox x:Name="svResList" Height="132" Background="#0C1526" BorderBrush="#2A3B5F"
                               BorderThickness="1" FontSize="12" ScrollViewer.VerticalScrollBarVisibility="Auto">
                        <ListBox.ItemContainerStyle>
                          <Style TargetType="ListBoxItem">
                            <Setter Property="Foreground" Value="#E2E8F0"/>
                            <Setter Property="Padding" Value="8,4"/>
                          </Style>
                        </ListBox.ItemContainerStyle>
                      </ListBox>
                      <StackPanel Orientation="Horizontal" Margin="0,12,0,0">
                        <TextBox x:Name="svResInput" Width="150" Height="28" VerticalContentAlignment="Center"/>
                        <Button x:Name="svResAdd" Content="添加" Style="{StaticResource BtnGhost}"
                                Height="28" Width="64" Margin="8,0,0,0" Padding="0,2"/>
                        <Button x:Name="svResDel" Content="删除选中" Style="{StaticResource BtnGhost}"
                                Height="28" Width="86" Margin="8,0,0,0" Padding="0,2"/>
                      </StackPanel>
                      <TextBlock Text="快速添加" Foreground="#94A3B8" FontSize="11" Margin="0,14,0,6"/>
                      <StackPanel Orientation="Horizontal">
                        <Button x:Name="svP169" Content="16:9" Style="{StaticResource BtnGhost}"
                                Height="28" Width="72" Padding="0,2"/>
                        <Button x:Name="svP1610" Content="16:10" Style="{StaticResource BtnGhost}"
                                Height="28" Width="72" Margin="8,0,0,0" Padding="0,2"/>
                        <Button x:Name="svP219" Content="21:9" Style="{StaticResource BtnGhost}"
                                Height="28" Width="72" Margin="8,0,0,0" Padding="0,2"/>
                        <TextBlock Text="每类补齐常用档位，已有的自动跳过" Foreground="#94A3B8" FontSize="10.5"
                                   VerticalAlignment="Center" Margin="12,0,0,0"/>
                      </StackPanel>
                    </StackPanel>
                  </StackPanel>
                </Border>
              </Grid>
            </StackPanel>

            <!-- 第 4 步 · 可选功能与端口（默认隐藏，Set-WizardStep 切换） -->
            <StackPanel Grid.Row="1" x:Name="grpStep4" Margin="4,0,10,0" Visibility="Collapsed">
              <Border Style="{StaticResource Card}">
                <StackPanel>
                  <TextBlock Text="可选功能" Foreground="#7DD3FC" FontSize="12.5"
                             FontWeight="Bold" Margin="0,0,0,8"/>
                  <CheckBox x:Name="chkGameMgr" Content="游戏菜单（GameManager），建议安装"/>
                  <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,2,0,0" TextWrapping="Wrap"
                             Text="可直接启动需要管理员权限的游戏；游戏快捷方式放入 Links 目录，串流端即可启动"/>
                  <CheckBox x:Name="chkReset" Margin="0,8,0,0" Content="解绑所有已配对设备"/>
                  <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,2,0,0" TextWrapping="Wrap"
                             Text="手机 / 平板 / 电视需重新配对"/>
                </StackPanel>
              </Border>
              <Border Style="{StaticResource Card}" Margin="0,0,0,12">
                <StackPanel>
                  <TextBlock Text="串流端口（双会话）" Foreground="#7DD3FC" FontSize="12.5"
                             FontWeight="Bold" Margin="0,0,0,8"/>
                  <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
                    <TextBlock Text="主会场（Console）端口族：" VerticalAlignment="Center"
                               Foreground="#94A3B8"/>
                    <TextBox x:Name="txtPortMain" Width="72" Height="24" Text="48100"
                             VerticalContentAlignment="Center"/>
                    <TextBlock Text=" 游戏会话（Game）端口族：" VerticalAlignment="Center"
                               Foreground="#94A3B8" Margin="8,0,0,0"/>
                    <TextBox x:Name="txtPortGame" Width="72" Height="24" Text="48200"
                             VerticalContentAlignment="Center"/>
                  </StackPanel>
                  <TextBlock Text="两个端口差值 ≥ 22（RTSP = port+21，端口族不能重叠）；默认相距 100，留空则维持默认"
                             Foreground="#94A3B8" FontSize="11" Margin="20,6,0,0"/>
                </StackPanel>
              </Border>
              <Border Style="{StaticResource Card}" Margin="0,0,0,12">
                <StackPanel>
                  <TextBlock Text="托盘应用（SunshineDuoTray）" Foreground="#7DD3FC" FontSize="12.5"
                             FontWeight="Bold" Margin="0,0,0,8"/>
                  <CheckBox x:Name="chkTrayDesktop" IsChecked="True" Content="创建桌面快捷方式"/>
                  <CheckBox x:Name="chkTrayStart" IsChecked="True" Margin="0,6,0,0" Content="创建开始菜单快捷方式"/>
                  <CheckBox x:Name="chkTrayAuto" Margin="0,6,0,0" Content="开机自启"/>
                  <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,6,0,0" TextWrapping="Wrap"
                             Text="勾选任意一项即部署托盘到 bin\SunshineDuoTray：查看双 Apollo 状态、打开后台、配对管理、重置后台密码、重启 Apollo。开机自启只对当前登录用户生效；安装后可在托盘菜单「设置」里随时修改"/>
                </StackPanel>
              </Border>
            </StackPanel>

            <!-- 第 5 步 · WebUI 后台账号（独立一页，与游戏用户分开） -->
            <StackPanel Grid.Row="1" x:Name="grpStep5" Margin="4,0,10,0" Visibility="Collapsed">
              <Border Style="{StaticResource Card}" Margin="0,0,0,12">
                <StackPanel>
                  <TextBlock Text="WebUI 后台账号" Foreground="#7DD3FC" FontSize="15" FontWeight="Bold"/>
                  <TextBlock Text="手机 / 平板浏览器登录后台，管理配对设备与串流设置"
                             Foreground="#94A3B8" FontSize="11" Margin="0,2,0,18"/>
                  <TextBlock Text="用户名" Foreground="#94A3B8" FontSize="11" Margin="0,0,0,4"/>
                  <StackPanel Orientation="Horizontal">
                    <TextBox x:Name="txtWebUser" Width="240" Height="28" FontSize="12.5"
                             VerticalContentAlignment="Center"/>
                    <TextBlock Text="与 RDP 登录用户无关" Foreground="#94A3B8" FontSize="11"
                               VerticalAlignment="Center" Margin="12,0,0,0"/>
                  </StackPanel>
                  <TextBlock Text="密码" Foreground="#94A3B8" FontSize="11" Margin="0,16,0,4"/>
                  <PasswordBox x:Name="pwdWebUi" Width="240" Height="28" HorizontalAlignment="Left"
                               VerticalContentAlignment="Center"/>
                  <TextBlock Text="确认密码" Foreground="#94A3B8" FontSize="11" Margin="0,12,0,4"/>
                  <PasswordBox x:Name="pwdWebUi2" Width="240" Height="28" HorizontalAlignment="Left"
                               VerticalContentAlignment="Center"/>
                  <TextBlock x:Name="lblWebPwState" Foreground="#94A3B8" FontSize="11" Margin="0,12,0,0"
                             TextWrapping="Wrap"/>
                  <TextBlock Foreground="#94A3B8" FontSize="10.5" Margin="0,14,0,0" TextWrapping="Wrap"
                             Text="安装完成页会显示后台地址；控制台与游戏会话共用一个账号密码"/>
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
              <RowDefinition Height="Auto"/>
              <RowDefinition Height="*"/>
            </Grid.RowDefinitions>

            <TextBlock Grid.Row="0" x:Name="lblPhaseTitle" Text="正在部署串流环境..."
                       FontSize="17" FontWeight="Bold" Foreground="White"/>
            <TextBlock Grid.Row="1" x:Name="lblCurrent" Text="" Foreground="#7DD3FC"
                       FontSize="12.5" Margin="0,6,0,0" TextTrimming="CharacterEllipsis"/>

            <StackPanel Grid.Row="2" Width="560" HorizontalAlignment="Center" Margin="0,14,0,0">
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
                <TextBlock x:Name="lblStepCount" Text="" Foreground="#94A3B8" FontSize="11"
                           HorizontalAlignment="Center"/>
              </Grid>
            </StackPanel>

            <StackPanel Grid.Row="3" x:Name="stepsHost" Width="560" HorizontalAlignment="Center"
                        Margin="0,10,0,4"/>

            <Border Grid.Row="4" Background="#D90C1526" CornerRadius="8" Margin="0,10,0,0"
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
              <Border x:Name="cardWebUi" Style="{StaticResource Card}" Margin="0,16,0,0" Padding="16,12,16,12"
                      Visibility="Collapsed">
                <StackPanel>
                  <TextBlock Text="WebUI 后台登录" Foreground="#7DD3FC" FontSize="13" FontWeight="Bold"/>
                  <TextBlock x:Name="lblWebUiInfo" Foreground="#E2E8F0" FontSize="12" Margin="0,8,0,0"
                             TextWrapping="Wrap"/>
                </StackPanel>
              </Border>
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
                    <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,2,0,0" Text="Apollo"/>
                    <CheckBox x:Name="chkUGamepad" IsChecked="True" Margin="0,4,0,0"
                              Content="卸载虚拟手柄驱动（ViGEmBus）"/>
                    <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="其他程序依赖它时请勿勾选"/>
                  </StackPanel>
                </Border>
                <Border Style="{StaticResource Card}">
                  <StackPanel>
                    <TextBlock Text="2 · RDP 并发解锁" Foreground="#94A3B8" FontSize="11" Margin="0,0,0,6"/>
                    <CheckBox x:Name="chkURdpWrap" IsChecked="True" Content="卸载 RdpWrapper"/>
                    <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="恢复系统默认的单会话限制"/>
                    <CheckBox x:Name="chkUGpuAccel" IsChecked="True" Margin="0,4,0,0"
                              Content="删除会话 GPU 加速策略"/>
                    <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="恢复远程会话默认渲染策略，三个注册表值一并移除"/>
                  </StackPanel>
                </Border>
                <Border Style="{StaticResource Card}">
                  <StackPanel>
                    <TextBlock Text="3 · 串流会话守护服务" Foreground="#94A3B8" FontSize="11" Margin="0,0,0,6"/>
                    <CheckBox x:Name="chkUWsguard" IsChecked="True"
                              Content="卸载 SunshineDuoCore 服务并取消 Sunshine 开机自启"/>
                    <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,2,0,0"
                               Text="保留密码与设置"/>
                    <CheckBox x:Name="chkUSudovda" IsChecked="False" Margin="0,4,0,0"
                              Content="卸载 SudoVDA 虚拟显示驱动"/>
                    <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="仅在安装过 SudoVDA 时勾选；卸载后屏幕可能闪烁"/>
                  </StackPanel>
                </Border>
                <Border Style="{StaticResource Card}">
                  <StackPanel>
                    <TextBlock Text="4 · 可选功能" Foreground="#94A3B8" FontSize="11" Margin="0,0,0,6"/>
                    <CheckBox x:Name="chkUGameMgr" IsChecked="True" Content="卸载游戏菜单（GameManager）"/>
                    <CheckBox x:Name="chkUTray" IsChecked="True" Margin="0,6,0,0"
                              Content="卸载 Sunshine-Duo 托盘（含快捷方式与开机自启）"/>
                    <TextBlock FontSize="11" Foreground="#94A3B8" Margin="20,2,0,0" TextWrapping="Wrap"
                               Text="删除 bin\SunshineDuoTray、快捷方式与自启项，不删除托盘配置"/>
                  </StackPanel>
                </Border>
                <TextBlock Foreground="#94A3B8" FontSize="11" Margin="2,2,2,0" TextWrapping="Wrap"
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
$UiControlNames = @('lblRoot','txtLog','cmbGpu','lblGpuInfo',
                   'chkConf','chkFirewall','chkRdpWrap','chkWsguard',
                   'chkGpuAccel','chkGamepad','chkGameMgr','chkReset','chkSudoVDA',
                   'chkTrayDesktop','chkTrayStart','chkTrayAuto',
                   'txtConsoleUser','lblConsoleAvatar','txtStreamUser','gameUserCard','pwdRdp','btnVerifyPw','lblPwState','lblCredState','chkAutoLogon',
                   'txtWebUser','pwdWebUi','pwdWebUi2','lblWebPwState',
                   'txtWidth','txtHeight','btnBack','btnNext','btnInstall',
                   'lblStep1','lblStep2','lblStep3','lblStep4','lblStep5','grpStep1','grpStep2','grpStep3','grpStep4','grpStep5','txtStep2Title',
                   'rootFrame','btnGoSetup',
                   'lblHeadTitle','btnMin','btnClose','lblSideRoot',
                   'pgW','pgS','pgR','pgF','pgU','ttW','ttS','ttR','ttF','ttU',
                   'lblWelcomeUser','lblWelcomeGpu','lblWelcomeWg','lblWelcomeCred',
                   'lblPhaseTitle','lblCurrent','lblStepCount','pgBar','sheen','sheenTt','sheenClip','stepsHost',
                   'lblFinishIcon','lblFinishTitle','lblFinishNote','resHost','btnViewLog','btnFinishOk',
                   'cardWebUi','lblWebUiInfo',
                   'chkUFirewall','chkUGamepad','chkURdpWrap','chkUGpuAccel','chkUWsguard','chkUSudovda','chkUGameMgr',
                   'chkUTray',
                   'btnUninstallBack','btnUninstallStart',
                   'svMirror','svOnly','svExtend','svModeDesc',
                   'svResList','svResInput','svResAdd','svResDel','svP169','svP1610','svP219',
                   'txtPortMain','txtPortGame','rdoDualSession','rdoConsoleOnly')
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
    Check 'bin\SunshineDuoCore\SunshineDuoCore.exe 存在'       { Test-Path $WgExe }
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
    foreach ($n in $UiControlNames) {
        Check "控件 $n" { $script:win.FindName($n) -ne $null }
    }
    Check '默认开机自动登录游戏用户' {
        $ui = @{}
        foreach ($n in $UiControlNames) { $ui[$n] = $script:win.FindName($n) }
        $ui.ContainsKey('chkAutoLogon') -and ([bool]$ui['chkAutoLogon'].IsChecked)
    }

    # DPAPI roundtrip（LocalMachine 作用域，与 SunshineDuoCore 解密方式兼容）
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
    Check 'bin\SunshineDuoCore config.json 模板' {
        $cfg = [ordered]@{
            username = 'Game'; password_mode = 'dpapi'; cred_path = $WgCredPath
            server = '127.0.0.2'; width = 1600; height = 720
            keepalive = $true; guard_uu = $true; guard_apollo = $true; display_keepalive = $true
            auto_logon = $true; log_level = 'warn'
            interval = 60; recycle_minutes = 30
            gameviewer_versions = @('4.0.*', '4.46.*')
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
foreach ($n in $UiControlNames) {
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
$S.LegacyStatePath = $LegacyStatePath
$S.ConsoleStatePath = $ConsoleStatePath
$S.GameStatePath    = $GameStatePath
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
$S.GameConfPath  = $GameConfPath
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

# ---- 五步向导 ----
$script:WizardStep = 1
function Set-WizardStep {
    param([int]$Step)
    $script:WizardStep = $Step
    foreach ($i in 1..5) {
        $ui["grpStep$i"].Visibility = $(if ($Step -eq $i) { 'Visible' } else { 'Collapsed' })
        $ui["lblStep$i"].Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString(
            $(if ($Step -eq $i) { '#3B82F6' } else { '#64748B' }))
    }
    $ui['btnBack'].Visibility    = $(if ($Step -eq 1) { 'Collapsed' } else { 'Visible' })
    $ui['btnNext'].Visibility    = $(if ($Step -eq 5) { 'Collapsed' } else { 'Visible' })
    $ui['btnInstall'].Visibility = $(if ($Step -eq 5) { 'Visible' } else { 'Collapsed' })
    # 进入后台账号页时预填用户名
    if ($Step -eq 5 -and [string]::IsNullOrEmpty($ui['txtWebUser'].Text)) {
        $ui['txtWebUser'].Text = $script:CurUser
    }
    # 第 2 步标题：单会话只留主用户，双会话带上游戏用户名
    if ($Step -eq 2) { Update-Step2Title }
}
function Update-Step2Title {
    $user = $ui['txtStreamUser'].Text.Trim()
    $ui['txtStep2Title'].Text = if ($ui['rdoConsoleOnly'].IsChecked) {
        '第 2 步 · 设置主用户（仅主会话，无需游戏账户）'
    } elseif ([string]::IsNullOrEmpty($user)) {
        '第 2 步 · 设置串流用户'
    } else {
        '第 2 步 · 设置串流用户（游戏用户：' + $user + '）'
    }
}
# SudoVDA 显示方式说明文本（Set-WizardStep 与单选切换共用）
function Update-SudovdaModeDesc {
    $desc = if ($ui['svOnly'].IsChecked) {
        '独占模式：客户端串流时（含「桌面」App）自动只保留 SudoVDA 虚拟显示器、物理显示器断开（拓扑由 SunshineDuoCore 运行期执行）'
    } elseif ($ui['svExtend'].IsChecked) {
        '扩展模式：物理显示器照常保留，虚拟显示器作为扩展桌面用于串流（桌面/游戏均走虚拟屏）'
    } else {
        '镜像模式：直接串流 Console 物理主屏画面，不创建虚拟显示器（无需 SudoVDA）'
    }
    $ui['svModeDesc'].Text = $desc
}
$svGroup = @($ui['svMirror'], $ui['svOnly'], $ui['svExtend'])
foreach ($rb in $svGroup) {
    $rb.Add_Checked({ Update-SudovdaModeDesc })
}

# ---- 安装模式切换：单会话时游戏端口输入框置灰，避免填错 ----
function Update-SessionModeDesc {
    $single = [bool]$ui['rdoConsoleOnly'].IsChecked
    $ui['gameUserCard'].Visibility = $(if ($single) { 'Collapsed' } else { 'Visible' })
    $ui['txtPortGame'].IsEnabled = -not $single
    $ui['txtPortGame'].ToolTip = $(if ($single) { '仅主会话模式不安装游戏会话，游戏端口不生效' } else { '游戏会话端口族（RTSP = 端口+21）' })
    if ($script:WizardStep -eq 2) { Update-Step2Title }
}
foreach ($rb in @($ui['rdoDualSession'], $ui['rdoConsoleOnly'])) {
    $rb.Add_Checked({ Update-SessionModeDesc })
}
Update-SessionModeDesc

# ---- 分辨率列表：1920x1080@60 格式的增删与预设 ----
function Format-ResString {
    param([string]$Raw)
    if ($Raw -match '^\s*(\d{2,5})\s*[xX×]\s*(\d{2,5})\s*(?:@\s*(\d{2,3})\s*)?$') {
        $w = [int]$Matches[1]; $h = [int]$Matches[2]
        if ($w -lt 320 -or $w -gt 7680 -or $h -lt 240 -or $h -gt 4320) { return $null }
        $s = [string]$w + 'x' + [string]$h
        if ($Matches[3]) { $s += "@$([int]$Matches[3])" }
        return $s
    }
    return $null
}
function Add-ResItem {
    param([string]$Raw)
    $norm = Format-ResString -Raw $Raw
    if (-not $norm) {
        $ui['svModeDesc'].Text = "分辨率格式无效：$Raw（应为 1920x1080@60）"
        return
    }
    foreach ($it in $ui['svResList'].Items) {
        if ([string]$it -eq $norm) { return }  # 已存在，跳过
    }
    $null = $ui['svResList'].Items.Add($norm)
    Update-SudovdaModeDesc
}
$ui['svResAdd'].Add_Click({
    $raw = $ui['svResInput'].Text.Trim()
    if ($raw) { Add-ResItem -Raw $raw; $ui['svResInput'].Text = '' }
})
$ui['svResInput'].Add_KeyDown({
    param($s, $e)
    if ($e.Key -eq 'Enter') {
        $raw = $ui['svResInput'].Text.Trim()
        if ($raw) { Add-ResItem -Raw $raw; $ui['svResInput'].Text = '' }
    }
})
$ui['svResDel'].Add_Click({
    $sel = $ui['svResList'].SelectedIndex
    if ($sel -ge 0) { $ui['svResList'].Items.RemoveAt($sel) }
})
# 预设：16:9 / 16:10 / 21:9 常用档位，已有的自动跳过
$ui['svP169'].Add_Click({
    foreach ($r in @('1280x720@60', '1920x1080@60', '2560x1440@120', '3840x2160@60')) { Add-ResItem -Raw $r }
})
$ui['svP1610'].Add_Click({
    foreach ($r in @('1920x1200@60', '2560x1600@120')) { Add-ResItem -Raw $r }
})
$ui['svP219'].Add_Click({
    foreach ($r in @('2560x1080@60', '3440x1440@100')) { Add-ResItem -Raw $r }
})
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
        $single = [bool]$ui['rdoConsoleOnly'].IsChecked
        # 单会话模式：跳过游戏用户验证，直接使用当前流式用户作为 console 用户
        if ($single) {
            $script:Phase = 'Install'
            # 直接进入步骤 3，设置端口后由后台函数处理
            Set-WizardStep ($script:WizardStep + 1)
            return
        }
        $pw = $ui['pwdRdp'].Password
        $streamUser = $ui['txtStreamUser'].Text.Trim()
        if ([string]::IsNullOrEmpty($streamUser) -or
            (-not (Get-LocalUser -Name $streamUser -ErrorAction SilentlyContinue))) {
            [void][System.Windows.MessageBox]::Show($window,
                "游戏用户 $streamUser 不是本机用户或不存在，请先创建该用户，而非回车。", '用户不合法',
                [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Warning)
            return
        }
        $expected = "$streamUser`:$pw"
        if ([string]::IsNullOrEmpty($pw)) {
            # 留空允许继续，但必须弹窗提示后果
            if (Test-Path $WgCredPath) {
                [void][System.Windows.MessageBox]::Show($window,
                    '密码已留空：将沿用已保存的 rdp.cred（游戏用户登录密码）。', '留空密码',
                    [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information)
            } else {
                [void][System.Windows.MessageBox]::Show($window,
                    '密码已留空且此前未保存过凭据：请回上一步输入密码并验证后再继续。', '留空密码',
                    [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information)
            }
        } elseif (-not $script:PwVerified -or $expected -ne $script:PwVerifiedFor) {
            [void][System.Windows.MessageBox]::Show($window,
                '密码未经过验证或验证未通过，请点「验证」后继续。', '密码验证失败',
                [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Warning)
            return
        }
    }
    if ($script:WizardStep -eq 3) {
        # 独占 / 扩展需要 SudoVDA 驱动 + 至少一条分辨率
        if ($ui['svOnly'].IsChecked -or $ui['svExtend'].IsChecked) {
            if (-not $ui['chkSudoVDA'].IsChecked) {
                [void][System.Windows.MessageBox]::Show($window,
                    '独占 / 扩展模式需要 SudoVDA 虚拟显示驱动，请勾选「安装 SudoVDA 驱动」。', '缺少驱动',
                    [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Warning)
                return
            }
            if ($ui['svResList'].Items.Count -eq 0) {
                [void][System.Windows.MessageBox]::Show($window,
                    '请至少添加一条虚拟屏分辨率（如 1920x1080@60），或改用「镜像」模式。', '缺少分辨率',
                    [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Warning)
                return
            }
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
# 主用户（Console 侧）固定显示当前登录用户，不改逻辑
$ui['txtConsoleUser'].Text = $script:CurUser
if ([string]::IsNullOrEmpty($script:CurUser)) { $ui['lblConsoleAvatar'].Text = '?' }
else { $ui['lblConsoleAvatar'].Text = $script:CurUser.Substring(0, 1).ToUpper() }
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

# 预填当前 bin\SunshineDuoCore config.json 里的虚拟屏分辨率与已保存的分辨率清单
try {
    $wg = Get-Content -LiteralPath $WgCfgPath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
    if ($wg.width)  { $ui['txtWidth'].Text  = [string][int]$wg.width }
    if ($wg.height) { $ui['txtHeight'].Text = [string][int]$wg.height }
    if ($wg.sudovda_resolutions) {
        foreach ($r in @($wg.sudovda_resolutions)) { Add-ResItem -Raw ([string]$r) }
    }
} catch { }
if (-not $curAd -and $script:GpuNames.Count -ge 1) { $ui['cmbGpu'].SelectedIndex = 0 }

# rdp.cred 状态
if (Test-Path $WgCredPath) {
    $ui['lblCredState'].Text = '已保存过游戏用户密码，留空则继续用原来的'
    $ui['lblWelcomeCred'].Text = '已有（留空沿用）'
} else {
    $ui['lblCredState'].Text = '请输入游戏用户密码并点「验证」'
    $ui['lblWelcomeCred'].Text = '无（安装时需输入）'
}
# 密码验证状态清零（后面会由验证工作回填）
$script:PwVerified = $false

# 密码验证：独立 Runspace 跑 LogonUser，UI 线程用 DispatcherTimer 收结果。
# 不能用 [Threading.Thread] 跑脚本块：PS 5.1 新线程没有 Runspace，脚本块一执行就抛未处理异常，整个进程直接退出。
$script:VerifyHandle = $null
$script:VerifyPS = $null
$script:VerifyRS = $null
$script:VerifyUser = ''
$script:VerifyPassword = ''
$script:VerifyLabel = $null
$script:VerifyBtn = $null
$script:VerifyOnDone = $null

$script:VerifyScript = {
    param($User, $Password)
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
    try {
        $ok = [PwCheck]::LogonUser($User, $domain, $Password, 3, 0, [ref]$tok)
        if ($ok) { [void][PwCheck]::CloseHandle($tok) }
        return $ok
    } catch {
        return $false
    }
}

$script:VerifyTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:VerifyTimer.Interval = [TimeSpan]::FromMilliseconds(100)
$script:VerifyTimer.Add_Tick({
    if ($null -eq $script:VerifyHandle -or -not $script:VerifyHandle.IsCompleted) { return }
    $script:VerifyTimer.Stop()
    $ps = $script:VerifyPS
    $rs = $script:VerifyRS
    $label = $script:VerifyLabel
    $btn = $script:VerifyBtn
    $done = $script:VerifyOnDone
    $user = $script:VerifyUser
    $password = $script:VerifyPassword
    $ok = $false
    try {
        $out = $ps.EndInvoke($script:VerifyHandle)
        if ($out.Count -gt 0) { $ok = [bool]$out[$out.Count - 1] }
        if ($ps.Streams.Error.Count -gt 0) { Write-Log "密码验证错误：$($ps.Streams.Error[0])" }
    } catch {
        Write-Log "密码验证异常：$($_.Exception.Message)"
    }
    try { $ps.Dispose() } catch { }
    try { $rs.Dispose() } catch { }
    $script:VerifyHandle = $null
    $script:VerifyPS = $null
    $script:VerifyRS = $null
    $script:VerifyUser = ''
    $script:VerifyPassword = ''
    $script:VerifyLabel = $null
    $script:VerifyBtn = $null
    $script:VerifyOnDone = $null

    $label.Text = if ($ok) { '密码正确' } else { '密码不对，请重新输入' }
    $label.Foreground = if ($ok) {
        [System.Windows.Media.BrushConverter]::new().ConvertFromString('#22C55E')
    } else {
        [System.Windows.Media.BrushConverter]::new().ConvertFromString('#F87171')
    }
    $script:PwVerified = $ok
    $script:PwVerifiedFor = if ($ok) { "$($user):$($password)" } else { '' }
    $btn.IsEnabled = $true
    Write-Log "密码验证状态：$($label.Text)（用户：$user）"
    if ($null -ne $done) { $done.Invoke($ok) }
})

function Start-PasswordVerification {
    param([string]$User, [string]$Password, $Label, $Btn, $OnDone)
    if ($null -ne $script:VerifyHandle) { return }
    $script:VerifyUser = $User
    $script:VerifyPassword = $Password
    $script:VerifyLabel = $Label
    $script:VerifyBtn = $Btn
    $script:VerifyOnDone = $OnDone
    $rs = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace()
    $rs.Open()
    $ps = [PowerShell]::Create()
    $ps.Runspace = $rs
    [void]$ps.AddScript($script:VerifyScript).AddArgument($User).AddArgument($Password)
    $script:VerifyRS = $rs
    $script:VerifyPS = $ps
    $script:VerifyHandle = $ps.BeginInvoke()
    $script:VerifyTimer.Start()
}

# SunshineDuoCore 现在只有单一版本：显示固定状态，不做新/旧版判断
$ui['lblWelcomeWg'].Text = '就绪（服务名 SunshineDuoCore / 动态路径）'

# 取消失落的验证状态：密码再改、用户再换即失效
$ui['pwdRdp'].Add_PasswordChanged({
    $script:PwVerified = $false
    $script:PwVerifiedFor = ''
    if ($ui['lblPwState'].Text -eq '密码正确') {
        $ui['lblPwState'].Text = '密码已改，请重新验证'
        $ui['lblPwState'].Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#F87171')
    }
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
    $ui['btnVerifyPw'].IsEnabled = $false
    $user = $ui['txtStreamUser'].Text.Trim()
    Start-PasswordVerification -User $user -Password $pw -Label $ui['lblPwState'] -Btn $ui['btnVerifyPw'] -OnDone $null
})
# 换用户即让密码失效，避免串流用户的凭据错配
$ui['txtStreamUser'].Add_TextChanged({
    $script:PwVerified = $false
    $script:PwVerifiedFor = ''
    $ui['lblPwState'].Text = '用户已变更，请重新验证'
    $ui['lblPwState'].Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#F87171')
})

# 装订单列化（操作 UI 组件的全部在 UI 线程）
function Start-InstallRun {
    param($User, $Password, [string]$Adapter, [string]$WebUser, [string]$WebUiPassword)
    if (-not (Get-LocalUser -Name $User -ErrorAction SilentlyContinue)) {
        [void][System.Windows.MessageBox]::Show($window, "本机不存在用户「$User」，请检查用户名。", '错误',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Error)
        return
    }
    if ($ui['chkReset'].IsChecked) {
        $r = [System.Windows.MessageBox]::Show($window,
            "确定解绑所有已配对设备吗？`n解绑后所有 Moonlight 客户端都需要重新配对。", '确认',
            [System.Windows.MessageBoxButton]::YesNo, [System.Windows.MessageBoxImage]::Warning)
        if ($r -ne [System.Windows.MessageBoxResult]::Yes) {
            $ui['chkReset'].IsChecked = $false
            Write-Log '用户取消：跳过状态与日志清理'
        }
    }
    # 步骤编排（gpuaccel 与 firewall 等顺序：conf → gpuaccel → firewall → gamepad → rdpwrap → SunshineDuoCore → sudovda → gamemgr → reset）
    # 单会话模式：rdoConsoleOnly 被勾选时，不安装游戏会话相关组件（rdpwrap、gpuaccel），
    # PortGame 设为 0，并在后续函数中通过 GameEnabled 控制 guard 标志。
    $single = [bool]$ui['rdoConsoleOnly'].IsChecked
    $steps = @('conf')
    if ($ui['chkReset'].IsChecked)    { $steps += 'reset' }
    if (-not $single -and $ui['chkGpuAccel'].IsChecked) { $steps += 'gpuaccel' }
    $steps += 'firewall'
    if ($ui['chkGamepad'].IsChecked)  { $steps += 'gamepad' }
    if (-not $single) { $steps += 'rdpwrap' }
    $steps += 'SunshineDuoCore'
    if ($ui['chkSudoVDA'].IsChecked) { $steps += 'sudovda' }
    if ($ui['chkGameMgr'].IsChecked)  { $steps += 'gamemgr' }
    if ($ui['chkTrayDesktop'].IsChecked -or $ui['chkTrayStart'].IsChecked -or $ui['chkTrayAuto'].IsChecked) { $steps += 'tray' }

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

    # 端口分配
    $pMain = $ui['txtPortMain'].Text.Trim()
    $pGame = $ui['txtPortGame'].Text.Trim()
    if ($pMain -eq '') { $pMain = '48100' }
    if ($pGame -eq '') { $pGame = '48200' }
    $single = [bool]$ui['rdoConsoleOnly'].IsChecked
    $portMain = 0; $portGame = 0
    if (-not [int]::TryParse($pMain, [ref]$portMain) -or $portMain -lt 1024 -or $portMain -gt 65520) {
        [void][System.Windows.MessageBox]::Show($window, '主会话端口须为 1024 至 65520 的整数。', '端口无效',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Warning)
        $ui['txtPortMain'].Focus()
        return
    }
    if (-not $single) {
        if (-not [int]::TryParse($pGame, [ref]$portGame) -or $portGame -lt 1024 -or $portGame -gt 65520) {
            [void][System.Windows.MessageBox]::Show($window, '游戏会话端口须为 1024 至 65520 的整数。', '端口无效',
                [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Warning)
            $ui['txtPortGame'].Focus()
            return
        }
        if ([math]::Abs($portGame - $portMain) -lt 20) {
            [void][System.Windows.MessageBox]::Show($window, '两个端口须相距至少 20。', '端口冲突',
                [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Warning)
            $ui['txtPortGame'].Focus()
            return
        }
    }
    $script:PortMain = $portMain
    $script:PortGame = $portGame

    $script:DisplayMode = 'mirror'
    if ($ui['svOnly'].IsChecked)        { $script:DisplayMode = 'only' }
    elseif ($ui['svExtend'].IsChecked)  { $script:DisplayMode = 'extend' }
    # 选中的虚拟屏分辨率（未选中则取第一条）
    $script:SudovdaRes = ''
    $selIdx = $ui['svResList'].SelectedIndex
    if ($selIdx -ge 0) { $script:SudovdaRes = [string]$ui['svResList'].Items[$selIdx] }
    elseif ($ui['svResList'].Items.Count -gt 0) { $script:SudovdaRes = [string]$ui['svResList'].Items[0] }
    $script:SudovdaResList = @($ui['svResList'].Items | ForEach-Object { [string]$_ })
    if ($script:DisplayMode -ne 'mirror') {
        Write-Log "显示模式: $script:DisplayMode（虚拟屏 $script:SudovdaRes）"
    }

    $labels = @{
        conf     = '写入 Sunshine 配置文件'
        gpuaccel = '会话 GPU 加速策略'
        firewall = '防火墙放行 Sunshine'
        gamepad  = 'ViGEmBus 虚拟手柄驱动'
        rdpwrap  = 'RDP Wrapper 并发解锁'
        SunshineDuoCore  = 'SunshineDuoCore 守护服务与开机自启'
        sudovda  = 'SudoVDA 虚拟显示驱动'
        gamemgr  = 'GameManager 游戏菜单'
        tray     = 'Sunshine-Duo 托盘与快捷方式'
        reset    = '解绑已配对设备'
    }
    $opts = @{
        Mode        = 'Install'
        Steps       = $steps
        Labels      = $labels
        Adapter     = $Adapter
        User        = $User
        Password    = $Password
        WebUser       = $WebUser
        WebUiPassword = $WebUiPassword
        Vw          = $script:Vw
        Vh          = $script:Vh
        AutoLogon   = [bool]$ui['chkAutoLogon'].IsChecked
        PortMain    = $script:PortMain
        PortGame    = $script:PortGame
        DisplayMode = $script:DisplayMode
        DisplayExclusive = ($script:DisplayMode -eq 'only')
        SudovdaRes     = $script:SudovdaRes
        SudovdaResList = $script:SudovdaResList
        TrayDesktop = [bool]$ui['chkTrayDesktop'].IsChecked
        TrayStart   = [bool]$ui['chkTrayStart'].IsChecked
        TrayAuto    = [bool]$ui['chkTrayAuto'].IsChecked
        GameEnabled = -not $single
    }
    $script:InstallOpts = $opts
    Show-Page r
    Start-RunBackground $opts
}

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
$script:RunLabels = @{}

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
    $script:RunLabels = $Opts.Labels
    $op = if ($Opts.Mode -eq 'Uninstall') { 'Invoke-UninstallRun' } else { 'Invoke-InstallRun' }
    $script:RunRS.SessionStateProxy.SetVariable('__Opts', $Opts)
    $script:RunPS = [System.Management.Automation.PowerShell]::Create()
    $script:RunPS.Runspace = $script:RunRS
    # AddStatement 必须留在两条语句之间：同一管道会先解析命令名，那时函数还没定义
    [void]$script:RunPS.AddScript($OpsCode)
    [void]$script:RunPS.AddStatement()
    [void]$script:RunPS.AddScript("try { $op -Opts `$__Opts } catch { `$S.Prog.LastError = `$_.Exception.Message } finally { `$S.Prog.Done = `$true }")
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
    $single = [bool]$ui['rdoConsoleOnly'].IsChecked
    $user = $(if ($single) { $script:CurUser } else { $ui['txtStreamUser'].Text.Trim() })
    if ([string]::IsNullOrEmpty($user)) {
        [void][System.Windows.MessageBox]::Show($window, '请先选择安装的串流用户（默认是当前登录用户）。', '提示',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information)
        return
    }
    # 后台账号（独立页）：用户名必填，密码必填且两次一致
    $webUser = $ui['txtWebUser'].Text.Trim()
    $webPw = $ui['pwdWebUi'].Password
    $webPw2 = $ui['pwdWebUi2'].Password
    if ([string]::IsNullOrEmpty($webUser)) {
        [void][System.Windows.MessageBox]::Show($window, '请填写后台用户名。', '后台账号',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Warning)
        $ui['txtWebUser'].Focus()
        return
    }
    if ([string]::IsNullOrEmpty($webPw)) {
        [void][System.Windows.MessageBox]::Show($window, '请填写后台密码。', '后台账号',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Warning)
        $ui['pwdWebUi'].Focus()
        return
    }
    if ($webPw -cne $webPw2) {
        $ui['lblWebPwState'].Text = '两次密码不一致'
        $ui['lblWebPwState'].Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#F87171')
        [void][System.Windows.MessageBox]::Show($window, '两次密码不一致，请重新输入。', '后台账号',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Warning)
        return
    }
    $ui['lblWebPwState'].Text = '密码一致'
    $ui['lblWebPwState'].Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#22C55E')
    # 单会话不装游戏会话，无需 RDP 密码；双会话：非空则验证 / 留空沿用现有 rdp.cred
    $pw = $(if ($single) { '' } else { $ui['pwdRdp'].Password })
    if (-not $single -and -not [string]::IsNullOrEmpty($pw)) {
        if ($script:PwVerified -and "$user`:$pw" -eq $script:PwVerifiedFor) {
            # 上一步已经明文验证过（同一轮窗口）：直接沿用，不走 LogonUser
            Write-Log "OK: 沿用上一步的密码验证结果（用户 $user）"
        } else {
            Write-Log "验证用户 $user 的登录密码..."
            $ui['lblPwState'].Text = '验证中...'
            $ui['lblPwState'].Foreground = [System.Windows.Media.Brushes]::Gray
            $ui['btnInstall'].IsEnabled = $false
            # 挂到独立 Runspace；验证结果回来后再查阅 → 继续安装
            Start-PasswordVerification -User $user -Password $pw -Label $ui['lblPwState'] -Btn $ui['btnInstall'] -OnDone {
                param($ok)
                if ($ok) { Start-InstallRun -User $user -Password $pw -Adapter $adapter -WebUser $webUser -WebUiPassword $webPw }
            }
            return
        }
    } elseif (-not $single -and -not (Test-Path $WgCredPath)) {
        [void][System.Windows.MessageBox]::Show($window, '请先输入登录密码并点「验证」，或已保存过密码可留空。', '需要密码',
            [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information)
        return
    }
    # 在 UI 线程执行：检查用户 / 解绑确认 / 整理参数 / 页面 / 启动
    Start-InstallRun -User $user -Password $pw -Adapter $adapter -WebUser $webUser -WebUiPassword $webPw
})

# ---- 卸载启动 ----
$ui['btnUninstallStart'].Add_Click({
    if ($script:Running) { return }
    $steps = @()
    if ($ui['chkUFirewall'].IsChecked) { $steps += 'firewall' }
    if ($ui['chkUGamepad'].IsChecked)  { $steps += 'gamepad' }
    if ($ui['chkURdpWrap'].IsChecked)  { $steps += 'rdpwrap' }
    if ($ui['chkUGpuAccel'].IsChecked) { $steps += 'gpuaccel' }
    if ($ui['chkUWsguard'].IsChecked)  { $steps += 'SunshineDuoCore' }
    if ($ui['chkUSudovda'].IsChecked)  { $steps += 'sudovda' }
    if ($ui['chkUGameMgr'].IsChecked)  { $steps += 'gamemgr' }
    if ($ui['chkUTray'].IsChecked)     { $steps += 'trayapp' }
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
        SunshineDuoCore  = '卸载 SunshineDuoCore 守护服务并取消自启'
        sudovda  = '卸载 SudoVDA 虚拟显示驱动'
        gamemgr  = '卸载 GameManager'
        trayapp  = '卸载托盘与快捷方式'
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
    # 后台已结束却没置 Done：Runspace 中途异常退出，也要收尾，不能一直停在 0%
    $aborted = $false
    if ($script:Running -and $script:RunHandle -and (-not $p.Done) -and $script:RunHandle.IsCompleted) {
        $aborted = $true
        $errText = @($p.LastError) + @($script:RunPS.Streams.Error | ForEach-Object { $_.ToString() })
        $errText = @($errText | Where-Object { $_ } | Select-Object -Unique)
        if ($errText.Count) { foreach ($e in $errText) { Write-Log "后台异常: $e" } }
        else { Write-Log '后台执行意外结束，未返回任何结果' }
        foreach ($k in $script:RunKeys) {
            if (@('ok', 'fail') -notcontains [string]$S.Prog.Steps[$k]) { $S.Prog.Steps[$k] = 'fail' }
        }
        $S.Prog.Results = @($script:RunKeys | ForEach-Object {
            [PSCustomObject]@{ Key = $_; Label = [string]$script:RunLabels[$_]; Ok = ([string]$S.Prog.Steps[$_] -eq 'ok') }
        })
    }
    # 完成：回到结果页
    if (($p.Done -or $aborted) -and $script:RunHandle -and (-not $script:RunFinal)) {
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
        $ui['lblFinishIcon'].Text = $(if ($aborted) { '!' } elseif ($failed -eq 0) { '✓' } else { '!' })
        $ui['lblFinishIcon'].Foreground = $conv.ConvertFromString($(if ($failed -eq 0 -and -not $aborted) { '#22C55E' } else { '#F59E0B' }))
        $ui['lblFinishTitle'].Text = $(if ($aborted) {
            if ($script:Phase -eq 'Uninstall') { '执行已中断' } else { '部署已中断' }
        } elseif ($failed -eq 0) {
            if ($script:Phase -eq 'Uninstall') { '卸载完成' } else { '部署完成' }
        } else {
            if ($script:Phase -eq 'Uninstall') { "卸载完成，$failed 项失败" } else { "部署完成，$failed 项失败" }
        })
        $ui['lblFinishNote'].Text = $(if ($aborted) {
            '后台执行异常中断，后续步骤已停止。请点「查看完整日志」查看错误详情。'
        } elseif ($failed -eq 0) {
            '全部步骤执行成功。详细过程请点「查看完整日志」查看。'
        } else {
            '部分步骤未成功，请查看日志排查。失败项不影响其他已成功项。'
        })
        # 安装模式下展示 WebUI 后台登录信息：装完即可直接进后台
        if ($script:Phase -eq 'Install' -and -not $aborted) {
            $wu = $script:InstallOpts
            $wuUser = [string]$wu.WebUser
            $wuPw = [string]$wu.WebUiPassword
            $single = $wu.GameEnabled -eq $false
            $lines = @("用户名：$wuUser")
            if (-not [string]::IsNullOrEmpty($wuPw)) { $lines += "密码：$wuPw" }
            $lines += '控制台会话后台：https://localhost:' + ([int]$wu.PortMain + 1)
            if (-not $single -and [int]$wu.PortGame -gt 0) {
                $lines += '游戏会话后台：https://localhost:' + ([int]$wu.PortGame + 1)
            }
            $suffix = if ($single) { '（仅主会话：不装游戏会话，仅本地串流）' } else { '（两处凭证相同，配对各自独立；证书不受信任时浏览器会提示，继续访问即可）' }
            $lines += $suffix
            $ui['lblWebUiInfo'].Text = ($lines -join "`n")
            $ui['cardWebUi'].Visibility = 'Visible'
        }
        Show-Page f
    }
})
$timer.Start()

# ---- 就绪提示 ----
Write-Log "Sunshine-Duo 部署工具就绪（根目录 $Root，渲染 Tier $([System.Windows.Media.RenderCapability]::Tier -shr 16)）"
$null = $window.ShowDialog()