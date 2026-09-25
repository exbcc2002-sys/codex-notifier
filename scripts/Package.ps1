param([switch]$SkipTests, [string]$WslDistro = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
function Invoke-Dotnet([string[]]$Arguments) {
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE)" }
}
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
$relay = Join-Path $root 'artifacts\portable-build\relay'
$out = Join-Path $root "artifacts\CodexNotifier-$version-win-x64-portable"
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false', '--nologo')
Invoke-Dotnet (@('publish', (Join-Path $root 'src\CodexNotifier.Relay\CodexNotifier.Relay.csproj'), '-o', $relay) + $common)
$relayExe = Join-Path $relay 'CodexNotifier.Relay.exe'
Invoke-Dotnet (@('publish', (Join-Path $root 'src\CodexNotifier.Desktop\CodexNotifier.Desktop.csproj'), '-o', $out, '-p:PortableBuild=true', "-p:EmbeddedRelayPath=$relayExe") + $common)
if (-not $SkipTests) {
    $testArgs = @('run', '--project', (Join-Path $root 'tests\CodexNotifier.Tests\CodexNotifier.Tests.csproj'), '-c', 'Release', '--', '--relay', $relayExe)
    if ($WslDistro) { $testArgs += @('--wsl', $WslDistro) }
    Invoke-Dotnet $testArgs
}
Copy-Item (Join-Path $root 'README.md') $out -Force
Copy-Item (Join-Path $root 'LICENSE') $out -Force
New-Item -ItemType Directory -Path (Join-Path $out 'docs') -Force | Out-Null
Copy-Item (Join-Path $root 'docs\*') (Join-Path $out 'docs') -Force
$zip = "$out.zip"
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force
$hashes = @((Get-FileHash (Join-Path $out 'CodexNotifier.exe') -Algorithm SHA256), (Get-FileHash $zip -Algorithm SHA256))
$hashes | ForEach-Object { "$($_.Hash.ToLower())  $([IO.Path]::GetFileName($_.Path))" } | Set-Content "$out.sha256.txt" -Encoding ASCII
Write-Host "Portable EXE: $out\CodexNotifier.exe"
Write-Host "Distribution ZIP: $zip"
