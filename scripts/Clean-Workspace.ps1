param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string]$KeepVersion,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar)
$rootPrefix = $projectRoot + [IO.Path]::DirectorySeparatorChar
$releaseName = "Boardcast-win-x64-$KeepVersion"
$releaseRoot = Join-Path $projectRoot 'artifacts\releases'
foreach ($required in @("$releaseName.zip", "$releaseName.zip.sha256", "$releaseName.verification.json", "$releaseName.upgrade-verification.json", 'publisher-public-key.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $releaseRoot $required) -PathType Leaf)) { throw "Verified current release is required before cleanup: $required" }
}
$package = Join-Path $releaseRoot "$releaseName.zip"
$expected = ([IO.File]::ReadAllText("$package.sha256").Trim() -split '\s+')[0]
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ine $expected) { throw 'Current release checksum mismatch.' }
$releaseReport = [IO.File]::ReadAllText((Join-Path $releaseRoot "$releaseName.verification.json")) | ConvertFrom-Json
$upgradeReport = [IO.File]::ReadAllText((Join-Path $releaseRoot "$releaseName.upgrade-verification.json")) | ConvertFrom-Json
if (-not $releaseReport.success -or $releaseReport.releaseVersion -cne $KeepVersion -or $releaseReport.outerZipSha256 -ine $expected -or
    -not $upgradeReport.success -or $upgradeReport.toVersion -cne $KeepVersion -or $upgradeReport.finalState -cne 'ready') {
    throw 'Current release acceptance must succeed before deleting historical packages.'
}
$runningPaths = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('Boardcast.exe','Boardcast.Daemon.exe','Boardcast.Updater.exe','boardcast-cli.exe') } | ForEach-Object { $_.ExecutablePath } | Where-Object { $_ })
$candidates = [Collections.Generic.List[string]]::new()
foreach ($sourceRoot in @('src','tests')) {
    foreach ($generated in Get-ChildItem -LiteralPath (Join-Path $projectRoot $sourceRoot) -Directory -Recurse -Force | Where-Object { $_.Name -in @('bin','obj') } | Sort-Object { $_.FullName.Length } -Descending) {
        $candidates.Add($generated.FullName)
    }
}
$artifactsPath = Join-Path $projectRoot 'artifacts'
foreach ($entry in Get-ChildItem -LiteralPath $artifactsPath -Force) {
    if ($entry.Name -eq 'releases') { continue }
    $candidates.Add($entry.FullName)
}
foreach ($entry in Get-ChildItem -LiteralPath $releaseRoot -Force) {
    if ($entry.Name -eq $releaseName -or $entry.Name -eq 'publisher-public-key.json' -or $entry.Name -like "$releaseName.*") { continue }
    $candidates.Add($entry.FullName)
}
foreach ($path in @('platforms\macos\.build','platforms\macos\dist','platforms\macos\.swiftpm')) {
    $target = Join-Path $projectRoot $path
    if (Test-Path -LiteralPath $target) { $candidates.Add($target) }
}
function SourceHashes {
    $hashes = @{}
    foreach ($name in @('src','tests','scripts','docs','assets','integrations','platforms','.github')) {
        $path = Join-Path $projectRoot $name
        if (-not (Test-Path -LiteralPath $path)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $path -Recurse -File -Force) {
            if ($file.FullName -match '[\\/](bin|obj|\.build|dist|\.swiftpm)[\\/]') { continue }
            $hashes[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    foreach ($name in @('README.md','Directory.Build.props','Boardcast.slnx','.gitignore')) {
        $path = Join-Path $projectRoot $name
        if (Test-Path -LiteralPath $path) { $hashes[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    }
    return $hashes
}
$before = if ($Apply) { SourceHashes } else { @{} }
$removed = 0
$bytes = 0L
$preserved = [Collections.Generic.List[string]]::new()
foreach ($candidate in $candidates) {
    $target = [IO.Path]::GetFullPath($candidate).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $target.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Cleanup path escaped workspace: $target" }
    if (-not (Test-Path -LiteralPath $target)) { continue }
    $entry = Get-Item -LiteralPath $target -Force
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Cleanup target is a link: $target" }
    if ($entry.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $target -Recurse -Force) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Cleanup tree contains a link: $($child.FullName)" }
        }
    }
    if (@($runningPaths | Where-Object { $_.Equals($target, [StringComparison]::OrdinalIgnoreCase) -or $_.StartsWith($target + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
        $preserved.Add($target)
        continue
    }
    $size = if ($entry.PSIsContainer) { (Get-ChildItem -LiteralPath $target -Recurse -File -Force | Measure-Object -Property Length -Sum).Sum } else { $entry.Length }
    if ($Apply) { Remove-Item -LiteralPath $target -Recurse -Force }
    $removed++
    $bytes += [long]$size
}
if ($Apply) {
    $after = SourceHashes
    if ($before.Count -ne $after.Count) { throw 'Cleanup changed the critical file count.' }
    foreach ($path in $before.Keys) {
        if (-not $after.ContainsKey($path) -or $after[$path] -ne $before[$path]) { throw "Cleanup changed a critical file: $path" }
    }
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ine $expected) { throw 'Cleanup changed the release package.' }
}
[pscustomobject]@{ Applied = [bool]$Apply; RemovedPaths = $removed; FreedMiB = [Math]::Round($bytes / 1MB, 1); VerifiedCriticalFiles = $before.Count; PreservedRunningPaths = @($preserved.ToArray()) }
