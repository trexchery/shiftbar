#!/bin/bash
# One command from a checkout: bash macos/install.sh
# Or: bash macos/install.sh /path/to/shiftbar-macos-arm64.zip
set -euo pipefail
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || { echo 'Shiftbar requires Apple Silicon macOS.' >&2; exit 1; }
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ARCHIVE="${1:-$ROOT/dist/shiftbar-macos-arm64.zip}"
if [[ ! -f "$ARCHIVE" ]]; then
    command -v brew >/dev/null || { echo 'Build dependencies require Homebrew: https://brew.sh' >&2; exit 1; }
    command -v uv >/dev/null || brew install uv
    command -v python3.12 >/dev/null || brew install python@3.12
    command -v dotnet >/dev/null || brew install --cask dotnet-sdk@8
    bash "$ROOT/macos/build-macos.sh"
fi
[[ -f "$ARCHIVE.sha256" ]] || { echo "Missing checksum: $ARCHIVE.sha256" >&2; exit 1; }
(cd "$(dirname "$ARCHIVE")" && shasum -a 256 -c "$(basename "$ARCHIVE").sha256")
DEST="${SHIFTBAR_INSTALL_DIR:-$HOME/Applications}"
mkdir -p "$DEST"
STAGE="$(mktemp -d "${TMPDIR:-/tmp}/shiftbar-install.XXXXXX")"
trap 'rmdir "$STAGE" 2>/dev/null || true' EXIT
ditto -x -k "$ARCHIVE" "$STAGE"
APP="$STAGE/Shiftbar.app"
codesign --verify --deep --strict "$APP"
# Preserve a previous install for recovery if copying is interrupted.
if [[ -d "$DEST/Shiftbar.app" ]]; then
    mv "$DEST/Shiftbar.app" "$DEST/Shiftbar.previous.$(date +%Y%m%d%H%M%S).app"
fi
ditto "$APP" "$DEST/Shiftbar.app"
echo "Installed $DEST/Shiftbar.app"
echo 'Enable Launch at Login in the menu to start automatically.'
open "$DEST/Shiftbar.app"
