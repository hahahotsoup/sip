# Build single-file executables for all platforms with unique names (version + arch).
# Usage: powershell -ExecutionPolicy Bypass -File publish.ps1
# Output: publish/<rid>/sip-v<version>-<rid>[.exe] + languages/ (runtime data auto-cleaned)
$ErrorActionPreference = 'Stop'

# 版本号自动读取自 sip.csproj(发布产物带版本,不硬编码)
[xml]$csproj = Get-Content (Join-Path $PSScriptRoot 'sip.csproj')
$version = $csproj.Project.PropertyGroup.Version
if (-not $version) { $version = '0.0.0' }
Write-Host "sip v$version"

$rids = @('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')

foreach ($rid in $rids) {
    Write-Host "==> Publishing $rid ..."
    $outDir = Join-Path $PSScriptRoot "publish/$rid"
    # 精确清理(保留 sip-web 等发布件):旧产物(sip*/sip.exe/sip-v*) + 运行时测试数据(readwithhotsoup*/) + 更新残留(_upd_*)
    if (Test-Path $outDir) {
        Get-ChildItem $outDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'sip*' -and $_.Name -notlike 'sip-web*' } | Remove-Item -Force
        Get-ChildItem $outDir -Directory -Filter 'readwithhotsoup*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
        Get-ChildItem $outDir -File -Filter '_upd_*' -ErrorAction SilentlyContinue | Remove-Item -Force
    }
    dotnet publish -c Release -r $rid --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugSymbols=false -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }

    # 产物唯一名: sip-v<版本>-<架构>[.exe](不再互相覆盖)
    $isWin = $rid -like 'win*'
    $src = Join-Path $outDir ($(if ($isWin) { 'sip.exe' } else { 'sip' }))
    $name = "sip-v$version-$rid" + $(if ($isWin) { '.exe' } else { '' })
    Move-Item $src (Join-Path $outDir $name) -Force
    # 保险:删除发布目录里任何残留的运行时数据(万一构建过程触发过 exe)
    Get-ChildItem $outDir -Directory -Filter 'readwithhotsoup*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
    Write-Host "    -> publish/$rid/$name ($([math]::Round((Get-Item (Join-Path $outDir $name)).Length / 1MB, 1)) MB)"
}

Write-Host ''
Write-Host 'Done. Unique-named outputs:'
Get-ChildItem (Join-Path $PSScriptRoot 'publish') -Directory | ForEach-Object {
    $f = Get-ChildItem $_.FullName -File | Where-Object { $_.Name -like 'sip-v*' }
    if ($f) { Write-Host ("  {0,-12} {1}" -f $_.Name, $f.Name) }
}