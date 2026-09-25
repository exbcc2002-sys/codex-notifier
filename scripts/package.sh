#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
'/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe' -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$PWD/scripts/Package.ps1")" -WslDistro "${WSL_DISTRO_NAME:-Ubuntu24.04}" "$@"
chmod +x artifacts/CodexNotifier-*-win-x64-portable/CodexNotifier.exe artifacts/portable-build/relay/CodexNotifier.Relay.exe
