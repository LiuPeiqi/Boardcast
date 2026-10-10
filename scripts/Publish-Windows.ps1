param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent,
    [string]$OutputDirectory = '',
    [string]$Version = '0.2.8',
    [string]$SigningKeyPem = '',
    [string]$ReleaseKeyId = 'boardcast-release-1'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Require-DotNetSdk.ps1')
$releaseRoot = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot "artifacts\Boardcast-$Runtime" }
if ($SigningKeyPem -and (Test-Path -LiteralPath $releaseRoot) -and @(Get-ChildItem -LiteralPath $releaseRoot -Force).Count -gt 0) {
    throw 'Use a fresh empty directory for a signed release; do not rebuild a distributed version in place.'
}
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
# These generated files must never survive an unsigned rebuild with an obsolete signature.
foreach ($generated in @('release-trust.json','release-manifest.json','Boardcast-update.zip')) {
    $generatedPath = Join-Path $releaseRoot $generated
    if (Test-Path -LiteralPath $generatedPath) { Remove-Item -LiteralPath $generatedPath }
}
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
foreach ($project in @('Boardcast.Windows','Boardcast.Daemon','Boardcast.Cli','Boardcast.Updater')) {
    & dotnet publish (Join-Path $projectRoot "src\$project\$project.csproj") -c Release -r $Runtime --self-contained $selfContained -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $releaseRoot
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $releaseRoot -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Configure-Firewall.ps1') -Destination $releaseRoot -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $releaseRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'integrations') -Destination $releaseRoot -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $releaseRoot 'platforms\macos') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'platforms\macos\README.md') -Destination (Join-Path $releaseRoot 'platforms\macos') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'platforms\macos\DEVELOPMENT.md') -Destination (Join-Path $releaseRoot 'platforms\macos') -Force
if ($SigningKeyPem) {
    & (Join-Path $PSScriptRoot 'Sign-Release.ps1') -ReleaseDirectory $releaseRoot -Version $Version -Runtime $Runtime -SigningKeyPem $SigningKeyPem -KeyId $ReleaseKeyId
} else { Write-Host 'Development build: LAN update installation remains disabled until an offline publisher key signs the release.' }
$zipPath = "$releaseRoot.zip"
Compress-Archive -Path (Join-Path $releaseRoot '*') -DestinationPath $zipPath -Force
Get-Item -LiteralPath $zipPath | Select-Object FullName,Length
$packageHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zipPath.sha256", $packageHash + '  ' + [IO.Path]::GetFileName($zipPath) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Host "SHA256: $packageHash"

