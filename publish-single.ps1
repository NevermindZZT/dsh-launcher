$ErrorActionPreference = 'Stop'
$dotnet = 'D:\code\dsh-launcher\.tools\dotnet\dotnet.exe'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:NUGET_PACKAGES = 'D:\code\dsh-launcher\.tools\packages'
$out = 'D:\code\dsh-launcher\dist'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& $dotnet publish 'D:\code\dsh-launcher\src\DshLauncher.csproj' -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false `
    -p:DebugType=None -p:DebugSymbols=false -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# 清理 XML 文档（非运行必需）
Remove-Item "$out\*.xml" -Force -ErrorAction SilentlyContinue
$f = Get-ChildItem $out -File
Write-Host ('发布完成: ' + ($f | ForEach-Object { $_.Name }) -join ', ')
Write-Host ('单文件大小: {0:N2} MB' -f (($f | Measure-Object Length -Sum).Sum / 1MB))
