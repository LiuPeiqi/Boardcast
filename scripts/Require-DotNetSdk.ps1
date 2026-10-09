param(
    [string]$MinimumSdkVersion = '10.0',
    [string]$RequiredTargetFramework = 'net10.0'
)
$ErrorActionPreference = 'Stop'

function Get-DotNetCommand {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw @"
未找到 .NET SDK（dotnet）。
本仓库无第三方 NuGet 包，但构建与测试需要 .NET $MinimumSdkVersion SDK（目标框架 $RequiredTargetFramework）。
安装：https://dotnet.microsoft.com/download/dotnet/10.0
安装后重新打开终端，执行：dotnet --list-sdks
"@
    }
    return $dotnet.Source
}

$dotnetPath = Get-DotNetCommand
$sdkLines = & $dotnetPath --list-sdks
if ($LASTEXITCODE -ne 0) { throw 'dotnet --list-sdks 失败。' }
$sdks = @($sdkLines | ForEach-Object {
    if ($_ -match '^(?<ver>\d+\.\d+\.\d+)') { [version]$Matches['ver'] }
})
if ($sdks.Count -eq 0) {
    throw "已安装 dotnet CLI，但未检测到 SDK。请安装 .NET $MinimumSdkVersion SDK：https://dotnet.microsoft.com/download/dotnet/10.0"
}

$min = [version]$MinimumSdkVersion
$matched = @($sdks | Where-Object { $_.Major -eq $min.Major -and $_.Minor -ge $min.Minor })
if ($matched.Count -eq 0) {
    $installed = ($sdks | ForEach-Object { $_.ToString() }) -join ', '
    throw "需要 .NET SDK $MinimumSdkVersion 或更高同主版本，当前已安装：$installed。安装：https://dotnet.microsoft.com/download/dotnet/10.0"
}

Write-Host "Build environment OK: dotnet SDK $(($matched | Sort-Object -Descending | Select-Object -First 1)) ($RequiredTargetFramework)"
