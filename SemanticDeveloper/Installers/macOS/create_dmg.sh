#!/usr/bin/env bash
set -euo pipefail

RID="${1:-osx-x64}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../../" && pwd)"
APP_PROJ="$ROOT/SemanticDeveloper/SemanticDeveloper.csproj"
PUBLISH_DIR="$SCRIPT_DIR/out/publish"
APP_DIR="$SCRIPT_DIR/out/SemanticDeveloper.app"
CONTENTS_DIR="$APP_DIR/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"
DIST_DIR="$SCRIPT_DIR/dist"

mkdir -p "$PUBLISH_DIR" "$MACOS_DIR" "$RESOURCES_DIR" "$DIST_DIR"

echo "Publishing SemanticDeveloper for $RID ..."
dotnet publish "$APP_PROJ" -c Release -r "$RID" --self-contained true \
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:PublishTrimmed=false \
  -o "$PUBLISH_DIR"

echo "Creating .app bundle ..."
cp -R "$SCRIPT_DIR/Info.plist" "$CONTENTS_DIR/Info.plist"
cp "$ROOT/SemanticDeveloper/Images/SemanticDeveloperLogo.ico" "$RESOURCES_DIR/Icon.icns" || true
cp -R "$PUBLISH_DIR"/* "$MACOS_DIR/"
chmod +x "$MACOS_DIR/SemanticDeveloper"

DMG_NAME="SemanticDeveloper-${RID}.dmg"
DMG_PATH="$DIST_DIR/$DMG_NAME"
test -f "$DMG_PATH" && rm -f "$DMG_PATH"

echo "Creating DMG ..."
hdiutil create -volname "SemanticDeveloper" -srcfolder "$APP_DIR" -ov -format UDZO "$DMG_PATH"
echo "Done: $DMG_PATH"

