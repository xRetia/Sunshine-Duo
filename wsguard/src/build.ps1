# build.ps1 - wsguard 一键重打包（PyInstaller onedir）
# 用法（在管理员/普通 PowerShell 均可，仅打包不部署）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
#   powershell ... -File .\build.ps1 -Deploy   # 打包后自动部署到 ..\（wsguard 主目录）
param([switch]$Deploy)

$ErrorActionPreference = 'Stop'

# ---- 路径推导 ----
$SrcDir   = $PSScriptRoot                       # src 正本目录
$WgDir    = Split-Path -Parent $SrcDir          # wsguard 主目录（部署目标）
$BuildRoot = Join-Path (Split-Path -Parent $WgDir) '.wsguard-build'   # Sunshine 根目录\.wsguard-build
$Python   = 'C:\Users\dfc643\.local\share\TeleAgent\runtimes\python\python.exe'
if (-not (Test-Path $Python)) { $Python = 'python' }   # 其他机器：回退 PATH 上的 python（需 3.12 x64）

# 构建依赖库（PyInstaller 与 aardwolf 运行库），不随仓库分发：
#   1) .wsguard-build 已就绪 → 直接用；
#   2) 本机备份目录存在 → 复制（离线快）；
#   3) 都没有 → pip 安装（依赖清单见 src\requirements-build.txt / requirements-rdp.txt）
$FallbackTools = 'C:\Users\dfc643\.local\share\TeleAgent\TeleAgent的工作空间\.temp\build-tools'
$FallbackLib   = 'C:\Users\dfc643\.local\share\TeleAgent\TeleAgent的工作空间\.temp\rdp-lib'
New-Item -ItemType Directory -Path $BuildRoot -Force | Out-Null
if (-not (Test-Path (Join-Path $BuildRoot 'build-tools\PyInstaller'))) {
    if (Test-Path (Join-Path $FallbackTools 'PyInstaller')) {
        robocopy $FallbackTools (Join-Path $BuildRoot 'build-tools') /E /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw '复制 build-tools 失败' }
        Write-Host 'build-tools 已复制到构建现场'
    } else {
        Write-Host 'build-tools 缺失，pip 安装构建依赖（requirements-build.txt）...'
        & $Python -m pip install --target (Join-Path $BuildRoot 'build-tools') -r (Join-Path $SrcDir 'requirements-build.txt')
        if ($LASTEXITCODE -ne 0) { throw "pip 安装构建依赖失败（exit=$LASTEXITCODE）" }
    }
}
if (-not (Test-Path (Join-Path $BuildRoot 'rdp-lib\aardwolf'))) {
    if (Test-Path (Join-Path $FallbackLib 'aardwolf')) {
        robocopy $FallbackLib (Join-Path $BuildRoot 'rdp-lib') /E /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw '复制 rdp-lib 失败' }
        Write-Host 'rdp-lib 已复制到构建现场'
    } else {
        Write-Host 'rdp-lib 缺失，pip 安装运行库（requirements-rdp.txt）...'
        & $Python -m pip install --target (Join-Path $BuildRoot 'rdp-lib') -r (Join-Path $SrcDir 'requirements-rdp.txt')
        if ($LASTEXITCODE -ne 0) { throw "pip 安装运行库失败（exit=$LASTEXITCODE）" }
    }
}
$BuildTools = Join-Path $BuildRoot 'build-tools'
$RdpLib     = Join-Path $BuildRoot 'rdp-lib'

foreach ($f in @('wsguard.py', 'wsguard.spec', 'hook-runtime-cryptodome.py', 'inject_watch.ps1')) {
    if (-not (Test-Path (Join-Path $SrcDir $f))) { throw "源文件缺失: $SrcDir\$f" }
}

# ---- 准备构建目录 ----
New-Item -ItemType Directory -Path $BuildRoot -Force | Out-Null
$workSpec = Join-Path $BuildRoot 'wsguard.spec'
$workPy   = Join-Path $BuildRoot 'wsguard_final.py'
$workRth  = Join-Path $BuildRoot 'hook-runtime-cryptodome.py'
Copy-Item (Join-Path $SrcDir 'wsguard.spec') $workSpec -Force
Copy-Item (Join-Path $SrcDir 'hook-runtime-cryptodome.py') $workRth -Force

