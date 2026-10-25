# inject_watch.ps1 - wsguard 内嵌 PS 守护脚本模板
# __WG_DIR__ 占位符由 wsguard.exe 运行时替换为其自身所在目录（目录可自由移动）
# 构建时以 UTF-16LE base64 编码嵌入 wsguard_final.py 的 PS_GUARD_B64
$ErrorActionPreference = 'Continue'
$wgDir = '__WG_DIR__'
$log = Join-Path $wgDir 'watch.log'
function TS { Get-Date -Format 'yyyy-MM-dd HH:mm:ss' }
# 日志轮转由 wsguard.exe 统一负责（watch-YYYYMMDD_HHMMSS.log 归档 + 保留份数），
# 此处不要再 Clear-Content，否则会和 Python 侧的轮转互相清掉对方刚写的内容。
# 日志级别与 wsguard.exe 一致：config.json log_level='info' 输出全量，
# 'warn'（默认）只记警告/错误。LogWarn/LogErr 始终写入。
$verbose = $false
$allowed = @()
try {
    $cfgFile = Join-Path $wgDir 'config.json'
    if (Test-Path -LiteralPath $cfgFile) {
        $parsed = Get-Content -LiteralPath $cfgFile -Raw | ConvertFrom-Json
        if ($parsed) {
            if ($parsed.log_level -and ($parsed.log_level -ieq 'info' -or $parsed.log_level -ieq 'debug')) { $verbose = $true }
            if ($parsed.gameviewer_versions) { $allowed = @($parsed.gameviewer_versions) }
        }
    }
} catch { }
function LogInfo([string]$s) { if ($verbose) { "$(TS) $s" | Add-Content -LiteralPath $log } }
function LogWarn([string]$s) { "$(TS) [WARN] $s" | Add-Content -LiteralPath $log }
function LogErr([string]$s)  { "$(TS) [ERROR] $s" | Add-Content -LiteralPath $log }

LogInfo 'watch tick'

$svc = Get-Process -Name 'GameViewerService' -ErrorAction SilentlyContinue
if (-not $svc) { LogWarn 'GameViewerService not running, GameViewer guard idle'; return }
LogInfo "service pid=$($svc.Id)"

# ---- version gate: only inject into known-compatible GameViewer builds ----
# allowed list lives in config.json next to the exe (gameviewer_versions,
# wildcard patterns like '4.0.*'); missing config / empty list = refuse.
# （config 在脚本开头已解析：$allowed + log_level）
$exePath = $svc.Path
if (-not $exePath) {
    try {
        $wmi = Get-CimInstance Win32_Process -Filter "ProcessId=$($svc.Id)" -ErrorAction Stop
        if ($wmi) { $exePath = $wmi.ExecutablePath }
    } catch { }
}
if (-not $exePath -or -not (Test-Path -LiteralPath $exePath)) {
    LogWarn "cannot resolve GameViewerService path (pid $($svc.Id)), skip inject"
    return
}
$ver = ''
try { $ver = (Get-Item -LiteralPath $exePath).VersionInfo.FileVersion } catch { }
if (-not $ver) { $ver = 'unknown' }
$gateOk = $false
foreach ($pat in $allowed) {
    if ($pat -and $ver -like [string]$pat) { $gateOk = $true; break }
}
if (-not $gateOk) {
    LogWarn "GameViewer version [$ver] not in allowed list [$($allowed -join ', ')], skip inject"
    return
}
LogInfo "gate: GameViewer version [$ver] allowed (list: $($allowed -join ', '))"

if (-not ('GvInj' -as [type])) {
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class GvInj {
    [DllImport("kernel32", SetLastError=true)] public static extern IntPtr OpenEvent(uint a, bool b, string n);
    [DllImport("kernel32", SetLastError=true)] public static extern IntPtr OpenProcess(uint a, bool b, int pid);
    [DllImport("kernel32", SetLastError=true)] public static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr a, UIntPtr s, uint t, uint p);
    [DllImport("kernel32", SetLastError=true)] public static extern bool WriteProcessMemory(IntPtr h, IntPtr a, byte[] b, UIntPtr s, out IntPtr w);
    [DllImport("kernel32", SetLastError=true)] public static extern IntPtr GetProcAddress(IntPtr m, string n);
    [DllImport("kernel32", SetLastError=true)] public static extern IntPtr GetModuleHandle(string n);
    [DllImport("kernel32", SetLastError=true)] public static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr sa, UIntPtr st, IntPtr start, IntPtr param, uint f, out uint tid);
    [DllImport("kernel32", SetLastError=true)] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32", SetLastError=true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32")] public static extern uint WTSGetActiveConsoleSessionId();
}
"@
}

# already injected?
$hEvt = [GvInj]::OpenEvent(0x100000, $false, 'Global\gv_hook_injected')
if ($hEvt -ne [IntPtr]::Zero) { [void][GvInj]::CloseHandle($hEvt); return }

# inject
$procId = $svc.Id
$h = [GvInj]::OpenProcess(0x1FFFFF, $false, $procId)
if ($h -eq [IntPtr]::Zero) { LogErr "open process $procId failed err=$([Runtime.InteropServices.Marshal]::GetLastWin32Error())"; return }

$dllPath = Join-Path $wgDir 'gv_hook.dll'
$pathBytes = [Text.Encoding]::ASCII.GetBytes($dllPath + [char]0)
$addr = [GvInj]::VirtualAllocEx($h, [IntPtr]::Zero, [UIntPtr][uint32]$pathBytes.Length, 0x3000, 0x40)
if ($addr -eq [IntPtr]::Zero) { LogErr 'alloc failed'; [void][GvInj]::CloseHandle($h); return }
$written = [IntPtr]::Zero
[void][GvInj]::WriteProcessMemory($h, $addr, $pathBytes, [UIntPtr][uint32]$pathBytes.Length, [ref]$written)
$k32 = [GvInj]::GetModuleHandle('kernel32.dll')
$load = [GvInj]::GetProcAddress($k32, 'LoadLibraryA')
$tid = [uint32]0
$th = [GvInj]::CreateRemoteThread($h, [IntPtr]::Zero, [UIntPtr]::Zero, $load, $addr, 0, [ref]$tid)
if ($th -eq [IntPtr]::Zero) { LogErr "thread failed err=$([Runtime.InteropServices.Marshal]::GetLastWin32Error())"; [void][GvInj]::CloseHandle($h); return }
[void][GvInj]::WaitForSingleObject($th, 15000)
[void][GvInj]::CloseHandle($th)
[void][GvInj]::CloseHandle($h)
# verify
$hEvt = [GvInj]::OpenEvent(0x100000, $false, 'Global\gv_hook_injected')
if ($hEvt -ne [IntPtr]::Zero) { [void][GvInj]::CloseHandle($hEvt); LogInfo "INJECTED into pid $procId (dll=$dllPath)" }
else { LogErr "inject into pid ${procId}: event missing (may have failed)"; return }

# self-heal: kill any GameViewerServer outside the active console session
# (service relaunches it; with the hook active it lands on the console session)
$console = [GvInj]::WTSGetActiveConsoleSessionId()
$srv = Get-CimInstance Win32_Process -Filter "Name='GameViewerServer.exe'" -ErrorAction SilentlyContinue
if ($srv -and $srv.SessionId -ne $console) {
    LogWarn "server in session $($srv.SessionId) (console=$console), killing pid $($srv.ProcessId) for relocation"
    Stop-Process -Id $srv.ProcessId -Force -ErrorAction SilentlyContinue
} elseif ($srv) {
    LogInfo "server ok in console session $($srv.SessionId) (pid $($srv.ProcessId))"
}
