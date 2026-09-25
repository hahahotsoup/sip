# 跑测试套件的正确姿势（把沙箱踩过的坑全部编码进去，别再各踩一遍）
#
# 为什么需要它：本沙箱里 `dotnet test` 起不来（vstest testhost 挂），只能走 harness-eng 的
# 反射运行器 `xr`；而 xr 有三个不写下来的话每次都会重踩的坑：
#   1) 必须在**测试输出目录里原地跑** —— SipInstance 用 AppContext.BaseDirectory/sip 找产品，
#      在 obj\_xr\... 下跑会让 87 个用例全报 DirectoryNotFoundException；
#   2) 要把 runtimes\win-x64\native\e_sqlite3.dll 复制到输出根 —— xr 的 deps.json 不描述
#      runtimes/ 探测，否则 SqliteConnection 静态构造抛 TypeInitializationException；
#   3) xr 不支持 IClassFixture → Web*/PrimaryDb 那几十个用例会以 "No parameterless constructor" 红，
#      那是**运行器限制，不是产品回归**（需要 http.sys 的用例在本沙箱一律不可用）。
# 另外每个实例要拷 ~731 MB 产品输出，落在默认 temp 上会压爆盘 —— 这里默认丢到大盘并**每跑一份独立目录**。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\verification\run_suite.ps1                      # 跑全量
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\verification\run_suite.ps1 -Filter CliSmoke     # 只跑匹配的
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\verification\run_suite.ps1 -CleanStale          # 先清旧实例目录（绝不动在跑的）
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\verification\run_suite.ps1 -List                # 只列出会用到的东西，不跑
param(
    [string]$Filter = "",
    [string]$Scratch = "",
    [switch]$CleanStale,
    [switch]$List,
    [int]$StaleMinutes = 30
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$testsOut = Join-Path $repo 'tests\Sip.Tests\bin\Release\net10.0'
$product = Join-Path $testsOut 'sip'

function Get-FreeGB([string]$drive) {
    try { return [math]::Round([System.IO.DriveInfo]::new($drive).AvailableFreeSpace / 1GB, 2) } catch { return -1 }
}

if (-not (Test-Path (Join-Path $product 'sip.exe'))) {
    Write-Error "产品输出不存在：$product`n  先跑：dotnet build tests\Sip.Tests\Sip.Tests.csproj -c Release --nologo"
}

# ── 1) xr 就位（优先取 obj\_xr 的构建产物）──
$xrBin = Join-Path $repo 'obj\_xr\bin\Release\net10.0'
foreach ($f in 'xr.exe', 'xr.dll', 'xr.runtimeconfig.json', 'xr.deps.json') {
    $dst = Join-Path $testsOut $f
    if (-not (Test-Path $dst) -and (Test-Path (Join-Path $xrBin $f))) { Copy-Item (Join-Path $xrBin $f) $testsOut -Force }
}
if (-not (Test-Path (Join-Path $testsOut 'xr.exe'))) {
    Write-Error "xr.exe 不存在。构建它：`n  dotnet build $xrBin\..\..\Runner.csproj -c Release   （或问 harness-eng 要）"
}

# ── 2) 原生库就位（否则 SqliteConnection 静态构造失败）──
$native = Join-Path $testsOut 'runtimes\win-x64\native\e_sqlite3.dll'
if ((Test-Path $native) -and -not (Test-Path (Join-Path $testsOut 'e_sqlite3.dll'))) {
    Copy-Item $native $testsOut -Force
    Write-Host "[fix] 复制 e_sqlite3.dll 到输出根（xr 的 deps.json 不描述 runtimes/ 探测）"
}

# ── 3) temp 根：默认落在可用空间最大的盘，且每次独立 ──
if (-not $Scratch) {
    $best = @('E:', 'D:', 'F:', 'C:') | ForEach-Object { [pscustomobject]@{ D = $_; GB = Get-FreeGB $_ } } |
            Sort-Object GB -Descending | Select-Object -First 1
    $Scratch = Join-Path ($best.D + '\') ("sip-verify\tmp-" + (Get-Date -Format 'MMdd-HHmmss'))
}
New-Item -ItemType Directory -Path $Scratch -Force | Out-Null
$env:SIP_TEST_TMP = $Scratch

# ── 4) 清理旧实例：只删 sip-* 且**不在跑**且够老；绝不删 template ──
if ($CleanStale) {
    $busy = Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessName -match '^(sip|xr|testhost)$' }
    if ($busy) {
        Write-Host "[skip] 有测试进程在跑（$($busy.ProcessName -join ','))，不清理任何目录"
    } else {
        $cut = (Get-Date).AddMinutes(-$StaleMinutes)
        foreach ($root in @($Scratch, (Join-Path $testsOut 'test-tmp'))) {
            if (-not (Test-Path $root)) { continue }
            Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'sip-*' -and $_.LastWriteTime -lt $cut } |
                ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue; Write-Host "[clean] $($_.FullName)" }
        }
    }
}

Write-Host "[env] SIP_TEST_TMP = $Scratch   ($(Get-FreeGB ([System.IO.Path]::GetPathRoot($Scratch))) GB free)"
Write-Host "[env] testsOut      = $testsOut"
if ($List) { return }

# ── 5) 原地跑 xr ──
$log = Join-Path $env:TEMP ("xr-run-" + (Get-Date -Format 'HHmmss') + ".txt")
Push-Location $testsOut
try {
    & (Join-Path $testsOut 'xr.exe') $testsOut $Filter 2>&1 | Tee-Object -FilePath $log | Out-Null
    $code = $LASTEXITCODE
}
finally { Pop-Location }

$lines = Get-Content $log
$summary = ($lines | Select-String '^== ').Line
Write-Host ""
Write-Host "── 汇总 ──"
if ($summary) { Write-Host $summary } else { Write-Host "!! 没有汇总行：xr 的 stdout 偶发截断，重跑一次（这不是产品红）" }
$fixtureFails = ($lines | Select-String 'No parameterless constructor').Count
$httpFails = ($lines | Select-String 'sip --start 重试 3 次仍未就绪|HttpListener').Count
Write-Host "其中 因 xr 不支持 IClassFixture 而红：$fixtureFails"
Write-Host "其中 因本环境起不了 HttpListener 而红：$httpFails  ← 环境，不算产品回归"
Write-Host "原始输出：$log"
exit $code
