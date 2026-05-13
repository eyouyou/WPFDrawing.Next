# §D2.5 嵌入 Python 运行时一键安装
#
# 用法(本脚本兼容 Windows PowerShell 5.1 + PowerShell 7+):
#   powershell scripts/setup-python.ps1            # 标准:下载 + 解压 + 改 _pth + 装 pip/numpy
#   pwsh       scripts/setup-python.ps1            # PS7+ 也行
#   ... -Force                                     # 强制重装(先删 Python312/)
#
# 由 MSBuild target "BootstrapEmbeddedPython"(build/EmbeddedPython.targets)
# 在首次 build 时自动调用 —— Python312/.hevo_setup_complete sentinel 不存在则跑这个,
# ~30s 一次性开销;已存在则 skip,日常 build 不触发。
#
# 编码:本文件保存为 UTF-8 with BOM,Windows PowerShell 5.1 才会按 UTF-8 解析中文/特殊字符。
# Python312/ 在 .gitignore 里,不入版本控制。
#
# ── "skip if exists" 的健壮性约定 ─────────────────────────────────────────
# 之前用 `python.exe exists` 当跳过条件,**不够鲁棒** —— 如果上次跑到一半挂了
# (解压完了 → 写 _pth / pip / numpy 任一步爆),python.exe 已落地但实际不能用:
#   • _pth 还是 zip 自带的、可能带 BOM
#   • numpy 没装,业务 .py 一 import 就 ModuleNotFoundError
# 下次 build 看 python.exe 在,直接跳,坏 Python 被默默部署到 bin/,demo 闪退。
#
# 现在的约定:所有"会失败"的步骤都跑完再 touch 一个 sentinel 文件
# `Python312/.hevo_setup_complete`。skip 判定看这个文件,不看 python.exe。
# 健壮性检查(BOM / numpy importable)兜底:即便 sentinel 在,也复核一遍;
# 任一失败 → 自动 -Force 重装,不让坏环境蒙混过关。
[CmdletBinding()]
param(
    # ⚠️ PowerShell 坑:param 默认值是在**调用方 scope**求值的,不是脚本 scope。
    # 所以 $PSScriptRoot 在 param 这一刻是调用方的(终端里就是空字符串),
    # `(Resolve-Path "$PSScriptRoot\..").Path` 会变成 `\..` 相对调用方 PWD,
    # MSBuild Exec 调用时 PWD 不是仓库根 → $RepoRoot 解析到 `C:\` 这种奇怪位置,
    # 整个 setup 跑去操作 `C:\Python312\`(命中系统装的另一份 Python),整个崩盘。
    # 把默认值留空,脚本 body 里用真实的 $PSScriptRoot 计算。
    [string]$RepoRoot = "",
    [string]$PythonVersion = "3.12.10",
    [switch]$Force,
    [string]$PipIndexUrl = "",   # 可选:加速,例 "https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple"
    [string[]]$Packages = @("numpy")
)

$ErrorActionPreference = "Stop"

# 修复 param 默认值 scope 坑:在脚本 body 里求值,这里 $PSScriptRoot 才是这个 .ps1 自己的目录。
if ([string]::IsNullOrEmpty($RepoRoot)) {
    $RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
}

$pythonDir   = Join-Path $RepoRoot "Python312"
$pythonExe   = Join-Path $pythonDir "python.exe"
$pthPath     = Join-Path $pythonDir "python312._pth"
$sentinel    = Join-Path $pythonDir ".hevo_setup_complete"
$zipName     = "python-$PythonVersion-embed-amd64.zip"
$zipUrl      = "https://www.python.org/ftp/python/$PythonVersion/$zipName"
$zipPath     = Join-Path $env:TEMP $zipName

# ── PowerShell -File 的 exit code 传播坑 ─────────────────────────────
# Invoke-WebRequest / Expand-Archive 这类 cmdlet 抛终止错误,即便 $ErrorActionPreference = "Stop"
# 也常常让脚本 exit 0(MS 文档不一致,实测 5.1 / 7 都见过)。下游 MSBuild Exec 拿到 exit=0
# 会以为"成功",继续往下走,build 后期才在 sentinel 缺失检查里炸,排查路径长一截。
# 显式 trap:任何未捕获的异常 → 打日志 + exit 1。脚本主体不用动。
trap {
    Write-Host "[setup-python] X 失败: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
    exit 1
}

