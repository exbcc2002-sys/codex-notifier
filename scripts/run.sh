#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
app_path="$project_root/artifacts/win-x64/CodexNotifier.Desktop.exe"
if [[ ! -f "$app_path" ]]; then echo 'First run: bash scripts/build.sh' >&2; exit 1; fi
chmod +x "$app_path"
if [[ -n "${CODEX_HOME:-}" && -f "$CODEX_HOME/config.toml" ]]; then
  exec "$app_path" --codex-home "$CODEX_HOME" --wsl "${WSL_DISTRO_NAME:-Ubuntu24.04}" "$@"
fi
exec "$app_path" "$@"
