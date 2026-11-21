# verify.ps1 - 手动验证 gv_hook 注入状态与 GameViewerServer 会话位置（调试用）
# 路径基于本脚本位置推导：src\verify.ps1 -> 上两级即 wsguard 主目录
$ErrorActionPreference = 'Continue'
$wgDir = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$log = Join-Path $wgDir 'verify.log'
Set-Content -LiteralPath $log -Value ("verify start: " + (Get-Date -Format 'HH:mm:ss') + " wgDir=$wgDir") -Encoding UTF8
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class V {
    [DllImport("kernel32", SetLastError=true)] public static extern IntPtr OpenEvent(uint a, bool b, string n);
    [DllImport("kernel32", SetLastError=true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32")] public static extern uint WTSGetActiveConsoleSessionId();
}
"@
$svc = Get-Process -Name 'GameViewerService' -ErrorAction SilentlyContinue
Add-Content -LiteralPath $log -Value ("service pid: " + $(if ($svc) { $svc.Id } else { 'NONE' })) -Encoding UTF8
$hEvt = [V]::OpenEvent(0x100000, $false, 'Global\gv_hook_injected')
Add-Content -LiteralPath $log -Value ("hook event: " + $hEvt) -Encoding UTF8
if ($hEvt -ne [IntPtr]::Zero) { [void][V]::CloseHandle($hEvt) }
$console = [V]::WTSGetActiveConsoleSessionId()
Add-Content -LiteralPath $log -Value ("console session: " + $console) -Encoding UTF8
# kill server with admin rights
& 'C:\Windows\System32\taskkill.exe' /F /IM 'GameViewerServer.exe' 2>&1 | Add-Content -LiteralPath $log -Encoding UTF8
Start-Sleep -Seconds 10
$srv = Get-CimInstance Win32_Process -Filter "Name='GameViewerServer.exe'" -ErrorAction SilentlyContinue
if ($srv) { Add-Content -LiteralPath $log -Value ("server now: pid=" + $srv.ProcessId + " session=" + $srv.SessionId + " (console=$console)") -Encoding UTF8 } else { Add-Content -LiteralPath $log -Value "server now: NOT RUNNING (cooldown?)" -Encoding UTF8 }
Add-Content -LiteralPath $log -Value ("verify done: " + (Get-Date -Format 'HH:mm:ss')) -Encoding UTF8
exit 0
