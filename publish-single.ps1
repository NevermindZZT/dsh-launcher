$ErrorActionPreference = 'Stop'
$dotnet = 'D:\code\dsh-launcher\dsh-launcher\.tools\dotnet\dotnet.exe'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:NUGET_PACKAGES = 'D:\code\dsh-launcher\dsh-launcher\.tools\packages'
$out = 'D:\code\dsh-launcher\dsh-launcher\dist'
$running = Get-Process -Name "DshLauncher" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "正在关闭占用发布文件的 DshLauncher.exe..."
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}
if (Test-Path $out) {
    $removed = $false
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Remove-Item $out -Recurse -Force -ErrorAction Stop
            $removed = $true
            break
        }
        catch {
            if ($attempt -eq 5) {
                throw "无法清理发布目录，文件可能仍被其他进程占用: $out"
            }
            Start-Sleep -Milliseconds 500
        }
    }
}
& $dotnet publish 'D:\code\dsh-launcher\dsh-launcher\src\DshLauncher.csproj' -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false `
    -p:DebugType=None -p:DebugSymbols=false -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# 清理 XML 文档（非运行必需）
Remove-Item "$out\*.xml" -Force -ErrorAction SilentlyContinue
$f = Get-ChildItem $out -File
Write-Host ('发布完成: ' + ($f | ForEach-Object { $_.Name }) -join ', ')
Write-Host ('单文件大小: {0:N2} MB' -f (($f | Measure-Object Length -Sum).Sum / 1MB))
