param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$')][string]$Version,
    [Parameter(Mandatory)][ValidateSet('win-x64','win-arm64')][string]$Runtime,
    [Parameter(Mandatory)][string]$SigningKeyPem,
    [string]$KeyId = 'boardcast-release-1',
    [int]$MinimumWindowsBuild = 19041
)
$ErrorActionPreference = 'Stop'
$releasePath = [IO.Path]::GetFullPath($ReleaseDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$privatePath = [IO.Path]::GetFullPath($SigningKeyPem)
if (-not [IO.Directory]::Exists($releasePath)) { throw 'Release directory does not exist.' }
if ($privatePath.StartsWith($releasePath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep the private signing key outside the release directory.' }
if ($KeyId.Length -lt 1 -or $KeyId.Length -gt 128) { throw 'KeyId must contain 1 to 128 characters.' }
$key = [Security.Cryptography.ECDsa]::Create()
try {
    $key.ImportFromPem([IO.File]::ReadAllText($privatePath))
    if ($key.KeySize -ne 256 -or $key.ExportParameters($false).Curve.Oid.Value -ne '1.2.840.10045.3.1.7') { throw 'The release key must be ECDSA P-256.' }
    $null = $key.ExportParameters($true) # Fail before publishing when the PEM contains only a public key.
    $oldEnvelope = Join-Path $releasePath 'release-manifest.json'
    if (Test-Path -LiteralPath $oldEnvelope) { Remove-Item -LiteralPath $oldEnvelope }
    $trust = [ordered]@{ keyId = $KeyId; publicKeyPem = $key.ExportSubjectPublicKeyInfoPem() }
    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $releasePath 'release-trust.json'), ($trust | ConvertTo-Json -Compress), $utf8)
    foreach ($required in @('Boardcast.exe','Boardcast.Daemon.exe','Boardcast.Updater.exe')) {
        if (-not [IO.File]::Exists((Join-Path $releasePath $required))) { throw "Missing required executable: $required" }
    }
    $files = @(Get-ChildItem -LiteralPath $releasePath -Recurse -File | Where-Object {
        $_.Name -notin @('Boardcast-update.zip','release-manifest.json') -and -not $_.Name.EndsWith('.sha256')
    } | Sort-Object FullName)
    if ($files.Count -gt 20000) { throw 'Release contains too many files.' }
    $unpackedBytes = ($files | Measure-Object -Property Length -Sum).Sum
    if ($unpackedBytes -gt 4GB) { throw 'Release exceeds the supported 4 GiB unpacked size.' }
    $packagePath = Join-Path $releasePath 'Boardcast-update.zip'
    if ([IO.File]::Exists($packagePath)) { Remove-Item -LiteralPath $packagePath }
    $archive = [IO.Compression.ZipFile]::Open($packagePath, [IO.Compression.ZipArchiveMode]::Create)
    $entries = [Collections.Generic.List[object]]::new()
    try {
        foreach ($file in $files) {
            if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Release files must not be links.' }
            $relative = [IO.Path]::GetRelativePath($releasePath, $file.FullName).Replace('\','/')
            if ($relative.StartsWith('../') -or $relative.StartsWith('/') -or $relative.Contains(':')) { throw 'Release file escaped its directory.' }
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            $entries.Add([ordered]@{ path = $relative; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
        }
    } finally { $archive.Dispose() }
    $package = Get-Item -LiteralPath $packagePath
    if ($package.Length -gt 2GB) { throw 'Release exceeds the supported 2 GiB package size.' }
    $manifest = [ordered]@{
        format = 1; product = 'Boardcast'; version = $Version; runtime = $Runtime; minimumWindowsBuild = $MinimumWindowsBuild
        packageBytes = $package.Length; packageSha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant(); files = @($entries.ToArray())
    }
    $payload = $utf8.GetBytes(($manifest | ConvertTo-Json -Depth 10 -Compress))
    $signature = $key.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    $envelope = [ordered]@{ format = 1; algorithm = 'ECDSA-P256-SHA256'; keyId = $KeyId; payload = [Convert]::ToBase64String($payload); signature = [Convert]::ToBase64String($signature) }
    $envelopeJson = $envelope | ConvertTo-Json -Compress
    if ($utf8.GetByteCount($envelopeJson) -gt 2MB) { throw 'Signed release manifest exceeds the supported 2 MiB limit; reduce file count or path lengths.' }
    [IO.File]::WriteAllText((Join-Path $releasePath 'release-manifest.json'), $envelopeJson, $utf8)
    Write-Host "Signed release $Version ($Runtime) with publisher key $KeyId. Keep the private key offline."
} finally { $key.Dispose() }
