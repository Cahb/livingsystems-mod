#!/usr/bin/env bash
# Test, build, and stage the mod into out/Mods/LivingSystems/ (NOT the live server).
#   out/Mods/LivingSystems/LivingSystems.dll         stable loader (changing it needs a server restart)
#   out/Mods/LivingSystems/logic/LivingSystems.*.dll reloadable with /ls reload
set -euo pipefail
cd "$(dirname "$0")"
./run.sh bash -c '
  set -e
  dotnet test tests/Core.Tests -c Release --nologo -v q
  dotnet build src/Loader -c Release --nologo -v q -o build/loader
  dotnet build src/Logic  -c Release --nologo -v q -o build/logic
' || exit 1
OUT=out/Mods/LivingSystems
rm -rf "$OUT"; mkdir -p "$OUT/logic"
cp build/loader/LivingSystems.dll deploy/LivingSystems_Info.yaml "$OUT/"
# the loader's SOURCE hash: deploy compares this, not the dll bytes (the compiler output differs build to build)
cat src/Loader/*.cs src/Loader/*.csproj | sha1sum | cut -d' ' -f1 > "$OUT/loader.srchash"
cp build/logic/LivingSystems.Logic.dll "$OUT/logic/"   # Core is compiled into the logic dll
mkdir -p "$OUT/prefabs"; cp deploy/prefabs/* "$OUT/prefabs/" 2>/dev/null || true
echo "Staged:"; (cd "$OUT" && find . -type f | sort)
