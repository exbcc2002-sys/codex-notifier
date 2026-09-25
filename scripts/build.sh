#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
script_path="$(wslpath -w "$project_root/scripts/Build.ps1")"
/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$script_path" -WslDistro "${WSL_DISTRO_NAME:-Ubuntu24.04}" "$@"

chmod +x "$project_root/artifacts/win-x64/CodexNotifier.Desktop.exe" "$project_root/artifacts/win-x64/Bridge/CodexNotifier.Relay.exe"
