param([int]$MiB = 128, [int]$IdleSeconds = 60, [int]$WarmupSeconds = 15, [string]$BinariesDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$daemon = Join-Path $projectRoot 'src\Boardcast.Daemon\bin\Release\net10.0\Boardcast.Daemon.exe'
$cli = Join-Path $projectRoot 'src\Boardcast.Cli\bin\Release\net10.0\boardcast-cli.exe'
if ($BinariesDirectory) { $daemon = Join-Path $BinariesDirectory 'Boardcast.Daemon.exe'; $cli = Join-Path $BinariesDirectory 'boardcast-cli.exe' }
if (-not (Test-Path -LiteralPath $daemon) -or -not (Test-Path -LiteralPath $cli)) { throw 'Run a Release build first.' }
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('Boardcast.Measurement-' + [Guid]::NewGuid().ToString('N'))
$senderData = Join-Path $runRoot 'sender'
$receiverData = Join-Path $runRoot 'receiver'
$receiveRoot = Join-Path $runRoot 'received'
foreach ($path in @($runRoot,$senderData,$receiverData,$receiveRoot)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
function Invoke-Boardcast([string]$Data, [string[]]$Command) {
    $result = & $cli --data-dir $Data @Command
    if ($LASTEXITCODE -ne 0) { throw "CLI failed: $Command - $result" }
    return $result | ConvertFrom-Json
}
function Free-Port {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start()
    $number = $listener.LocalEndpoint.Port
    $listener.Stop()
    return $number
}
$senderPort = Free-Port
$receiverPort = Free-Port
$senderProcess = $null
$receiverProcess = $null
try {
    $senderProcess = Start-Process -FilePath $daemon -ArgumentList @('--data-dir', ('"' + $senderData + '"'), '--port', $senderPort, '--no-ui') -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $runRoot 'sender.err')
    $receiverProcess = Start-Process -FilePath $daemon -ArgumentList @('--data-dir', ('"' + $receiverData + '"'), '--port', $receiverPort, '--no-ui') -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $runRoot 'receiver.err')
    Start-Sleep -Seconds 2
    $sender = Invoke-Boardcast $senderData @('status')
    $receiver = Invoke-Boardcast $receiverData @('status')
    $configuration = $receiver.snapshot.settings
    $configuration.receiveDirectory = $receiveRoot
    $configuration.devices = @(@{ fingerprint=$sender.snapshot.fingerprint; name='Benchmark sender'; alias=''; trusted=$true; autoAccept=$true; lastAddress='127.0.0.1'; port=$senderPort })
    $configuration.rules = @(@{ id=[Guid]::NewGuid().ToString('N'); name='Local measurement'; enabled=$true; deviceFingerprint=$sender.snapshot.fingerprint; scope='any'; extensions=@(); folderNamePattern=''; maxBytes=0; destination=$receiveRoot; autoAccept=$true })
    $settingsFile = Join-Path $runRoot 'receiver-settings.json'
    [IO.File]::WriteAllText($settingsFile, ($configuration | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    Invoke-Boardcast $receiverData @('settings','--file',$settingsFile) | Out-Null
    Invoke-Boardcast $senderData @('connect','127.0.0.1',"$receiverPort") | Out-Null
    Start-Sleep -Seconds $WarmupSeconds
    # Observe the daemon while no UI or CLI polling runs, including one discovery cycle.
    $receiverProcess.Refresh()
    $before = $receiverProcess.TotalProcessorTime.TotalMilliseconds
    $idleWatch = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $IdleSeconds
    $receiverProcess.Refresh()
    $idleMs = $receiverProcess.TotalProcessorTime.TotalMilliseconds - $before
    $idleElapsed = $idleWatch.Elapsed.TotalSeconds
    $idleMemory = $receiverProcess.WorkingSet64
    $fixture = Join-Path $runRoot 'payload.bin'
    $buffer = New-Object byte[] (1024*1024)
    $stream = [IO.File]::Create($fixture)
    try { for ($i=0; $i -lt $MiB; $i++) { $stream.Write($buffer,0,$buffer.Length) } } finally { $stream.Dispose() }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $send = Invoke-Boardcast $senderData @('send',$receiver.snapshot.fingerprint,$fixture)
    $id = $send.transferId
    $transfer = $null
    do {
        Start-Sleep -Milliseconds 150
        $status = Invoke-Boardcast $senderData @('status')
        $transfer = $status.snapshot.transfers | Where-Object { $_.id -eq $id } | Select-Object -First 1
        if ($watch.Elapsed.TotalSeconds -gt 120) { throw 'Transfer timed out.' }
    } while (-not $transfer -or $transfer.state -notin @('completed','failed','cancelled','rejected'))
    $watch.Stop()
    if ($transfer.state -ne 'completed') { throw "Transfer failed: $($transfer.message)" }
    $received = Get-ChildItem -LiteralPath $receiveRoot -Filter payload.bin -Recurse -File | Select-Object -First 1
    if (-not $received) { throw 'Received file missing.' }
    $sentHash = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash
    $receivedHash = (Get-FileHash -LiteralPath $received.FullName -Algorithm SHA256).Hash
    if ($sentHash -ne $receivedHash) { throw 'File hash mismatch.' }
    $result = [ordered]@{
        measuredAt = [DateTimeOffset]::Now.ToString('o')
        environment = 'Windows localhost TCP/TLS, two daemon processes, disk writes, CLI poll overhead included; not a LAN bandwidth result'
        warmupSeconds = $WarmupSeconds
        payloadMiB = $MiB
        elapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds,3)
        endToEndMiBPerSecond = [Math]::Round($MiB / $watch.Elapsed.TotalSeconds,2)
        idleWindowSeconds = [Math]::Round($idleElapsed,3)
        idleCpuMilliseconds = [Math]::Round($idleMs,3)
        idleSingleCorePercent = [Math]::Round($idleMs / ($idleElapsed * 1000) * 100,3)
        idleWorkingSetMiB = [Math]::Round($idleMemory / 1MB,2)
        sha256Matches = $true
    }
    $json = $result | ConvertTo-Json
    $evidenceDirectory = Join-Path $projectRoot 'artifacts'
    New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
    $evidencePath = Join-Path $evidenceDirectory 'measurement.json'
    [IO.File]::WriteAllText($evidencePath, $json, [Text.UTF8Encoding]::new($false))
    $json
    Write-Host "Evidence: $evidencePath"
}
finally {
    foreach ($pair in @(@($senderData,$senderProcess), @($receiverData,$receiverProcess))) {
        if ($pair[1]) {
            try { Invoke-Boardcast $pair[0] @('stop') | Out-Null } catch { }
            if (-not $pair[1].WaitForExit(3000)) { $pair[1].Kill(); $pair[1].WaitForExit() }
            $pair[1].Dispose()
        }
    }
    $resolvedRunRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $runRoot).ProviderPath)
    $temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedRunRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolvedRunRoot).StartsWith('Boardcast.Measurement-', [StringComparison]::Ordinal)) {
        throw 'Measurement cleanup escaped its temporary directory.'
    }
    Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
}
