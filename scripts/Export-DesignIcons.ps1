param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../docs/assets/icons'),
    [ValidateRange(48, 512)][int]$PixelSize = 112
)
$ErrorActionPreference = 'Stop'
$taskRepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskOutputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$taskWorkRoot = Join-Path $taskRepoRoot 'artifacts/mac-icon-design-export'
[IO.Directory]::CreateDirectory($taskWorkRoot) | Out-Null
[IO.Directory]::CreateDirectory($taskOutputRoot) | Out-Null
$taskFileSource = Join-Path $taskRepoRoot 'src/Boardcast.Windows/FileItemIcon.cs'
$taskPlatformSource = Join-Path $taskRepoRoot 'src/Boardcast.Windows/MainWindow.xaml.cs'
$taskSourceVersion = [regex]::Match([IO.File]::ReadAllText((Join-Path $taskRepoRoot 'Directory.Build.props')), '<Version>([^<]+)</Version>').Groups[1].Value
$taskUtf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $taskWorkRoot 'FileItemIcon.cs'), [IO.File]::ReadAllText($taskFileSource), $taskUtf8)
[IO.File]::WriteAllText((Join-Path $taskWorkRoot 'PlatformSource.txt'), [IO.File]::ReadAllText($taskPlatformSource), $taskUtf8)
[IO.File]::WriteAllText((Join-Path $taskWorkRoot 'Program.cs'), [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'design-icons/Program.cs')), $taskUtf8)
$taskProject = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><LangVersion>latest</LangVersion>
    <AssemblyName>Boardcast.DesignIconExporter</AssemblyName>
  </PropertyGroup>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $taskWorkRoot 'DesignIconExporter.csproj'), $taskProject, $taskUtf8)
& dotnet run --project (Join-Path $taskWorkRoot 'DesignIconExporter.csproj') --configuration Release -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -- $taskOutputRoot $taskRepoRoot $PixelSize $taskSourceVersion
if ($LASTEXITCODE -ne 0) { throw "Design icon export failed: $LASTEXITCODE" }
