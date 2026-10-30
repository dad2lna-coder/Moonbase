#!/bin/sh
set -eu
cd "$(dirname "$0")"

version=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' plugin.json | head -n 1)
if [ -z "$version" ]; then
  echo "plugin.json is missing a version" >&2
  exit 1
fi

dotnet build Moonfin.Plugin.RomManager.csproj -c Release

dll="bin/Release/net8.0/Moonfin.Plugin.RomManager.dll"
if [ ! -f "$dll" ]; then
  echo "missing $dll" >&2
  exit 1
fi

stage=$(mktemp -d)
mkdir -p "$stage/client" "$stage/server"
cp plugin.json "$stage/plugin.json"
cp client/index.html client/app.js client/app.css client/format.js "$stage/client/"
cp "$dll" "$stage/server/Moonfin.Plugin.RomManager.dll"

if find "$stage" -iname 'Moonfin.Server.dll' -o -iname 'Jellyfin*.dll' -o -iname 'MediaBrowser*.dll' | grep -q .; then
  echo "refusing to pack host assemblies" >&2
  exit 1
fi

mkdir -p dist
out="$(pwd)/dist/rom-manager-${version}.zip"
rm -f "$out"

if command -v zip >/dev/null 2>&1; then
  (
    cd "$stage"
    zip -r -X "$out" plugin.json client server/Moonfin.Plugin.RomManager.dll
  )
else
  python3 - "$out" "$stage" <<'PY'
import sys
import zipfile
from pathlib import Path

out, stage = sys.argv[1:]
names = [
    "plugin.json",
    "client/index.html",
    "client/app.js",
    "client/app.css",
    "client/format.js",
    "server/Moonfin.Plugin.RomManager.dll",
]
with zipfile.ZipFile(out, "w", compression=zipfile.ZIP_DEFLATED) as archive:
    for name in names:
        archive.write(Path(stage) / name, name)
PY
fi

rm -rf "$stage"
echo "$out"
