param([switch]$SkipTests, [string]$WslDistro = "", [string]$OutputDirectory = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { throw 'Install the .NET 10 SDK for Windows first.' }
function Invoke-Dotnet([string[]]$Arguments) {
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE)" }
}
$out = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $root 'artifacts\win-x64' }
Invoke-Dotnet @('publish', (Join-Path $root 'src\CodexNotifier.Desktop\CodexNotifier.Desktop.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', $out, '--nologo')
Invoke-Dotnet @('publish', (Join-Path $root 'src\CodexNotifier.Relay\CodexNotifier.Relay.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', (Join-Path $out 'Bridge'), '--nologo')
if (-not $SkipTests) {
    $testArgs = @('run', '--project', (Join-Path $root 'tests\CodexNotifier.Tests\CodexNotifier.Tests.csproj'), '-c', 'Release', '--', '--relay', (Join-Path $out 'Bridge\CodexNotifier.Relay.exe'))
    if ($WslDistro) { $testArgs += @('--wsl', $WslDistro) }
    Invoke-Dotnet $testArgs
}
Copy-Item (Join-Path $root 'README.md') $out -Force
Copy-Item (Join-Path $root 'LICENSE') $out -Force
New-Item -ItemType Directory -Path (Join-Path $out 'docs') -Force | Out-Null
Copy-Item (Join-Path $root 'docs\*') (Join-Path $out 'docs') -Force
Write-Host "Ready: $out\CodexNotifier.Desktop.exe"