# ── 健壮性检查:已部署的 Python 是不是真能用 ─────────────────────────
# 返回 $null = OK,返回字符串 = 失败原因(供日志 + 触发自动重装)。
function Test-EmbeddedPythonHealth {
    if (-not (Test-Path $pythonExe)) { return "python.exe 缺失" }
    if (-not (Test-Path $pthPath))   { return "python312._pth 缺失" }

    # _pth 带 BOM 会让 init_fs_encoding fatal,必须 utf-8 无 BOM。
    try {
        $bytes = [System.IO.File]::ReadAllBytes($pthPath)
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            return "python312._pth 带 BOM (会触发 init_fs_encoding fatal error)"
        }
    } catch { return "读 python312._pth 失败:$_" }

    # numpy 能不能 import —— 业务 .py 必需。直接 spawn 进程一次,几十毫秒。
    try {
        $out = & $pythonExe -c "import numpy; print(numpy.__version__)" 2>&1
        if ($LASTEXITCODE -ne 0) { return "python -c 'import numpy' 失败:$out" }
    } catch { return "调 python.exe 失败:$_" }

    return $null
}

# ── 并行 build 串行化:全局命名 Mutex ─────────────────────────────────
# `dotnet build` 并行多项目时,凡是引 Hevo.Charting.PythonNet 的项目(LowCodeDemo / Tests /
# Benchmarks / Mcp / PythonNet 自身 = 5 个)都同时跑 BootstrapEmbeddedPython,
# 每个 node 启动一份 setup-python.ps1。没锁的话竞态:
#   - node A: 健康检查发现 _pth 短暂腐败,删整个 Python312/
#   - node B: 同时 Test-Path sentinel → 看到 A 还没删完 → 误判跳过
#   - 后续 MSBuild Error: Exists(sentinel) = false → 报"半残"
# 用 System.Threading.Mutex(Global\) 跨进程同步,第一个拿锁的 node 干活,
# 其它 node 排队,锁内**重新**做健康检查 + sentinel 判断(可能上一个 node 刚干完)。
$mutexName = "Global\HevoEmbeddedPythonSetup"
$mutex = New-Object System.Threading.Mutex($false, $mutexName)
$mutexAcquired = $false
try {
    # WaitOne 最多 5 分钟:首次 setup ~30s + 缓冲。AbandonedMutexException 在上一个持锁
    # 进程被异常 kill 时抛(典型:用户 Ctrl+C 中断 build)—— 此时上次 setup 状态未知,
    # 直接清掉 Python312/ 重来最稳妥。
    try {
        $mutexAcquired = $mutex.WaitOne([TimeSpan]::FromMinutes(5))
    } catch [System.Threading.AbandonedMutexException] {
        Write-Host "[setup-python] ! 上一个持锁进程异常退出,清理后重装" -ForegroundColor Yellow
        $mutexAcquired = $true
        if (Test-Path $pythonDir) { Remove-Item -Recurse -Force $pythonDir }
    }
    if (-not $mutexAcquired) {
        throw "等其他 setup-python 实例释放锁超时(>5min)。重试或检查是否有卡死进程。"
    }

# ── 0. -Force 直接清干净 ─────────────────────────────────────────────
if ($Force -and (Test-Path $pythonDir)) {
    Write-Host "[setup-python] -Force: 删除现有 $pythonDir" -ForegroundColor Yellow
    Remove-Item -Recurse -Force $pythonDir
}

# ── 0.5 已部署 + 健康:skip ──────────────────────────────────────────
# 判定优先级:
#   1. python.exe 不存在 → 走完整 setup
#   2. python.exe 存在 + 健康检查通过 → skip(顺便补 sentinel,迁移友好:
#      老版本 setup 或手动 robocopy 部署的 Python 也认。)
#   3. python.exe 存在 + 健康检查失败 → 清掉重装(_pth BOM / numpy 缺失等腐败)
#
# 设计取舍:不"看 sentinel 缺失就强制重装" —— 那种保守策略会在迁移期(老脚本部署的 Python
# 没写 sentinel)把好好的环境清掉,触发不必要的下载(我们最怕的就是下载挂)。
# 让健康检查做权威判断:能用就用,不能用才重装。
if (Test-Path $pythonExe) {
    $unhealthy = Test-EmbeddedPythonHealth
    if (-not $unhealthy) {
        if (-not (Test-Path $sentinel)) {
            # 健康但无 sentinel:老脚本部署 / 手动 robocopy 部署的 Python。补写 sentinel。
            $sentinelContent = "setup verified at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')`nPython $PythonVersion (preexisting healthy install, sentinel back-filled)`n"
            $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
            [System.IO.File]::WriteAllText($sentinel, $sentinelContent, $utf8NoBom)
            Write-Host "[setup-python] OK Python312/ 已部署且健康(补写 sentinel),跳过" -ForegroundColor Green
        } else {
            Write-Host "[setup-python] OK Python312/ 已部署且健康,跳过(用 -Force 强制重装)" -ForegroundColor Green
        }
        exit 0
    }
    Write-Host "[setup-python] X 现有 Python312/ 不健康:$unhealthy" -ForegroundColor Yellow
    Write-Host "[setup-python]   清理重装..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $pythonDir
}

# ── 1. 下载 zip(若已缓存则复用)──────────────────────────────────────
if (-not (Test-Path $zipPath)) {
    Write-Host "[setup-python] 下载 $zipUrl ..." -ForegroundColor Cyan
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing
} else {
    Write-Host "[setup-python] OK 复用缓存 zip $zipPath" -ForegroundColor Green
}

# ── 2. 解压到 Python312/ ─────────────────────────────────────────────
Write-Host "[setup-python] 解压到 $pythonDir ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $pythonDir | Out-Null
Expand-Archive -Path $zipPath -DestinationPath $pythonDir -Force

# ── 3. 改 _pth ───────────────────────────────────────────────────────
# 取消 import site 注释,加 site-packages 路径(让 pip 装的包能被找到)。
#
# X Python embedded 启动器解析 _pth 时**不接受 BOM**,带 BOM 触发
#   Fatal Python error: init_fs_encoding: failed to get the Python codec of the filesystem encoding
# Windows PowerShell 5.1 的 `Set-Content -Encoding UTF8` 默认**带 BOM**,
# PowerShell 7+ 的 `-Encoding UTF8` 默认无 BOM(差异点!)。
# 显式走 .NET `UTF8Encoding($false)` 写入 → 跨 5.1 / 7 一致无 BOM。
$pthContent = @"
python312.zip
.
Lib\site-packages

# Uncomment to run site.main() automatically
import site
"@
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($pthPath, $pthContent, $utf8NoBom)
Write-Host "[setup-python] OK python312._pth (utf-8 no BOM)" -ForegroundColor Green

# ── 4. 装 pip ────────────────────────────────────────────────────────
Write-Host "[setup-python] 安装 pip ..." -ForegroundColor Cyan
$getPipPath = Join-Path $pythonDir "get-pip.py"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri "https://bootstrap.pypa.io/get-pip.py" -OutFile $getPipPath -UseBasicParsing
& $pythonExe $getPipPath --no-warn-script-location | Out-Host
Remove-Item $getPipPath

# ── 5. 装 numpy(及业务额外包)─────────────────────────────────────
foreach ($pkg in $Packages) {
    Write-Host "[setup-python] 安装 $pkg ..." -ForegroundColor Cyan
    $pipArgs = @("-m", "pip", "install", "--no-warn-script-location", $pkg)
    if ($PipIndexUrl) { $pipArgs += @("-i", $PipIndexUrl) }
    & $pythonExe @pipArgs | Out-Host
}

# ── 6. 健康检查 ──────────────────────────────────────────────────────
Write-Host "[setup-python] 验证..." -ForegroundColor Cyan
$unhealthy = Test-EmbeddedPythonHealth
if ($unhealthy) {
    throw "[setup-python] X 验证失败:$unhealthy。Python312/ 处于半残状态,下次 build 会自动重装。"
}
$check = & $pythonExe -c "import numpy; print('numpy', numpy.__version__)"
Write-Host "[setup-python] OK $check" -ForegroundColor Green

# ── 7. 写 sentinel(必须最后一步)+ 清缓存 ─────────────────────────
# 上面任何步骤抛 ErrorActionPreference=Stop 都会让脚本退出,sentinel 不会被写,
# 下次 build 检测不到 → 自动重装,避免半残环境扩散。
$sentinelContent = "setup completed at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')`nPython $PythonVersion`n"
[System.IO.File]::WriteAllText($sentinel, $sentinelContent, $utf8NoBom)

if (Test-Path $zipPath) { Remove-Item $zipPath }

Write-Host "[setup-python] 完成 - Python312/ 已部署 + sentinel 已写" -ForegroundColor Green

}
finally {
    if ($mutexAcquired) {
        try { $mutex.ReleaseMutex() } catch { }
    }
    $mutex.Dispose()
}
