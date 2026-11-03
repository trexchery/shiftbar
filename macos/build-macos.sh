#!/bin/bash
# Standalone Apple Silicon app: frozen upstream Python engine + shared .NET orchestration.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
if [[ "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
    echo 'Build on Apple Silicon macOS (or use the macOS workflow artifact).' >&2
    exit 1
fi
for tool in uv dotnet sips iconutil codesign ditto hdiutil; do
    command -v "$tool" >/dev/null || { echo "Missing build tool: $tool" >&2; exit 1; }
done
PYTHON="${SHIFTBAR_PYTHON:-python3.12}"
command -v "$PYTHON" >/dev/null || { echo "Install a framework Python 3.12, e.g. brew install python@3.12" >&2; exit 1; }
BUILD="$ROOT/build/macos"
mkdir -p "$BUILD" "$ROOT/dist"
# Clean only these generated subdirectories, so rebuilds cannot retain old code.
rm -rf "$BUILD/py2app" "$BUILD/dist" "$BUILD/backend"
# The lock includes all transitive Python and Objective-C dependencies.
UV_PROJECT_ENVIRONMENT="$BUILD/venv" uv sync --locked --python "$PYTHON" --group macos-build --extra menubar --no-editable
PY="$BUILD/venv/bin/python"
dotnet publish dock/Shiftbar.Backend/Shiftbar.Backend.csproj \
    -c Release -r osx-arm64 --self-contained true \
    -p:PublishSingleFile=false \
    --nologo -o "$BUILD/backend"
ICONSET="$BUILD/shiftbar.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" assets/shiftbar.png --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" assets/shiftbar.png --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$BUILD/shiftbar.icns"
# Keep py2app separate from the source package's PEP 621 metadata: py2app
# rejects install_requires, which setuptools imports from a root pyproject.
(cd "$ROOT/macos" && "$PY" setup.py py2app --bdist-base "$BUILD/py2app" --dist-dir "$BUILD/dist")
APP="$BUILD/dist/Shiftbar.app"
# py2app sets the name from the script, so normalize the bundle and launcher.
if [[ ! -d "$APP" && -d "$BUILD/dist/main.app" ]]; then
    mv "$BUILD/dist/main.app" "$APP"
fi
if [[ -f "$APP/Contents/MacOS/main" ]]; then
    mv "$APP/Contents/MacOS/main" "$APP/Contents/MacOS/Shiftbar"
    /usr/libexec/PlistBuddy -c 'Set :CFBundleExecutable Shiftbar' "$APP/Contents/Info.plist"
fi
# py2app's macholib must not rewrite .NET apphosts or runtime libraries.
# Copy this self-contained runtime after Python's dependency relocation.
ditto "$BUILD/backend" "$APP/Contents/Resources/backend"
chmod +x "$APP/Contents/Resources/backend/shiftbar-backend" "$APP/Contents/MacOS/cswap"
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"
"$PY" macos/smoke.py "$APP"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$ROOT/dist/shiftbar-macos-arm64.zip"
STAGE="$(mktemp -d "$BUILD/dmg.XXXXXX")"
trap 'rmdir "$STAGE" 2>/dev/null || true' EXIT
ditto "$APP" "$STAGE/Shiftbar.app"
ln -s /Applications "$STAGE/Applications"
hdiutil create -ov -format UDZO -volname 'Shiftbar' -srcfolder "$STAGE" "$ROOT/dist/shiftbar-macos-arm64.dmg"
cd "$ROOT/dist"
shasum -a 256 shiftbar-macos-arm64.zip > shiftbar-macos-arm64.zip.sha256
shasum -a 256 shiftbar-macos-arm64.dmg > shiftbar-macos-arm64.dmg.sha256
echo "Built $APP and dist/shiftbar-macos-arm64.{zip,dmg}"
