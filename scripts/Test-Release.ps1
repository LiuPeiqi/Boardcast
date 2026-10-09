#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ReleaseZip,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [string]$PublicKeyRecord = '',
    [string]$ReportPath = '',
    [string]$WorkDirectory = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$zipPath = [IO.Path]::GetFullPath($ReleaseZip)
if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) { throw 'Release ZIP does not exist.' }
if (-not $PublicKeyRecord) { $PublicKeyRecord = Join-Path $projectRoot 'artifacts\releases\publisher-public-key.json' }
$PublicKeyRecord = [IO.Path]::GetFullPath($PublicKeyRecord)
$publisherRecord = Get-Content -LiteralPath $PublicKeyRecord -Raw | ConvertFrom-Json
$checksumPath = "$zipPath.sha256"
$checksumParts = (Get-Content -LiteralPath $checksumPath -Raw).Trim() -split '\s+',2
if ($checksumParts.Count -ne 2 -or $checksumParts[0] -notmatch '^[0-9a-fA-F]{64}$' -or $checksumParts[1].TrimStart('*') -cne [IO.Path]::GetFileName($zipPath)) { throw 'External ZIP checksum file is invalid.' }
$outerHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($outerHash -ne $checksumParts[0]) { throw 'External ZIP SHA-256 does not match its checksum file.' }
if (-not $WorkDirectory) { $WorkDirectory = Join-Path $projectRoot ('artifacts\release-acceptance\' + [IO.Path]::GetFileNameWithoutExtension($zipPath) + '-' + [Guid]::NewGuid().ToString('N')) }
$releaseRoot = [IO.Path]::GetFullPath($WorkDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (Test-Path -LiteralPath $releaseRoot) { throw 'Use a new extraction directory; refusing to overwrite acceptance inputs.' }
if (-not $ReportPath) { $ReportPath = [IO.Path]::ChangeExtension($zipPath, '.verification.json') }
$reportFullPath = [IO.Path]::GetFullPath($ReportPath)
if ($reportFullPath.StartsWith($releaseRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Save the acceptance report outside the signed release.' }
$inputHashes = @{}
foreach ($inputPath in @($zipPath,$checksumPath,$PublicKeyRecord)) {
    if ($reportFullPath.Equals($inputPath,[StringComparison]::OrdinalIgnoreCase)) { throw 'The report path must not overwrite a release ZIP, checksum or publisher public key record.' }
    $inputHashes[$inputPath] = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash
}
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        $relative = $entry.FullName.Replace('\','/').TrimEnd('/')
        if (-not $relative -or $relative.StartsWith('/') -or $relative.Contains(':') -or @($relative.Split('/') | Where-Object { $_ -in @('','.', '..') }).Count) { throw "Unsafe ZIP entry: $($entry.FullName)" }
        if (-not $names.Add($relative)) { throw "Duplicate ZIP entry: $relative" }
        if (($entry.ExternalAttributes -shr 16 -band 0xF000) -eq 0xA000 -or ($entry.ExternalAttributes -band 0x400) -ne 0) { throw "ZIP link is not allowed: $relative" }
        if ($relative -match '(^|/)(bin|obj|artifacts|\.git)(/|$)|\.(log|tmp|bak|old|pfx|p12)$|(^|/)[^/]*private[^/]*\.pem$') { throw "Private or temporary file in ZIP: $relative" }
    }
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath,$releaseRoot)
} finally { $archive.Dispose() }
foreach ($required in @('Boardcast.exe','Boardcast.Daemon.exe','Boardcast.Updater.exe','boardcast-cli.exe','Boardcast.Core.dll','release-trust.json','release-manifest.json','Boardcast-update.zip','coreclr.dll','clrjit.dll','hostfxr.dll','hostpolicy.dll','PresentationCore.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $releaseRoot $required) -PathType Leaf)) { throw "Required self-contained release file missing: $required" }
}
foreach ($binary in @('Boardcast.exe','Boardcast.Daemon.exe','Boardcast.Updater.exe','boardcast-cli.exe','Boardcast.Core.dll')) {
    $version = (Get-Item -LiteralPath (Join-Path $releaseRoot $binary)).VersionInfo.ProductVersion
    if (-not $version -or ($version -split '\+')[0] -cne $ExpectedVersion) { throw "Unexpected binary version: $binary / $version" }
}
$trust = Get-Content -LiteralPath (Join-Path $releaseRoot 'release-trust.json') -Raw | ConvertFrom-Json
$key = [Security.Cryptography.ECDsa]::Create()
try {
    $key.ImportFromPem($trust.publicKeyPem)
    $fingerprint = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($key.ExportSubjectPublicKeyInfo()))
    if ($trust.keyId -cne $publisherRecord.keyId -or $fingerprint -ne $publisherRecord.publicKeySha256) { throw 'The release changed its publisher key or key ID.' }
} finally { $key.Dispose() }
foreach ($file in Get-ChildItem -LiteralPath $releaseRoot -Recurse -File | Where-Object Extension -in @('.pem','.json')) {
    if ([IO.File]::ReadAllText($file.FullName) -match '-----BEGIN ([A-Z0-9]+ )?PRIVATE KEY-----') { throw 'A private key was included in the release.' }
}
$validationProject = Join-Path $projectRoot 'tests\Boardcast.ReleaseValidation\Boardcast.ReleaseValidation.csproj'
& dotnet build $validationProject -c Release --nologo "-p:ReleaseCorePath=$releaseRoot\Boardcast.Core.dll"
if ($LASTEXITCODE -ne 0) { throw 'Released-Core acceptance build failed.' }
$validationDll = Join-Path $projectRoot 'tests\Boardcast.ReleaseValidation\bin\Release\net10.0\Boardcast.ReleaseValidation.dll'
& dotnet $validationDll $releaseRoot $reportFullPath $ExpectedVersion
if ($LASTEXITCODE -ne 0) { throw 'Released-Core acceptance failed.' }
$result = Get-Content -LiteralPath $reportFullPath -Raw | ConvertFrom-Json
if (-not $result.success -or $result.runtime -cne $Runtime) { throw 'Release acceptance report failed or runtime differs from the requested runtime.' }
$requests = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"release-acceptance","version":"1"}}}',
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}',
    '{"jsonrpc":"2.0","id":3,"method":"ping"}'
)
$responses = @($requests | & (Join-Path $releaseRoot 'boardcast-cli.exe') mcp | ForEach-Object { $_ | ConvertFrom-Json })
$expectedTools = @('list_devices','list_transfers','discover_devices','send_files','accept_transfer','reject_transfer','pause_transfer','resume_transfer','cancel_transfer')
if ($LASTEXITCODE -ne 0 -or $responses.Count -ne 3 -or @($responses | Where-Object error).Count -ne 0 -or ($responses.id -join ',') -ne '1,2,3' -or $responses[0].result.serverInfo.version -cne $ExpectedVersion -or (Compare-Object $expectedTools @($responses[1].result.tools.name))) { throw 'Distributed CLI MCP smoke test failed.' }
foreach ($inputPath in $inputHashes.Keys) {
    if ($inputHashes[$inputPath] -ne (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash) { throw "A release acceptance input changed during validation: $inputPath" }
}
$result | Add-Member -NotePropertyName releaseInputsUnchanged -NotePropertyValue $true -Force
$result | Add-Member -NotePropertyName outerZipPath -NotePropertyValue $zipPath -Force
$result | Add-Member -NotePropertyName outerZipBytes -NotePropertyValue (Get-Item -LiteralPath $zipPath).Length -Force
$result | Add-Member -NotePropertyName outerZipSha256 -NotePropertyValue $outerHash -Force
$result | Add-Member -NotePropertyName cliMcpVerified -NotePropertyValue $true -Force
$result.checks += @('The external ZIP checksum, safe paths and absence of private keys pass.','All application components have the expected version and self-contained runtime files.','The publisher public key matches the retained release identity.','The distributed CLI reports the expected version and all nine MCP tools.')
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportFullPath -Encoding utf8
Write-Host "Final release acceptance passed: $ExpectedVersion / $Runtime / $($result.installedFilesVerified) signed files."
Write-Host "Extracted release: $releaseRoot"
Write-Host "Report: $reportFullPath"
