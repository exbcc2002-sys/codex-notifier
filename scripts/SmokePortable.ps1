$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
$source = Join-Path $root "artifacts\CodexNotifier-$version-win-x64-portable\CodexNotifier.exe"
$isolated = Join-Path $env:TEMP ('CodexNotifier portable smoke ' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $isolated | Out-Null
$exe = Join-Path $isolated 'CodexNotifier.exe'
Copy-Item $source $exe
Copy-Item (Join-Path (Split-Path $source) 'Audio') (Join-Path $isolated 'Audio') -Recurse
$data = Join-Path $isolated 'data'
# Only the EXE and bundled Audio directory are copied. Force a fresh bundle extraction, separate from installed runtimes.
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $isolated 'bundles'
$env:DOTNET_ROOT = Join-Path $isolated 'no-installed-runtime'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$process = Start-Process -FilePath $exe -ArgumentList @('--smoke-test', '--data-dir', "`"$data`"") -PassThru
Start-Sleep -Milliseconds 1500
$loaded = (Get-Process -Id $process.Id).Modules | ForEach-Object { $_.FileName }
if ($loaded | Where-Object { $_ -like "$env:ProgramFiles\dotnet\*" }) { throw 'Loaded an installed .NET runtime' }
if (-not ($loaded | Where-Object { $_.StartsWith($env:DOTNET_BUNDLE_EXTRACT_BASE_DIR) })) { throw "No bundled native modules loaded: $loaded" }
if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Portable smoke timed out' }
$result = Get-Content (Join-Path $data 'smoke-result.txt') -Raw
if (-not $result.StartsWith('PASS:')) { throw $result }
Write-Host $result
Write-Host "PASS: native modules loaded from the extracted bundle; no installed dotnet modules loaded."
$loaded | Where-Object { $_.StartsWith($env:DOTNET_BUNDLE_EXTRACT_BASE_DIR) } | Write-Host
Write-Host "Isolated output: $isolated"
