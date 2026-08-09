# Build single-file executables for all platforms (framework-dependent, needs .NET 10 runtime on target).
# Usage: powershell -ExecutionPolicy Bypass -File publish.ps1
# Output goes to publish/<rid>/, e.g. publish/win-x64/sip.exe
$ErrorActionPreference = 'Stop'

$rids = @('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')

foreach ($rid in $rids) {
    Write-Host "==> Publishing $rid ..."
    # IncludeNativeLibrariesForSelfExtract=true is required: framework-dependent single-file
    # does not bundle native libraries (e.g. SQLite e_sqlite3) unless explicitly enabled.
    dotnet publish -c Release -r $rid --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugSymbols=false -o "publish/$rid"
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }
    Write-Host "    -> publish/$rid"
}

Write-Host ''
Write-Host 'Done. Single-file outputs in publish/:'
Get-ChildItem publish -Directory | ForEach-Object {
    $exe = Get-ChildItem $_.FullName -File | Where-Object { $_.Name -eq 'sip' -or $_.Name -eq 'sip.exe' }
    if ($exe) { Write-Host ("  {0,-12} {1,10:N1} MB" -f $_.Name, ($exe.Length / 1MB)) }
}
