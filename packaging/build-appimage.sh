#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
APP_DIR="$ROOT_DIR/publish/AppDir"
PUBLISH_DIR="$ROOT_DIR/publish/linux-x64"

# 1. Publish the app
echo "Publishing for linux-x64..."
dotnet publish "$ROOT_DIR/pdfSignr/pdfSignr.csproj" -p:PublishProfile=linux-x64

# 2. Create AppDir structure
echo "Creating AppDir..."
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR/usr/bin"
mkdir -p "$APP_DIR/usr/share/icons/hicolor/256x256/apps"

# Copy published files
cp -r "$PUBLISH_DIR"/* "$APP_DIR/usr/bin/"

# Copy desktop file and icon
cp "$SCRIPT_DIR/pdfSignr.desktop" "$APP_DIR/pdfSignr.desktop"
cp "$ROOT_DIR/pdfSignr/Assets/signature_icon.png" "$APP_DIR/pdfSignr.png"
cp "$ROOT_DIR/pdfSignr/Assets/signature_icon.png" "$APP_DIR/usr/share/icons/hicolor/256x256/apps/pdfSignr.png"

# Create AppRun
cat > "$APP_DIR/AppRun" << 'APPRUN'
#!/bin/bash
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/pdfSignr" "$@"
APPRUN
chmod +x "$APP_DIR/AppRun"

# 3. Build AppImage
if ! command -v appimagetool &>/dev/null; then
    echo ""
    echo "appimagetool not found. Install it to build the AppImage:"
    echo "  wget https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
    echo "  chmod +x appimagetool-x86_64.AppImage"
    echo "  sudo mv appimagetool-x86_64.AppImage /usr/local/bin/appimagetool"
    echo ""
    echo "AppDir is ready at: $APP_DIR"
    echo "Run manually: appimagetool \"$APP_DIR\" \"$ROOT_DIR/publish/pdfSignr-x86_64.AppImage\""
    exit 0
fi

echo "Building AppImage..."
ARCH=x86_64 appimagetool "$APP_DIR" "$ROOT_DIR/publish/pdfSignr-x86_64.AppImage"
echo "Done: $ROOT_DIR/publish/pdfSignr-x86_64.AppImage"