# ---- 生成 wsguard_final.py：注入 PS 守护脚本（UTF-16LE base64）----
$py = [IO.File]::ReadAllText((Join-Path $SrcDir 'wsguard.py'))
$ps = [IO.File]::ReadAllText((Join-Path $SrcDir 'inject_watch.ps1'))
$psB64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($ps))  # Unicode = UTF-16LE
if ($py -notmatch "__PS_B64__") { throw 'wsguard.py 中未找到 __PS_B64__ 占位符' }
$py = $py.Replace("PS_GUARD_B64 = '__PS_B64__'", "PS_GUARD_B64 = '$psB64'")
[IO.File]::WriteAllText($workPy, $py, (New-Object Text.UTF8Encoding($true)))
Write-Host "wsguard_final.py 已生成（PS 守护脚本注入 $($psB64.Length) 字符）"

# ---- gv_hook.dll（src 不保存二进制，从部署目录取；没有则提示用 gen_hook.ps1 生成）----
$dll = Join-Path $WgDir 'gv_hook.dll'
if (-not (Test-Path $dll)) { throw "缺少 gv_hook.dll（$dll）；请先用 src\gen_hook.ps1 生成" }

# ---- PyInstaller 打包 ----
$env:PYTHONPATH = "$BuildTools;$RdpLib"
Set-Location $BuildRoot
& $Python -m PyInstaller $workSpec --noconfirm --distpath (Join-Path $BuildRoot 'dist') --workpath (Join-Path $BuildRoot 'build')
if ($LASTEXITCODE -ne 0) { throw "PyInstaller 打包失败（exit=$LASTEXITCODE）" }

$dist = Join-Path $BuildRoot 'dist\wsguard_final'
if (-not (Test-Path (Join-Path $dist 'wsguard_final.exe'))) { throw '打包产物缺失' }
$exeSize = (Get-Item (Join-Path $dist 'wsguard_final.exe')).Length
$files = (Get-ChildItem $dist -Recurse -File).Count
Write-Host "打包完成: $dist （exe $exeSize 字节，共 $files 个文件）"

# ---- 部署（可选）：exe 改名 + _internal 镜像同步，不动 rdp.cred/config.json/gv_hook.dll/src ----
if ($Deploy) {
    # Stopped 状态下文件未锁定可直接覆盖（binPath 为动态路径推导，注册信息不受影响）；
    # 仅当 wsguard 进程仍在运行时才阻止部署
    if (Get-Process -Name 'wsguard' -ErrorAction SilentlyContinue) {
        throw 'wsguard.exe 正在运行（服务 Running），请先 sc stop WSGuard 再部署'
    }
    Copy-Item (Join-Path $dist 'wsguard_final.exe') (Join-Path $WgDir 'wsguard.exe') -Force
    $internal = Join-Path $WgDir '_internal'
    robocopy (Join-Path $dist '_internal') $internal /MIR /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy 同步 _internal 失败（exit=$LASTEXITCODE）" }
    Write-Host "部署完成: $WgDir\wsguard.exe（服务未注册状态方可覆盖）"
} else {
    Write-Host "产物位于 $dist；加 -Deploy 参数可自动部署到 $WgDir"
}

# ---- 版本标记（sunshine-setup.ps1 据此识别新版本）----
$buildInfo = [ordered]@{
    service_name = 'WSGuard'
    display_name = 'Game Windows Session Guard'
    style        = 'dynamic-path'
    built        = (Get-Date -Format 'yyyy-MM-ddTHH:mm:ss')
}
$infoPath = Join-Path (Split-Path -Parent $dist) 'build.info'
[IO.File]::WriteAllText($infoPath, ($buildInfo | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
if ($Deploy) {
    Copy-Item $infoPath (Join-Path $WgDir 'build.info') -Force
    Write-Host "已写入 $WgDir\build.info（版本标记）"
} else {
    Write-Host "已写入 $infoPath（版本标记；部署时随 -Deploy 写入 wsguard 目录）"
}
