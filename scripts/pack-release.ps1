# ============================================================
# pack-release.ps1 — 打包运行时发布 ZIP：moedesk_{VERSION}_win64.zip
# 版本取自仓库根目录 .VERSION；仓库根由脚本位置向上推导。
# 排除：docs、src、README.md、AGENTS.md、.gitignore、.git 及本机生成目录；
# config 仅保留 apps.json（其余为本机生成，不入发布包）。
# 用法：powershell -ExecutionPolicy Bypass -File scripts\pack-release.ps1 [-OutputDir 目录]
# ============================================================
param(
    [string]$OutputDir = ""
)
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot

$Version = (Get-Content (Join-Path $Root '.VERSION') -Raw).Trim()
if (-not $Version) { throw '.VERSION 为空，无法确定发布版本' }
if (-not $OutputDir) { $OutputDir = $Root }
$ZipName = "moedesk_${Version}_win64.zip"
$Dest = Join-Path $OutputDir $ZipName

$ExcludeTopDirs = @('docs', 'src', '.git', '.github', '.temp', '.wsguard-build', '.qoder')
$ExcludeRootFiles = @('README.md', 'AGENTS.md', '.gitignore', '.GITPROXY', $ZipName)
# 本机生成/运行状态（服务自写，不进发布包；发布包里由安装脚本生成）
$ExcludeRel = @(
    'bin/SunshineDuoCore/config.json',
    'bin/SunshineDuoCore/display-worker.json',
    'bin/SunshineDuoCore/display-disabled.json',
    'bin/SunshineDuoCore/display-mode-request.json',
    'bin/SunshineDuoCore/display-mode-result.json',
    'bin/SunshineDuoCore/display-worker.stop',
    'bin/SunshineDuoCore/cursor-hidden.marker',
    'bin/SunshineDuoCore/easytier.out',
    'bin/SunshineDuoCore/rdp.cred',
    'bin/SunshineDuoCore/build.info',
    'bin/SunshineDuoTray/trayapp.json',
    'tools/GameManager.json',
    'tools/install-log.txt'
)

function Test-Include([string]$Rel) {
    $rel = $Rel -replace '\\', '/'
    $top = ($rel -split '/')[0]
    if ($ExcludeTopDirs -contains $top) { return $false }
    if ($ExcludeRootFiles -contains $rel) { return $false }
    if ($ExcludeRel -contains $rel) { return $false }
    if ($top -eq 'config' -and $rel -ne 'config/apps.json') { return $false }
    if ($rel -match '\.log($|\.)') { return $false }
    return $true
}

if (Test-Path -LiteralPath $Dest) { Remove-Item -LiteralPath $Dest -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($Dest, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $count = 0
    Get-ChildItem -LiteralPath $Root -Recurse -File -Force | ForEach-Object {
        $rel = $_.FullName.Substring($Root.Length).TrimStart('\', '/')
        if (-not (Test-Include $rel)) { return }
        $entryName = $rel -replace '\\', '/'
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $_.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        $count++
    }
}
finally { $zip.Dispose() }

$size = '{0:N1} MB' -f ((Get-Item -LiteralPath $Dest).Length / 1MB)
Write-Host "打包完成：$Dest（$count 个文件，$size）"
