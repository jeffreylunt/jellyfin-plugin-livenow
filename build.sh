#!/usr/bin/env bash
# Build the Live Now plugin in a .NET 9 SDK container (no local dotnet needed),
# then assemble the installable plugin folder + versioned ZIP under dist/.
set -euo pipefail

cd "$(dirname "$0")"
PROJ=src/Jellyfin.Plugin.LiveNow
VERSION=1.0.0.0

echo "==> Building $PROJ (net9.0)…"
docker run --rm \
  -v "$PWD/$PROJ":/src -w /src \
  mcr.microsoft.com/dotnet/sdk:9.0 \
  bash -c "rm -rf /src/bin /src/obj && dotnet build -c Release -o /src/bin/out"

echo "==> Packaging dist/live-now_${VERSION}.zip…"
# Files must sit at the ARCHIVE ROOT (not nested in a folder) or Jellyfin's
# repository installer extracts to a double-nested path and the plugin won't load.
rm -rf dist/stage "dist/live-now_${VERSION}.zip"
STAGE="dist/stage"
mkdir -p "$STAGE"
cp "$PROJ/bin/out/Jellyfin.Plugin.LiveNow.dll" "$STAGE/"
cp "$PROJ/meta.json" "$STAGE/"
( cd "$STAGE" && zip -rq "../live-now_${VERSION}.zip" Jellyfin.Plugin.LiveNow.dll meta.json )

echo "==> Done."
echo "    DLL: $PROJ/bin/out/Jellyfin.Plugin.LiveNow.dll"
echo "    ZIP: dist/live-now_${VERSION}.zip"
echo "    MD5: $(md5sum "dist/live-now_${VERSION}.zip" 2>/dev/null | awk '{print $1}' || md5 -q "dist/live-now_${VERSION}.zip")"
