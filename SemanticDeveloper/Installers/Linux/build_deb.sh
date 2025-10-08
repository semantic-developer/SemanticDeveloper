#!/usr/bin/env bash
set -euo pipefail

RID="${1:-linux-x64}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../../" && pwd)"
APP_PROJ="$ROOT/SemanticDeveloper/SemanticDeveloper.csproj"
PUBLISH_DIR="$SCRIPT_DIR/out/publish"
PKG_ROOT="$SCRIPT_DIR/pkgroot"
DIST_DIR="$SCRIPT_DIR/dist"
VERSION="1.0.3"
ARCH="amd64"
if [[ "$RID" == "linux-arm64" ]]; then ARCH="arm64"; fi

rm -rf "$PKG_ROOT" "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR" "$PKG_ROOT/opt/semantic-developer" "$PKG_ROOT/usr/bin" "$PKG_ROOT/usr/share/applications" "$DIST_DIR"

echo "Publishing SemanticDeveloper for $RID ..."
dotnet publish "$APP_PROJ" -c Release -r "$RID" --self-contained true \
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:PublishTrimmed=false \
  -o "$PUBLISH_DIR"

echo "Staging files ..."
cp -R "$PUBLISH_DIR"/* "$PKG_ROOT/opt/semantic-developer/"
ln -sf "/opt/semantic-developer/SemanticDeveloper" "$PKG_ROOT/usr/bin/semantic-developer"

echo "Adding desktop entry ..."
install -m 644 "$SCRIPT_DIR/debian/usr/share/applications/semantic-developer.desktop" "$PKG_ROOT/usr/share/applications/semantic-developer.desktop"

echo "Preparing control files ..."
mkdir -p "$PKG_ROOT/DEBIAN"
CONTROL_FILE="$PKG_ROOT/DEBIAN/control"
cat > "$CONTROL_FILE" <<EOF
Package: semantic-developer
Version: $VERSION
Section: utils
Priority: optional
Architecture: $ARCH
Maintainer: Stainless Designer LLC
Depends: libgtk-3-0, libxi6, libxrender1, libx11-xcb1, libxcb1, libc6
Description: SemanticDeveloper — Avalonia desktop app for Codex CLI
EOF

echo "Building .deb ..."
DEB_PATH="$DIST_DIR/semantic-developer_${VERSION}_${ARCH}.deb"
fakeroot dpkg-deb --build "$PKG_ROOT" "$DEB_PATH"
echo "Done: $DEB_PATH"

