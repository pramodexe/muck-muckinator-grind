#!/usr/bin/env bash
# Builds the mod and makes a Thunderstore-ready zip in dist/.
set -euo pipefail
cd "$(dirname "$0")"

VERSION=$(python3 -c "import json;print(json.load(open('manifest.json'))['version_number'])")
dotnet build -c Release
cp bin/Release/netstandard2.0/MuckinatorGrind.dll release/

STAGE=$(mktemp -d)
cp manifest.json icon.png CHANGELOG.md release/MuckinatorGrind.dll "$STAGE/"
# Thunderstore can't load images by relative path, so point the logo at GitHub in the zip's README.
sed 's#src="art/logo.png"#src="https://raw.githubusercontent.com/pramodexe/muckinator-grind/main/art/logo.png"#' README.md > "$STAGE/README.md"
mkdir -p dist
OUT="$PWD/dist/MuckinatorGrind-$VERSION.zip"
rm -f "$OUT"
(cd "$STAGE" && zip -q -X "$OUT" manifest.json icon.png README.md CHANGELOG.md MuckinatorGrind.dll)
rm -rf "$STAGE"
echo "Made $OUT"
