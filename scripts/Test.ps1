$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Require-DotNetSdk.ps1')
& dotnet build (Join-Path $projectRoot 'Boardcast.slnx') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
foreach ($testProject in @('Boardcast.PolicyTests','Boardcast.GroupTests','Boardcast.TransportTests','Boardcast.RecoveryTests','Boardcast.ScannerTests','Boardcast.UpdateTests','Boardcast.AutomaticUpdateTests','Boardcast.LayoutTests')) {
    if ($testProject -eq 'Boardcast.LayoutTests') {
        & dotnet run --project (Join-Path $projectRoot "tests\$testProject\$testProject.csproj") -c Release --no-build -- --render-preview --output-dir (Join-Path $projectRoot 'artifacts/layout-current')
    } else {
        & dotnet run --project (Join-Path $projectRoot "tests\$testProject\$testProject.csproj") -c Release --no-build
    }
    if ($LASTEXITCODE -ne 0) { throw "Test failed: $testProject" }
}
$cli = Join-Path $projectRoot 'src\Boardcast.Cli\bin\Release\net10.0\boardcast-cli.exe'
$messages = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke-test","version":"1"}}}',
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}',
    '{"jsonrpc":"2.0","id":3,"method":"ping"}'
)
$results = $messages | & $cli mcp
if ($LASTEXITCODE -ne 0) { throw 'MCP smoke test failed.' }
$parsed = @($results | ForEach-Object { $_ | ConvertFrom-Json })
if ($parsed.Count -ne 3) { throw 'MCP response count mismatch.' }
for ($index = 0; $index -lt $parsed.Count; $index++) {
    $response = $parsed[$index]
    if ($response.jsonrpc -ne '2.0' -or $response.id -ne ($index + 1) -or
        $null -ne $response.PSObject.Properties['error'] -or $null -eq $response.PSObject.Properties['result'] -or $null -eq $response.result) {
        throw "MCP response $($index + 1) is not a successful matching response."
    }
}
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$expectedVersion = [string]$buildProperties.Project.PropertyGroup.Version
if ($parsed[0].result.protocolVersion -ne '2024-11-05' -or $parsed[0].result.serverInfo.name -ne 'boardcast' -or
    $parsed[0].result.serverInfo.version -ne $expectedVersion -or $null -eq $parsed[0].result.capabilities.tools) {
    throw 'MCP initialization metadata mismatch.'
}
$expectedTools = @('list_devices','list_transfers','discover_devices','send_files','accept_transfer','reject_transfer','pause_transfer','resume_transfer','cancel_transfer')
$actualTools = @($parsed[1].result.tools | ForEach-Object { $_.name })
if ($actualTools.Count -ne $expectedTools.Count -or @($actualTools | Sort-Object -Unique).Count -ne $expectedTools.Count -or
    @(Compare-Object -ReferenceObject $expectedTools -DifferenceObject $actualTools -CaseSensitive).Count -ne 0) {
    throw 'MCP tool list mismatch.'
}
if (@($parsed[2].result.PSObject.Properties).Count -ne 0) { throw 'MCP ping result mismatch.' }
Write-Host 'MCP smoke test passed.'



