#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$OldReleaseDirectory,
    [Parameter(Mandatory)][string]$NewReleaseDirectory,
    [string]$ExpectedOldVersion = '',
    [string]$ExpectedNewVersion = '',
    [string]$ReportPath = '',
    [string]$WorkDirectory = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $projectRoot 'tests\Boardcast.UpgradeValidation'
$newRoot = [IO.Path]::GetFullPath($NewReleaseDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$oldRoot = [IO.Path]::GetFullPath($OldReleaseDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$inputHashes = @{}
foreach ($root in @($oldRoot,$newRoot)) {
    foreach ($file in @('Boardcast.Core.dll','release-trust.json','release-manifest.json','Boardcast-update.zip')) {
        $path = Join-Path $root $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required release file missing: $path" }
        $inputHashes[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
}
function Get-SignedVersion([string]$Root) {
    $envelope = Get-Content -LiteralPath (Join-Path $Root 'release-manifest.json') -Raw | ConvertFrom-Json
    return ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.payload)) | ConvertFrom-Json).version
}
$oldSignedVersion = Get-SignedVersion $oldRoot
$newSignedVersion = Get-SignedVersion $newRoot
if (-not $ExpectedOldVersion) { $ExpectedOldVersion = $oldSignedVersion }
if (-not $ExpectedNewVersion) { $ExpectedNewVersion = $newSignedVersion }
if ($ExpectedOldVersion -cne $oldSignedVersion -or $ExpectedNewVersion -cne $newSignedVersion) { throw 'The selected release manifests do not have the requested versions.' }
if (-not $WorkDirectory) { $WorkDirectory = Join-Path $projectRoot ('artifacts\upgrade-validation\' + $ExpectedOldVersion + '-to-' + $ExpectedNewVersion + '-' + [Guid]::NewGuid().ToString('N')) }
$runRoot = [IO.Path]::GetFullPath($WorkDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (Test-Path -LiteralPath $runRoot) { throw 'Use a new isolated run directory; refusing to overwrite acceptance inputs.' }
foreach ($root in @($oldRoot,$newRoot)) {
    if ($runRoot.Equals($root,[StringComparison]::OrdinalIgnoreCase) -or $runRoot.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Keep runtime acceptance data outside both signed releases.' }
}
if (-not $ReportPath) { $ReportPath = Join-Path $projectRoot ("artifacts\releases\Boardcast-win-x64-$ExpectedNewVersion.upgrade-verification.json") }
$reportFullPath = [IO.Path]::GetFullPath($ReportPath)
foreach ($name in @('client-data','server-data','client-identity','server-identity')) {
    $temporaryRoot = Join-Path $runRoot $name
    if ($reportFullPath.Equals($temporaryRoot,[StringComparison]::OrdinalIgnoreCase) -or $reportFullPath.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Keep the final report outside temporary identities and caches that acceptance cleans up.' }
}
foreach ($root in @($oldRoot,$newRoot)) {
    if ($reportFullPath.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Save the acceptance report outside both signed releases.' }
}
& dotnet build (Join-Path $testRoot 'Client\Boardcast.UpgradeValidation.Client.csproj') -c Release --nologo "-p:ReleaseCorePath=$oldRoot\Boardcast.Core.dll"
if ($LASTEXITCODE -ne 0) { throw 'Old-client acceptance build failed.' }
& dotnet build (Join-Path $testRoot 'Server\Boardcast.UpgradeValidation.Server.csproj') -c Release --nologo "-p:ReleaseCorePath=$newRoot\Boardcast.Core.dll"
if ($LASTEXITCODE -ne 0) { throw 'New-server acceptance build failed.' }
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$serverDll = Join-Path $testRoot 'Server\bin\Release\net10.0\Boardcast.UpgradeValidation.Server.dll'
$clientDll = Join-Path $testRoot 'Client\bin\Release\net10.0\Boardcast.UpgradeValidation.Client.dll'
$serverReport = Join-Path $runRoot 'server.json'
$clientReport = Join-Path $runRoot 'client.json'
$server = $null
$start = [Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in @($serverDll,'server',$newRoot,$oldRoot,$runRoot,$ExpectedNewVersion,$serverReport)) { $start.ArgumentList.Add($argument) }
try {
    $server = [Diagnostics.Process]::Start($start)
    if ($null -eq $server) { throw 'Could not start the isolated update server.' }
    $serverOutput = $server.StandardOutput.ReadToEndAsync()
    $serverError = $server.StandardError.ReadToEndAsync()
    $readyDeadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath (Join-Path $runRoot 'endpoint.json'))) {
        if ($server.HasExited) { throw "Update server exited with code $($server.ExitCode)." }
        if ([DateTime]::UtcNow -gt $readyDeadline) { throw 'Update server readiness timed out.' }
        Start-Sleep -Milliseconds 100
    }
    & dotnet $clientDll client $oldRoot $newRoot $runRoot $ExpectedOldVersion $clientReport
    if ($LASTEXITCODE -ne 0) { throw 'Old-client upgrade acceptance failed.' }
}
finally {
    if ($null -ne $server) {
        [IO.File]::WriteAllText((Join-Path $runRoot 'stop'),'stop')
        if (-not $server.WaitForExit(10000)) { $server.Kill($true); $server.WaitForExit() }
        if ($null -ne $serverOutput) { [IO.File]::WriteAllText((Join-Path $runRoot 'server.stdout.log'),$serverOutput.GetAwaiter().GetResult()) }
        if ($null -ne $serverError) { [IO.File]::WriteAllText((Join-Path $runRoot 'server.stderr.log'),$serverError.GetAwaiter().GetResult()) }
        $server.Dispose()
    }
}
$clientResult = Get-Content -LiteralPath $clientReport -Raw | ConvertFrom-Json
$serverResult = Get-Content -LiteralPath $serverReport -Raw | ConvertFrom-Json
if (-not $clientResult.success -or -not $serverResult.success -or $clientResult.error -or $serverResult.error) { throw 'An upgrade acceptance report did not succeed.' }
foreach ($path in $inputHashes.Keys) {
    if ($inputHashes[$path] -ne (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash) { throw "A signed release input changed during acceptance: $path" }
}
$summary = [ordered]@{
    success = $true
    fromVersion = $clientResult.appVersion
    toVersion = $clientResult.updateVersion
    finalState = $clientResult.finalState
    packageBytes = $clientResult.packageBytes
    packageSha256 = $clientResult.packageSha256
    bytesReceived = $clientResult.bytesReceived
    reusedBytes = $clientResult.reusedBytes
    keyId = $clientResult.keyId
    publisherPublicKeySha256 = $clientResult.publicKeySha256
    oldCoreSha256 = $clientResult.loadedCoreSha256
    newCoreSha256 = $serverResult.loadedCoreSha256
    releaseInputsUnchanged = $true
    clientChecks = $clientResult.checks
    serverChecks = $serverResult.checks
    boundaries = @('Real loopback mTLS full download and ready-state recovery; no updater installation or user program replacement.','No physical two-device, firewall traversal, or clean-machine test.')
}
New-Item -ItemType Directory -Path (Split-Path -Parent $reportFullPath) -Force | Out-Null
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportFullPath -Encoding utf8
# These identities and caches belong solely to this isolated acceptance run.
foreach ($name in @('client-data','server-data','client-identity','server-identity')) {
    $cleanup = [IO.Path]::GetFullPath((Join-Path $runRoot $name))
    if (-not $cleanup.StartsWith($runRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup escaped the isolated run root.' }
    if (Test-Path -LiteralPath $cleanup) {
        $items = @((Get-Item -LiteralPath $cleanup -Force)) + @(Get-ChildItem -LiteralPath $cleanup -Recurse -Force)
        if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'A link exists in generated acceptance data; refusing recursive cleanup.' }
        Remove-Item -LiteralPath $cleanup -Recurse -Force
    }
}
Write-Host "Upgrade acceptance passed: $ExpectedOldVersion -> $ExpectedNewVersion / $($summary.bytesReceived) bytes / $($summary.finalState)."
Write-Host "Report: $reportFullPath"
